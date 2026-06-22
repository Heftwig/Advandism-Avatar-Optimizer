#region License
/*
MIT License

Copyright(c) 2017-2020 Mattias Edlund

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityMeshSimplifier.Internal
{
    /// <summary>
    /// Immutable acceleration structure built from the unsimplified mesh. It is deliberately
    /// pure managed data so nearest-point queries are safe on simplification worker threads.
    /// </summary>
    internal sealed class ReferenceMesh
    {
        internal struct SurfaceHit
        {
            public Vector3d position;
            public Vector3 barycentric;
            public int vertex0;
            public int vertex1;
            public int vertex2;
            public int component;
            public int triangleIndex;
            public double distanceSqr;
        }

        internal struct BoundaryHit
        {
            public Vector3d position;
            public double t;
            public int vertex0;
            public int vertex1;
            public int component;
            public int surfaceComponent;
            public int segmentIndex;
            public double distanceSqr;
        }

        private struct Bounds3d
        {
            public Vector3d min;
            public Vector3d max;

            public static Bounds3d Empty
            {
                get
                {
                    return new Bounds3d
                    {
                        min = new Vector3d(double.MaxValue, double.MaxValue, double.MaxValue),
                        max = new Vector3d(double.MinValue, double.MinValue, double.MinValue)
                    };
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Encapsulate(ref Vector3d point)
            {
                if (point.x < min.x) min.x = point.x;
                if (point.y < min.y) min.y = point.y;
                if (point.z < min.z) min.z = point.z;
                if (point.x > max.x) max.x = point.x;
                if (point.y > max.y) max.y = point.y;
                if (point.z > max.z) max.z = point.z;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Encapsulate(ref Bounds3d other)
            {
                Encapsulate(ref other.min);
                Encapsulate(ref other.max);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public double DistanceSqr(ref Vector3d point)
            {
                double dx = point.x < min.x ? min.x - point.x : (point.x > max.x ? point.x - max.x : 0.0);
                double dy = point.y < min.y ? min.y - point.y : (point.y > max.y ? point.y - max.y : 0.0);
                double dz = point.z < min.z ? min.z - point.z : (point.z > max.z ? point.z - max.z : 0.0);
                return dx * dx + dy * dy + dz * dz;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public double Extent(int axis)
            {
                return max[axis] - min[axis];
            }
        }

        private struct ReferenceTriangle
        {
            public int vertex0;
            public int vertex1;
            public int vertex2;
            public int component;
            public Bounds3d bounds;
            public Vector3d centroid;

            // Immutable geometric terms reused by every Stage 4 tolerance and envelope query.
            // Precomputing these removes two vector subtractions, a cross product and several
            // squared-length evaluations from the hottest BVH primitive test.
            public Vector3d edgeAB;
            public Vector3d edgeAC;
            public double edgeDotABAB;
            public double edgeDotABAC;
            public double edgeDotACAC;
            public double edgeScale;
            public double crossMagnitudeSqr;
        }

        private struct ReferenceBoundarySegment
        {
            public int vertex0;
            public int vertex1;
            public int component;
            public int surfaceComponent;
            public Bounds3d bounds;
            public Vector3d centroid;
        }

        private struct BvhNode
        {
            public Bounds3d bounds;
            public int start;
            public int count;
            public int left;
            public int right;
            // A non-negative value means every primitive below this node belongs to the same component.
            // -1 means mixed. These tags allow component-restricted queries to prune whole branches.
            public int component;
            public int secondaryComponent;
        }

        private struct EdgeRecord
        {
            public int vertex0;
            public int vertex1;
            public int firstTriangle;
            public int count;
        }

        private struct SymmetryCounts
        {
            public int matched;
            public int tested;
            public int negativeSide;
            public int positiveSide;
        }

        private sealed class DisjointSet
        {
            private readonly int[] parent;
            private readonly byte[] rank;

            public DisjointSet(int count)
            {
                parent = new int[count];
                rank = new byte[count];
                for (int i = 0; i < count; i++)
                    parent[i] = i;
            }

            public int Find(int value)
            {
                int root = value;
                while (parent[root] != root)
                    root = parent[root];
                while (parent[value] != value)
                {
                    int next = parent[value];
                    parent[value] = root;
                    value = next;
                }
                return root;
            }

            public void Union(int a, int b)
            {
                int rootA = Find(a);
                int rootB = Find(b);
                if (rootA == rootB)
                    return;

                if (rank[rootA] < rank[rootB])
                    parent[rootA] = rootB;
                else if (rank[rootA] > rank[rootB])
                    parent[rootB] = rootA;
                else
                {
                    parent[rootB] = rootA;
                    rank[rootA]++;
                }
            }
        }

        private sealed class TriangleCentroidComparer : IComparer<int>
        {
            private readonly ReferenceTriangle[] triangles;
            private readonly int axis;

            public TriangleCentroidComparer(ReferenceTriangle[] triangles, int axis)
            {
                this.triangles = triangles;
                this.axis = axis;
            }

            public int Compare(int x, int y)
            {
                int result = triangles[x].centroid[axis].CompareTo(triangles[y].centroid[axis]);
                return result != 0 ? result : x.CompareTo(y);
            }
        }

        private sealed class SegmentCentroidComparer : IComparer<int>
        {
            private readonly ReferenceBoundarySegment[] segments;
            private readonly int axis;

            public SegmentCentroidComparer(ReferenceBoundarySegment[] segments, int axis)
            {
                this.segments = segments;
                this.axis = axis;
            }

            public int Compare(int x, int y)
            {
                int result = segments[x].centroid[axis].CompareTo(segments[y].centroid[axis]);
                return result != 0 ? result : x.CompareTo(y);
            }
        }

        private const int LeafSize = 8;
        private const int ParallelLoopMinimumItems = 2048;
        private const int InitialTraversalStackCapacity = 64;

        // Nearest-surface and Stage 4 validation queries run on worker threads. A per-thread
        // primitive stack removes recursive instance dispatch without sharing mutable state
        // between parallel mesh jobs or symmetry sampling workers.
        [ThreadStatic]
        private static int[] traversalStack;
        private readonly Vector3d[] vertices;
        private readonly ReferenceTriangle[] triangles;
        private readonly ReferenceBoundarySegment[] boundarySegments;
        private readonly int[] triangleOrder;
        private readonly int[] boundaryOrder;
        private readonly BvhNode[] triangleNodes;
        private readonly BvhNode[] boundaryNodes;
        private readonly TriangleCentroidComparer[] triangleComparers;
        private readonly SegmentCentroidComparer[] boundaryComparers;
        private readonly int[] vertexComponents;
        private readonly int[] vertexBoundaryComponents;
        private readonly int[] vertexSurfaceHints;
        private readonly int[] vertexBoundaryHints;
        private readonly double boundsDiagonal;
        private readonly bool hasBilateralSymmetry;
        private readonly double symmetryPlaneX;
        private readonly double symmetryMatchTolerance;

        public double BoundsDiagonal
        {
            get { return boundsDiagonal; }
        }

        public bool HasBoundary
        {
            get { return boundarySegments.Length > 0; }
        }

        public bool HasBilateralSymmetry
        {
            get { return hasBilateralSymmetry; }
        }

        public double SymmetryPlaneX
        {
            get { return symmetryPlaneX; }
        }

        public double SymmetryMatchTolerance
        {
            get { return symmetryMatchTolerance; }
        }

        public ReferenceMesh(
            Vector3d[] vertices,
            Triangle[] sourceTriangles,
            int sourceTriangleCount,
            int maximumDegreeOfParallelism,
            CancellationToken cancellationToken)
        {
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (sourceTriangles == null)
                throw new ArgumentNullException(nameof(sourceTriangles));
            if (sourceTriangleCount < 0 || sourceTriangleCount > sourceTriangles.Length)
                throw new ArgumentOutOfRangeException(nameof(sourceTriangleCount));
            if (maximumDegreeOfParallelism < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumDegreeOfParallelism));

            cancellationToken.ThrowIfCancellationRequested();
            // MeshSimplifier passes a fresh managed snapshot and relinquishes it after construction.
            // Keep that buffer directly instead of cloning every avatar vertex a second time.
            this.vertices = vertices;
            var liveTriangles = new List<Triangle>(sourceTriangleCount);
            for (int i = 0; i < sourceTriangleCount; i++)
            {
                if (!sourceTriangles[i].deleted)
                    liveTriangles.Add(sourceTriangles[i]);
            }

            triangles = new ReferenceTriangle[liveTriangles.Count];
            vertexComponents = new int[vertices.Length];
            vertexBoundaryComponents = new int[vertices.Length];
            vertexSurfaceHints = new int[vertices.Length];
            vertexBoundaryHints = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertexComponents[i] = -1;
                vertexBoundaryComponents[i] = -1;
                vertexSurfaceHints[i] = -1;
                vertexBoundaryHints[i] = -1;
            }

            var triangleDisjointSet = new DisjointSet(liveTriangles.Count);
            var edges = new Dictionary<ulong, EdgeRecord>(Math.Max(4, liveTriangles.Count * 2));
            for (int triangleIndex = 0; triangleIndex < liveTriangles.Count; triangleIndex++)
            {
                if ((triangleIndex & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                Triangle source = liveTriangles[triangleIndex];
                AddTriangleEdge(edges, triangleDisjointSet, triangleIndex, source.v0, source.v1);
                AddTriangleEdge(edges, triangleDisjointSet, triangleIndex, source.v1, source.v2);
                AddTriangleEdge(edges, triangleDisjointSet, triangleIndex, source.v2, source.v0);
            }

            var rootToComponent = new Dictionary<int, int>();
            int componentCount = 0;
            Bounds3d meshBounds = Bounds3d.Empty;
            for (int i = 0; i < this.vertices.Length; i++)
            {
                if ((i & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                meshBounds.Encapsulate(ref this.vertices[i]);
            }

            var triangleComponents = new int[liveTriangles.Count];
            for (int triangleIndex = 0; triangleIndex < liveTriangles.Count; triangleIndex++)
            {
                if ((triangleIndex & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int root = triangleDisjointSet.Find(triangleIndex);
                int component;
                if (!rootToComponent.TryGetValue(root, out component))
                {
                    component = componentCount++;
                    rootToComponent.Add(root, component);
                }
                triangleComponents[triangleIndex] = component;
            }

            Action<int> buildTriangle = triangleIndex =>
            {
                Triangle source = liveTriangles[triangleIndex];
                Vector3d a = this.vertices[source.v0];
                Vector3d b = this.vertices[source.v1];
                Vector3d c = this.vertices[source.v2];
                Bounds3d bounds = Bounds3d.Empty;
                bounds.Encapsulate(ref a);
                bounds.Encapsulate(ref b);
                bounds.Encapsulate(ref c);
                Vector3d edgeAB = b - a;
                Vector3d edgeAC = c - a;
                Vector3d cross;
                Vector3d.Cross(ref edgeAB, ref edgeAC, out cross);
                triangles[triangleIndex] = new ReferenceTriangle
                {
                    vertex0 = source.v0,
                    vertex1 = source.v1,
                    vertex2 = source.v2,
                    component = triangleComponents[triangleIndex],
                    bounds = bounds,
                    centroid = (a + b + c) / 3.0,
                    edgeAB = edgeAB,
                    edgeAC = edgeAC,
                    edgeDotABAB = Vector3d.Dot(ref edgeAB, ref edgeAB),
                    edgeDotABAC = Vector3d.Dot(ref edgeAB, ref edgeAC),
                    edgeDotACAC = Vector3d.Dot(ref edgeAC, ref edgeAC),
                    edgeScale = Math.Max(edgeAB.MagnitudeSqr, edgeAC.MagnitudeSqr),
                    crossMagnitudeSqr = cross.MagnitudeSqr
                };
            };

            if (maximumDegreeOfParallelism > 1 && liveTriangles.Count >= ParallelLoopMinimumItems)
            {
                Parallel.For(
                    0,
                    liveTriangles.Count,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maximumDegreeOfParallelism,
                        CancellationToken = cancellationToken
                    },
                    buildTriangle);
            }
            else
            {
                for (int triangleIndex = 0; triangleIndex < liveTriangles.Count; triangleIndex++)
                {
                    if ((triangleIndex & 4095) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    buildTriangle(triangleIndex);
                }
            }

            // Keep component/hint assignment sequential and in source order so the exact hint chosen
            // for a shared vertex stays deterministic regardless of worker scheduling.
            for (int triangleIndex = 0; triangleIndex < liveTriangles.Count; triangleIndex++)
            {
                Triangle source = liveTriangles[triangleIndex];
                int component = triangleComponents[triangleIndex];
                AssignVertexComponent(source.v0, component);
                AssignVertexComponent(source.v1, component);
                AssignVertexComponent(source.v2, component);
                AssignVertexSurfaceHint(source.v0, triangleIndex);
                AssignVertexSurfaceHint(source.v1, triangleIndex);
                AssignVertexSurfaceHint(source.v2, triangleIndex);
            }

            var boundaryList = new List<ReferenceBoundarySegment>();
            foreach (KeyValuePair<ulong, EdgeRecord> pair in edges)
            {
                EdgeRecord edge = pair.Value;
                if (edge.count != 1)
                    continue;

                int surfaceComponent = triangles[edge.firstTriangle].component;
                Vector3d a = this.vertices[edge.vertex0];
                Vector3d b = this.vertices[edge.vertex1];
                Bounds3d bounds = Bounds3d.Empty;
                bounds.Encapsulate(ref a);
                bounds.Encapsulate(ref b);
                boundaryList.Add(new ReferenceBoundarySegment
                {
                    vertex0 = edge.vertex0,
                    vertex1 = edge.vertex1,
                    component = -1,
                    surfaceComponent = surfaceComponent,
                    bounds = bounds,
                    centroid = (a + b) * 0.5
                });
            }

            boundarySegments = boundaryList.ToArray();
            if (boundarySegments.Length > 0)
            {
                BuildBoundaryComponents();
                for (int segmentIndex = 0; segmentIndex < boundarySegments.Length; segmentIndex++)
                {
                    ref ReferenceBoundarySegment segment = ref boundarySegments[segmentIndex];
                    AssignVertexBoundaryHint(segment.vertex0, segmentIndex);
                    AssignVertexBoundaryHint(segment.vertex1, segmentIndex);
                }
            }

            triangleComparers = new[]
            {
                new TriangleCentroidComparer(triangles, 0),
                new TriangleCentroidComparer(triangles, 1),
                new TriangleCentroidComparer(triangles, 2)
            };
            boundaryComparers = new[]
            {
                new SegmentCentroidComparer(boundarySegments, 0),
                new SegmentCentroidComparer(boundarySegments, 1),
                new SegmentCentroidComparer(boundarySegments, 2)
            };

            triangleOrder = new int[triangles.Length];
            for (int i = 0; i < triangleOrder.Length; i++)
                triangleOrder[i] = i;
            var triangleNodeList = new List<BvhNode>(Math.Max(1, triangles.Length * 2));
            if (triangles.Length > 0)
                BuildTriangleNode(triangleNodeList, 0, triangles.Length, cancellationToken);
            triangleNodes = triangleNodeList.ToArray();

            boundaryOrder = new int[boundarySegments.Length];
            for (int i = 0; i < boundaryOrder.Length; i++)
                boundaryOrder[i] = i;
            var boundaryNodeList = new List<BvhNode>(Math.Max(1, boundarySegments.Length * 2));
            if (boundarySegments.Length > 0)
                BuildBoundaryNode(boundaryNodeList, 0, boundarySegments.Length, cancellationToken);
            boundaryNodes = boundaryNodeList.ToArray();

            Vector3d diagonal = meshBounds.max - meshBounds.min;
            boundsDiagonal = diagonal.Magnitude;
            symmetryMatchTolerance = Math.Max(boundsDiagonal * 0.0025, 1e-9);

            double boundsCenterX = (meshBounds.min.x + meshBounds.max.x) * 0.5;
            double zeroScore = EvaluateBilateralSymmetry(0.0, symmetryMatchTolerance, maximumDegreeOfParallelism, cancellationToken);
            double centerScore = Math.Abs(boundsCenterX) > symmetryMatchTolerance * 0.25
                ? EvaluateBilateralSymmetry(boundsCenterX, symmetryMatchTolerance, maximumDegreeOfParallelism, cancellationToken)
                : zeroScore;

            // Avatar/skinned meshes normally use local X=0. Prefer that plane unless the mesh's
            // own bounds center produces a materially better match (for centered standalone pieces).
            if (centerScore > zeroScore + 0.08)
            {
                symmetryPlaneX = boundsCenterX;
                hasBilateralSymmetry = centerScore >= 0.70;
            }
            else
            {
                symmetryPlaneX = 0.0;
                hasBilateralSymmetry = zeroScore >= 0.70;
            }
        }

        private double EvaluateBilateralSymmetry(
            double planeX,
            double tolerance,
            int maximumDegreeOfParallelism,
            CancellationToken cancellationToken)
        {
            if (vertices.Length < 12 || triangleNodes.Length == 0)
                return 0.0;

            const int maximumSamples = 2048;
            int stride = Math.Max(1, vertices.Length / maximumSamples);
            int sampleCount = (vertices.Length + stride - 1) / stride;
            double ignoreBand = tolerance * 1.5;
            double toleranceSqr = tolerance * tolerance;
            SymmetryCounts totals = new SymmetryCounts();

            if (maximumDegreeOfParallelism > 1 && sampleCount >= 512)
            {
                object mergeLock = new object();
                Parallel.For(
                    0,
                    sampleCount,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maximumDegreeOfParallelism,
                        CancellationToken = cancellationToken
                    },
                    () => new SymmetryCounts(),
                    (sampleIndex, loopState, local) =>
                    {
                        int i = sampleIndex * stride;
                        if (i >= vertices.Length)
                            return local;

                        Vector3d point = vertices[i];
                        double signedDistance = point.x - planeX;
                        if (Math.Abs(signedDistance) <= ignoreBand)
                            return local;

                        if (signedDistance < 0.0) local.negativeSide++;
                        else local.positiveSide++;
                        local.tested++;

                        Vector3d mirrored = point;
                        mirrored.x = planeX * 2.0 - point.x;
                        SurfaceHit hit;
                        if (TryFindClosestSurface(ref mirrored, -1, toleranceSqr, out hit))
                            local.matched++;
                        return local;
                    },
                    local =>
                    {
                        lock (mergeLock)
                        {
                            totals.matched += local.matched;
                            totals.tested += local.tested;
                            totals.negativeSide += local.negativeSide;
                            totals.positiveSide += local.positiveSide;
                        }
                    });
            }
            else
            {
                for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                {
                    if ((sampleIndex & 255) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    int i = sampleIndex * stride;
                    if (i >= vertices.Length)
                        break;

                    Vector3d point = vertices[i];
                    double signedDistance = point.x - planeX;
                    if (Math.Abs(signedDistance) <= ignoreBand)
                        continue;

                    if (signedDistance < 0.0) totals.negativeSide++;
                    else totals.positiveSide++;
                    totals.tested++;

                    Vector3d mirrored = point;
                    mirrored.x = planeX * 2.0 - point.x;
                    SurfaceHit hit;
                    if (TryFindClosestSurface(ref mirrored, -1, toleranceSqr, out hit))
                        totals.matched++;
                }
            }

            if (totals.tested < 8 || totals.negativeSide < 4 || totals.positiveSide < 4)
                return 0.0;

            double matchRatio = (double)totals.matched / totals.tested;
            double sideBalance = (double)Math.Min(totals.negativeSide, totals.positiveSide) /
                                 Math.Max(totals.negativeSide, totals.positiveSide);
            return matchRatio * (0.85 + 0.15 * sideBalance);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetVertexComponent(int vertexIndex)
        {
            return vertexIndex >= 0 && vertexIndex < vertexComponents.Length ? vertexComponents[vertexIndex] : -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetVertexBoundaryComponent(int vertexIndex)
        {
            return vertexIndex >= 0 && vertexIndex < vertexBoundaryComponents.Length ? vertexBoundaryComponents[vertexIndex] : -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetVertexSurfaceHint(int vertexIndex)
        {
            return vertexIndex >= 0 && vertexIndex < vertexSurfaceHints.Length ? vertexSurfaceHints[vertexIndex] : -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetVertexBoundaryHint(int vertexIndex)
        {
            return vertexIndex >= 0 && vertexIndex < vertexBoundaryHints.Length ? vertexBoundaryHints[vertexIndex] : -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int[] AcquireTraversalStack()
        {
            int[] stack = traversalStack;
            if (stack == null)
            {
                stack = new int[InitialTraversalStackCapacity];
                traversalStack = stack;
            }
            return stack;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int[] GrowTraversalStack(int[] stack, int requiredCapacity)
        {
            int capacity = stack.Length * 2;
            if (capacity < requiredCapacity)
                capacity = requiredCapacity;
            Array.Resize(ref stack, capacity);
            traversalStack = stack;
            return stack;
        }

        public bool TryFindClosestSurface(
            ref Vector3d point,
            int preferredComponent,
            double maximumDistanceSqr,
            out SurfaceHit hit)
        {
            return TryFindClosestSurface(ref point, preferredComponent, maximumDistanceSqr, -1, out hit);
        }

        public bool TryFindClosestSurface(
            ref Vector3d point,
            int preferredComponent,
            double maximumDistanceSqr,
            int hintTriangleIndex,
            out SurfaceHit hit)
        {
            hit = new SurfaceHit
            {
                triangleIndex = -1,
                distanceSqr = maximumDistanceSqr > 0.0 ? maximumDistanceSqr : double.MaxValue
            };
            if (triangleNodes.Length == 0)
                return false;

            bool found = TrySeedSurfaceHit(ref point, preferredComponent, hintTriangleIndex, ref hit);
            QuerySurfaceNode(0, ref point, preferredComponent, ref hit, ref found);
            return found;
        }

        public bool TryFindClosestBoundary(
            ref Vector3d point,
            int preferredBoundaryComponent,
            int preferredSurfaceComponent,
            double maximumDistanceSqr,
            out BoundaryHit hit)
        {
            return TryFindClosestBoundary(
                ref point,
                preferredBoundaryComponent,
                preferredSurfaceComponent,
                maximumDistanceSqr,
                -1,
                out hit);
        }

        public bool TryFindClosestBoundary(
            ref Vector3d point,
            int preferredBoundaryComponent,
            int preferredSurfaceComponent,
            double maximumDistanceSqr,
            int hintSegmentIndex,
            out BoundaryHit hit)
        {
            hit = new BoundaryHit
            {
                segmentIndex = -1,
                distanceSqr = maximumDistanceSqr > 0.0 ? maximumDistanceSqr : double.MaxValue
            };
            if (boundaryNodes.Length == 0)
                return false;

            bool found = TrySeedBoundaryHit(
                ref point,
                preferredBoundaryComponent,
                preferredSurfaceComponent,
                hintSegmentIndex,
                ref hit);
            QueryBoundaryNode(0, ref point, preferredBoundaryComponent, preferredSurfaceComponent, ref hit, ref found);
            return found;
        }

        /// <summary>
        /// Tests whether any source triangle lies within the supplied squared distance. Unlike a
        /// closest-point query this exits as soon as a qualifying primitive is found, which is
        /// substantially faster for Stage 4 validation samples while preserving the exact boolean
        /// constraint: a sample is valid if and only if its closest distance is within the limit.
        /// </summary>
        public bool TryFindSurfaceWithinDistance(
            ref Vector3d point,
            int preferredComponent,
            double maximumDistanceSqr,
            int hintTriangleIndex,
            out int triangleIndex)
        {
            triangleIndex = -1;
            if (triangleNodes.Length == 0)
                return false;

            double limitSqr = maximumDistanceSqr > 0.0
                ? maximumDistanceSqr
                : double.MaxValue;

            if (IsSurfaceTriangleWithinDistance(
                ref point, preferredComponent, limitSqr, hintTriangleIndex))
            {
                triangleIndex = hintTriangleIndex;
                return true;
            }

            return QuerySurfaceNodeWithinDistance(
                0, ref point, preferredComponent, limitSqr, ref triangleIndex);
        }

        /// <summary>
        /// Boundary equivalent of TryFindSurfaceWithinDistance. It intentionally returns the first
        /// segment that proves the tolerance test instead of spending time finding the absolute
        /// nearest segment.
        /// </summary>
        public bool TryFindBoundaryWithinDistance(
            ref Vector3d point,
            int preferredBoundaryComponent,
            int preferredSurfaceComponent,
            double maximumDistanceSqr,
            int hintSegmentIndex,
            out int segmentIndex)
        {
            segmentIndex = -1;
            if (boundaryNodes.Length == 0)
                return false;

            double limitSqr = maximumDistanceSqr > 0.0
                ? maximumDistanceSqr
                : double.MaxValue;

            if (IsBoundarySegmentWithinDistance(
                ref point,
                preferredBoundaryComponent,
                preferredSurfaceComponent,
                limitSqr,
                hintSegmentIndex))
            {
                segmentIndex = hintSegmentIndex;
                return true;
            }

            return QueryBoundaryNodeWithinDistance(
                0,
                ref point,
                preferredBoundaryComponent,
                preferredSurfaceComponent,
                limitSqr,
                ref segmentIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsSurfaceTriangleWithinDistance(
            ref Vector3d point,
            int preferredComponent,
            double maximumDistanceSqr,
            int triangleIndex)
        {
            if ((uint)triangleIndex >= (uint)triangles.Length)
                return false;

            ref ReferenceTriangle triangle = ref triangles[triangleIndex];
            if (preferredComponent >= 0 && triangle.component != preferredComponent)
                return false;
            if (triangle.bounds.DistanceSqr(ref point) > maximumDistanceSqr)
                return false;

            ref Vector3d a = ref vertices[triangle.vertex0];
            ref Vector3d b = ref vertices[triangle.vertex1];
            ref Vector3d c = ref vertices[triangle.vertex2];
            return DistanceSqrToTriangle(
                       ref point,
                       ref a,
                       ref b,
                       ref c,
                       ref triangle.edgeAB,
                       ref triangle.edgeAC,
                       triangle.edgeDotABAB,
                       triangle.edgeDotABAC,
                       triangle.edgeDotACAC,
                       triangle.edgeScale,
                       triangle.crossMagnitudeSqr) <= maximumDistanceSqr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsBoundarySegmentWithinDistance(
            ref Vector3d point,
            int preferredBoundaryComponent,
            int preferredSurfaceComponent,
            double maximumDistanceSqr,
            int segmentIndex)
        {
            if ((uint)segmentIndex >= (uint)boundarySegments.Length)
                return false;

            ref ReferenceBoundarySegment segment = ref boundarySegments[segmentIndex];
            if (preferredBoundaryComponent >= 0 && segment.component != preferredBoundaryComponent)
                return false;
            if (preferredBoundaryComponent < 0 &&
                preferredSurfaceComponent >= 0 &&
                segment.surfaceComponent != preferredSurfaceComponent)
            {
                return false;
            }
            if (segment.bounds.DistanceSqr(ref point) > maximumDistanceSqr)
                return false;

            ref Vector3d a = ref vertices[segment.vertex0];
            ref Vector3d b = ref vertices[segment.vertex1];
            Vector3d closest;
            ClosestPointOnSegment(ref point, ref a, ref b, out closest);
            return (closest - point).MagnitudeSqr <= maximumDistanceSqr;
        }

        private bool QuerySurfaceNodeWithinDistance(
            int nodeIndex,
            ref Vector3d point,
            int preferredComponent,
            double maximumDistanceSqr,
            ref int triangleIndex)
        {
            int[] stack = AcquireTraversalStack();
            int stackCount = 0;
            stack[stackCount++] = nodeIndex;

            while (stackCount > 0)
            {
                int currentNodeIndex = stack[--stackCount];
                ref BvhNode node = ref triangleNodes[currentNodeIndex];
                if (!MatchesSurfaceComponent(ref node, preferredComponent))
                    continue;
                if (node.bounds.DistanceSqr(ref point) > maximumDistanceSqr)
                    continue;

                if (node.count > 0)
                {
                    int end = node.start + node.count;
                    for (int i = node.start; i < end; i++)
                    {
                        int candidateIndex = triangleOrder[i];
                        if (!IsSurfaceTriangleWithinDistance(
                            ref point,
                            preferredComponent,
                            maximumDistanceSqr,
                            candidateIndex))
                        {
                            continue;
                        }

                        triangleIndex = candidateIndex;
                        return true;
                    }
                    continue;
                }

                int leftIndex = node.left;
                int rightIndex = node.right;
                ref BvhNode leftNode = ref triangleNodes[leftIndex];
                ref BvhNode rightNode = ref triangleNodes[rightIndex];
                double leftDistance = MatchesSurfaceComponent(ref leftNode, preferredComponent)
                    ? leftNode.bounds.DistanceSqr(ref point)
                    : double.PositiveInfinity;
                double rightDistance = MatchesSurfaceComponent(ref rightNode, preferredComponent)
                    ? rightNode.bounds.DistanceSqr(ref point)
                    : double.PositiveInfinity;

                int nearIndex;
                int farIndex;
                double nearDistance;
                double farDistance;
                if (leftDistance <= rightDistance)
                {
                    nearIndex = leftIndex;
                    nearDistance = leftDistance;
                    farIndex = rightIndex;
                    farDistance = rightDistance;
                }
                else
                {
                    nearIndex = rightIndex;
                    nearDistance = rightDistance;
                    farIndex = leftIndex;
                    farDistance = leftDistance;
                }

                bool pushFar = farDistance <= maximumDistanceSqr;
                bool pushNear = nearDistance <= maximumDistanceSqr;
                int needed = stackCount + (pushFar ? 1 : 0) + (pushNear ? 1 : 0);
                if (needed > stack.Length)
                    stack = GrowTraversalStack(stack, needed);
                if (pushFar)
                    stack[stackCount++] = farIndex;
                if (pushNear)
                    stack[stackCount++] = nearIndex;
            }

            return false;
        }

        private bool QueryBoundaryNodeWithinDistance(
            int nodeIndex,
            ref Vector3d point,
            int preferredBoundaryComponent,
            int preferredSurfaceComponent,
            double maximumDistanceSqr,
            ref int segmentIndex)
        {
            int[] stack = AcquireTraversalStack();
            int stackCount = 0;
            stack[stackCount++] = nodeIndex;

            while (stackCount > 0)
            {
                int currentNodeIndex = stack[--stackCount];
                ref BvhNode node = ref boundaryNodes[currentNodeIndex];
                if (!MatchesBoundaryComponent(
                    ref node,
                    preferredBoundaryComponent,
                    preferredSurfaceComponent))
                {
                    continue;
                }
                if (node.bounds.DistanceSqr(ref point) > maximumDistanceSqr)
                    continue;

                if (node.count > 0)
                {
                    int end = node.start + node.count;
                    for (int i = node.start; i < end; i++)
                    {
                        int candidateIndex = boundaryOrder[i];
                        if (!IsBoundarySegmentWithinDistance(
                            ref point,
                            preferredBoundaryComponent,
                            preferredSurfaceComponent,
                            maximumDistanceSqr,
                            candidateIndex))
                        {
                            continue;
                        }

                        segmentIndex = candidateIndex;
                        return true;
                    }
                    continue;
                }

                int leftIndex = node.left;
                int rightIndex = node.right;
                ref BvhNode leftNode = ref boundaryNodes[leftIndex];
                ref BvhNode rightNode = ref boundaryNodes[rightIndex];
                double leftDistance = MatchesBoundaryComponent(
                    ref leftNode,
                    preferredBoundaryComponent,
                    preferredSurfaceComponent)
                    ? leftNode.bounds.DistanceSqr(ref point)
                    : double.PositiveInfinity;
                double rightDistance = MatchesBoundaryComponent(
                    ref rightNode,
                    preferredBoundaryComponent,
                    preferredSurfaceComponent)
                    ? rightNode.bounds.DistanceSqr(ref point)
                    : double.PositiveInfinity;

                int nearIndex;
                int farIndex;
                double nearDistance;
                double farDistance;
                if (leftDistance <= rightDistance)
                {
                    nearIndex = leftIndex;
                    nearDistance = leftDistance;
                    farIndex = rightIndex;
                    farDistance = rightDistance;
                }
                else
                {
                    nearIndex = rightIndex;
                    nearDistance = rightDistance;
                    farIndex = leftIndex;
                    farDistance = leftDistance;
                }

                bool pushFar = farDistance <= maximumDistanceSqr;
                bool pushNear = nearDistance <= maximumDistanceSqr;
                int needed = stackCount + (pushFar ? 1 : 0) + (pushNear ? 1 : 0);
                if (needed > stack.Length)
                    stack = GrowTraversalStack(stack, needed);
                if (pushFar)
                    stack[stackCount++] = farIndex;
                if (pushNear)
                    stack[stackCount++] = nearIndex;
            }

            return false;
        }

        private bool TrySeedSurfaceHit(
            ref Vector3d point,
            int preferredComponent,
            int triangleIndex,
            ref SurfaceHit best)
        {
            if (triangleIndex < 0 || triangleIndex >= triangles.Length)
                return false;

            ref ReferenceTriangle triangle = ref triangles[triangleIndex];
            if (preferredComponent >= 0 && triangle.component != preferredComponent)
                return false;
            if (triangle.bounds.DistanceSqr(ref point) > best.distanceSqr)
                return false;

            ref Vector3d a = ref vertices[triangle.vertex0];
            ref Vector3d b = ref vertices[triangle.vertex1];
            ref Vector3d c = ref vertices[triangle.vertex2];
            Vector3d closest;
            Vector3 barycentric;
            ClosestPointOnTriangle(
                ref point,
                ref a,
                ref b,
                ref c,
                ref triangle.edgeAB,
                ref triangle.edgeAC,
                triangle.edgeDotABAB,
                triangle.edgeDotABAC,
                triangle.edgeDotACAC,
                triangle.edgeScale,
                triangle.crossMagnitudeSqr,
                out closest,
                out barycentric);
            double distanceSqr = (closest - point).MagnitudeSqr;
            if (distanceSqr > best.distanceSqr)
                return false;

            best.position = closest;
            best.barycentric = barycentric;
            best.vertex0 = triangle.vertex0;
            best.vertex1 = triangle.vertex1;
            best.vertex2 = triangle.vertex2;
            best.component = triangle.component;
            best.triangleIndex = triangleIndex;
            best.distanceSqr = distanceSqr;
            return true;
        }

        private bool TrySeedBoundaryHit(
            ref Vector3d point,
            int preferredBoundaryComponent,
            int preferredSurfaceComponent,
            int segmentIndex,
            ref BoundaryHit best)
        {
            if (segmentIndex < 0 || segmentIndex >= boundarySegments.Length)
                return false;

            ref ReferenceBoundarySegment segment = ref boundarySegments[segmentIndex];
            if (preferredBoundaryComponent >= 0 && segment.component != preferredBoundaryComponent)
                return false;
            if (preferredBoundaryComponent < 0 && preferredSurfaceComponent >= 0 && segment.surfaceComponent != preferredSurfaceComponent)
                return false;
            if (segment.bounds.DistanceSqr(ref point) > best.distanceSqr)
                return false;

            ref Vector3d a = ref vertices[segment.vertex0];
            ref Vector3d b = ref vertices[segment.vertex1];
            Vector3d closest;
            double t = ClosestPointOnSegment(ref point, ref a, ref b, out closest);
            double distanceSqr = (closest - point).MagnitudeSqr;
            if (distanceSqr > best.distanceSqr)
                return false;

            best.position = closest;
            best.t = t;
            best.vertex0 = segment.vertex0;
            best.vertex1 = segment.vertex1;
            best.component = segment.component;
            best.surfaceComponent = segment.surfaceComponent;
            best.segmentIndex = segmentIndex;
            best.distanceSqr = distanceSqr;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssignVertexComponent(int vertexIndex, int component)
        {
            if (vertexComponents[vertexIndex] < 0)
                vertexComponents[vertexIndex] = component;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssignVertexSurfaceHint(int vertexIndex, int triangleIndex)
        {
            if (vertexSurfaceHints[vertexIndex] < 0)
                vertexSurfaceHints[vertexIndex] = triangleIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssignVertexBoundaryHint(int vertexIndex, int segmentIndex)
        {
            if (vertexBoundaryHints[vertexIndex] < 0)
                vertexBoundaryHints[vertexIndex] = segmentIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong GetEdgeKey(int vertex0, int vertex1)
        {
            uint min = (uint)Math.Min(vertex0, vertex1);
            uint max = (uint)Math.Max(vertex0, vertex1);
            return ((ulong)min << 32) | max;
        }

        private static void AddTriangleEdge(
            Dictionary<ulong, EdgeRecord> edges,
            DisjointSet triangles,
            int triangleIndex,
            int vertex0,
            int vertex1)
        {
            ulong key = GetEdgeKey(vertex0, vertex1);
            EdgeRecord edge;
            if (edges.TryGetValue(key, out edge))
            {
                triangles.Union(edge.firstTriangle, triangleIndex);
                edge.count++;
                edges[key] = edge;
            }
            else
            {
                edges.Add(key, new EdgeRecord
                {
                    vertex0 = Math.Min(vertex0, vertex1),
                    vertex1 = Math.Max(vertex0, vertex1),
                    firstTriangle = triangleIndex,
                    count = 1
                });
            }
        }

        private void BuildBoundaryComponents()
        {
            var disjointSet = new DisjointSet(boundarySegments.Length);
            var firstSegmentByVertex = new Dictionary<int, int>();
            for (int segmentIndex = 0; segmentIndex < boundarySegments.Length; segmentIndex++)
            {
                ref ReferenceBoundarySegment segment = ref boundarySegments[segmentIndex];
                UnionBoundaryVertex(firstSegmentByVertex, disjointSet, segmentIndex, segment.vertex0);
                UnionBoundaryVertex(firstSegmentByVertex, disjointSet, segmentIndex, segment.vertex1);
            }

            var rootToComponent = new Dictionary<int, int>();
            int componentCount = 0;
            for (int segmentIndex = 0; segmentIndex < boundarySegments.Length; segmentIndex++)
            {
                int root = disjointSet.Find(segmentIndex);
                int component;
                if (!rootToComponent.TryGetValue(root, out component))
                {
                    component = componentCount++;
                    rootToComponent.Add(root, component);
                }

                ref ReferenceBoundarySegment segment = ref boundarySegments[segmentIndex];
                segment.component = component;
                if (vertexBoundaryComponents[segment.vertex0] < 0)
                    vertexBoundaryComponents[segment.vertex0] = component;
                if (vertexBoundaryComponents[segment.vertex1] < 0)
                    vertexBoundaryComponents[segment.vertex1] = component;
            }
        }

        private static void UnionBoundaryVertex(
            Dictionary<int, int> firstSegmentByVertex,
            DisjointSet disjointSet,
            int segmentIndex,
            int vertexIndex)
        {
            int firstSegment;
            if (firstSegmentByVertex.TryGetValue(vertexIndex, out firstSegment))
                disjointSet.Union(firstSegment, segmentIndex);
            else
                firstSegmentByVertex.Add(vertexIndex, segmentIndex);
        }

        private int BuildTriangleNode(
            List<BvhNode> nodes,
            int start,
            int count,
            CancellationToken cancellationToken)
        {
            int nodeIndex = nodes.Count;
            if ((nodeIndex & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            nodes.Add(new BvhNode());
            Bounds3d bounds = Bounds3d.Empty;
            Bounds3d centroidBounds = Bounds3d.Empty;
            int homogeneousComponent = int.MinValue;
            for (int i = start; i < start + count; i++)
            {
                ReferenceTriangle triangle = triangles[triangleOrder[i]];
                bounds.Encapsulate(ref triangle.bounds);
                Vector3d centroid = triangle.centroid;
                centroidBounds.Encapsulate(ref centroid);
                if (homogeneousComponent == int.MinValue)
                    homogeneousComponent = triangle.component;
                else if (homogeneousComponent != triangle.component)
                    homogeneousComponent = -1;
            }

            if (homogeneousComponent == int.MinValue)
                homogeneousComponent = -1;

            if (count <= LeafSize)
            {
                nodes[nodeIndex] = new BvhNode
                {
                    bounds = bounds,
                    start = start,
                    count = count,
                    left = -1,
                    right = -1,
                    component = homogeneousComponent,
                    secondaryComponent = -1
                };
                return nodeIndex;
            }

            int axis = LargestAxis(ref centroidBounds);
            Array.Sort(triangleOrder, start, count, triangleComparers[axis]);
            int leftCount = count / 2;
            int left = BuildTriangleNode(nodes, start, leftCount, cancellationToken);
            int right = BuildTriangleNode(
                nodes, start + leftCount, count - leftCount, cancellationToken);
            nodes[nodeIndex] = new BvhNode
            {
                bounds = bounds,
                start = 0,
                count = 0,
                left = left,
                right = right,
                component = homogeneousComponent,
                secondaryComponent = -1
            };
            return nodeIndex;
        }

        private int BuildBoundaryNode(
            List<BvhNode> nodes,
            int start,
            int count,
            CancellationToken cancellationToken)
        {
            int nodeIndex = nodes.Count;
            if ((nodeIndex & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            nodes.Add(new BvhNode());
            Bounds3d bounds = Bounds3d.Empty;
            Bounds3d centroidBounds = Bounds3d.Empty;
            int homogeneousBoundaryComponent = int.MinValue;
            int homogeneousSurfaceComponent = int.MinValue;
            for (int i = start; i < start + count; i++)
            {
                ReferenceBoundarySegment segment = boundarySegments[boundaryOrder[i]];
                bounds.Encapsulate(ref segment.bounds);
                Vector3d centroid = segment.centroid;
                centroidBounds.Encapsulate(ref centroid);

                if (homogeneousBoundaryComponent == int.MinValue)
                    homogeneousBoundaryComponent = segment.component;
                else if (homogeneousBoundaryComponent != segment.component)
                    homogeneousBoundaryComponent = -1;

                if (homogeneousSurfaceComponent == int.MinValue)
                    homogeneousSurfaceComponent = segment.surfaceComponent;
                else if (homogeneousSurfaceComponent != segment.surfaceComponent)
                    homogeneousSurfaceComponent = -1;
            }

            if (homogeneousBoundaryComponent == int.MinValue)
                homogeneousBoundaryComponent = -1;
            if (homogeneousSurfaceComponent == int.MinValue)
                homogeneousSurfaceComponent = -1;

            if (count <= LeafSize)
            {
                nodes[nodeIndex] = new BvhNode
                {
                    bounds = bounds,
                    start = start,
                    count = count,
                    left = -1,
                    right = -1,
                    component = homogeneousBoundaryComponent,
                    secondaryComponent = homogeneousSurfaceComponent
                };
                return nodeIndex;
            }

            int axis = LargestAxis(ref centroidBounds);
            Array.Sort(boundaryOrder, start, count, boundaryComparers[axis]);
            int leftCount = count / 2;
            int left = BuildBoundaryNode(nodes, start, leftCount, cancellationToken);
            int right = BuildBoundaryNode(
                nodes, start + leftCount, count - leftCount, cancellationToken);
            nodes[nodeIndex] = new BvhNode
            {
                bounds = bounds,
                start = 0,
                count = 0,
                left = left,
                right = right,
                component = homogeneousBoundaryComponent,
                secondaryComponent = homogeneousSurfaceComponent
            };
            return nodeIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LargestAxis(ref Bounds3d bounds)
        {
            double x = bounds.Extent(0);
            double y = bounds.Extent(1);
            double z = bounds.Extent(2);
            if (x >= y && x >= z)
                return 0;
            return y >= z ? 1 : 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool MatchesSurfaceComponent(ref BvhNode node, int preferredComponent)
        {
            return preferredComponent < 0 ||
                node.component < 0 ||
                node.component == preferredComponent;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool MatchesBoundaryComponent(
            ref BvhNode node,
            int preferredBoundaryComponent,
            int preferredSurfaceComponent)
        {
            if (preferredBoundaryComponent >= 0)
                return node.component < 0 || node.component == preferredBoundaryComponent;
            return preferredSurfaceComponent < 0 ||
                node.secondaryComponent < 0 ||
                node.secondaryComponent == preferredSurfaceComponent;
        }

        private void QuerySurfaceNode(
            int nodeIndex,
            ref Vector3d point,
            int preferredComponent,
            ref SurfaceHit best,
            ref bool found)
        {
            int[] stack = AcquireTraversalStack();
            int stackCount = 0;
            stack[stackCount++] = nodeIndex;

            while (stackCount > 0)
            {
                int currentNodeIndex = stack[--stackCount];
                ref BvhNode node = ref triangleNodes[currentNodeIndex];
                if (!MatchesSurfaceComponent(ref node, preferredComponent))
                    continue;
                if (node.bounds.DistanceSqr(ref point) > best.distanceSqr)
                    continue;

                if (node.count > 0)
                {
                    int end = node.start + node.count;
                    for (int i = node.start; i < end; i++)
                    {
                        int triangleIndex = triangleOrder[i];
                        ref ReferenceTriangle triangle = ref triangles[triangleIndex];
                        if (preferredComponent >= 0 && triangle.component != preferredComponent)
                            continue;
                        if (triangle.bounds.DistanceSqr(ref point) > best.distanceSqr)
                            continue;

                        ref Vector3d a = ref vertices[triangle.vertex0];
                        ref Vector3d b = ref vertices[triangle.vertex1];
                        ref Vector3d c = ref vertices[triangle.vertex2];
                        Vector3d closest;
                        Vector3 barycentric;
                        ClosestPointOnTriangle(
                            ref point,
                            ref a,
                            ref b,
                            ref c,
                            ref triangle.edgeAB,
                            ref triangle.edgeAC,
                            triangle.edgeDotABAB,
                            triangle.edgeDotABAC,
                            triangle.edgeDotACAC,
                            triangle.edgeScale,
                            triangle.crossMagnitudeSqr,
                            out closest,
                            out barycentric);
                        double distanceSqr = (closest - point).MagnitudeSqr;
                        if (distanceSqr <= best.distanceSqr)
                        {
                            best.position = closest;
                            best.barycentric = barycentric;
                            best.vertex0 = triangle.vertex0;
                            best.vertex1 = triangle.vertex1;
                            best.vertex2 = triangle.vertex2;
                            best.component = triangle.component;
                            best.triangleIndex = triangleIndex;
                            best.distanceSqr = distanceSqr;
                            found = true;
                        }
                    }
                    continue;
                }

                int leftIndex = node.left;
                int rightIndex = node.right;
                ref BvhNode leftNode = ref triangleNodes[leftIndex];
                ref BvhNode rightNode = ref triangleNodes[rightIndex];
                double leftDistance = MatchesSurfaceComponent(ref leftNode, preferredComponent)
                    ? leftNode.bounds.DistanceSqr(ref point)
                    : double.PositiveInfinity;
                double rightDistance = MatchesSurfaceComponent(ref rightNode, preferredComponent)
                    ? rightNode.bounds.DistanceSqr(ref point)
                    : double.PositiveInfinity;

                int nearIndex;
                int farIndex;
                double nearDistance;
                double farDistance;
                if (leftDistance <= rightDistance)
                {
                    nearIndex = leftIndex;
                    nearDistance = leftDistance;
                    farIndex = rightIndex;
                    farDistance = rightDistance;
                }
                else
                {
                    nearIndex = rightIndex;
                    nearDistance = rightDistance;
                    farIndex = leftIndex;
                    farDistance = leftDistance;
                }

                bool pushFar = farDistance <= best.distanceSqr;
                bool pushNear = nearDistance <= best.distanceSqr;
                int needed = stackCount + (pushFar ? 1 : 0) + (pushNear ? 1 : 0);
                if (needed > stack.Length)
                    stack = GrowTraversalStack(stack, needed);
                if (pushFar)
                    stack[stackCount++] = farIndex;
                if (pushNear)
                    stack[stackCount++] = nearIndex;
            }
        }

        private void QueryBoundaryNode(
            int nodeIndex,
            ref Vector3d point,
            int preferredBoundaryComponent,
            int preferredSurfaceComponent,
            ref BoundaryHit best,
            ref bool found)
        {
            int[] stack = AcquireTraversalStack();
            int stackCount = 0;
            stack[stackCount++] = nodeIndex;

            while (stackCount > 0)
            {
                int currentNodeIndex = stack[--stackCount];
                ref BvhNode node = ref boundaryNodes[currentNodeIndex];
                if (!MatchesBoundaryComponent(
                    ref node,
                    preferredBoundaryComponent,
                    preferredSurfaceComponent))
                {
                    continue;
                }
                if (node.bounds.DistanceSqr(ref point) > best.distanceSqr)
                    continue;

                if (node.count > 0)
                {
                    int end = node.start + node.count;
                    for (int i = node.start; i < end; i++)
                    {
                        int segmentIndex = boundaryOrder[i];
                        ref ReferenceBoundarySegment segment = ref boundarySegments[segmentIndex];
                        if (preferredBoundaryComponent >= 0 &&
                            segment.component != preferredBoundaryComponent)
                        {
                            continue;
                        }
                        if (preferredBoundaryComponent < 0 &&
                            preferredSurfaceComponent >= 0 &&
                            segment.surfaceComponent != preferredSurfaceComponent)
                        {
                            continue;
                        }
                        if (segment.bounds.DistanceSqr(ref point) > best.distanceSqr)
                            continue;

                        ref Vector3d a = ref vertices[segment.vertex0];
                        ref Vector3d b = ref vertices[segment.vertex1];
                        Vector3d closest;
                        double t = ClosestPointOnSegment(ref point, ref a, ref b, out closest);
                        double distanceSqr = (closest - point).MagnitudeSqr;
                        if (distanceSqr <= best.distanceSqr)
                        {
                            best.position = closest;
                            best.t = t;
                            best.vertex0 = segment.vertex0;
                            best.vertex1 = segment.vertex1;
                            best.component = segment.component;
                            best.surfaceComponent = segment.surfaceComponent;
                            best.segmentIndex = segmentIndex;
                            best.distanceSqr = distanceSqr;
                            found = true;
                        }
                    }
                    continue;
                }

                int leftIndex = node.left;
                int rightIndex = node.right;
                ref BvhNode leftNode = ref boundaryNodes[leftIndex];
                ref BvhNode rightNode = ref boundaryNodes[rightIndex];
                double leftDistance = MatchesBoundaryComponent(
                    ref leftNode,
                    preferredBoundaryComponent,
                    preferredSurfaceComponent)
                    ? leftNode.bounds.DistanceSqr(ref point)
                    : double.PositiveInfinity;
                double rightDistance = MatchesBoundaryComponent(
                    ref rightNode,
                    preferredBoundaryComponent,
                    preferredSurfaceComponent)
                    ? rightNode.bounds.DistanceSqr(ref point)
                    : double.PositiveInfinity;

                int nearIndex;
                int farIndex;
                double nearDistance;
                double farDistance;
                if (leftDistance <= rightDistance)
                {
                    nearIndex = leftIndex;
                    nearDistance = leftDistance;
                    farIndex = rightIndex;
                    farDistance = rightDistance;
                }
                else
                {
                    nearIndex = rightIndex;
                    nearDistance = rightDistance;
                    farIndex = leftIndex;
                    farDistance = leftDistance;
                }

                bool pushFar = farDistance <= best.distanceSqr;
                bool pushNear = nearDistance <= best.distanceSqr;
                int needed = stackCount + (pushFar ? 1 : 0) + (pushNear ? 1 : 0);
                if (needed > stack.Length)
                    stack = GrowTraversalStack(stack, needed);
                if (pushFar)
                    stack[stackCount++] = farIndex;
                if (pushNear)
                    stack[stackCount++] = nearIndex;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ClosestPointOnSegment(ref Vector3d point, ref Vector3d a, ref Vector3d b, out Vector3d closest)
        {
            Vector3d ab = b - a;
            double lengthSqr = ab.MagnitudeSqr;
            double t = 0.0;
            if (lengthSqr > 1e-30)
            {
                Vector3d ap = point - a;
                t = Vector3d.Dot(ref ap, ref ab) / lengthSqr;
                if (t < 0.0) t = 0.0;
                else if (t > 1.0) t = 1.0;
            }
            closest = a + ab * t;
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double DistanceSqrToTriangle(
            ref Vector3d point,
            ref Vector3d a,
            ref Vector3d b,
            ref Vector3d c,
            ref Vector3d ab,
            ref Vector3d ac,
            double d00,
            double d01,
            double d11,
            double scale,
            double crossMagnitudeSqr)
        {
            if (scale <= 1e-30 ||
                crossMagnitudeSqr <= scale * scale * 1e-24)
            {
                return DistanceSqrToDegenerateTriangle(
                    ref point, ref a, ref b, ref c);
            }

            double apx = point.x - a.x;
            double apy = point.y - a.y;
            double apz = point.z - a.z;
            double apSqr = apx * apx + apy * apy + apz * apz;
            double d1 = ab.x * apx + ab.y * apy + ab.z * apz;
            double d2 = ac.x * apx + ac.y * apy + ac.z * apz;
            if (d1 <= 0.0 && d2 <= 0.0)
                return apSqr;

            double d3 = d1 - d00;
            double d4 = d2 - d01;
            if (d3 >= 0.0 && d4 <= d3)
            {
                double result = apSqr - 2.0 * d1 + d00;
                return result > 0.0 ? result : 0.0;
            }

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
            {
                double v = d1 / (d1 - d3);
                double result = apSqr - 2.0 * v * d1 + v * v * d00;
                return result > 0.0 ? result : 0.0;
            }

            double d5 = d1 - d01;
            double d6 = d2 - d11;
            if (d6 >= 0.0 && d5 <= d6)
            {
                double result = apSqr - 2.0 * d2 + d11;
                return result > 0.0 ? result : 0.0;
            }

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
            {
                double w = d2 / (d2 - d6);
                double result = apSqr - 2.0 * w * d2 + w * w * d11;
                return result > 0.0 ? result : 0.0;
            }

            double va = d3 * d6 - d5 * d4;
            double d43 = d4 - d3;
            double d56 = d5 - d6;
            if (va <= 0.0 && d43 >= 0.0 && d56 >= 0.0)
            {
                double w = d43 / (d43 + d56);
                double bpSqr = apSqr - 2.0 * d1 + d00;
                double bpDotBC = d4 - d3;
                double bcSqr = d00 + d11 - 2.0 * d01;
                double result = bpSqr - 2.0 * w * bpDotBC + w * w * bcSqr;
                return result > 0.0 ? result : 0.0;
            }

            double denominator = va + vb + vc;
            if (Math.Abs(denominator) <= 1e-30)
            {
                return DistanceSqrToDegenerateTriangle(
                    ref point, ref a, ref b, ref c);
            }

            double inverseDenominator = 1.0 / denominator;
            double faceV = vb * inverseDenominator;
            double faceW = vc * inverseDenominator;
            double resultFace = apSqr - faceV * d1 - faceW * d2;
            return resultFace > 0.0 ? resultFace : 0.0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double DistanceSqrToDegenerateTriangle(
            ref Vector3d point,
            ref Vector3d a,
            ref Vector3d b,
            ref Vector3d c)
        {
            Vector3d closest;
            ClosestPointOnSegment(ref point, ref a, ref b, out closest);
            double best = (closest - point).MagnitudeSqr;
            ClosestPointOnSegment(ref point, ref b, ref c, out closest);
            double candidate = (closest - point).MagnitudeSqr;
            if (candidate < best)
                best = candidate;
            ClosestPointOnSegment(ref point, ref c, ref a, out closest);
            candidate = (closest - point).MagnitudeSqr;
            return candidate < best ? candidate : best;
        }

        private static void ClosestPointOnTriangle(
            ref Vector3d point,
            ref Vector3d a,
            ref Vector3d b,
            ref Vector3d c,
            ref Vector3d ab,
            ref Vector3d ac,
            double d00,
            double d01,
            double d11,
            double scale,
            double crossMagnitudeSqr,
            out Vector3d closest,
            out Vector3 barycentric)
        {
            if (scale <= 1e-30 || crossMagnitudeSqr <= scale * scale * 1e-24)
            {
                ClosestPointOnDegenerateTriangle(ref point, ref a, ref b, ref c, out closest, out barycentric);
                return;
            }

            Vector3d ap = point - a;
            double d1 = Vector3d.Dot(ref ab, ref ap);
            double d2 = Vector3d.Dot(ref ac, ref ap);
            if (d1 <= 0.0 && d2 <= 0.0)
            {
                closest = a;
                barycentric = new Vector3(1f, 0f, 0f);
                return;
            }

            double d3 = d1 - d00;
            double d4 = d2 - d01;
            if (d3 >= 0.0 && d4 <= d3)
            {
                closest = b;
                barycentric = new Vector3(0f, 1f, 0f);
                return;
            }

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
            {
                double v = d1 / (d1 - d3);
                closest = a + ab * v;
                barycentric = new Vector3((float)(1.0 - v), (float)v, 0f);
                return;
            }

            double d5 = d1 - d01;
            double d6 = d2 - d11;
            if (d6 >= 0.0 && d5 <= d6)
            {
                closest = c;
                barycentric = new Vector3(0f, 0f, 1f);
                return;
            }

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
            {
                double w = d2 / (d2 - d6);
                closest = a + ac * w;
                barycentric = new Vector3((float)(1.0 - w), 0f, (float)w);
                return;
            }

            double va = d3 * d6 - d5 * d4;
            if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0)
            {
                double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                Vector3d bc = c - b;
                closest = b + bc * w;
                barycentric = new Vector3(0f, (float)(1.0 - w), (float)w);
                return;
            }

            double denominator = va + vb + vc;
            if (Math.Abs(denominator) <= 1e-30)
            {
                ClosestPointOnDegenerateTriangle(ref point, ref a, ref b, ref c, out closest, out barycentric);
                return;
            }

            double inverseDenominator = 1.0 / denominator;
            double faceV = vb * inverseDenominator;
            double faceW = vc * inverseDenominator;
            double faceU = 1.0 - faceV - faceW;
            closest = a * faceU + b * faceV + c * faceW;
            barycentric = new Vector3((float)faceU, (float)faceV, (float)faceW);
        }

        private static void ClosestPointOnDegenerateTriangle(
            ref Vector3d point,
            ref Vector3d a,
            ref Vector3d b,
            ref Vector3d c,
            out Vector3d closest,
            out Vector3 barycentric)
        {
            Vector3d ab;
            Vector3d bc;
            Vector3d ca;
            double tab = ClosestPointOnSegment(ref point, ref a, ref b, out ab);
            double tbc = ClosestPointOnSegment(ref point, ref b, ref c, out bc);
            double tca = ClosestPointOnSegment(ref point, ref c, ref a, out ca);
            double dab = (ab - point).MagnitudeSqr;
            double dbc = (bc - point).MagnitudeSqr;
            double dca = (ca - point).MagnitudeSqr;
            if (dab <= dbc && dab <= dca)
            {
                closest = ab;
                barycentric = new Vector3((float)(1.0 - tab), (float)tab, 0f);
            }
            else if (dbc <= dca)
            {
                closest = bc;
                barycentric = new Vector3(0f, (float)(1.0 - tbc), (float)tbc);
            }
            else
            {
                closest = ca;
                barycentric = new Vector3((float)tca, 0f, (float)(1.0 - tca));
            }
        }
    }
}
