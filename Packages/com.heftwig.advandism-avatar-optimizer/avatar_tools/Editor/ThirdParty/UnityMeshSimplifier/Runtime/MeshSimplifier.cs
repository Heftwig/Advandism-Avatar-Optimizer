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

#region Original License
/////////////////////////////////////////////
//
// Mesh Simplification Tutorial
//
// (C) by Sven Forstmann in 2014
//
// License : MIT
// http://opensource.org/licenses/MIT
//
//https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification
#endregion

#if UNITY_2018_2_OR_NEWER
#define UNITY_8UV_SUPPORT
#endif

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityMeshSimplifier.Internal;

namespace UnityMeshSimplifier
{
    /// <summary>Stages reported by MeshSimplifier.ProgressChanged.</summary>
    public enum SimplificationProgressStage
    {
        StartingPass,
        Iterating,
        CompletedPass,
        Completed
    }

    /// <summary>Thread-safe value snapshot describing triangle-target simplification progress.</summary>
    public struct SimplificationProgress
    {
        public SimplificationProgressStage Stage;
        public int PassIndex;
        public int PassCount;
        public int IterationIndex;
        public int IterationCount;
        public int StartTriangleCount;
        public int CurrentTriangleCount;
        public int TargetTriangleCount;
        public int ReferenceRejectedCollapses;
        public int InvalidAttributePlacementRejections;

        public SimplificationProgress(
            SimplificationProgressStage stage,
            int passIndex,
            int passCount,
            int iterationIndex,
            int iterationCount,
            int startTriangleCount,
            int currentTriangleCount,
            int targetTriangleCount,
            int referenceRejectedCollapses,
            int invalidAttributePlacementRejections)
        {
            Stage = stage;
            PassIndex = passIndex;
            PassCount = passCount;
            IterationIndex = iterationIndex;
            IterationCount = iterationCount;
            StartTriangleCount = startTriangleCount;
            CurrentTriangleCount = currentTriangleCount;
            TargetTriangleCount = targetTriangleCount;
            ReferenceRejectedCollapses = referenceRejectedCollapses;
            InvalidAttributePlacementRejections = invalidAttributePlacementRejections;
        }
    }

    /// <summary>
    /// The mesh simplifier.
    /// Deeply based on https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification but rewritten completely in C#.
    /// </summary>
    public sealed class MeshSimplifier
    {
        private struct CollapsePlacement
        {
            public Vector3d position;
            public int attribute0;
            public int attribute1;
            public int attribute2;
            public Vector3 barycentric;
            private int referenceTriangleHintPlusOne;
            private int referenceBoundaryHintPlusOne;

            public int ReferenceTriangleHint
            {
                get { return referenceTriangleHintPlusOne - 1; }
                set { referenceTriangleHintPlusOne = value + 1; }
            }

            public int ReferenceBoundaryHint
            {
                get { return referenceBoundaryHintPlusOne - 1; }
                set { referenceBoundaryHintPlusOne = value + 1; }
            }

            public CollapsePlacement(
                Vector3d position,
                int attribute0,
                int attribute1,
                int attribute2,
                Vector3 barycentric,
                int referenceTriangleHint = -1,
                int referenceBoundaryHint = -1)
            {
                this.position = position;
                this.attribute0 = attribute0;
                this.attribute1 = attribute1;
                this.attribute2 = attribute2;
                this.barycentric = barycentric;
                this.referenceTriangleHintPlusOne = referenceTriangleHint + 1;
                this.referenceBoundaryHintPlusOne = referenceBoundaryHint + 1;
            }
        }

        #region Consts & Static Read-Only
        private const int TriangleEdgeCount = 3;
        private const int TriangleVertexCount = 3;
        private const double DoubleEpsilon = 1.0E-3;
        private const int ParallelExpensiveLoopMinimumItems = 2048;
        private const int ParallelCheapLoopMinimumItems = 32768;
        private static readonly int UVChannelCount = MeshUtils.UVChannelCount;
        #endregion

        #region Fields
        private SimplificationOptions simplificationOptions = SimplificationOptions.Default;
        private bool verbose = false;

        private int subMeshCount = 0;
        private int[] subMeshOffsets = null;
        private ResizableArray<Triangle> triangles = null;
        private ResizableArray<Vertex> vertices = null;
        private ResizableArray<Ref> refs = null;

        private ResizableArray<Vector3> vertNormals = null;
        private ResizableArray<Vector4> vertTangents = null;
        private UVChannels<Vector2> vertUV2D = null;
        private UVChannels<Vector3> vertUV3D = null;
        private UVChannels<Vector4> vertUV4D = null;
        private ResizableArray<Color> vertColors = null;
        private ResizableArray<BoneWeight1[]> vertBoneWeights = null;
        private ResizableArray<BlendShapeContainer> blendShapes = null;

        private Matrix4x4[] bindposes = null;
        private int[] collapseFlipTriangleBuffer0 = Array.Empty<int>();
        private int[] collapseFlipVertexBuffer00 = Array.Empty<int>();
        private int[] collapseFlipVertexBuffer01 = Array.Empty<int>();
        private int collapseFlipCount0 = 0;
        private int[] collapseFlipTriangleBuffer1 = Array.Empty<int>();
        private int[] collapseFlipVertexBuffer10 = Array.Empty<int>();
        private int[] collapseFlipVertexBuffer11 = Array.Empty<int>();
        private int collapseFlipCount1 = 0;
        private int[] collapseFutureTriangleBuffer = Array.Empty<int>();
        private int[] collapseFutureFixedVertexBuffer0 = Array.Empty<int>();
        private int[] collapseFutureFixedVertexBuffer1 = Array.Empty<int>();
        private int collapseFutureTriangleCount = 0;
        private int preparedCollapseVertex0 = -1;
        private int preparedCollapseVertex1 = -1;
        private int[] topologyNeighborBuffer0 = Array.Empty<int>();
        private ulong[] topologyTriangleFanKeyBuffer = Array.Empty<ulong>();
        private int[] topologyVertexVisitStamps = Array.Empty<int>();
        private int topologyVertexVisitStamp = 0;
        private readonly List<int> boundaryTouchedVertexBuffer = new List<int>(32);
        private int[] boundaryNeighborVisitStamps = Array.Empty<int>();
        private int[] boundaryNeighborCounts = Array.Empty<int>();
        private int boundaryNeighborVisitStamp = 0;
        private readonly HashSet<int> subMeshHashSet1 = new HashSet<int>();
        private readonly HashSet<int> subMeshHashSet2 = new HashSet<int>();
        private readonly HashSet<int> referenceBoundaryNeighborSet = new HashSet<int>();
        private readonly Dictionary<ulong, int> referenceBoundaryEdgeUse = new Dictionary<ulong, int>();
        private int[] referenceTriangleVisitStamps = Array.Empty<int>();
        private int referenceTriangleVisitStamp = 0;
        private int[] referenceMidpointVisitStamps = Array.Empty<int>();
        private int referenceMidpointVisitStamp = 0;
        private readonly Dictionary<int, double> boneWeightAccumulator = new Dictionary<int, double>(16);
        private readonly List<KeyValuePair<int, double>> boneWeightSortBuffer = new List<KeyValuePair<int, double>>(16);
        private readonly ResizableArray<bool> deletedTriangleBuffer0 = new ResizableArray<bool>(32);
        private readonly ResizableArray<bool> deletedTriangleBuffer1 = new ResizableArray<bool>(32);
        private readonly ConcurrentQueue<string> verboseMessageQueue = new ConcurrentQueue<string>();
        private readonly int unityThreadId;
        private int simplificationInProgress = 0;
        private string sourceMeshName = "Mesh";
        private ReferenceMesh referenceMesh = null;
        private int referenceConstraintRejectedCollapses = 0;
        private int invalidAttributePlacementRejections = 0;
        private int lastSimplificationIterationCount = 0;
        private int collapseSweepIndex = 0;
        private double cachedFeatureCosineThreshold = 0.0;
        private double cachedMinimumNormalDot = 0.0;
        private double cachedReferenceSurfaceToleranceSqr = 0.0;
        private double cachedReferenceBoundaryToleranceSqr = 0.0;
        private VertexPlacementMode cachedVertexPlacementMode = VertexPlacementMode.Optimal;
        private bool cachedPreserveSurfaceCurvature = false;
        private bool cachedPreserveSurfaceEnvelope = false;
        private bool cachedPreserveBorderEdges = false;
        private bool cachedPreserveBilateralSymmetry = false;
        private double[] cachedIterationThresholds = Array.Empty<double>();
        private SymmetricMatrix[] triangleQuadricBuffer = Array.Empty<SymmetricMatrix>();
        private double[] triangleErrorBuffer = Array.Empty<double>();
        private int maximumDegreeOfParallelism = 1;
        private ParallelOptions cachedParallelOptions = null;
        private CancellationToken cachedParallelCancellationToken;
        private int cachedParallelWorkerCount = 0;
        private Action<SimplificationProgress> progressChanged = null;
        #endregion

        #region Properties
        /// <summary>
        /// Optional callback invoked from the simplification thread with immutable progress snapshots.
        /// Do not call UnityEngine or UnityEditor APIs from this callback.
        /// </summary>
        public Action<SimplificationProgress> ProgressChanged
        {
            get { return progressChanged; }
            set
            {
                EnsureNotSimplifying();
                progressChanged = value;
            }
        }

        /// <summary>
        /// Gets or sets all of the simplification options as a single block.
        /// Default value: SimplificationOptions.Default
        /// </summary>
        public SimplificationOptions SimplificationOptions
        {
            get { return this.simplificationOptions; }
            set
            {
                EnsureNotSimplifying();
                ValidateOptions(value);
                this.simplificationOptions = value;
            }
        }

        /// <summary>
        /// Gets or sets if the border edges should be preserved.
        /// Default value: false
        /// </summary>
        [Obsolete("Use MeshSimplifier.SimplificationOptions instead.", false)]
        public bool PreserveBorderEdges
        {
            get { return simplificationOptions.PreserveBorderEdges; }
            set
            {
                var simplificationOptions = this.simplificationOptions;
                simplificationOptions.PreserveBorderEdges = value;
                SimplificationOptions = simplificationOptions;
            }
        }

        /// <summary>
        /// Gets or sets if the UV seam edges should be preserved.
        /// Default value: true
        /// </summary>
        [Obsolete("Use MeshSimplifier.SimplificationOptions instead.", false)]
        public bool PreserveUVSeamEdges
        {
            get { return simplificationOptions.PreserveUVSeamEdges; }
            set
            {
                var simplificationOptions = this.simplificationOptions;
                simplificationOptions.PreserveUVSeamEdges = value;
                SimplificationOptions = simplificationOptions;
            }
        }

        /// <summary>
        /// Gets or sets if the UV foldover edges should be preserved.
        /// Default value: true
        /// </summary>
        [Obsolete("Use MeshSimplifier.SimplificationOptions instead.", false)]
        public bool PreserveUVFoldoverEdges
        {
            get { return simplificationOptions.PreserveUVFoldoverEdges; }
            set
            {
                var simplificationOptions = this.simplificationOptions;
                simplificationOptions.PreserveUVFoldoverEdges = value;
                SimplificationOptions = simplificationOptions;
            }
        }

        /// <summary>
        /// Gets or sets if the discrete curvature of the mesh surface be taken into account during simplification.
        /// Default value: false
        /// </summary>
        [Obsolete("Use MeshSimplifier.SimplificationOptions instead.", false)]
        public bool PreserveSurfaceCurvature
        {
            get { return simplificationOptions.PreserveSurfaceCurvature; }
            set
            {
                var simplificationOptions = this.simplificationOptions;
                simplificationOptions.PreserveSurfaceCurvature = value;
                SimplificationOptions = simplificationOptions;
            }
        }

        /// <summary>
        /// Gets or sets if a feature for smarter vertex linking should be enabled, reducing artifacts in the
        /// decimated result at the cost of a slightly more expensive initialization by treating vertices at
        /// the same position as the same vertex while separating the attributes.
        /// Default value: true
        /// </summary>
        [Obsolete("Use MeshSimplifier.SimplificationOptions instead.", false)]
        public bool EnableSmartLink
        {
            get { return simplificationOptions.EnableSmartLink; }
            set
            {
                var simplificationOptions = this.simplificationOptions;
                simplificationOptions.EnableSmartLink = value;
                SimplificationOptions = simplificationOptions;
            }
        }

        /// <summary>
        /// Gets or sets the maximum iteration count. Higher number is more expensive but can bring you closer to your target quality.
        /// Sometimes a lower maximum count might be desired in order to lower the performance cost.
        /// Default value: 100
        /// </summary>
        [Obsolete("Use MeshSimplifier.SimplificationOptions instead.", false)]
        public int MaxIterationCount
        {
            get { return simplificationOptions.MaxIterationCount; }
            set
            {
                var simplificationOptions = this.simplificationOptions;
                simplificationOptions.MaxIterationCount = value;
                SimplificationOptions = simplificationOptions;
            }
        }

        /// <summary>
        /// Gets or sets how quickly the accepted error threshold grows. Lower values are more conservative; higher values simplify more aggressively.
        /// Default value: 7.0
        /// </summary>
        [Obsolete("Use MeshSimplifier.SimplificationOptions instead.", false)]
        public double Agressiveness
        {
            get { return simplificationOptions.Agressiveness; }
            set
            {
                var simplificationOptions = this.simplificationOptions;
                simplificationOptions.Agressiveness = value;
                SimplificationOptions = simplificationOptions;
            }
        }

        /// <summary>
        /// Gets or sets if verbose information should be printed to the console.
        /// Default value: false
        /// </summary>
        public bool Verbose
        {
            get { return verbose; }
            set
            {
                EnsureNotSimplifying();
                verbose = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum distance between two vertices in order to link them.
        /// Note that this value is only used if EnableSmartLink is true.
        /// </summary>
        [Obsolete("Use MeshSimplifier.SimplificationOptions instead.", false)]
        public double VertexLinkDistance
        {
            get { return simplificationOptions.VertexLinkDistance; }
            set
            {
                var simplificationOptions = this.simplificationOptions;
                simplificationOptions.VertexLinkDistance = value > double.Epsilon ? value : double.Epsilon;
                SimplificationOptions = simplificationOptions;
            }
        }

        /// <summary>
        /// Gets or sets the maximum squared distance between two vertices in order to link them.
        /// Note that this value is only used if EnableSmartLink is true.
        /// Default value: double.Epsilon
        /// </summary>
        [Obsolete("Use MeshSimplifier.SimplificationOptions instead.", false)]
        public double VertexLinkDistanceSqr
        {
            get { return simplificationOptions.VertexLinkDistance * simplificationOptions.VertexLinkDistance; }
            set
            {
                var simplificationOptions = this.simplificationOptions;
                simplificationOptions.VertexLinkDistance = Math.Sqrt(value);
                SimplificationOptions = simplificationOptions;
            }
        }

        /// <summary>
        /// Gets or sets the vertex positions.
        /// </summary>
        public Vector3[] Vertices
        {
            get
            {
                EnsureNotSimplifying();
                int vertexCount = this.vertices.Length;
                var result = new Vector3[vertexCount];
                var vertArr = this.vertices.Data;
                for (int i = 0; i < vertexCount; i++)
                    result[i] = (Vector3)vertArr[i].p;

                return result;
            }
            set
            {
                EnsureNotSimplifying();
                if (value == null)
                    throw new ArgumentNullException(nameof(value));

                if (vertices.Length != value.Length)
                    ClearVertexAttributes();

                referenceMesh = null;
                vertices.Resize(value.Length);
                var vertArr = vertices.Data;
                for (int i = 0; i < value.Length; i++)
                {
                    Vector3 position = value[i];
                    if (!IsFinite(position))
                        throw new ArgumentException("Vertex positions must contain finite values.", nameof(value));

                    vertArr[i] = new Vertex(i, position);
                }
            }
        }

        /// <summary>
        /// Gets the count of sub-meshes.
        /// </summary>
        public int SubMeshCount
        {
            get
            {
                EnsureNotSimplifying();
                return subMeshCount;
            }
        }

        /// <summary>
        /// Gets the count of blend shapes.
        /// </summary>
        public int BlendShapeCount
        {
            get
            {
                EnsureNotSimplifying();
                return blendShapes != null ? blendShapes.Length : 0;
            }
        }

        /// <summary>
        /// Gets or sets the vertex normals.
        /// </summary>
        public Vector3[] Normals
        {
            get
            {
                EnsureNotSimplifying();
                return vertNormals != null ? vertNormals.ToArray() : null;
            }
            set
            {
                EnsureNotSimplifying();
                ValidateFiniteValues(value, "normals");
                InitializeVertexAttribute(value, ref vertNormals, "normals");
            }
        }

        /// <summary>
        /// Gets or sets the vertex tangents.
        /// </summary>
        public Vector4[] Tangents
        {
            get
            {
                EnsureNotSimplifying();
                return vertTangents != null ? vertTangents.ToArray() : null;
            }
            set
            {
                EnsureNotSimplifying();
                ValidateFiniteValues(value, "tangents");
                InitializeVertexAttribute(value, ref vertTangents, "tangents");
            }
        }

        /// <summary>
        /// Gets or sets the vertex 2D UV set 1.
        /// </summary>
        public Vector2[] UV1
        {
            get { return GetUVs2D(0); }
            set { SetUVs(0, value); }
        }

        /// <summary>
        /// Gets or sets the vertex 2D UV set 2.
        /// </summary>
        public Vector2[] UV2
        {
            get { return GetUVs2D(1); }
            set { SetUVs(1, value); }
        }

        /// <summary>
        /// Gets or sets the vertex 2D UV set 3.
        /// </summary>
        public Vector2[] UV3
        {
            get { return GetUVs2D(2); }
            set { SetUVs(2, value); }
        }

        /// <summary>
        /// Gets or sets the vertex 2D UV set 4.
        /// </summary>
        public Vector2[] UV4
        {
            get { return GetUVs2D(3); }
            set { SetUVs(3, value); }
        }

#if UNITY_8UV_SUPPORT
        /// <summary>
        /// Gets or sets the vertex 2D UV set 5.
        /// </summary>
        public Vector2[] UV5
        {
            get { return GetUVs2D(4); }
            set { SetUVs(4, value); }
        }

        /// <summary>
        /// Gets or sets the vertex 2D UV set 6.
        /// </summary>
        public Vector2[] UV6
        {
            get { return GetUVs2D(5); }
            set { SetUVs(5, value); }
        }

        /// <summary>
        /// Gets or sets the vertex 2D UV set 7.
        /// </summary>
        public Vector2[] UV7
        {
            get { return GetUVs2D(6); }
            set { SetUVs(6, value); }
        }

        /// <summary>
        /// Gets or sets the vertex 2D UV set 8.
        /// </summary>
        public Vector2[] UV8
        {
            get { return GetUVs2D(7); }
            set { SetUVs(7, value); }
        }
#endif

        /// <summary>
        /// Gets or sets the vertex colors.
        /// </summary>
        public Color[] Colors
        {
            get
            {
                EnsureNotSimplifying();
                return vertColors != null ? vertColors.ToArray() : null;
            }
            set
            {
                EnsureNotSimplifying();
                ValidateFiniteValues(value, "colors");
                InitializeVertexAttribute(value, ref vertColors, "colors");
            }
        }

        /// <summary>
        /// Gets or sets legacy four-influence bone weights. This API truncates additional influences.
        /// Use BoneWeights1 for avatar-safe variable-count skinning.
        /// </summary>
        [Obsolete("Use BoneWeights1 to preserve all avatar bone influences.", false)]
        public BoneWeight[] BoneWeights
        {
            get
            {
                EnsureNotSimplifying();
                return MeshUtils.ConvertToLegacyBoneWeights(GetBoneWeights1Copy());
            }
            set
            {
                EnsureNotSimplifying();
                BoneWeights1 = MeshUtils.ConvertLegacyBoneWeights(value);
            }
        }

        /// <summary>
        /// Gets or sets all variable-count bone influences. Each outer array element represents one vertex.
        /// </summary>
        public BoneWeight1[][] BoneWeights1
        {
            get
            {
                EnsureNotSimplifying();
                return GetBoneWeights1Copy();
            }
            set
            {
                EnsureNotSimplifying();
                InitializeBoneWeights(value);
            }
        }

        /// <summary>
        /// Removes influences whose bone slot is unavailable, then renormalizes the remaining influences.
        /// Bind-pose and renderer bone indices are not compacted, so the result remains aligned with the
        /// original renderer. Orphaned vertices inherit the nearest connected valid skinning. Disconnected
        /// unresolved vertices receive the optional fallback, or remain unweighted when the fallback is -1.
        /// </summary>
        public void FilterBoneInfluences(bool[] validBoneSlots, int fallbackBoneIndex)
        {
            EnsureNotSimplifying();
            if (validBoneSlots == null)
                throw new ArgumentNullException(nameof(validBoneSlots));
            if (vertBoneWeights == null)
                return;

            int bindposeCount = bindposes != null ? bindposes.Length : 0;
            if (validBoneSlots.Length < bindposeCount)
                throw new ArgumentException("The valid bone slot mask must cover every mesh bindpose.", nameof(validBoneSlots));
            if (fallbackBoneIndex >= 0 &&
                (fallbackBoneIndex >= bindposeCount || !validBoneSlots[fallbackBoneIndex]))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fallbackBoneIndex),
                    "The fallback bone index must be -1 or identify an available renderer bone slot inside the bind-pose range.");
            }

            int vertexCount = vertBoneWeights.Length;
            var filteredWeights = new BoneWeight1[vertexCount][];
            var hasResolvedWeights = new bool[vertexCount];
            int resolvedCount = 0;

            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                BoneWeight1[] source = vertBoneWeights[vertexIndex] ?? Array.Empty<BoneWeight1>();
                boneWeightAccumulator.Clear();
                for (int influenceIndex = 0; influenceIndex < source.Length; influenceIndex++)
                {
                    BoneWeight1 influence = source[influenceIndex];
                    int boneIndex = influence.boneIndex;
                    if (boneIndex < 0 || boneIndex >= bindposeCount || !validBoneSlots[boneIndex])
                        continue;
                    if (influence.weight <= 0f || float.IsNaN(influence.weight) || float.IsInfinity(influence.weight))
                        continue;

                    double accumulated;
                    if (boneWeightAccumulator.TryGetValue(boneIndex, out accumulated))
                        boneWeightAccumulator[boneIndex] = accumulated + influence.weight;
                    else
                        boneWeightAccumulator.Add(boneIndex, influence.weight);
                }

                BoneWeight1[] filtered = BuildNormalizedBoneWeights(0f, byte.MaxValue);
                filteredWeights[vertexIndex] = filtered;
                if (filtered.Length > 0)
                {
                    hasResolvedWeights[vertexIndex] = true;
                    resolvedCount++;
                }
            }

            if (resolvedCount > 0 && resolvedCount < vertexCount && triangles != null && triangles.Length > 0)
            {
                var neighborCounts = new int[vertexCount];
                Triangle[] triangleData = triangles.Data;
                for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex++)
                {
                    Triangle triangle = triangleData[triangleIndex];
                    if (triangle.deleted)
                        continue;

                    CountBoneFilterEdge(neighborCounts, triangle.v0, triangle.v1, vertexCount);
                    CountBoneFilterEdge(neighborCounts, triangle.v1, triangle.v2, vertexCount);
                    CountBoneFilterEdge(neighborCounts, triangle.v2, triangle.v0, vertexCount);
                }

                var neighborOffsets = new int[vertexCount + 1];
                for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                    neighborOffsets[vertexIndex + 1] = checked(neighborOffsets[vertexIndex] + neighborCounts[vertexIndex]);

                var neighborWriteOffsets = new int[vertexCount];
                Array.Copy(neighborOffsets, neighborWriteOffsets, vertexCount);
                var neighborData = new int[neighborOffsets[vertexCount]];
                for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex++)
                {
                    Triangle triangle = triangleData[triangleIndex];
                    if (triangle.deleted)
                        continue;

                    WriteBoneFilterEdge(neighborData, neighborWriteOffsets, triangle.v0, triangle.v1, vertexCount);
                    WriteBoneFilterEdge(neighborData, neighborWriteOffsets, triangle.v1, triangle.v2, vertexCount);
                    WriteBoneFilterEdge(neighborData, neighborWriteOffsets, triangle.v2, triangle.v0, vertexCount);
                }

                var queue = new Queue<int>(resolvedCount);
                for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                {
                    if (hasResolvedWeights[vertexIndex])
                        queue.Enqueue(vertexIndex);
                }

                while (queue.Count > 0)
                {
                    int sourceVertex = queue.Dequeue();
                    BoneWeight1[] sourceWeights = filteredWeights[sourceVertex];
                    int neighborEnd = neighborOffsets[sourceVertex + 1];
                    for (int neighborOffset = neighborOffsets[sourceVertex]; neighborOffset < neighborEnd; neighborOffset++)
                    {
                        int destinationVertex = neighborData[neighborOffset];
                        if (hasResolvedWeights[destinationVertex])
                            continue;

                        filteredWeights[destinationVertex] = (BoneWeight1[])sourceWeights.Clone();
                        hasResolvedWeights[destinationVertex] = true;
                        queue.Enqueue(destinationVertex);
                    }
                }
            }

            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                BoneWeight1[] filtered = filteredWeights[vertexIndex];
                if ((filtered == null || filtered.Length == 0) && fallbackBoneIndex >= 0)
                {
                    filtered = new[]
                    {
                        new BoneWeight1 { boneIndex = fallbackBoneIndex, weight = 1f }
                    };
                }

                vertBoneWeights[vertexIndex] = filtered ?? Array.Empty<BoneWeight1>();
            }

            ValidateBoneWeightBindposes();
        }

        private static void CountBoneFilterEdge(int[] neighborCounts, int vertex0, int vertex1, int vertexCount)
        {
            if (vertex0 < 0 || vertex0 >= vertexCount || vertex1 < 0 || vertex1 >= vertexCount || vertex0 == vertex1)
                return;
            neighborCounts[vertex0] = checked(neighborCounts[vertex0] + 1);
            neighborCounts[vertex1] = checked(neighborCounts[vertex1] + 1);
        }

        private static void WriteBoneFilterEdge(
            int[] neighborData,
            int[] neighborWriteOffsets,
            int vertex0,
            int vertex1,
            int vertexCount)
        {
            if (vertex0 < 0 || vertex0 >= vertexCount || vertex1 < 0 || vertex1 >= vertexCount || vertex0 == vertex1)
                return;
            neighborData[neighborWriteOffsets[vertex0]++] = vertex1;
            neighborData[neighborWriteOffsets[vertex1]++] = vertex0;
        }

        /// <summary>
        /// Gets or sets a copy of the mesh bind poses. Bone indices are validated against these bind poses
        /// when a mesh is initialized or emitted.
        /// </summary>
        public Matrix4x4[] BindPoses
        {
            get
            {
                EnsureNotSimplifying();
                return bindposes != null ? (Matrix4x4[])bindposes.Clone() : null;
            }
            set
            {
                EnsureNotSimplifying();
                ValidateFiniteValues(value, "bind poses");
                bindposes = value != null && value.Length > 0 ? (Matrix4x4[])value.Clone() : null;
            }
        }

        /// <summary>
        /// True while a synchronous or asynchronous simplification pass is mutating this instance.
        /// </summary>
        public bool IsSimplifying
        {
            get { return Volatile.Read(ref simplificationInProgress) != 0; }
        }

        /// <summary>
        /// Maximum worker count used by safe, read-only or independent preprocessing loops inside one
        /// simplifier. The edge-collapse mutation loop itself remains sequential. Set this before
        /// starting simplification. A value of 1 disables internal parallel loops.
        /// </summary>
        public int MaxDegreeOfParallelism
        {
            get { return Volatile.Read(ref maximumDegreeOfParallelism); }
            set
            {
                EnsureNotSimplifying();
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(value), "The maximum degree of parallelism must be at least one.");
                Volatile.Write(ref maximumDegreeOfParallelism, value);
                cachedParallelOptions = null;
                cachedParallelWorkerCount = 0;
            }
        }
        #endregion

        #region Constructors
        /// <summary>
        /// Creates a new mesh simplifier.
        /// </summary>
        public MeshSimplifier()
        {
            unityThreadId = Thread.CurrentThread.ManagedThreadId;
            triangles = new ResizableArray<Triangle>(0);
            vertices = new ResizableArray<Vertex>(0);
            refs = new ResizableArray<Ref>(0);
        }

        /// <summary>
        /// Creates a new mesh simplifier.
        /// </summary>
        /// <param name="mesh">The original mesh to simplify.</param>
        public MeshSimplifier(Mesh mesh)
            : this()
        {
            if (mesh != null)
            {
                Initialize(mesh);
            }
        }
        #endregion

        #region Private Methods
        private void EnsureNotSimplifying()
        {
            if (Volatile.Read(ref simplificationInProgress) != 0)
                throw new InvalidOperationException("This MeshSimplifier is currently simplifying. Wait for the active operation to finish before reading or mutating it.");
        }

        private void EnsureUnityThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != unityThreadId)
                throw new InvalidOperationException("Unity Mesh objects must be read and created on the thread that owns this MeshSimplifier. Call Initialize and ToMesh on Unity's main thread.");
        }

        private void BeginSimplification()
        {
            if (Interlocked.CompareExchange(ref simplificationInProgress, 1, 0) != 0)
                throw new InvalidOperationException("A simplification operation is already running on this instance.");
            PrepareHotPathCaches();
        }

        private void EndSimplification()
        {
            Volatile.Write(ref simplificationInProgress, 0);
        }

        private void PrepareHotPathCaches()
        {
            collapseSweepIndex = 0;
            cachedVertexPlacementMode = simplificationOptions.VertexPlacement;
            cachedPreserveSurfaceCurvature = simplificationOptions.PreserveSurfaceCurvature;
            cachedPreserveSurfaceEnvelope = simplificationOptions.PreserveSurfaceEnvelope;
            cachedPreserveBorderEdges = simplificationOptions.PreserveBorderEdges;
            cachedPreserveBilateralSymmetry = simplificationOptions.PreserveBilateralSymmetry;
            cachedFeatureCosineThreshold = Math.Cos(GetFeatureAngleDegrees() * (Math.PI / 180.0));
            cachedMinimumNormalDot = Math.Cos(GetMaxTriangleNormalDeviationDegrees() * (Math.PI / 180.0));

            int iterationCount = Math.Max(0, simplificationOptions.MaxIterationCount);
            if (cachedIterationThresholds.Length < iterationCount)
                cachedIterationThresholds = new double[iterationCount];
            for (int iteration = 0; iteration < iterationCount; iteration++)
                cachedIterationThresholds[iteration] = 0.000000001 * Math.Pow(iteration + 3, simplificationOptions.Agressiveness);

            UpdateReferenceToleranceCache();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanUseParallelLoop(int itemCount, int minimumItems)
        {
            return maximumDegreeOfParallelism > 1 && itemCount >= minimumItems;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanUseParallelExpensiveLoop(int itemCount)
        {
            return CanUseParallelLoop(itemCount, ParallelExpensiveLoopMinimumItems);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanUseParallelCheapLoop(int itemCount)
        {
            return CanUseParallelLoop(itemCount, ParallelCheapLoopMinimumItems);
        }

        private ParallelOptions CreateParallelOptions(CancellationToken cancellationToken)
        {
            int workerCount = maximumDegreeOfParallelism;
            ParallelOptions options = cachedParallelOptions;
            if (options == null ||
                cachedParallelWorkerCount != workerCount ||
                cachedParallelCancellationToken != cancellationToken)
            {
                options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = workerCount,
                    CancellationToken = cancellationToken
                };
                cachedParallelOptions = options;
                cachedParallelWorkerCount = workerCount;
                cachedParallelCancellationToken = cancellationToken;
            }
            return options;
        }

        private void UpdateReferenceToleranceCache()
        {
            double diagonal = referenceMesh != null ? referenceMesh.BoundsDiagonal : 0.0;
            double surfaceTolerance = simplificationOptions.MaxSurfaceDeviation > 0.0
                ? simplificationOptions.MaxSurfaceDeviation
                : Math.Max(diagonal * 0.00005, 1e-12);
            double boundaryTolerance = simplificationOptions.MaxBoundaryDeviation > 0.0
                ? simplificationOptions.MaxBoundaryDeviation
                : Math.Max(diagonal * 0.00001, 1e-12);
            cachedReferenceSurfaceToleranceSqr = surfaceTolerance * surfaceTolerance;
            cachedReferenceBoundaryToleranceSqr = boundaryTolerance * boundaryTolerance;
        }

        private void ClearVertexAttributes()
        {
            vertNormals = null;
            vertTangents = null;
            vertUV2D = null;
            vertUV3D = null;
            vertUV4D = null;
            vertColors = null;
            vertBoneWeights = null;
            blendShapes = null;
            bindposes = null;
        }

        private void InitializeBoneWeights(BoneWeight1[][] boneWeights)
        {
            if (boneWeights == null || boneWeights.Length == 0)
            {
                vertBoneWeights = null;
                return;
            }
            if (boneWeights.Length != vertices.Length)
                throw new ArgumentException("The bone weight vertex count must match the vertex count.", nameof(boneWeights));

            if (vertBoneWeights == null)
                vertBoneWeights = new ResizableArray<BoneWeight1[]>(boneWeights.Length, boneWeights.Length);
            else
                vertBoneWeights.Resize(boneWeights.Length, false, true);

            for (int vertexIndex = 0; vertexIndex < boneWeights.Length; vertexIndex++)
            {
                BoneWeight1[] vertexWeights = boneWeights[vertexIndex] ?? Array.Empty<BoneWeight1>();
                if (vertexWeights.Length > byte.MaxValue)
                    throw new ArgumentException(string.Format("Vertex {0} has more than 255 bone influences.", vertexIndex), nameof(boneWeights));

                for (int influenceIndex = 0; influenceIndex < vertexWeights.Length; influenceIndex++)
                {
                    BoneWeight1 influence = vertexWeights[influenceIndex];
                    if (influence.boneIndex < 0)
                        throw new ArgumentException(string.Format("Vertex {0} contains a negative bone index.", vertexIndex), nameof(boneWeights));
                    if (float.IsNaN(influence.weight) || float.IsInfinity(influence.weight) || influence.weight < 0f)
                        throw new ArgumentException(string.Format("Vertex {0} contains an invalid bone weight.", vertexIndex), nameof(boneWeights));
                }

                vertBoneWeights[vertexIndex] = NormalizeBoneWeights(vertexWeights, 0f, byte.MaxValue);
            }
        }

        private void ValidateBoneWeightBindposes()
        {
            if (vertBoneWeights == null)
                return;

            int bindposeCount = bindposes != null ? bindposes.Length : 0;
            for (int vertexIndex = 0; vertexIndex < vertBoneWeights.Length; vertexIndex++)
            {
                BoneWeight1[] weights = vertBoneWeights[vertexIndex];
                if (weights == null)
                    continue;

                for (int influenceIndex = 0; influenceIndex < weights.Length; influenceIndex++)
                {
                    if (weights[influenceIndex].boneIndex >= bindposeCount)
                    {
                        throw new InvalidOperationException(string.Format(
                            "Vertex {0} references bone index {1}, but the mesh only has {2} bind poses.",
                            vertexIndex, weights[influenceIndex].boneIndex, bindposeCount));
                    }
                }
            }
        }

        private BoneWeight1[][] GetBoneWeights1Copy()
        {
            if (vertBoneWeights == null)
                return null;

            var result = new BoneWeight1[vertBoneWeights.Length][];
            for (int vertexIndex = 0; vertexIndex < result.Length; vertexIndex++)
            {
                BoneWeight1[] source = vertBoneWeights[vertexIndex];
                result[vertexIndex] = source != null ? (BoneWeight1[])source.Clone() : Array.Empty<BoneWeight1>();
            }
            return result;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static bool IsFinite(Vector2 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y);
        }

        private static bool IsFinite(Vector4 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z)
                && !float.IsNaN(value.w) && !float.IsInfinity(value.w);
        }

        private static bool IsFinite(Color value)
        {
            return !float.IsNaN(value.r) && !float.IsInfinity(value.r)
                && !float.IsNaN(value.g) && !float.IsInfinity(value.g)
                && !float.IsNaN(value.b) && !float.IsInfinity(value.b)
                && !float.IsNaN(value.a) && !float.IsInfinity(value.a);
        }

        private static bool IsFinite(Matrix4x4 value)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    float component = value[row, column];
                    if (float.IsNaN(component) || float.IsInfinity(component))
                        return false;
                }
            }
            return true;
        }

        private static void ValidateFiniteValues(Matrix4x4[] values, string attributeName)
        {
            if (values == null)
                return;
            for (int i = 0; i < values.Length; i++)
            {
                if (!IsFinite(values[i]))
                    throw new ArgumentException(string.Format("Vertex attribute '{0}' contains a non-finite value at index {1}.", attributeName, i), attributeName);
            }
        }

        private static void ValidateFiniteValues(Vector3[] values, string attributeName)
        {
            if (values == null)
                return;
            for (int i = 0; i < values.Length; i++)
            {
                if (!IsFinite(values[i]))
                    throw new ArgumentException(string.Format("Vertex attribute '{0}' contains a non-finite value at index {1}.", attributeName, i), attributeName);
            }
        }

        private static void ValidateFiniteValues(Vector4[] values, string attributeName)
        {
            if (values == null)
                return;
            for (int i = 0; i < values.Length; i++)
            {
                if (!IsFinite(values[i]))
                    throw new ArgumentException(string.Format("Vertex attribute '{0}' contains a non-finite value at index {1}.", attributeName, i), attributeName);
            }
        }

        private static void ValidateFiniteValues(Color[] values, string attributeName)
        {
            if (values == null)
                return;
            for (int i = 0; i < values.Length; i++)
            {
                if (!IsFinite(values[i]))
                    throw new ArgumentException(string.Format("Vertex attribute '{0}' contains a non-finite value at index {1}.", attributeName, i), attributeName);
            }
        }

        private static void ValidateFiniteUVs(IList<Vector2> values, string parameterName)
        {
            if (values == null)
                return;
            for (int i = 0; i < values.Count; i++)
            {
                if (!IsFinite(values[i]))
                    throw new ArgumentException(string.Format("UV data contains a non-finite value at index {0}.", i), parameterName);
            }
        }

        private static void ValidateFiniteUVs(IList<Vector3> values, string parameterName)
        {
            if (values == null)
                return;
            for (int i = 0; i < values.Count; i++)
            {
                if (!IsFinite(values[i]))
                    throw new ArgumentException(string.Format("UV data contains a non-finite value at index {0}.", i), parameterName);
            }
        }

        private static void ValidateFiniteUVs(IList<Vector4> values, string parameterName)
        {
            if (values == null)
                return;
            for (int i = 0; i < values.Count; i++)
            {
                if (!IsFinite(values[i]))
                    throw new ArgumentException(string.Format("UV data contains a non-finite value at index {0}.", i), parameterName);
            }
        }

        private void ValidateUVCount<T>(IList<T> uvs)
        {
            if (uvs != null && uvs.Count != 0 && uvs.Count != vertices.Length)
                throw new ArgumentException("The UV count must be empty or match the vertex count.", nameof(uvs));
        }

        private void ValidateTriangleIndices(int[] triangleIndices, string parameterName)
        {
            int vertexCount = vertices.Length;
            for (int i = 0; i < triangleIndices.Length; i++)
            {
                int vertexIndex = triangleIndices[i];
                if (vertexIndex < 0 || vertexIndex >= vertexCount)
                    throw new ArgumentOutOfRangeException(parameterName, string.Format("Triangle index {0} at array position {1} is outside the vertex range [0, {2}).", vertexIndex, i, vertexCount));
            }
        }

        private BlendShape CloneAndValidateBlendShape(BlendShape blendShape, string parameterName, int shapeIndex)
        {
            if (string.IsNullOrEmpty(blendShape.ShapeName))
                throw new ArgumentException(string.Format("Blend shape at index {0} must have a non-empty name.", shapeIndex), parameterName);
            if (blendShape.Frames == null || blendShape.Frames.Length == 0)
                throw new ArgumentException(string.Format("Blend shape '{0}' must contain at least one frame.", blendShape.ShapeName), parameterName);

            int vertexCount = vertices.Length;
            float previousWeight = float.NegativeInfinity;
            var clonedFrames = new BlendShapeFrame[blendShape.Frames.Length];
            for (int frameIndex = 0; frameIndex < blendShape.Frames.Length; frameIndex++)
            {
                BlendShapeFrame frame = blendShape.Frames[frameIndex];
                if (float.IsNaN(frame.FrameWeight) || float.IsInfinity(frame.FrameWeight))
                    throw new ArgumentException(string.Format("Blend shape '{0}' frame {1} has a non-finite weight.", blendShape.ShapeName, frameIndex), parameterName);
                if (frameIndex > 0 && frame.FrameWeight <= previousWeight)
                    throw new ArgumentException(string.Format("Blend shape '{0}' frame weights must be strictly increasing.", blendShape.ShapeName), parameterName);
                previousWeight = frame.FrameWeight;

                ValidateBlendShapeFrameArray(frame.DeltaVertices, vertexCount, blendShape.ShapeName, frameIndex, "delta vertices", parameterName, false);
                ValidateBlendShapeFrameArray(frame.DeltaNormals, vertexCount, blendShape.ShapeName, frameIndex, "delta normals", parameterName, true);
                ValidateBlendShapeFrameArray(frame.DeltaTangents, vertexCount, blendShape.ShapeName, frameIndex, "delta tangents", parameterName, true);
                clonedFrames[frameIndex] = new BlendShapeFrame(
                    frame.FrameWeight, frame.DeltaVertices, frame.DeltaNormals, frame.DeltaTangents);
            }

            return new BlendShape(blendShape.ShapeName, clonedFrames);
        }

        private static void ValidateBlendShapeFrameArray(Vector3[] values, int vertexCount, string shapeName, int frameIndex, string label, string parameterName, bool allowNull)
        {
            if (values == null)
            {
                if (allowNull)
                    return;
                throw new ArgumentException(string.Format("Blend shape '{0}' frame {1} {2} cannot be null.", shapeName, frameIndex, label), parameterName);
            }
            if (values.Length != vertexCount)
                throw new ArgumentException(string.Format("Blend shape '{0}' frame {1} {2} must contain exactly {3} values.", shapeName, frameIndex, label, vertexCount), parameterName);

            for (int i = 0; i < values.Length; i++)
            {
                if (!IsFinite(values[i]))
                    throw new ArgumentException(string.Format("Blend shape '{0}' frame {1} contains a non-finite {2} value at vertex {3}.", shapeName, frameIndex, label, i), parameterName);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool NeedsReferenceMesh()
        {
            return simplificationOptions.VertexPlacement == VertexPlacementMode.ReferenceAccurate ||
                   simplificationOptions.PreserveSurfaceEnvelope;
        }

        private void BuildReferenceMesh(CancellationToken cancellationToken)
        {
            int vertexCount = vertices.Length;
            var positions = new Vector3d[vertexCount];
            Vertex[] vertexData = vertices.Data;

            if (CanUseParallelCheapLoop(vertexCount))
            {
                Parallel.For(
                    0,
                    vertexCount,
                    CreateParallelOptions(cancellationToken),
                    i => positions[i] = vertexData[i].p);
            }
            else
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    if ((i & 4095) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    positions[i] = vertexData[i].p;
                }
            }

            referenceMesh = new ReferenceMesh(
                positions,
                triangles.Data,
                triangles.Length,
                maximumDegreeOfParallelism,
                cancellationToken);

            if (CanUseParallelCheapLoop(vertexCount))
            {
                Parallel.For(
                    0,
                    vertexCount,
                    CreateParallelOptions(cancellationToken),
                    i =>
                    {
                        Vertex vertex = vertexData[i];
                        vertex.referenceComponent = referenceMesh.GetVertexComponent(i);
                        vertex.referenceBoundaryComponent = referenceMesh.GetVertexBoundaryComponent(i);
                        vertex.referenceTriangleHint = referenceMesh.GetVertexSurfaceHint(i);
                        vertex.referenceBoundarySegmentHint = referenceMesh.GetVertexBoundaryHint(i);
                        vertexData[i] = vertex;
                    });
            }
            else
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    if ((i & 4095) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    Vertex vertex = vertexData[i];
                    vertex.referenceComponent = referenceMesh.GetVertexComponent(i);
                    vertex.referenceBoundaryComponent = referenceMesh.GetVertexBoundaryComponent(i);
                    vertex.referenceTriangleHint = referenceMesh.GetVertexSurfaceHint(i);
                    vertex.referenceBoundarySegmentHint = referenceMesh.GetVertexBoundaryHint(i);
                    vertexData[i] = vertex;
                }
            }

            referenceConstraintRejectedCollapses = 0;
            UpdateReferenceToleranceCache();
        }

        private void RemoveDegenerateTriangles()
        {
            Triangle[] triangleData = triangles.Data;
            Vertex[] vertexData = vertices.Data;
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < triangles.Length; readIndex++)
            {
                Triangle triangle = triangleData[readIndex];
                if (triangle.v0 == triangle.v1 || triangle.v1 == triangle.v2 || triangle.v2 == triangle.v0)
                    continue;

                Vector3d edgeAB = vertexData[triangle.v1].p - vertexData[triangle.v0].p;
                Vector3d edgeAC = vertexData[triangle.v2].p - vertexData[triangle.v0].p;
                Vector3d cross;
                Vector3d.Cross(ref edgeAB, ref edgeAC, out cross);
                double edgeScale = Math.Max(edgeAB.MagnitudeSqr, edgeAC.MagnitudeSqr);
                if (edgeScale <= 0.0 || cross.MagnitudeSqr <= edgeScale * edgeScale * 1e-24)
                    continue;

                if (writeIndex != readIndex)
                {
                    triangle.index = writeIndex;
                    triangleData[writeIndex] = triangle;
                }
                writeIndex++;
            }

            if (writeIndex != triangles.Length)
            {
                triangles.Resize(writeIndex);
                subMeshOffsets = null;
            }
        }

        private void LogVerbose(string format, params object[] args)
        {
            if (verbose)
                verboseMessageQueue.Enqueue(string.Format(format, args));
        }

        /// <summary>
        /// Flushes queued verbose messages to Unity's console. This must be called on the thread that owns
        /// this simplifier; ToMesh and synchronous simplification flush automatically.
        /// </summary>
        public void FlushVerboseMessages()
        {
            EnsureUnityThread();
            string message;
            while (verboseMessageQueue.TryDequeue(out message))
                Debug.Log(message);
        }

        #region Initialize Vertex Attribute
        private void InitializeVertexAttribute<T>(T[] attributeValues, ref ResizableArray<T> attributeArray, string attributeName)
        {
            if (attributeValues != null && attributeValues.Length == vertices.Length)
            {
                if (attributeArray == null)
                {
                    attributeArray = new ResizableArray<T>(attributeValues.Length, attributeValues.Length);
                }
                else
                {
                    attributeArray.Resize(attributeValues.Length);
                }

                var arrayData = attributeArray.Data;
                Array.Copy(attributeValues, 0, arrayData, 0, attributeValues.Length);
            }
            else
            {
                if (attributeValues != null && attributeValues.Length > 0)
                    throw new ArgumentException(string.Format("Vertex attribute '{0}' has {1} values, but {2} are required.", attributeName, attributeValues.Length, vertices.Length), attributeName);
                attributeArray = null;
            }
        }
        #endregion

        #region Calculate Error
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double VertexError(ref SymmetricMatrix q, double x, double y, double z)
        {
            return q.m0 * x * x + 2 * q.m1 * x * y + 2 * q.m2 * x * z + 2 * q.m3 * x + q.m4 * y * y
                + 2 * q.m5 * y * z + 2 * q.m6 * y + q.m7 * z * z + 2 * q.m8 * z + q.m9;
        }

        private int FindSharedTriangles(
            ref Vertex vert0,
            ref Vertex vert1,
            bool liveOnly,
            out int firstTriangle,
            out int secondTriangle)
        {
            firstTriangle = -1;
            secondTriangle = -1;

            int searchStart;
            int searchCount;
            int otherVertexIndex;
            if (vert0.tcount <= vert1.tcount)
            {
                searchStart = vert0.tstart;
                searchCount = vert0.tcount;
                otherVertexIndex = vert1.index;
            }
            else
            {
                searchStart = vert1.tstart;
                searchCount = vert1.tcount;
                otherVertexIndex = vert0.index;
            }

            int count = 0;
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            int triangleLength = triangles.Length;
            int end = searchStart + searchCount;
            for (int refIndex = searchStart; refIndex < end; refIndex++)
            {
                int triangleIndex = referenceData[refIndex].tid;
                if ((uint)triangleIndex >= (uint)triangleLength)
                    continue;

                ref Triangle triangle = ref triangleData[triangleIndex];
                if ((liveOnly && triangle.deleted) ||
                    !TriangleContainsVertex(ref triangle, otherVertexIndex))
                {
                    continue;
                }

                if (count == 0) firstTriangle = triangleIndex;
                else if (count == 1) secondTriangle = triangleIndex;
                count++;
            }

            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double TriangleNormalCurvature(
            ref Vector3d adjacentNormal,
            ref Triangle edgeTriangle)
        {
            double dot =
                adjacentNormal.x * edgeTriangle.n.x +
                adjacentNormal.y * edgeTriangle.n.y +
                adjacentNormal.z * edgeTriangle.n.z;
            if (dot < -1.0) dot = -1.0;
            else if (dot > 1.0) dot = 1.0;
            return (1.0 - dot) * 0.5;
        }

        private double MinimumCurvatureAgainstSharedEdge(
            ref Vector3d adjacentNormal,
            ref Vertex vert0,
            ref Vertex vert1,
            int sharedTriangleCount,
            int firstTriangle,
            int secondTriangle)
        {
            Triangle[] triangleData = triangles.Data;
            if (sharedTriangleCount <= 2)
            {
                double minimum = TriangleNormalCurvature(
                    ref adjacentNormal, ref triangleData[firstTriangle]);
                if (sharedTriangleCount == 2)
                {
                    double second = TriangleNormalCurvature(
                        ref adjacentNormal, ref triangleData[secondTriangle]);
                    if (second < minimum)
                        minimum = second;
                }
                return minimum;
            }

            int searchStart;
            int searchCount;
            int otherVertexIndex;
            if (vert0.tcount <= vert1.tcount)
            {
                searchStart = vert0.tstart;
                searchCount = vert0.tcount;
                otherVertexIndex = vert1.index;
            }
            else
            {
                searchStart = vert1.tstart;
                searchCount = vert1.tcount;
                otherVertexIndex = vert0.index;
            }

            double minimumCurvature = 1.0;
            Ref[] referenceData = refs.Data;
            int triangleLength = triangles.Length;
            int end = searchStart + searchCount;
            for (int refIndex = searchStart; refIndex < end; refIndex++)
            {
                int triangleIndex = referenceData[refIndex].tid;
                if ((uint)triangleIndex >= (uint)triangleLength)
                    continue;

                ref Triangle edgeTriangle = ref triangleData[triangleIndex];
                if (edgeTriangle.deleted ||
                    !TriangleContainsVertex(ref edgeTriangle, otherVertexIndex))
                {
                    continue;
                }

                double candidate = TriangleNormalCurvature(
                    ref adjacentNormal, ref edgeTriangle);
                if (candidate < minimumCurvature)
                    minimumCurvature = candidate;
            }
            return minimumCurvature;
        }

        private void AccumulateCurvatureForVertex(
            ref Vertex vertex,
            ref Vertex edgeVertex0,
            ref Vertex edgeVertex1,
            int sharedTriangleCount,
            int firstTriangle,
            int secondTriangle,
            ref double curvature)
        {
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            int end = vertex.tstart + vertex.tcount;
            for (int refIndex = vertex.tstart; refIndex < end; refIndex++)
            {
                int adjacentTriangleIndex = referenceData[refIndex].tid;
                if ((uint)adjacentTriangleIndex >= (uint)triangles.Length)
                    continue;

                ref Triangle adjacentTriangle = ref triangleData[adjacentTriangleIndex];
                if (adjacentTriangle.deleted)
                    continue;

                Vector3d adjacentNormal = adjacentTriangle.n;
                double minimumCurvature = MinimumCurvatureAgainstSharedEdge(
                    ref adjacentNormal,
                    ref edgeVertex0,
                    ref edgeVertex1,
                    sharedTriangleCount,
                    firstTriangle,
                    secondTriangle);
                if (minimumCurvature > curvature)
                    curvature = minimumCurvature;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double CurvatureError(ref Vertex vert0, ref Vertex vert1)
        {
            double edgeDx = vert0.p.x - vert1.p.x;
            double edgeDy = vert0.p.y - vert1.p.y;
            double edgeDz = vert0.p.z - vert1.p.z;
            double edgeLength = Math.Sqrt(
                edgeDx * edgeDx + edgeDy * edgeDy + edgeDz * edgeDz);
            int firstTriangle;
            int secondTriangle;
            int sharedTriangleCount = FindSharedTriangles(
                ref vert0, ref vert1, true, out firstTriangle, out secondTriangle);
            if (sharedTriangleCount == 0)
                return edgeLength;

            double curvature = 0.0;
            AccumulateCurvatureForVertex(
                ref vert0, ref vert0, ref vert1,
                sharedTriangleCount, firstTriangle, secondTriangle, ref curvature);
            AccumulateCurvatureForVertex(
                ref vert1, ref vert0, ref vert1,
                sharedTriangleCount, firstTriangle, secondTriangle, ref curvature);
            return edgeLength * curvature;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double CalculateError(ref Vertex vert0, ref Vertex vert1, out Vector3d result)
        {
            SymmetricMatrix combinedQuadric;
            return CalculateError(ref vert0, ref vert1, out result, out combinedQuadric);
        }

        private double CalculateError(
            ref Vertex vert0,
            ref Vertex vert1,
            out Vector3d result,
            out SymmetricMatrix combinedQuadric)
        {
            combinedQuadric = new SymmetricMatrix(
                vert0.q.m0 + vert1.q.m0,
                vert0.q.m1 + vert1.q.m1,
                vert0.q.m2 + vert1.q.m2,
                vert0.q.m3 + vert1.q.m3,
                vert0.q.m4 + vert1.q.m4,
                vert0.q.m5 + vert1.q.m5,
                vert0.q.m6 + vert1.q.m6,
                vert0.q.m7 + vert1.q.m7,
                vert0.q.m8 + vert1.q.m8,
                vert0.q.m9 + vert1.q.m9);
            SymmetricMatrix q = combinedQuadric;
            bool borderEdge = vert0.borderEdge && vert1.borderEdge;
            double error;
            double det = q.Determinant1();
            if (det != 0.0 && !borderEdge)
            {
                double inverseDet = 1.0 / det;
                result = new Vector3d(
                    -inverseDet * q.Determinant2(),
                    inverseDet * q.Determinant3(),
                    -inverseDet * q.Determinant4());

                error = VertexError(ref q, result.x, result.y, result.z);
            }
            else
            {
                Vector3d p1 = vert0.p;
                Vector3d p2 = vert1.p;
                Vector3d p3 = new Vector3d(
                    (p1.x + p2.x) * 0.5,
                    (p1.y + p2.y) * 0.5,
                    (p1.z + p2.z) * 0.5);
                double error1 = VertexError(ref q, p1.x, p1.y, p1.z);
                double error2 = VertexError(ref q, p2.x, p2.y, p2.z);
                double error3 = VertexError(ref q, p3.x, p3.y, p3.z);

                if (error1 < error2)
                {
                    if (error1 < error3)
                    {
                        error = error1;
                        result = p1;
                    }
                    else
                    {
                        error = error3;
                        result = p3;
                    }
                }
                else if (error2 < error3)
                {
                    error = error2;
                    result = p2;
                }
                else
                {
                    error = error3;
                    result = p3;
                }
            }

            if (cachedPreserveSurfaceCurvature)
                error += CurvatureError(ref vert0, ref vert1);
            return error;
        }
        #endregion

        #region Collapse Placement
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(ref Vector3d value)
        {
            return !(double.IsNaN(value.x) || double.IsInfinity(value.x) ||
                     double.IsNaN(value.y) || double.IsInfinity(value.y) ||
                     double.IsNaN(value.z) || double.IsInfinity(value.z));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetFeatureAngleDegrees()
        {
            float value = simplificationOptions.FeatureAngleDegrees;
            return value > 0f ? value : 45f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetMaxTriangleNormalDeviationDegrees()
        {
            float value = simplificationOptions.MaxTriangleNormalDeviationDegrees;
            return value > 0f ? value : 78f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Clamp01(double value)
        {
            return value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ClosestPointOnSegment(
            ref Vector3d point,
            ref Vector3d a,
            ref Vector3d b,
            out Vector3d closest)
        {
            double abx = b.x - a.x;
            double aby = b.y - a.y;
            double abz = b.z - a.z;
            double lengthSqr = abx * abx + aby * aby + abz * abz;
            double t = 0.0;
            if (lengthSqr > 1e-30)
            {
                double apx = point.x - a.x;
                double apy = point.y - a.y;
                double apz = point.z - a.z;
                t = (apx * abx + apy * aby + apz * abz) / lengthSqr;
                if (t < 0.0) t = 0.0;
                else if (t > 1.0) t = 1.0;
            }

            closest = new Vector3d(
                a.x + abx * t,
                a.y + aby * t,
                a.z + abz * t);
            return t;
        }

        private static void ClosestPointOnDegenerateTriangle(
            ref Vector3d point,
            ref Vector3d a,
            ref Vector3d b,
            ref Vector3d c,
            out Vector3d closest,
            out Vector3 barycentric)
        {
            Vector3d candidateAB;
            Vector3d candidateBC;
            Vector3d candidateCA;
            double tAB = ClosestPointOnSegment(ref point, ref a, ref b, out candidateAB);
            double tBC = ClosestPointOnSegment(ref point, ref b, ref c, out candidateBC);
            double tCA = ClosestPointOnSegment(ref point, ref c, ref a, out candidateCA);

            double distanceAB = (candidateAB - point).MagnitudeSqr;
            double distanceBC = (candidateBC - point).MagnitudeSqr;
            double distanceCA = (candidateCA - point).MagnitudeSqr;
            if (distanceAB <= distanceBC && distanceAB <= distanceCA)
            {
                closest = candidateAB;
                barycentric = new Vector3((float)(1.0 - tAB), (float)tAB, 0f);
            }
            else if (distanceBC <= distanceCA)
            {
                closest = candidateBC;
                barycentric = new Vector3(0f, (float)(1.0 - tBC), (float)tBC);
            }
            else
            {
                closest = candidateCA;
                barycentric = new Vector3((float)tCA, 0f, (float)(1.0 - tCA));
            }
        }

        private static void ClosestPointOnTriangle(
            ref Vector3d point,
            ref Vector3d a,
            ref Vector3d b,
            ref Vector3d c,
            out Vector3d closest,
            out Vector3 barycentric)
        {
            double abx = b.x - a.x;
            double aby = b.y - a.y;
            double abz = b.z - a.z;
            double acx = c.x - a.x;
            double acy = c.y - a.y;
            double acz = c.z - a.z;
            double crossX = aby * acz - abz * acy;
            double crossY = abz * acx - abx * acz;
            double crossZ = abx * acy - aby * acx;
            double abLengthSqr = abx * abx + aby * aby + abz * abz;
            double acLengthSqr = acx * acx + acy * acy + acz * acz;
            double scale = abLengthSqr > acLengthSqr ? abLengthSqr : acLengthSqr;
            double crossLengthSqr = crossX * crossX + crossY * crossY + crossZ * crossZ;
            if (scale <= 1e-30 || crossLengthSqr <= scale * scale * 1e-24)
            {
                ClosestPointOnDegenerateTriangle(
                    ref point, ref a, ref b, ref c, out closest, out barycentric);
                return;
            }

            double apx = point.x - a.x;
            double apy = point.y - a.y;
            double apz = point.z - a.z;
            double d1 = abx * apx + aby * apy + abz * apz;
            double d2 = acx * apx + acy * apy + acz * apz;
            if (d1 <= 0.0 && d2 <= 0.0)
            {
                closest = a;
                barycentric = new Vector3(1f, 0f, 0f);
                return;
            }

            double bpx = point.x - b.x;
            double bpy = point.y - b.y;
            double bpz = point.z - b.z;
            double d3 = abx * bpx + aby * bpy + abz * bpz;
            double d4 = acx * bpx + acy * bpy + acz * bpz;
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
                closest = new Vector3d(
                    a.x + abx * v,
                    a.y + aby * v,
                    a.z + abz * v);
                barycentric = new Vector3((float)(1.0 - v), (float)v, 0f);
                return;
            }

            double cpx = point.x - c.x;
            double cpy = point.y - c.y;
            double cpz = point.z - c.z;
            double d5 = abx * cpx + aby * cpy + abz * cpz;
            double d6 = acx * cpx + acy * cpy + acz * cpz;
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
                closest = new Vector3d(
                    a.x + acx * w,
                    a.y + acy * w,
                    a.z + acz * w);
                barycentric = new Vector3((float)(1.0 - w), 0f, (float)w);
                return;
            }

            double va = d3 * d6 - d5 * d4;
            double d43 = d4 - d3;
            double d56 = d5 - d6;
            if (va <= 0.0 && d43 >= 0.0 && d56 >= 0.0)
            {
                double w = d43 / (d43 + d56);
                closest = new Vector3d(
                    b.x + (c.x - b.x) * w,
                    b.y + (c.y - b.y) * w,
                    b.z + (c.z - b.z) * w);
                barycentric = new Vector3(0f, (float)(1.0 - w), (float)w);
                return;
            }

            double denominator = va + vb + vc;
            if (Math.Abs(denominator) <= 1e-30)
            {
                ClosestPointOnDegenerateTriangle(
                    ref point, ref a, ref b, ref c, out closest, out barycentric);
                return;
            }

            double inverseDenominator = 1.0 / denominator;
            double faceV = vb * inverseDenominator;
            double faceW = vc * inverseDenominator;
            double faceU = 1.0 - faceV - faceW;
            closest = new Vector3d(
                a.x * faceU + b.x * faceV + c.x * faceW,
                a.y * faceU + b.y * faceV + c.y * faceW,
                a.z * faceU + b.z * faceV + c.z * faceW);
            barycentric = new Vector3((float)faceU, (float)faceV, (float)faceW);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetCurrentTriangleNormal(ref Triangle triangle, out Vector3d normal)
        {
            Vertex[] vertexData = vertices.Data;
            ref Vector3d a = ref vertexData[triangle.v0].p;
            ref Vector3d b = ref vertexData[triangle.v1].p;
            ref Vector3d c = ref vertexData[triangle.v2].p;

            double abx = b.x - a.x;
            double aby = b.y - a.y;
            double abz = b.z - a.z;
            double acx = c.x - a.x;
            double acy = c.y - a.y;
            double acz = c.z - a.z;
            double nx = aby * acz - abz * acy;
            double ny = abz * acx - abx * acz;
            double nz = abx * acy - aby * acx;
            double magnitudeSqr = nx * nx + ny * ny + nz * nz;
            if (magnitudeSqr <= 1e-30 ||
                double.IsNaN(magnitudeSqr) || double.IsInfinity(magnitudeSqr))
            {
                normal = Vector3d.zero;
                return false;
            }

            double inverseMagnitude = 1.0 / Math.Sqrt(magnitudeSqr);
            normal = new Vector3d(
                nx * inverseMagnitude,
                ny * inverseMagnitude,
                nz * inverseMagnitude);
            return true;
        }

        private bool IsFeatureEdge(int i0, int i1, ref Vertex vert0, ref Vertex vert1)
        {
            if ((vert0.borderEdge && vert1.borderEdge) ||
                (vert0.uvSeamEdge && vert1.uvSeamEdge) ||
                (vert0.uvFoldoverEdge && vert1.uvFoldoverEdge) ||
                (vert0.subMeshEdge && vert1.subMeshEdge) ||
                (vert0.boneWeightSeamEdge && vert1.boneWeightSeamEdge))
            {
                return true;
            }

            int firstTriangleIndex;
            int secondTriangleIndex;
            if (FindSharedTriangles(
                    ref vert0,
                    ref vert1,
                    false,
                    out firstTriangleIndex,
                    out secondTriangleIndex) != 2)
            {
                return true;
            }

            Triangle[] triangleData = triangles.Data;
            ref Triangle firstTriangle = ref triangleData[firstTriangleIndex];
            ref Triangle secondTriangle = ref triangleData[secondTriangleIndex];
            if (firstTriangle.deleted || secondTriangle.deleted)
                return true;
            Vector3d firstNormal;
            Vector3d secondNormal;
            if (!TryGetCurrentTriangleNormal(ref firstTriangle, out firstNormal) ||
                !TryGetCurrentTriangleNormal(ref secondTriangle, out secondNormal))
            {
                return true;
            }

            double dot = MathHelper.Clamp(
                Vector3d.Dot(ref firstNormal, ref secondNormal), -1.0, 1.0);
            return dot <= cachedFeatureCosineThreshold;
        }

        private CollapsePlacement CreateEdgePlacement(
            ref Vector3d requestedPosition,
            int i0,
            int i1,
            int attribute0,
            int attribute1,
            int attribute2)
        {
            Vertex[] vertexData = vertices.Data;
            ref Vector3d a = ref vertexData[i0].p;
            ref Vector3d b = ref vertexData[i1].p;
            Vector3d closest;
            double t = ClosestPointOnSegment(ref requestedPosition, ref a, ref b, out closest);
            return new CollapsePlacement(
                closest,
                attribute0,
                attribute1,
                attribute2,
                new Vector3((float)(1.0 - t), (float)t, 0f));
        }

        private CollapsePlacement CreateCurrentTrianglePlacement(
            ref Vector3d position,
            int i0,
            int i1,
            int i2,
            int attribute0,
            int attribute1,
            int attribute2)
        {
            Vector3 barycentric;
            Vertex[] vertexData = vertices.Data;
            ref Vector3d a = ref vertexData[i0].p;
            ref Vector3d b = ref vertexData[i1].p;
            ref Vector3d c = ref vertexData[i2].p;
            CalculateBarycentricCoords(ref position, ref a, ref b, ref c, out barycentric);
            return new CollapsePlacement(position, attribute0, attribute1, attribute2, barycentric);
        }

        private void EvaluateLocalSurfaceRing(
            ref Vertex sourceVertex,
            ref Vector3d requestedPosition,
            int i0,
            int i1,
            ref CollapsePlacement placement,
            ref bool found,
            ref double bestDistanceSqr)
        {
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            Vertex[] vertexData = vertices.Data;
            int end = sourceVertex.tstart + sourceVertex.tcount;
            for (int refIndex = sourceVertex.tstart; refIndex < end; refIndex++)
            {
                int triangleIndex = referenceData[refIndex].tid;
                if ((uint)triangleIndex >= (uint)triangles.Length)
                    continue;

                ref Triangle triangle = ref triangleData[triangleIndex];
                if (triangle.deleted)
                    continue;

                bool contains0 = TriangleContainsVertex(ref triangle, i0);
                bool contains1 = TriangleContainsVertex(ref triangle, i1);
                if (contains0 && contains1)
                    continue;

                ref Vector3d a = ref vertexData[triangle.v0].p;
                ref Vector3d b = ref vertexData[triangle.v1].p;
                ref Vector3d c = ref vertexData[triangle.v2].p;
                Vector3d closest;
                Vector3 barycentric;
                ClosestPointOnTriangle(
                    ref requestedPosition, ref a, ref b, ref c, out closest, out barycentric);
                if (!IsFinite(ref closest))
                    continue;

                double distanceX = closest.x - requestedPosition.x;
                double distanceY = closest.y - requestedPosition.y;
                double distanceZ = closest.z - requestedPosition.z;
                double distanceSqr =
                    distanceX * distanceX + distanceY * distanceY + distanceZ * distanceZ;
                if (distanceSqr < bestDistanceSqr)
                {
                    bestDistanceSqr = distanceSqr;
                    placement = new CollapsePlacement(
                        closest,
                        triangle.va0,
                        triangle.va1,
                        triangle.va2,
                        barycentric);
                    found = true;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryProjectToLocalSurface(
            ref Vector3d requestedPosition,
            int i0,
            int i1,
            out CollapsePlacement placement)
        {
            Vertex[] vertexData = vertices.Data;
            return TryProjectToLocalSurface(
                ref requestedPosition,
                i0,
                i1,
                ref vertexData[i0],
                ref vertexData[i1],
                out placement);
        }

        private bool TryProjectToLocalSurface(
            ref Vector3d requestedPosition,
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1,
            out CollapsePlacement placement)
        {
            placement = new CollapsePlacement();
            bool found = false;
            double bestDistanceSqr = double.MaxValue;

            EvaluateLocalSurfaceRing(
                ref vert0, ref requestedPosition, i0, i1,
                ref placement, ref found, ref bestDistanceSqr);
            EvaluateLocalSurfaceRing(
                ref vert1, ref requestedPosition, i0, i1,
                ref placement, ref found, ref bestDistanceSqr);
            return found;
        }

        private bool TryProjectToPreparedLocalSurface(
            ref Vector3d requestedPosition,
            out CollapsePlacement placement)
        {
            placement = new CollapsePlacement();
            bool found = false;
            double bestDistanceSqr = double.MaxValue;
            Triangle[] triangleData = triangles.Data;
            Vertex[] vertexData = vertices.Data;
            int futureCount = collapseFutureTriangleCount;

            for (int index = 0; index < futureCount; index++)
            {
                ref Triangle triangle = ref triangleData[collapseFutureTriangleBuffer[index]];
                if (triangle.deleted)
                    continue;

                ref Vector3d a = ref vertexData[triangle.v0].p;
                ref Vector3d b = ref vertexData[triangle.v1].p;
                ref Vector3d c = ref vertexData[triangle.v2].p;
                Vector3d closest;
                Vector3 barycentric;
                ClosestPointOnTriangle(
                    ref requestedPosition,
                    ref a,
                    ref b,
                    ref c,
                    out closest,
                    out barycentric);
                if (!IsFinite(ref closest))
                    continue;

                double distanceX = closest.x - requestedPosition.x;
                double distanceY = closest.y - requestedPosition.y;
                double distanceZ = closest.z - requestedPosition.z;
                double distanceSqr =
                    distanceX * distanceX + distanceY * distanceY + distanceZ * distanceZ;
                if (distanceSqr < bestDistanceSqr)
                {
                    bestDistanceSqr = distanceSqr;
                    placement = new CollapsePlacement(
                        closest,
                        triangle.va0,
                        triangle.va1,
                        triangle.va2,
                        barycentric);
                    found = true;
                }
            }

            return found;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClampCorrectionMagnitude(ref Vector3d correction, double maximumCorrection)
        {
            double correctionLengthSqr = correction.MagnitudeSqr;
            double maximumCorrectionSqr = maximumCorrection * maximumCorrection;
            if (correctionLengthSqr > maximumCorrectionSqr && correctionLengthSqr > 1e-30)
                correction *= maximumCorrection / Math.Sqrt(correctionLengthSqr);
        }

        private bool TryAccumulateReferenceEnvelopeCorrection(
            ref Vector3d sample,
            double inverseInfluence,
            double weight,
            int component,
            double maximumDistance,
            double maximumDistanceSqr,
            double maximumCorrection,
            ref int surfaceHint,
            ref int mirroredSurfaceHint,
            ref Vector3d correctionSum,
            ref double totalWeight)
        {
            ReferenceMesh.SurfaceHit hit;
            if (!referenceMesh.TryFindClosestSurface(
                ref sample, component, maximumDistanceSqr, surfaceHint, out hit))
            {
                return false;
            }

            surfaceHint = hit.triangleIndex;
            Vector3d correction = (hit.position - sample) * inverseInfluence;
            ClampCorrectionMagnitude(ref correction, maximumCorrection);

            if (cachedPreserveBilateralSymmetry &&
                referenceMesh.HasBilateralSymmetry &&
                Math.Abs(sample.x - referenceMesh.SymmetryPlaneX) > referenceMesh.SymmetryMatchTolerance)
            {
                Vector3d mirroredSample = sample;
                mirroredSample.x = referenceMesh.SymmetryPlaneX * 2.0 - sample.x;

                double directDistance = Math.Sqrt(Math.Max(hit.distanceSqr, 0.0));
                double mirroredSearchDistance = Math.Min(
                    maximumDistance,
                    Math.Max(
                        referenceMesh.SymmetryMatchTolerance * 3.0,
                        directDistance + referenceMesh.SymmetryMatchTolerance * 2.0));
                double mirroredSearchDistanceSqr =
                    mirroredSearchDistance * mirroredSearchDistance;

                ReferenceMesh.SurfaceHit mirroredHit;
                if (referenceMesh.TryFindClosestSurface(
                    ref mirroredSample,
                    -1,
                    mirroredSearchDistanceSqr,
                    mirroredSurfaceHint,
                    out mirroredHit))
                {
                    mirroredSurfaceHint = mirroredHit.triangleIndex;
                    Vector3d mirroredHitPosition = mirroredHit.position;
                    mirroredHitPosition.x = referenceMesh.SymmetryPlaneX * 2.0 - mirroredHitPosition.x;
                    Vector3d mirroredCorrection =
                        (mirroredHitPosition - sample) * inverseInfluence;
                    ClampCorrectionMagnitude(ref mirroredCorrection, maximumCorrection);

                    double mirroredDistance = Math.Sqrt(Math.Max(mirroredHit.distanceSqr, 0.0));
                    double confidence = 1.0 - MathHelper.Clamp(
                        mirroredDistance / Math.Max(mirroredSearchDistance, 1e-15), 0.0, 1.0);

                    double mirrorBlend = 0.5 * confidence;
                    correction = correction * (1.0 - mirrorBlend) +
                                 mirroredCorrection * mirrorBlend;
                    ClampCorrectionMagnitude(ref correction, maximumCorrection);
                }
            }

            correctionSum += correction * weight;
            totalWeight += weight;
            return true;
        }

        private void CollectLocalTriangleRing(
            ref Vertex vertex,
            int visitStamp,
            List<int> destination)
        {
            Ref[] referenceData = refs.Data;
            int end = vertex.tstart + vertex.tcount;
            for (int refIndex = vertex.tstart; refIndex < end; refIndex++)
            {
                int triangleIndex = referenceData[refIndex].tid;
                if (TryMarkReferenceTriangleVisited(triangleIndex, visitStamp))
                    destination.Add(triangleIndex);
            }
        }

        private void EnsureCollapseNeighborhoodCapacity(int firstCount, int secondCount, bool includeFutureTriangles)
        {
            if (collapseFlipTriangleBuffer0.Length < firstCount)
            {
                int capacity = Math.Max(firstCount, collapseFlipTriangleBuffer0.Length * 2 + 16);
                Array.Resize(ref collapseFlipTriangleBuffer0, capacity);
                Array.Resize(ref collapseFlipVertexBuffer00, capacity);
                Array.Resize(ref collapseFlipVertexBuffer01, capacity);
            }

            if (collapseFlipTriangleBuffer1.Length < secondCount)
            {
                int capacity = Math.Max(secondCount, collapseFlipTriangleBuffer1.Length * 2 + 16);
                Array.Resize(ref collapseFlipTriangleBuffer1, capacity);
                Array.Resize(ref collapseFlipVertexBuffer10, capacity);
                Array.Resize(ref collapseFlipVertexBuffer11, capacity);
            }

            int futureCount = includeFutureTriangles ? firstCount + secondCount : 0;
            if (collapseFutureTriangleBuffer.Length < futureCount)
            {
                int capacity = Math.Max(futureCount, collapseFutureTriangleBuffer.Length * 2 + 16);
                Array.Resize(ref collapseFutureTriangleBuffer, capacity);
                Array.Resize(ref collapseFutureFixedVertexBuffer0, capacity);
                Array.Resize(ref collapseFutureFixedVertexBuffer1, capacity);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GetFutureFixedVertices(
            ref Triangle triangle,
            int collapsed0,
            int collapsed1,
            out int fixed0,
            out int fixed1)
        {
            fixed0 = -1;
            fixed1 = -1;
            int fixedCount = 0;

            int vertexIndex = triangle.v0;
            if (vertexIndex != collapsed0 && vertexIndex != collapsed1)
            {
                fixed0 = vertexIndex;
                fixedCount = 1;
            }

            vertexIndex = triangle.v1;
            if (vertexIndex != collapsed0 && vertexIndex != collapsed1)
            {
                if (fixedCount == 0) fixed0 = vertexIndex;
                else if (fixedCount == 1) fixed1 = vertexIndex;
                fixedCount++;
            }

            vertexIndex = triangle.v2;
            if (vertexIndex != collapsed0 && vertexIndex != collapsed1)
            {
                if (fixedCount == 0) fixed0 = vertexIndex;
                else if (fixedCount == 1) fixed1 = vertexIndex;
                fixedCount++;
            }

            if (fixedCount != 2)
            {
                fixed0 = -1;
                fixed1 = -1;
            }
        }

        private void PrepareCollapseNeighborhoodSide(
            ref Vertex sourceVertex,
            int otherIndex,
            int collapsed0,
            int collapsed1,
            bool[] deleted,
            bool collectFutureTriangles,
            int visitStamp,
            int[] flipTriangles,
            int[] flipVertices0,
            int[] flipVertices1,
            ref int flipCount)
        {
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            int refStart = sourceVertex.tstart;
            int refEnd = refStart + sourceVertex.tcount;
            int deletedIndex = 0;
            for (int refIndex = refStart; refIndex < refEnd; refIndex++, deletedIndex++)
            {
                Ref reference = referenceData[refIndex];
                ref Triangle triangle = ref triangleData[reference.tid];
                if (triangle.deleted)
                    continue;

                int id1;
                int id2;
                if (reference.tvertex == 0)
                {
                    id1 = triangle.v1;
                    id2 = triangle.v2;
                }
                else if (reference.tvertex == 1)
                {
                    id1 = triangle.v2;
                    id2 = triangle.v0;
                }
                else
                {
                    id1 = triangle.v0;
                    id2 = triangle.v1;
                }

                if (id1 == otherIndex || id2 == otherIndex)
                {
                    deleted[deletedIndex] = true;
                    continue;
                }

                deleted[deletedIndex] = false;
                int writeIndex = flipCount++;
                flipTriangles[writeIndex] = reference.tid;
                flipVertices0[writeIndex] = id1;
                flipVertices1[writeIndex] = id2;

                if (!collectFutureTriangles ||
                    !TryMarkReferenceTriangleVisited(reference.tid, visitStamp))
                {
                    continue;
                }

                int fixed0;
                int fixed1;
                GetFutureFixedVertices(
                    ref triangle,
                    collapsed0,
                    collapsed1,
                    out fixed0,
                    out fixed1);
                int futureIndex = collapseFutureTriangleCount++;
                collapseFutureTriangleBuffer[futureIndex] = reference.tid;
                collapseFutureFixedVertexBuffer0[futureIndex] = fixed0;
                collapseFutureFixedVertexBuffer1[futureIndex] = fixed1;
            }
        }

        private void PrepareCollapseNeighborhood(
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1,
            bool[] deleted0,
            bool[] deleted1)
        {
            bool collectFutureTriangles =
                cachedPreserveSurfaceEnvelope ||
                cachedVertexPlacementMode == VertexPlacementMode.ReferenceAccurate ||
                cachedVertexPlacementMode == VertexPlacementMode.SurfaceProjected ||
                cachedVertexPlacementMode == VertexPlacementMode.AvatarHybrid;
            EnsureCollapseNeighborhoodCapacity(
                vert0.tcount,
                vert1.tcount,
                collectFutureTriangles);
            collapseFlipCount0 = 0;
            collapseFlipCount1 = 0;
            collapseFutureTriangleCount = 0;
            preparedCollapseVertex0 = i0;
            preparedCollapseVertex1 = i1;

            int visitStamp = collectFutureTriangles ? BeginReferenceTriangleVisit() : 0;
            PrepareCollapseNeighborhoodSide(
                ref vert0,
                i1,
                i0,
                i1,
                deleted0,
                collectFutureTriangles,
                visitStamp,
                collapseFlipTriangleBuffer0,
                collapseFlipVertexBuffer00,
                collapseFlipVertexBuffer01,
                ref collapseFlipCount0);
            PrepareCollapseNeighborhoodSide(
                ref vert1,
                i0,
                i0,
                i1,
                deleted1,
                collectFutureTriangles,
                visitStamp,
                collapseFlipTriangleBuffer1,
                collapseFlipVertexBuffer10,
                collapseFlipVertexBuffer11,
                ref collapseFlipCount1);
        }

        /// <summary>
        /// Fits the replacement one-ring to the immutable source envelope. A normal edge collapse can
        /// leave every surviving vertex on the source surface and still shrink a curved hair/clothing
        /// shell because the new, longer triangle chords cut inside the curve. Rather than rejecting
        /// those collapses, sample their future edge midpoints and centroids, project the samples onto
        /// the original surface, and solve the corresponding vertex correction by interpolation.
        /// </summary>
        private bool TryFitReferenceEnvelope(
            ref Vector3d anchorPosition,
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1,
            ref int surfaceHint,
            out Vector3d fittedPosition)
        {
            fittedPosition = anchorPosition;
            if (!cachedPreserveSurfaceEnvelope || referenceMesh == null)
                return false;

            int futureTriangleCount = collapseFutureTriangleCount;
            if (futureTriangleCount == 0 ||
                preparedCollapseVertex0 != i0 ||
                preparedCollapseVertex1 != i1)
            {
                return false;
            }

            int component = MergeReferenceComponent(
                vert0.referenceComponent,
                vert1.referenceComponent);
            Vertex[] vertexData = vertices.Data;

            double localRadiusSqr = 0.0;
            int validFutureTriangleCount = 0;
            for (int localIndex = 0; localIndex < futureTriangleCount; localIndex++)
            {
                int fixed0 = collapseFutureFixedVertexBuffer0[localIndex];
                int fixed1 = collapseFutureFixedVertexBuffer1[localIndex];
                if (fixed0 < 0 || fixed1 < 0)
                    continue;

                validFutureTriangleCount++;
                ref Vector3d fixedPosition0 = ref vertexData[fixed0].p;
                ref Vector3d fixedPosition1 = ref vertexData[fixed1].p;
                double deltaX = fixedPosition0.x - anchorPosition.x;
                double deltaY = fixedPosition0.y - anchorPosition.y;
                double deltaZ = fixedPosition0.z - anchorPosition.z;
                double radiusSqr = deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
                if (radiusSqr > localRadiusSqr)
                    localRadiusSqr = radiusSqr;

                deltaX = fixedPosition1.x - anchorPosition.x;
                deltaY = fixedPosition1.y - anchorPosition.y;
                deltaZ = fixedPosition1.z - anchorPosition.z;
                radiusSqr = deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
                if (radiusSqr > localRadiusSqr)
                    localRadiusSqr = radiusSqr;
            }

            if (validFutureTriangleCount == 0 || localRadiusSqr <= 1e-30)
                return false;

            double localRadius = Math.Sqrt(localRadiusSqr);
            double referenceFloor = Math.Max(
                referenceMesh.BoundsDiagonal * 1e-8,
                1e-12);
            double queryRadius = Math.Max(localRadius * 2.5, referenceFloor);
            double queryRadiusSqr = queryRadius * queryRadius;
            double maximumSampleCorrection = Math.Max(
                localRadius * 0.75,
                referenceFloor);
            double maximumStep = Math.Max(localRadius * 0.24, referenceFloor);
            double maximumTotalOffset = Math.Max(
                localRadius * 0.38,
                referenceFloor);
            double maximumTotalOffsetSqr =
                maximumTotalOffset * maximumTotalOffset;

            Vector3d current = anchorPosition;
            bool adjusted = false;
            int mirroredSurfaceHint = -1;
            for (int iteration = 0; iteration < 3; iteration++)
            {
                Vector3d correctionSum = new Vector3d();
                double totalWeight = 0.0;

                Vector3d anchorSample = current;
                TryAccumulateReferenceEnvelopeCorrection(
                    ref anchorSample,
                    1.0,
                    1.0,
                    component,
                    queryRadius,
                    queryRadiusSqr,
                    maximumSampleCorrection,
                    ref surfaceHint,
                    ref mirroredSurfaceHint,
                    ref correctionSum,
                    ref totalWeight);

                for (int localIndex = 0;
                    localIndex < futureTriangleCount;
                    localIndex++)
                {
                    int fixed0 = collapseFutureFixedVertexBuffer0[localIndex];
                    int fixed1 = collapseFutureFixedVertexBuffer1[localIndex];
                    if (fixed0 < 0 || fixed1 < 0)
                        continue;

                    Vector3d fixedPosition0 = vertexData[fixed0].p;
                    Vector3d fixedPosition1 = vertexData[fixed1].p;

                    Vector3d sample =
                        (current + fixedPosition0 + fixedPosition1) / 3.0;
                    TryAccumulateReferenceEnvelopeCorrection(
                        ref sample,
                        3.0,
                        2.0,
                        component,
                        queryRadius,
                        queryRadiusSqr,
                        maximumSampleCorrection,
                        ref surfaceHint,
                        ref mirroredSurfaceHint,
                        ref correctionSum,
                        ref totalWeight);

                    sample = (current + fixedPosition0) * 0.5;
                    TryAccumulateReferenceEnvelopeCorrection(
                        ref sample,
                        2.0,
                        1.0,
                        component,
                        queryRadius,
                        queryRadiusSqr,
                        maximumSampleCorrection,
                        ref surfaceHint,
                        ref mirroredSurfaceHint,
                        ref correctionSum,
                        ref totalWeight);

                    sample = (current + fixedPosition1) * 0.5;
                    TryAccumulateReferenceEnvelopeCorrection(
                        ref sample,
                        2.0,
                        1.0,
                        component,
                        queryRadius,
                        queryRadiusSqr,
                        maximumSampleCorrection,
                        ref surfaceHint,
                        ref mirroredSurfaceHint,
                        ref correctionSum,
                        ref totalWeight);
                }

                if (totalWeight <= 0.0)
                    break;

                Vector3d correction = correctionSum / totalWeight;
                double correctionLengthSqr = correction.MagnitudeSqr;
                if (correctionLengthSqr <= localRadiusSqr * 1e-16)
                    break;

                double correctionLength = Math.Sqrt(correctionLengthSqr);
                if (correctionLength > maximumStep)
                    correction *= maximumStep / correctionLength;

                correction *= iteration == 0 ? 0.72 : 0.55;
                Vector3d candidate = current + correction;
                Vector3d totalOffset = candidate - anchorPosition;
                double totalOffsetSqr = totalOffset.MagnitudeSqr;
                if (totalOffsetSqr > maximumTotalOffsetSqr &&
                    totalOffsetSqr > 1e-30)
                {
                    candidate = anchorPosition + totalOffset *
                        (maximumTotalOffset / Math.Sqrt(totalOffsetSqr));
                }

                if (!IsFinite(ref candidate))
                    break;

                current = candidate;
                adjusted = true;
            }

            fittedPosition = current;
            return adjusted;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double GetReferenceSurfaceToleranceSqr()
        {
            return cachedReferenceSurfaceToleranceSqr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double GetReferenceBoundaryToleranceSqr()
        {
            return cachedReferenceBoundaryToleranceSqr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MergeReferenceComponent(int first, int second)
        {
            if (first < 0) return second;
            if (second < 0) return first;
            return first == second ? first : -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MergeReferenceHint(int first, int second)
        {
            if (first >= 0)
                return first;
            return second;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MakeUndirectedEdgeKey(int indexA, int indexB)
        {
            uint min = (uint)Math.Min(indexA, indexB);
            uint max = (uint)Math.Max(indexA, indexB);
            return ((ulong)min << 32) | max;
        }

        private int BeginReferenceTriangleVisit()
        {
            int triangleCount = triangles != null ? triangles.Length : 0;
            if (referenceTriangleVisitStamps.Length < triangleCount)
                Array.Resize(ref referenceTriangleVisitStamps, Math.Max(triangleCount, referenceTriangleVisitStamps.Length * 2 + 32));

            if (referenceTriangleVisitStamp == int.MaxValue)
            {
                Array.Clear(referenceTriangleVisitStamps, 0, referenceTriangleVisitStamps.Length);
                referenceTriangleVisitStamp = 1;
            }
            else
            {
                referenceTriangleVisitStamp++;
                if (referenceTriangleVisitStamp == 0)
                    referenceTriangleVisitStamp = 1;
            }

            return referenceTriangleVisitStamp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryMarkReferenceTriangleVisited(int triangleIndex, int visitStamp)
        {
            if (triangleIndex < 0 || triangleIndex >= triangles.Length)
                return false;
            if (referenceTriangleVisitStamps[triangleIndex] == visitStamp)
                return false;
            referenceTriangleVisitStamps[triangleIndex] = visitStamp;
            return true;
        }

        private int BeginReferenceMidpointVisit()
        {
            int vertexCount = vertices != null ? vertices.Length : 0;
            if (referenceMidpointVisitStamps.Length < vertexCount)
            {
                Array.Resize(
                    ref referenceMidpointVisitStamps,
                    Math.Max(
                        vertexCount,
                        referenceMidpointVisitStamps.Length * 2 + 32));
            }

            if (referenceMidpointVisitStamp == int.MaxValue)
            {
                Array.Clear(
                    referenceMidpointVisitStamps,
                    0,
                    referenceMidpointVisitStamps.Length);
                referenceMidpointVisitStamp = 1;
            }
            else
            {
                referenceMidpointVisitStamp++;
                if (referenceMidpointVisitStamp == 0)
                    referenceMidpointVisitStamp = 1;
            }

            return referenceMidpointVisitStamp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryMarkReferenceMidpointVisited(int vertexIndex, int visitStamp)
        {
            if ((uint)vertexIndex >= (uint)vertices.Length)
                return false;
            if (referenceMidpointVisitStamps[vertexIndex] == visitStamp)
                return false;
            referenceMidpointVisitStamps[vertexIndex] = visitStamp;
            return true;
        }

        private bool IsCurrentBoundaryEdge(int i0, int i1, ref Vertex vert0, ref Vertex vert1)
        {
            if (!vert0.borderEdge || !vert1.borderEdge)
                return false;

            Vertex searchVertex = vert0.tcount <= vert1.tcount ? vert0 : vert1;
            int otherIndex = vert0.tcount <= vert1.tcount ? i1 : i0;
            int live = 0;
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            for (int k = 0; k < searchVertex.tcount; k++)
            {
                int triangleIndex = referenceData[searchVertex.tstart + k].tid;
                if (triangleIndex < 0 || triangleIndex >= triangles.Length)
                    continue;
                Triangle triangle = triangleData[triangleIndex];
                if (triangle.deleted ||
                    (triangle.v0 != otherIndex && triangle.v1 != otherIndex && triangle.v2 != otherIndex))
                {
                    continue;
                }

                live++;
                if (live > 1)
                    return false;
            }
            return live == 1;
        }

        private bool TryProjectToReferenceSurface(
            ref Vector3d requestedPosition,
            int i0,
            int i1,
            int i2,
            int attribute0,
            int attribute1,
            int attribute2,
            ref Vertex vert0,
            ref Vertex vert1,
            out CollapsePlacement placement)
        {
            placement = new CollapsePlacement();
            if (referenceMesh == null)
                return false;

            int component = MergeReferenceComponent(vert0.referenceComponent, vert1.referenceComponent);
            int hint = MergeReferenceHint(vert0.referenceTriangleHint, vert1.referenceTriangleHint);
            ReferenceMesh.SurfaceHit hit;
            if (!referenceMesh.TryFindClosestSurface(
                ref requestedPosition, component, double.MaxValue, hint, out hit))
            {
                return false;
            }

            Vector3d projected = hit.position;
            placement = CreateCurrentTrianglePlacement(
                ref projected, i0, i1, i2, attribute0, attribute1, attribute2);
            placement.ReferenceTriangleHint = hit.triangleIndex;
            return true;
        }

        private bool TryProjectToReferenceBoundary(
            ref Vector3d requestedPosition,
            int i0,
            int i1,
            int attribute0,
            int attribute1,
            int attribute2,
            ref Vertex vert0,
            ref Vertex vert1,
            out CollapsePlacement placement)
        {
            placement = new CollapsePlacement();
            if (referenceMesh == null || !referenceMesh.HasBoundary)
                return false;

            int boundaryComponent = MergeReferenceComponent(
                vert0.referenceBoundaryComponent,
                vert1.referenceBoundaryComponent);
            int surfaceComponent = MergeReferenceComponent(
                vert0.referenceComponent,
                vert1.referenceComponent);
            int hint = MergeReferenceHint(
                vert0.referenceBoundarySegmentHint,
                vert1.referenceBoundarySegmentHint);
            ReferenceMesh.BoundaryHit hit;
            if (!referenceMesh.TryFindClosestBoundary(
                ref requestedPosition,
                boundaryComponent,
                surfaceComponent,
                double.MaxValue,
                hint,
                out hit))
            {
                return false;
            }

            Vector3d a = vert0.p;
            Vector3d b = vert1.p;
            Vector3d ignored;
            double t = ClosestPointOnSegment(ref hit.position, ref a, ref b, out ignored);
            placement = new CollapsePlacement(
                hit.position,
                attribute0,
                attribute1,
                attribute2,
                new Vector3((float)(1.0 - t), (float)t, 0f),
                -1,
                hit.segmentIndex);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsReferenceSurfaceSampleValid(
            ref Vector3d sample,
            int component,
            double toleranceSqr,
            ref int surfaceHint)
        {
            int matchingTriangle;
            if (!referenceMesh.TryFindSurfaceWithinDistance(
                ref sample,
                component,
                toleranceSqr,
                surfaceHint,
                out matchingTriangle))
            {
                return false;
            }

            surfaceHint = matchingTriangle;
            return true;
        }

        private bool ValidateReferenceTriangle(
            ref Vector3d a,
            ref Vector3d b,
            ref Vector3d c,
            int component,
            double toleranceSqr,
            ref int surfaceHint)
        {
            Vector3d sample = new Vector3d(
                (a.x + b.x + c.x) / 3.0,
                (a.y + b.y + c.y) / 3.0,
                (a.z + b.z + c.z) / 3.0);
            if (!IsReferenceSurfaceSampleValid(ref sample, component, toleranceSqr, ref surfaceHint)) return false;
            sample = new Vector3d(
                (a.x + b.x) * 0.5,
                (a.y + b.y) * 0.5,
                (a.z + b.z) * 0.5);
            if (!IsReferenceSurfaceSampleValid(ref sample, component, toleranceSqr, ref surfaceHint)) return false;
            sample = new Vector3d(
                (b.x + c.x) * 0.5,
                (b.y + c.y) * 0.5,
                (b.z + c.z) * 0.5);
            if (!IsReferenceSurfaceSampleValid(ref sample, component, toleranceSqr, ref surfaceHint)) return false;
            sample = new Vector3d(
                (c.x + a.x) * 0.5,
                (c.y + a.y) * 0.5,
                (c.z + a.z) * 0.5);
            return IsReferenceSurfaceSampleValid(ref sample, component, toleranceSqr, ref surfaceHint);
        }

        private bool ValidateReferenceFutureTriangle(
            ref Triangle triangle,
            ref Vector3d position,
            int i0,
            int i1,
            int component,
            double toleranceSqr,
            int midpointVisitStamp,
            ref int surfaceHint)
        {
            Vertex[] vertexData = vertices.Data;
            int fixed0 = -1;
            int fixed1 = -1;
            int fixedCount = 0;

            int vertexIndex = triangle.v0;
            if (vertexIndex != i0 && vertexIndex != i1)
            {
                fixed0 = vertexIndex;
                fixedCount = 1;
            }

            vertexIndex = triangle.v1;
            if (vertexIndex != i0 && vertexIndex != i1)
            {
                if (fixedCount == 0) fixed0 = vertexIndex;
                else if (fixedCount == 1) fixed1 = vertexIndex;
                fixedCount++;
            }

            vertexIndex = triangle.v2;
            if (vertexIndex != i0 && vertexIndex != i1)
            {
                if (fixedCount == 0) fixed0 = vertexIndex;
                else if (fixedCount == 1) fixed1 = vertexIndex;
                fixedCount++;
            }

            if (fixedCount != 2 || fixed0 < 0 || fixed1 < 0)
            {
                Vector3d a = triangle.v0 == i0 || triangle.v0 == i1
                    ? position
                    : vertexData[triangle.v0].p;
                Vector3d b = triangle.v1 == i0 || triangle.v1 == i1
                    ? position
                    : vertexData[triangle.v1].p;
                Vector3d c = triangle.v2 == i0 || triangle.v2 == i1
                    ? position
                    : vertexData[triangle.v2].p;
                return ValidateReferenceTriangle(
                    ref a,
                    ref b,
                    ref c,
                    component,
                    toleranceSqr,
                    ref surfaceHint);
            }

            Vector3d fixedPosition0 = vertexData[fixed0].p;
            Vector3d fixedPosition1 = vertexData[fixed1].p;

            Vector3d sample = new Vector3d(
                (position.x + fixedPosition0.x + fixedPosition1.x) / 3.0,
                (position.y + fixedPosition0.y + fixedPosition1.y) / 3.0,
                (position.z + fixedPosition0.z + fixedPosition1.z) / 3.0);
            if (!IsReferenceSurfaceSampleValid(
                ref sample,
                component,
                toleranceSqr,
                ref surfaceHint))
            {
                return false;
            }

            sample = new Vector3d(
                (fixedPosition0.x + fixedPosition1.x) * 0.5,
                (fixedPosition0.y + fixedPosition1.y) * 0.5,
                (fixedPosition0.z + fixedPosition1.z) * 0.5);
            if (!IsReferenceSurfaceSampleValid(
                ref sample,
                component,
                toleranceSqr,
                ref surfaceHint))
            {
                return false;
            }

            if (TryMarkReferenceMidpointVisited(fixed0, midpointVisitStamp))
            {
                sample = new Vector3d(
                    (position.x + fixedPosition0.x) * 0.5,
                    (position.y + fixedPosition0.y) * 0.5,
                    (position.z + fixedPosition0.z) * 0.5);
                if (!IsReferenceSurfaceSampleValid(
                    ref sample,
                    component,
                    toleranceSqr,
                    ref surfaceHint))
                {
                    return false;
                }
            }

            if (TryMarkReferenceMidpointVisited(fixed1, midpointVisitStamp))
            {
                sample = new Vector3d(
                    (position.x + fixedPosition1.x) * 0.5,
                    (position.y + fixedPosition1.y) * 0.5,
                    (position.z + fixedPosition1.z) * 0.5);
                if (!IsReferenceSurfaceSampleValid(
                    ref sample,
                    component,
                    toleranceSqr,
                    ref surfaceHint))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ValidatePreparedReferenceFutureTriangle(
            int triangleIndex,
            int fixed0,
            int fixed1,
            ref Vector3d position,
            int i0,
            int i1,
            int component,
            double toleranceSqr,
            int midpointVisitStamp,
            ref int surfaceHint)
        {
            Triangle[] triangleData = triangles.Data;
            if (fixed0 < 0 || fixed1 < 0)
            {
                ref Triangle fallbackTriangle = ref triangleData[triangleIndex];
                return ValidateReferenceFutureTriangle(
                    ref fallbackTriangle,
                    ref position,
                    i0,
                    i1,
                    component,
                    toleranceSqr,
                    midpointVisitStamp,
                    ref surfaceHint);
            }

            Vertex[] vertexData = vertices.Data;
            ref Vector3d fixedPosition0 = ref vertexData[fixed0].p;
            ref Vector3d fixedPosition1 = ref vertexData[fixed1].p;
            Vector3d sample = new Vector3d(
                (position.x + fixedPosition0.x + fixedPosition1.x) / 3.0,
                (position.y + fixedPosition0.y + fixedPosition1.y) / 3.0,
                (position.z + fixedPosition0.z + fixedPosition1.z) / 3.0);
            if (!IsReferenceSurfaceSampleValid(
                ref sample,
                component,
                toleranceSqr,
                ref surfaceHint))
            {
                return false;
            }

            sample.x = (fixedPosition0.x + fixedPosition1.x) * 0.5;
            sample.y = (fixedPosition0.y + fixedPosition1.y) * 0.5;
            sample.z = (fixedPosition0.z + fixedPosition1.z) * 0.5;
            if (!IsReferenceSurfaceSampleValid(
                ref sample,
                component,
                toleranceSqr,
                ref surfaceHint))
            {
                return false;
            }

            if (TryMarkReferenceMidpointVisited(fixed0, midpointVisitStamp))
            {
                sample.x = (position.x + fixedPosition0.x) * 0.5;
                sample.y = (position.y + fixedPosition0.y) * 0.5;
                sample.z = (position.z + fixedPosition0.z) * 0.5;
                if (!IsReferenceSurfaceSampleValid(
                    ref sample,
                    component,
                    toleranceSqr,
                    ref surfaceHint))
                {
                    return false;
                }
            }

            if (TryMarkReferenceMidpointVisited(fixed1, midpointVisitStamp))
            {
                sample.x = (position.x + fixedPosition1.x) * 0.5;
                sample.y = (position.y + fixedPosition1.y) * 0.5;
                sample.z = (position.z + fixedPosition1.z) * 0.5;
                if (!IsReferenceSurfaceSampleValid(
                    ref sample,
                    component,
                    toleranceSqr,
                    ref surfaceHint))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ValidateReferenceTrianglesForVertex(
            ref Vertex vertex,
            int triangleVisitStamp,
            int midpointVisitStamp,
            ref Vector3d position,
            int i0,
            int i1,
            int component,
            double toleranceSqr,
            ref int surfaceHint)
        {
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            for (int k = 0; k < vertex.tcount; k++)
            {
                int triangleIndex = referenceData[vertex.tstart + k].tid;
                if (!TryMarkReferenceTriangleVisited(
                    triangleIndex,
                    triangleVisitStamp))
                {
                    continue;
                }

                ref Triangle triangle = ref triangleData[triangleIndex];
                if (triangle.deleted)
                    continue;

                bool contains0 =
                    triangle.v0 == i0 ||
                    triangle.v1 == i0 ||
                    triangle.v2 == i0;
                bool contains1 =
                    triangle.v0 == i1 ||
                    triangle.v1 == i1 ||
                    triangle.v2 == i1;
                if (contains0 && contains1)
                    continue;

                if (!ValidateReferenceFutureTriangle(
                    ref triangle,
                    ref position,
                    i0,
                    i1,
                    component,
                    toleranceSqr,
                    midpointVisitStamp,
                    ref surfaceHint))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ValidateReferenceSurfaceDeviation(
            ref Vector3d position,
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1,
            ref int surfaceHint)
        {
            double toleranceSqr = GetReferenceSurfaceToleranceSqr();
            int component = MergeReferenceComponent(
                vert0.referenceComponent,
                vert1.referenceComponent);
            int midpointVisitStamp = BeginReferenceMidpointVisit();

            int futureCount = collapseFutureTriangleCount;
            for (int index = 0; index < futureCount; index++)
            {
                if (!ValidatePreparedReferenceFutureTriangle(
                    collapseFutureTriangleBuffer[index],
                    collapseFutureFixedVertexBuffer0[index],
                    collapseFutureFixedVertexBuffer1[index],
                    ref position,
                    i0,
                    i1,
                    component,
                    toleranceSqr,
                    midpointVisitStamp,
                    ref surfaceHint))
                {
                    return false;
                }
            }

            return true;
        }

        private void AddBoundaryEdgeUse(int a, int b, int collapsed0, int collapsed1)
        {
            if (a != collapsed0 && a != collapsed1 && b != collapsed0 && b != collapsed1)
                return;
            ulong key = MakeUndirectedEdgeKey(a, b);
            int count;
            referenceBoundaryEdgeUse.TryGetValue(key, out count);
            referenceBoundaryEdgeUse[key] = count + 1;
        }

        private void CollectReferenceBoundaryEdgesForVertex(
            ref Vertex vertex,
            int visitStamp,
            int collapsed0,
            int collapsed1)
        {
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            for (int k = 0; k < vertex.tcount; k++)
            {
                int triangleIndex = referenceData[vertex.tstart + k].tid;
                if (!TryMarkReferenceTriangleVisited(triangleIndex, visitStamp))
                    continue;

                ref Triangle triangle = ref triangleData[triangleIndex];
                if (triangle.deleted)
                    continue;
                AddBoundaryEdgeUse(triangle.v0, triangle.v1, collapsed0, collapsed1);
                AddBoundaryEdgeUse(triangle.v1, triangle.v2, collapsed0, collapsed1);
                AddBoundaryEdgeUse(triangle.v2, triangle.v0, collapsed0, collapsed1);
            }
        }

        private bool ValidateReferenceBoundaryDeviation(
            ref Vector3d position,
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1,
            ref int boundaryHint)
        {
            if (cachedPreserveBorderEdges)
                return true;
            if (!IsCurrentBoundaryEdge(i0, i1, ref vert0, ref vert1))
                return true;

            double toleranceSqr = GetReferenceBoundaryToleranceSqr();
            int boundaryComponent = MergeReferenceComponent(
                vert0.referenceBoundaryComponent,
                vert1.referenceBoundaryComponent);
            int surfaceComponent = MergeReferenceComponent(
                vert0.referenceComponent,
                vert1.referenceComponent);

            referenceBoundaryEdgeUse.Clear();
            referenceBoundaryNeighborSet.Clear();
            int visitStamp = BeginReferenceTriangleVisit();
            CollectReferenceBoundaryEdgesForVertex(ref vert0, visitStamp, i0, i1);
            CollectReferenceBoundaryEdgesForVertex(ref vert1, visitStamp, i0, i1);

            foreach (KeyValuePair<ulong, int> pair in referenceBoundaryEdgeUse)
            {
                if (pair.Value != 1)
                    continue;
                int a = (int)(pair.Key >> 32);
                int b = (int)(pair.Key & 0xffffffffUL);
                int other = a == i0 || a == i1 ? b : a;
                if (other != i0 && other != i1)
                    referenceBoundaryNeighborSet.Add(other);
            }

            int matchingSegment;
            Vector3d sample = position;
            if (!referenceMesh.TryFindBoundaryWithinDistance(
                ref sample,
                boundaryComponent,
                surfaceComponent,
                toleranceSqr,
                boundaryHint,
                out matchingSegment))
            {
                return false;
            }
            boundaryHint = matchingSegment;

            foreach (int neighbor in referenceBoundaryNeighborSet)
            {
                Vector3d other = vertices[neighbor].p;
                Vector3d delta = other - position;
                for (int step = 1; step <= 3; step++)
                {
                    sample = position + delta * (step * 0.25);
                    if (!referenceMesh.TryFindBoundaryWithinDistance(
                        ref sample,
                        boundaryComponent,
                        surfaceComponent,
                        toleranceSqr,
                        boundaryHint,
                        out matchingSegment))
                    {
                        return false;
                    }
                    boundaryHint = matchingSegment;
                }
            }
            return true;
        }

        private bool ValidateReferenceConstraints(
            ref Vector3d position,
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1,
            ref int surfaceHint,
            ref int boundaryHint)
        {
            if (referenceMesh == null)
                return false;
            return ValidateReferenceSurfaceDeviation(
                       ref position, i0, i1, ref vert0, ref vert1, ref surfaceHint) &&
                   ValidateReferenceBoundaryDeviation(
                       ref position, i0, i1, ref vert0, ref vert1, ref boundaryHint);
        }

        private bool TryAcceptPlacementCore(
            ref CollapsePlacement placement,
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1,
            bool countReferenceRejection)
        {
            Vector3d position = placement.position;
            if (!IsFinite(ref position) || !IsFinite(placement.barycentric))
                return false;

            if (FlippedPrepared(ref position, true))
                return false;
            if (FlippedPrepared(ref position, false))
                return false;
            if (cachedVertexPlacementMode == VertexPlacementMode.ReferenceAccurate)
            {
                int surfaceHint = placement.ReferenceTriangleHint;
                if (surfaceHint < 0)
                    surfaceHint = MergeReferenceHint(vert0.referenceTriangleHint, vert1.referenceTriangleHint);
                int boundaryHint = placement.ReferenceBoundaryHint;
                if (boundaryHint < 0)
                {
                    boundaryHint = MergeReferenceHint(
                        vert0.referenceBoundarySegmentHint,
                        vert1.referenceBoundarySegmentHint);
                }

                if (!ValidateReferenceConstraints(
                    ref position,
                    i0,
                    i1,
                    ref vert0,
                    ref vert1,
                    ref surfaceHint,
                    ref boundaryHint))
                {
                    if (countReferenceRejection)
                        referenceConstraintRejectedCollapses++;
                    return false;
                }

                placement.ReferenceTriangleHint = surfaceHint;
                placement.ReferenceBoundaryHint = boundaryHint;
            }

            placement.position = position;
            SanitizeBarycentricCoords(ref placement.barycentric);
            return true;
        }

        private bool TryAcceptSinglePlacementDirect(
            ref CollapsePlacement placement,
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1,
            bool[] deleted0,
            bool[] deleted1)
        {
            Vector3d position = placement.position;
            if (!IsFinite(ref position) || !IsFinite(placement.barycentric))
                return false;
            if (Flipped(ref position, i0, i1, ref vert0, deleted0))
                return false;
            if (Flipped(ref position, i1, i0, ref vert1, deleted1))
                return false;

            placement.position = position;
            SanitizeBarycentricCoords(ref placement.barycentric);
            return true;
        }

        private bool TryAcceptPlacement(
            ref CollapsePlacement placement,
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1)
        {
            if (cachedPreserveSurfaceEnvelope && referenceMesh != null)
            {
                CollapsePlacement fittedPlacement = placement;
                int surfaceHint = fittedPlacement.ReferenceTriangleHint;
                if (surfaceHint < 0)
                    surfaceHint = MergeReferenceHint(vert0.referenceTriangleHint, vert1.referenceTriangleHint);

                Vector3d fittedPosition;
                Vector3d anchorPosition = placement.position;
                if (TryFitReferenceEnvelope(
                    ref anchorPosition, i0, i1, ref vert0, ref vert1,
                    ref surfaceHint, out fittedPosition))
                {
                    Vector3d envelopeOffset = fittedPosition - anchorPosition;
                    for (int stepIndex = 0; stepIndex < 4; stepIndex++)
                    {
                        double interpolationStep = stepIndex == 0 ? 1.0 :
                            (stepIndex == 1 ? 0.75 : (stepIndex == 2 ? 0.5 : 0.25));
                        fittedPlacement = placement;
                        fittedPlacement.position = anchorPosition + envelopeOffset * interpolationStep;
                        fittedPlacement.ReferenceTriangleHint = surfaceHint;
                        if (TryAcceptPlacementCore(
                            ref fittedPlacement, i0, i1, ref vert0, ref vert1,
                            false))
                        {
                            placement = fittedPlacement;
                            return true;
                        }
                    }
                }
            }

            return TryAcceptPlacementCore(
                ref placement, i0, i1, ref vert0, ref vert1,
                true);
        }

        private bool TryEdgeFallbacks(
            ref Vector3d requestedPosition,
            int i0,
            int i1,
            int attribute0,
            int attribute1,
            int attribute2,
            ref Vertex vert0,
            ref Vertex vert1,
            out CollapsePlacement placement)
        {
            placement = CreateEdgePlacement(ref requestedPosition, i0, i1, attribute0, attribute1, attribute2);
            if (TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1))
                return true;

            Vector3d midpointRequest = (vert0.p + vert1.p) * 0.5;
            placement = CreateEdgePlacement(ref midpointRequest, i0, i1, attribute0, attribute1, attribute2);
            if (TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1))
                return true;

            Vector3d endpoint0 = vert0.p;
            placement = CreateEdgePlacement(ref endpoint0, i0, i1, attribute0, attribute1, attribute2);
            if (TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1))
                return true;

            Vector3d endpoint1 = vert1.p;
            placement = CreateEdgePlacement(ref endpoint1, i0, i1, attribute0, attribute1, attribute2);
            return TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1);
        }

        private bool TryResolveCollapsePlacement(
            int i0,
            int i1,
            int i2,
            int attribute0,
            int attribute1,
            int attribute2,
            ref Vertex vert0,
            ref Vertex vert1,
            bool[] deleted0,
            bool[] deleted1,
            out CollapsePlacement placement)
        {
            VertexPlacementMode mode = cachedVertexPlacementMode;
            bool usePreparedNeighborhood =
                cachedPreserveSurfaceEnvelope || mode != VertexPlacementMode.Optimal;
            if (usePreparedNeighborhood)
            {
                PrepareCollapseNeighborhood(
                    i0,
                    i1,
                    ref vert0,
                    ref vert1,
                    deleted0,
                    deleted1);
            }

            Vector3d optimalPosition;
            CalculateError(ref vert0, ref vert1, out optimalPosition);
            if (!IsFinite(ref optimalPosition))
                optimalPosition = (vert0.p + vert1.p) * 0.5;

            if (mode == VertexPlacementMode.Optimal)
            {
                placement = CreateCurrentTrianglePlacement(
                    ref optimalPosition,
                    i0,
                    i1,
                    i2,
                    attribute0,
                    attribute1,
                    attribute2);
                if (!usePreparedNeighborhood)
                {
                    return TryAcceptSinglePlacementDirect(
                        ref placement,
                        i0,
                        i1,
                        ref vert0,
                        ref vert1,
                        deleted0,
                        deleted1);
                }
                return TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1);
            }

            if (mode == VertexPlacementMode.ReferenceAccurate)
            {
                bool boundaryEdge =
                    !cachedPreserveBorderEdges &&
                    IsCurrentBoundaryEdge(i0, i1, ref vert0, ref vert1);
                if (boundaryEdge &&
                    TryProjectToReferenceBoundary(
                        ref optimalPosition,
                        i0,
                        i1,
                        attribute0,
                        attribute1,
                        attribute2,
                        ref vert0,
                        ref vert1,
                        out placement) &&
                    TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1))
                {
                    return true;
                }

                if (TryProjectToReferenceSurface(
                        ref optimalPosition,
                        i0,
                        i1,
                        i2,
                        attribute0,
                        attribute1,
                        attribute2,
                        ref vert0,
                        ref vert1,
                        out placement) &&
                    TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1))
                {
                    return true;
                }

                Vector3d midpoint = (vert0.p + vert1.p) * 0.5;
                if (boundaryEdge &&
                    TryProjectToReferenceBoundary(
                        ref midpoint,
                        i0,
                        i1,
                        attribute0,
                        attribute1,
                        attribute2,
                        ref vert0,
                        ref vert1,
                        out placement) &&
                    TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1))
                {
                    return true;
                }

                if (TryProjectToReferenceSurface(
                        ref midpoint,
                        i0,
                        i1,
                        i2,
                        attribute0,
                        attribute1,
                        attribute2,
                        ref vert0,
                        ref vert1,
                        out placement) &&
                    TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1))
                {
                    return true;
                }

                if (TryProjectToPreparedLocalSurface(ref optimalPosition, out placement) &&
                    TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1))
                {
                    return true;
                }

                placement = new CollapsePlacement();
                return false;
            }

            bool useEdgePlacement = mode == VertexPlacementMode.EdgeInterpolated;
            if (mode == VertexPlacementMode.AvatarHybrid)
                useEdgePlacement = IsFeatureEdge(i0, i1, ref vert0, ref vert1);

            if (!useEdgePlacement && (mode == VertexPlacementMode.SurfaceProjected || mode == VertexPlacementMode.AvatarHybrid))
            {
                if (TryProjectToPreparedLocalSurface(ref optimalPosition, out placement) &&
                    TryAcceptPlacement(ref placement, i0, i1, ref vert0, ref vert1))
                {
                    return true;
                }
            }

            return TryEdgeFallbacks(
                ref optimalPosition,
                i0,
                i1,
                attribute0,
                attribute1,
                attribute2,
                ref vert0,
                ref vert1,
                out placement);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double CalculateConstrainedEdgeError(int i0, int i1, out Vector3d position)
        {
            Vertex[] vertexData = vertices.Data;
            return CalculateConstrainedEdgeError(
                i0, i1, ref vertexData[i0], ref vertexData[i1], out position);
        }

        private double CalculateConstrainedEdgeError(
            int i0,
            int i1,
            ref Vertex vert0,
            ref Vertex vert1,
            out Vector3d position)
        {
            SymmetricMatrix q;
            double originalError = CalculateError(
                ref vert0, ref vert1, out position, out q);
            VertexPlacementMode mode = cachedVertexPlacementMode;
            if (mode == VertexPlacementMode.Optimal)
                return originalError;

            double unconstrainedQuadricError = VertexError(ref q, position.x, position.y, position.z);
            double curvaturePenalty = Math.Max(0.0, originalError - unconstrainedQuadricError);
            Vector3d constrainedPosition = position;
            bool useEdgePlacement = mode == VertexPlacementMode.EdgeInterpolated;
            if (mode == VertexPlacementMode.AvatarHybrid)
                useEdgePlacement = IsFeatureEdge(i0, i1, ref vert0, ref vert1);

            if (mode == VertexPlacementMode.ReferenceAccurate && referenceMesh != null)
            {
                if (!cachedPreserveBorderEdges &&
                    IsCurrentBoundaryEdge(i0, i1, ref vert0, ref vert1))
                {
                    Vector3d a = vert0.p;
                    Vector3d b = vert1.p;
                    ClosestPointOnSegment(ref position, ref a, ref b, out constrainedPosition);
                }
                else
                {
                    CollapsePlacement projected;
                    if (TryProjectToLocalSurface(
                        ref position, i0, i1, ref vert0, ref vert1, out projected))
                    {
                        constrainedPosition = projected.position;
                    }
                    else
                    {
                        constrainedPosition = new Vector3d(
                            (vert0.p.x + vert1.p.x) * 0.5,
                            (vert0.p.y + vert1.p.y) * 0.5,
                            (vert0.p.z + vert1.p.z) * 0.5);
                    }
                }
            }
            else if (useEdgePlacement)
            {
                Vector3d a = vert0.p;
                Vector3d b = vert1.p;
                ClosestPointOnSegment(ref position, ref a, ref b, out constrainedPosition);
            }
            else if (mode == VertexPlacementMode.SurfaceProjected || mode == VertexPlacementMode.AvatarHybrid)
            {
                CollapsePlacement projected;
                if (TryProjectToLocalSurface(
                    ref position, i0, i1, ref vert0, ref vert1, out projected))
                {
                    constrainedPosition = projected.position;
                }
                else
                {
                    Vector3d a = vert0.p;
                    Vector3d b = vert1.p;
                    ClosestPointOnSegment(ref position, ref a, ref b, out constrainedPosition);
                }
            }

            if (!IsFinite(ref constrainedPosition))
            {
                constrainedPosition = new Vector3d(
                    (vert0.p.x + vert1.p.x) * 0.5,
                    (vert0.p.y + vert1.p.y) * 0.5,
                    (vert0.p.z + vert1.p.z) * 0.5);
            }

            position = constrainedPosition;
            return VertexError(ref q, position.x, position.y, position.z) + curvaturePenalty;
        }
        #endregion

        #region Calculate Barycentric Coordinates
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalculateBarycentricCoords(
            ref Vector3d point,
            ref Vector3d a,
            ref Vector3d b,
            ref Vector3d c,
            out Vector3 result)
        {
            double v0x = b.x - a.x;
            double v0y = b.y - a.y;
            double v0z = b.z - a.z;
            double v1x = c.x - a.x;
            double v1y = c.y - a.y;
            double v1z = c.z - a.z;
            double v2x = point.x - a.x;
            double v2y = point.y - a.y;
            double v2z = point.z - a.z;

            double d00 = v0x * v0x + v0y * v0y + v0z * v0z;
            double d01 = v0x * v1x + v0y * v1y + v0z * v1z;
            double d11 = v1x * v1x + v1y * v1y + v1z * v1z;
            double d20 = v2x * v0x + v2y * v0y + v2z * v0z;
            double d21 = v2x * v1x + v2y * v1y + v2z * v1z;
            double denom = d00 * d11 - d01 * d01;

            double denomScale = Math.Abs(d00 * d11);
            if (denomScale <= 0.0 || Math.Abs(denom) <= denomScale * 1e-15)
            {
                if (d00 > 1e-30)
                {
                    double edgeT = d20 / d00;
                    result = new Vector3((float)(1.0 - edgeT), (float)edgeT, 0f);
                }
                else
                {
                    result = new Vector3(1f, 0f, 0f);
                }
                return;
            }

            double inverseDenom = 1.0 / denom;
            double v = (d11 * d20 - d01 * d21) * inverseDenom;
            double w = (d00 * d21 - d01 * d20) * inverseDenom;
            double u = 1.0 - v - w;
            result = new Vector3((float)u, (float)v, (float)w);
        }
        #endregion

        #region Normalize Tangent
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector4 NormalizeTangent(Vector4 tangent, Vector4 fallback, Vector3 normal)
        {
            Vector3 tangentVec = new Vector3(tangent.x, tangent.y, tangent.z);
            Vector3 fallbackVec = new Vector3(fallback.x, fallback.y, fallback.z);

            if (normal.sqrMagnitude > 1e-20f)
            {
                tangentVec -= normal * Vector3.Dot(normal, tangentVec);
                fallbackVec -= normal * Vector3.Dot(normal, fallbackVec);
            }

            if (tangentVec.sqrMagnitude > 1e-20f)
            {
                tangentVec.Normalize();
            }
            else if (fallbackVec.sqrMagnitude > 1e-20f)
            {
                tangentVec = fallbackVec.normalized;
            }
            else
            {
                tangentVec = Vector3.zero;
            }

            float handednessSource = Math.Abs(tangent.w) > 1e-20f ? tangent.w : fallback.w;
            float handedness = handednessSource < 0f ? -1f : (handednessSource > 0f ? 1f : 0f);
            return new Vector4(tangentVec.x, tangentVec.y, tangentVec.z, handedness);
        }

        private static void SanitizeBarycentricCoords(ref Vector3 barycentricCoord)
        {
            if (!IsFinite(barycentricCoord))
            {
                barycentricCoord = new Vector3(1f, 0f, 0f);
                return;
            }

            float sum = barycentricCoord.x + barycentricCoord.y + barycentricCoord.z;
            if (float.IsNaN(sum) || float.IsInfinity(sum) || Math.Abs(sum) <= 1e-20f)
            {
                barycentricCoord = new Vector3(1f, 0f, 0f);
                return;
            }

            barycentricCoord /= sum;
        }

        private BoneWeight1[] NormalizeBoneWeights(BoneWeight1[] source, float threshold, int maxInfluences)
        {
            boneWeightAccumulator.Clear();
            AccumulateBoneWeights(source, 1f);
            return BuildNormalizedBoneWeights(threshold, maxInfluences);
        }

        private void InterpolateBoneWeights(int dst, int i0, int i1, int i2, ref Vector3 barycentricCoord)
        {
            if (vertBoneWeights == null)
                return;

            BoneWeight1[] weights0 = vertBoneWeights[i0];
            BoneWeight1[] weights1 = vertBoneWeights[i1];
            BoneWeight1[] weights2 = vertBoneWeights[i2];

            boneWeightAccumulator.Clear();
            AccumulateBoneWeights(weights0, barycentricCoord.x);
            AccumulateBoneWeights(weights1, barycentricCoord.y);
            AccumulateBoneWeights(weights2, barycentricCoord.z);

            int maxInfluences = simplificationOptions.MaxBoneWeightsPerVertex <= 0
                ? byte.MaxValue
                : Math.Min(byte.MaxValue, simplificationOptions.MaxBoneWeightsPerVertex);
            BoneWeight1[] result = BuildNormalizedBoneWeights(simplificationOptions.BoneWeightThreshold, maxInfluences);
            if (result.Length == 0)
            {
                BoneWeight1 fallback;
                if (TryGetStrongestInfluence(weights0, weights1, weights2, out fallback))
                {
                    fallback.weight = 1f;
                    result = new[] { fallback };
                }
            }

            vertBoneWeights[dst] = result;
        }

        private void AccumulateBoneWeights(BoneWeight1[] weights, float factor)
        {
            if (weights == null || factor <= 0f)
                return;

            for (int i = 0; i < weights.Length; i++)
            {
                BoneWeight1 influence = weights[i];
                double weightedValue = (double)influence.weight * factor;
                if (influence.boneIndex < 0 || weightedValue <= 0.0 || double.IsNaN(weightedValue) || double.IsInfinity(weightedValue))
                    continue;

                double current;
                if (boneWeightAccumulator.TryGetValue(influence.boneIndex, out current))
                    boneWeightAccumulator[influence.boneIndex] = current + weightedValue;
                else
                    boneWeightAccumulator.Add(influence.boneIndex, weightedValue);
            }
        }

        private BoneWeight1[] BuildNormalizedBoneWeights(float threshold, int maxInfluences)
        {
            boneWeightSortBuffer.Clear();
            foreach (KeyValuePair<int, double> pair in boneWeightAccumulator)
            {
                if (pair.Value <= threshold || double.IsNaN(pair.Value) || double.IsInfinity(pair.Value))
                    continue;

                boneWeightSortBuffer.Add(pair);
            }

            boneWeightSortBuffer.Sort(CompareAccumulatedBoneWeightsDescending);
            int count = Math.Min(Math.Max(0, maxInfluences), boneWeightSortBuffer.Count);
            if (count == 0)
                return Array.Empty<BoneWeight1>();

            double sum = 0.0;
            for (int i = 0; i < count; i++)
                sum += boneWeightSortBuffer[i].Value;

            if (sum <= double.Epsilon || double.IsNaN(sum) || double.IsInfinity(sum))
                return Array.Empty<BoneWeight1>();

            double invSum = 1.0 / sum;
            var result = new BoneWeight1[count];
            float normalizedSum = 0f;
            for (int i = 0; i < count; i++)
            {
                KeyValuePair<int, double> influence = boneWeightSortBuffer[i];
                float normalizedWeight = (float)(influence.Value * invSum);
                result[i] = new BoneWeight1 { boneIndex = influence.Key, weight = normalizedWeight };
                normalizedSum += normalizedWeight;
            }

            BoneWeight1 strongest = result[0];
            strongest.weight += 1f - normalizedSum;
            if (strongest.weight <= 0f || float.IsNaN(strongest.weight) || float.IsInfinity(strongest.weight))
                return Array.Empty<BoneWeight1>();
            result[0] = strongest;
            return result;
        }

        private static int CompareBoneWeightsDescending(BoneWeight1 x, BoneWeight1 y)
        {
            int weightComparison = y.weight.CompareTo(x.weight);
            return weightComparison != 0 ? weightComparison : x.boneIndex.CompareTo(y.boneIndex);
        }

        private static int CompareAccumulatedBoneWeightsDescending(KeyValuePair<int, double> x, KeyValuePair<int, double> y)
        {
            int weightComparison = y.Value.CompareTo(x.Value);
            return weightComparison != 0 ? weightComparison : x.Key.CompareTo(y.Key);
        }

        private static bool TryGetStrongestInfluence(BoneWeight1[] weights0, BoneWeight1[] weights1, BoneWeight1[] weights2, out BoneWeight1 strongest)
        {
            strongest = new BoneWeight1();
            float strongestWeight = 0f;
            FindStrongestInfluence(weights0, ref strongest, ref strongestWeight);
            FindStrongestInfluence(weights1, ref strongest, ref strongestWeight);
            FindStrongestInfluence(weights2, ref strongest, ref strongestWeight);
            return strongestWeight > 0f;
        }

        private static void FindStrongestInfluence(BoneWeight1[] weights, ref BoneWeight1 strongest, ref float strongestWeight)
        {
            if (weights == null)
                return;

            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i].weight > strongestWeight)
                {
                    strongest = weights[i];
                    strongestWeight = weights[i].weight;
                }
            }
        }
        #endregion

        #region Flipped
        private bool FlippedPrepared(ref Vector3d p, bool firstSide)
        {
            int count = firstSide ? collapseFlipCount0 : collapseFlipCount1;
            int[] triangleBuffer = firstSide
                ? collapseFlipTriangleBuffer0
                : collapseFlipTriangleBuffer1;
            int[] vertexBuffer0 = firstSide
                ? collapseFlipVertexBuffer00
                : collapseFlipVertexBuffer10;
            int[] vertexBuffer1 = firstSide
                ? collapseFlipVertexBuffer01
                : collapseFlipVertexBuffer11;
            Triangle[] triangleData = triangles.Data;
            Vertex[] vertexData = vertices.Data;
            double minimumNormalDot = cachedMinimumNormalDot;
            const double collinearDotSqr = 0.998001;

            for (int k = 0; k < count; k++)
            {
                ref Triangle triangle = ref triangleData[triangleBuffer[k]];
                if (triangle.deleted)
                    continue;

                ref Vector3d point1 = ref vertexData[vertexBuffer0[k]].p;
                ref Vector3d point2 = ref vertexData[vertexBuffer1[k]].p;
                double d1x = point1.x - p.x;
                double d1y = point1.y - p.y;
                double d1z = point1.z - p.z;
                double d2x = point2.x - p.x;
                double d2y = point2.y - p.y;
                double d2z = point2.z - p.z;
                double length1Sqr = d1x * d1x + d1y * d1y + d1z * d1z;
                double length2Sqr = d2x * d2x + d2y * d2y + d2z * d2z;
                double lengthProduct = length1Sqr * length2Sqr;
                if (lengthProduct <= 1e-60 ||
                    double.IsNaN(lengthProduct) || double.IsInfinity(lengthProduct))
                {
                    return true;
                }

                double edgeDot = d1x * d2x + d1y * d2y + d1z * d2z;
                if (edgeDot * edgeDot > collinearDotSqr * lengthProduct)
                    return true;

                double nx = d1y * d2z - d1z * d2y;
                double ny = d1z * d2x - d1x * d2z;
                double nz = d1x * d2y - d1y * d2x;
                double normalLengthSqr = nx * nx + ny * ny + nz * nz;
                if (normalLengthSqr <= 1e-60 ||
                    double.IsNaN(normalLengthSqr) || double.IsInfinity(normalLengthSqr))
                {
                    return true;
                }

                double inverseNormalLength = 1.0 / Math.Sqrt(normalLengthSqr);
                double normalDot =
                    (nx * triangle.n.x + ny * triangle.n.y + nz * triangle.n.z) *
                    inverseNormalLength;
                if (normalDot < minimumNormalDot)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Check if a triangle flips when this edge is removed
        /// </summary>
        private bool Flipped(ref Vector3d p, int i0, int i1, ref Vertex v0, bool[] deleted)
        {
            int tcount = v0.tcount;
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            Vertex[] vertexData = vertices.Data;
            double minimumNormalDot = cachedMinimumNormalDot;
            const double collinearDotSqr = 0.998001;
            int refStart = v0.tstart;
            for (int k = 0; k < tcount; k++)
            {
                Ref r = referenceData[refStart + k];
                ref Triangle triangle = ref triangleData[r.tid];
                if (triangle.deleted)
                    continue;

                int id1;
                int id2;
                if (r.tvertex == 0)
                {
                    id1 = triangle.v1;
                    id2 = triangle.v2;
                }
                else if (r.tvertex == 1)
                {
                    id1 = triangle.v2;
                    id2 = triangle.v0;
                }
                else
                {
                    id1 = triangle.v0;
                    id2 = triangle.v1;
                }

                if (id1 == i1 || id2 == i1)
                {
                    deleted[k] = true;
                    continue;
                }

                ref Vector3d point1 = ref vertexData[id1].p;
                ref Vector3d point2 = ref vertexData[id2].p;
                double d1x = point1.x - p.x;
                double d1y = point1.y - p.y;
                double d1z = point1.z - p.z;
                double d2x = point2.x - p.x;
                double d2y = point2.y - p.y;
                double d2z = point2.z - p.z;
                double length1Sqr = d1x * d1x + d1y * d1y + d1z * d1z;
                double length2Sqr = d2x * d2x + d2y * d2y + d2z * d2z;
                double lengthProduct = length1Sqr * length2Sqr;
                if (lengthProduct <= 1e-60 ||
                    double.IsNaN(lengthProduct) || double.IsInfinity(lengthProduct))
                {
                    return true;
                }

                double edgeDot = d1x * d2x + d1y * d2y + d1z * d2z;
                if (edgeDot * edgeDot > collinearDotSqr * lengthProduct)
                    return true;

                double nx = d1y * d2z - d1z * d2y;
                double ny = d1z * d2x - d1x * d2z;
                double nz = d1x * d2y - d1y * d2x;
                double normalLengthSqr = nx * nx + ny * ny + nz * nz;
                if (normalLengthSqr <= 1e-60 ||
                    double.IsNaN(normalLengthSqr) || double.IsInfinity(normalLengthSqr))
                {
                    return true;
                }

                double inverseNormalLength = 1.0 / Math.Sqrt(normalLengthSqr);
                deleted[k] = false;
                double normalDot =
                    (nx * triangle.n.x + ny * triangle.n.y + nz * triangle.n.z) *
                    inverseNormalLength;
                if (normalDot < minimumNormalDot)
                    return true;
            }

            return false;
        }
        #endregion

        #region Update Triangles
        /// <summary>
        /// Update triangle connections and edge error after a edge is collapsed.
        /// </summary>
        private void UpdateTriangles(int i0, int ia0, ref Vertex v, ResizableArray<bool> deleted, ref int deletedTriangles)
        {
            Vector3d p;
            int tcount = v.tcount;
            Ref[] sourceReferences = refs.Data;
            bool[] deletedData = deleted.Data;
            int refStart = v.tstart;

            int writeStart = refs.Length;
            refs.Resize(writeStart + tcount);
            Ref[] destinationReferences = refs.Data;
            Triangle[] triangleData = triangles.Data;
            Vertex[] vertexData = vertices.Data;
            int writeIndex = writeStart;

            for (int k = 0; k < tcount; k++)
            {
                Ref r = sourceReferences[refStart + k];
                int tid = r.tid;
                ref Triangle triangle = ref triangleData[tid];
                if (triangle.deleted)
                    continue;

                if (deletedData[k])
                {
                    triangle.deleted = true;
                    ++deletedTriangles;
                    continue;
                }

                if (r.tvertex == 0)
                {
                    triangle.v0 = i0;
                    if (ia0 != -1)
                        triangle.va0 = ia0;
                }
                else if (r.tvertex == 1)
                {
                    triangle.v1 = i0;
                    if (ia0 != -1)
                        triangle.va1 = ia0;
                }
                else
                {
                    triangle.v2 = i0;
                    if (ia0 != -1)
                        triangle.va2 = ia0;
                }

                triangle.dirty = true;
                ref Vertex edgeVertex0 = ref vertexData[triangle.v0];
                ref Vertex edgeVertex1 = ref vertexData[triangle.v1];
                ref Vertex edgeVertex2 = ref vertexData[triangle.v2];
                triangle.err0 = CalculateConstrainedEdgeError(
                    triangle.v0, triangle.v1, ref edgeVertex0, ref edgeVertex1, out p);
                triangle.err1 = CalculateConstrainedEdgeError(
                    triangle.v1, triangle.v2, ref edgeVertex1, ref edgeVertex2, out p);
                triangle.err2 = CalculateConstrainedEdgeError(
                    triangle.v2, triangle.v0, ref edgeVertex2, ref edgeVertex0, out p);
                triangle.err3 = MathHelper.Min(triangle.err0, triangle.err1, triangle.err2);
                destinationReferences[writeIndex++] = r;
            }

            refs.Resize(writeIndex);
        }
        #endregion

        #region Interpolate Vertex Attributes
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsValidAttributeIndex(int index)
        {
            return index >= 0 && index < vertices.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool AreAttributeIndicesValid(int dst, int i0, int i1, int i2)
        {
            return IsValidAttributeIndex(dst) &&
                   IsValidAttributeIndex(i0) &&
                   IsValidAttributeIndex(i1) &&
                   IsValidAttributeIndex(i2);
        }

        private void InterpolateVertexAttributes(int dst, int i0, int i1, int i2, ref Vector3 barycentricCoord)
        {
            if (!AreAttributeIndicesValid(dst, i0, i1, i2))
            {
                throw new InvalidOperationException(string.Format(
                    "Cannot interpolate vertex attributes because one or more indices are invalid. Destination {0}, sources [{1}, {2}, {3}], attribute vertex count {4}.",
                    dst, i0, i1, i2, vertices.Length));
            }

            SanitizeBarycentricCoords(ref barycentricCoord);

            if (vertNormals != null)
            {
                Vector3 normal = (vertNormals[i0] * barycentricCoord.x) + (vertNormals[i1] * barycentricCoord.y) + (vertNormals[i2] * barycentricCoord.z);
                vertNormals[dst] = normal.sqrMagnitude > 1e-20f ? normal.normalized : vertNormals[i0];
            }
            if (vertTangents != null)
            {
                vertTangents[dst] = NormalizeTangent(
                    (vertTangents[i0] * barycentricCoord.x) + (vertTangents[i1] * barycentricCoord.y) + (vertTangents[i2] * barycentricCoord.z),
                    vertTangents[i0],
                    vertNormals != null ? vertNormals[dst] : Vector3.zero);
            }
            if (vertUV2D != null)
            {
                for (int i = 0; i < UVChannelCount; i++)
                {
                    var vertUV = vertUV2D[i];
                    if (vertUV != null)
                        vertUV[dst] = (vertUV[i0] * barycentricCoord.x) + (vertUV[i1] * barycentricCoord.y) + (vertUV[i2] * barycentricCoord.z);
                }
            }
            if (vertUV3D != null)
            {
                for (int i = 0; i < UVChannelCount; i++)
                {
                    var vertUV = vertUV3D[i];
                    if (vertUV != null)
                        vertUV[dst] = (vertUV[i0] * barycentricCoord.x) + (vertUV[i1] * barycentricCoord.y) + (vertUV[i2] * barycentricCoord.z);
                }
            }
            if (vertUV4D != null)
            {
                for (int i = 0; i < UVChannelCount; i++)
                {
                    var vertUV = vertUV4D[i];
                    if (vertUV != null)
                        vertUV[dst] = (vertUV[i0] * barycentricCoord.x) + (vertUV[i1] * barycentricCoord.y) + (vertUV[i2] * barycentricCoord.z);
                }
            }
            if (vertColors != null)
            {
                vertColors[dst] = (vertColors[i0] * barycentricCoord.x) + (vertColors[i1] * barycentricCoord.y) + (vertColors[i2] * barycentricCoord.z);
            }
            if (blendShapes != null)
            {
                for (int i = 0; i < blendShapes.Length; i++)
                    blendShapes[i].InterpolateVertexAttributes(dst, i0, i1, i2, ref barycentricCoord);
            }

            InterpolateBoneWeights(dst, i0, i1, i2, ref barycentricCoord);
        }
        #endregion

        #region Attribute Seam Comparison
        private bool AreUVsTheSame(int channel, int indexA, int indexB)
        {
            if (vertUV2D != null)
            {
                var vertUV = vertUV2D[channel];
                if (vertUV != null)
                    return vertUV[indexA] == vertUV[indexB];
            }

            if (vertUV3D != null)
            {
                var vertUV = vertUV3D[channel];
                if (vertUV != null)
                    return vertUV[indexA] == vertUV[indexB];
            }

            if (vertUV4D != null)
            {
                var vertUV = vertUV4D[channel];
                if (vertUV != null)
                    return vertUV[indexA] == vertUV[indexB];
            }

            return true;
        }

        private bool AreAllUVsTheSame(int indexA, int indexB)
        {
            for (int channel = 0; channel < UVChannelCount; channel++)
            {
                if (!AreUVsTheSame(channel, indexA, indexB))
                    return false;
            }
            return true;
        }

        private bool AreBoneWeightsTheSame(int indexA, int indexB)
        {
            if (vertBoneWeights == null)
                return true;

            BoneWeight1[] weightsA = vertBoneWeights[indexA] ?? Array.Empty<BoneWeight1>();
            BoneWeight1[] weightsB = vertBoneWeights[indexB] ?? Array.Empty<BoneWeight1>();
            if (weightsA.Length != weightsB.Length)
                return false;

            const float tolerance = 0.00001f;
            for (int i = 0; i < weightsA.Length; i++)
            {
                if (weightsA[i].boneIndex != weightsB[i].boneIndex || Math.Abs(weightsA[i].weight - weightsB[i].weight) > tolerance)
                    return false;
            }
            return true;
        }

        private bool HaveSameSubMeshMembership(int indexA, int indexB)
        {
            subMeshHashSet1.Clear();
            subMeshHashSet2.Clear();

            Vertex vertexA = vertices[indexA];
            Vertex vertexB = vertices[indexB];
            for (int i = 0; i < vertexA.tcount; i++)
                subMeshHashSet1.Add(triangles[refs[vertexA.tstart + i].tid].subMeshIndex);
            for (int i = 0; i < vertexB.tcount; i++)
                subMeshHashSet2.Add(triangles[refs[vertexB.tstart + i].tid].subMeshIndex);

            return subMeshHashSet1.SetEquals(subMeshHashSet2);
        }

        private void MarkSubMeshBoundaryEdges()
        {
            var edgeSubMeshes = new Dictionary<ulong, int>(triangles.Length * 2);
            var triangleData = triangles.Data;
            var vertexData = vertices.Data;

            for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex++)
            {
                Triangle triangle = triangleData[triangleIndex];
                MarkSubMeshBoundaryEdge(triangle.v0, triangle.v1, triangle.subMeshIndex, edgeSubMeshes, vertexData);
                MarkSubMeshBoundaryEdge(triangle.v1, triangle.v2, triangle.subMeshIndex, edgeSubMeshes, vertexData);
                MarkSubMeshBoundaryEdge(triangle.v2, triangle.v0, triangle.subMeshIndex, edgeSubMeshes, vertexData);
            }
        }

        private static void MarkSubMeshBoundaryEdge(int indexA, int indexB, int subMeshIndex, Dictionary<ulong, int> edgeSubMeshes, Vertex[] vertexData)
        {
            uint min = (uint)Math.Min(indexA, indexB);
            uint max = (uint)Math.Max(indexA, indexB);
            ulong key = ((ulong)min << 32) | max;

            int existingSubMesh;
            if (edgeSubMeshes.TryGetValue(key, out existingSubMesh))
            {
                if (existingSubMesh != subMeshIndex)
                {
                    vertexData[indexA].subMeshEdge = true;
                    vertexData[indexB].subMeshEdge = true;
                }
            }
            else
            {
                edgeSubMeshes.Add(key, subMeshIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetAxisValue(ref Vector3d point, int axis)
        {
            return axis == 0 ? point.x : (axis == 1 ? point.y : point.z);
        }
        #endregion

        #region Topology Safety
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TriangleContainsVertex(ref Triangle triangle, int vertexIndex)
        {
            return triangle.v0 == vertexIndex || triangle.v1 == vertexIndex || triangle.v2 == vertexIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetTriangleOppositeVertex(ref Triangle triangle, int vertex0, int vertex1)
        {
            if (triangle.v0 != vertex0 && triangle.v0 != vertex1) return triangle.v0;
            if (triangle.v1 != vertex0 && triangle.v1 != vertex1) return triangle.v1;
            if (triangle.v2 != vertex0 && triangle.v2 != vertex1) return triangle.v2;
            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetTriangleFanEdge(ref Triangle triangle, int centerVertex, out int other0, out int other1)
        {
            other0 = -1;
            other1 = -1;
            if (triangle.v0 != centerVertex)
                other0 = triangle.v0;
            if (triangle.v1 != centerVertex)
            {
                if (other0 < 0) other0 = triangle.v1;
                else other1 = triangle.v1;
            }
            if (triangle.v2 != centerVertex)
            {
                if (other0 < 0) other0 = triangle.v2;
                else other1 = triangle.v2;
            }
            return other0 >= 0 && other1 >= 0 && other0 != other1;
        }

        private int BeginTopologyVertexVisit()
        {
            int vertexCount = vertices != null ? vertices.Length : 0;
            if (topologyVertexVisitStamps.Length < vertexCount)
            {
                Array.Resize(
                    ref topologyVertexVisitStamps,
                    Math.Max(vertexCount, topologyVertexVisitStamps.Length * 2 + 32));
            }

            if (topologyVertexVisitStamp == int.MaxValue)
            {
                Array.Clear(topologyVertexVisitStamps, 0, topologyVertexVisitStamps.Length);
                topologyVertexVisitStamp = 1;
            }
            else
            {
                topologyVertexVisitStamp++;
                if (topologyVertexVisitStamp == 0)
                    topologyVertexVisitStamp = 1;
            }

            return topologyVertexVisitStamp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryMarkTopologyVertexVisited(int vertexIndex, int visitStamp)
        {
            if ((uint)vertexIndex >= (uint)vertices.Length)
                return false;
            if (topologyVertexVisitStamps[vertexIndex] == visitStamp)
                return false;
            topologyVertexVisitStamps[vertexIndex] = visitStamp;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureTopologyScratchCapacity(int neighborCapacity, int fanEdgeCapacity)
        {
            if (topologyNeighborBuffer0.Length < neighborCapacity)
            {
                int capacity = Math.Max(neighborCapacity, topologyNeighborBuffer0.Length * 2 + 16);
                Array.Resize(ref topologyNeighborBuffer0, capacity);
            }
            if (topologyTriangleFanKeyBuffer.Length < fanEdgeCapacity)
            {
                int capacity = Math.Max(fanEdgeCapacity, topologyTriangleFanKeyBuffer.Length * 2 + 16);
                Array.Resize(ref topologyTriangleFanKeyBuffer, capacity);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryAddTopologyNeighborDirect(
            int candidate,
            int centerVertex,
            int excludedVertex,
            int visitStamp,
            int[] destination,
            ref int destinationCount)
        {
            if (candidate == centerVertex || candidate == excludedVertex)
                return;
            if (!TryMarkTopologyVertexVisited(candidate, visitStamp))
                return;
            if (destination != null)
                destination[destinationCount++] = candidate;
        }

        private int CollectTopologyNeighborsDirect(
            ref Vertex vertex,
            int excludedVertex,
            int visitStamp,
            int[] destination)
        {
            int destinationCount = 0;
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            int end = vertex.tstart + vertex.tcount;
            for (int refIndex = vertex.tstart; refIndex < end; refIndex++)
            {
                int triangleIndex = referenceData[refIndex].tid;
                if ((uint)triangleIndex >= (uint)triangles.Length)
                    continue;

                ref Triangle triangle = ref triangleData[triangleIndex];
                if (triangle.deleted)
                    continue;

                TryAddTopologyNeighborDirect(
                    triangle.v0, vertex.index, excludedVertex, visitStamp, destination, ref destinationCount);
                TryAddTopologyNeighborDirect(
                    triangle.v1, vertex.index, excludedVertex, visitStamp, destination, ref destinationCount);
                TryAddTopologyNeighborDirect(
                    triangle.v2, vertex.index, excludedVertex, visitStamp, destination, ref destinationCount);
            }
            return destinationCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ContainsFanEdgeDirect(ulong[] fanEdges, int fanEdgeCount, ulong key)
        {
            for (int i = 0; i < fanEdgeCount; i++)
            {
                if (fanEdges[i] == key)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Enforces the triangle-mesh link condition before an edge collapse. Besides rejecting
        /// non-manifold edges, this catches collapses that would create duplicate faces or pinch a
        /// surface apart. Those invalid collapses are a common source of visible eye/face cracks at
        /// extremely low targets.
        /// </summary>
        private bool PreservesManifoldTopology(int vertex0, int vertex1, ref Vertex vert0, ref Vertex vert1)
        {
            Ref[] referenceData = refs.Data;
            Triangle[] triangleData = triangles.Data;
            EnsureTopologyScratchCapacity(Math.Max(4, vert0.tcount * 2), Math.Max(4, vert0.tcount));

            int fanEdgeCount = 0;
            int sharedTriangleCount = 0;
            int opposite0 = -1;
            int opposite1 = -1;
            for (int k = 0; k < vert0.tcount; k++)
            {
                int triangleIndex = referenceData[vert0.tstart + k].tid;
                if ((uint)triangleIndex >= (uint)triangles.Length)
                    continue;

                ref Triangle triangle = ref triangleData[triangleIndex];
                if (triangle.deleted)
                    continue;

                if (TriangleContainsVertex(ref triangle, vertex1))
                {
                    int opposite = GetTriangleOppositeVertex(ref triangle, vertex0, vertex1);
                    if (opposite < 0)
                        return false;

                    sharedTriangleCount++;
                    if (sharedTriangleCount == 1)
                    {
                        opposite0 = opposite;
                    }
                    else if (sharedTriangleCount == 2)
                    {
                        if (opposite == opposite0)
                            return false;
                        opposite1 = opposite;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    int other0, other1;
                    if (!TryGetTriangleFanEdge(ref triangle, vertex0, out other0, out other1))
                        return false;
                    topologyTriangleFanKeyBuffer[fanEdgeCount++] = MakeUndirectedEdgeKey(other0, other1);
                }
            }

            if (sharedTriangleCount < 1)
                return false;

            for (int k = 0; k < vert1.tcount; k++)
            {
                int triangleIndex = referenceData[vert1.tstart + k].tid;
                if ((uint)triangleIndex >= (uint)triangles.Length)
                    continue;

                ref Triangle triangle = ref triangleData[triangleIndex];
                if (triangle.deleted || TriangleContainsVertex(ref triangle, vertex0))
                    continue;

                int other0, other1;
                if (!TryGetTriangleFanEdge(ref triangle, vertex1, out other0, out other1))
                    return false;
                if (ContainsFanEdgeDirect(
                    topologyTriangleFanKeyBuffer,
                    fanEdgeCount,
                    MakeUndirectedEdgeKey(other0, other1)))
                {
                    return false;
                }
            }

            int firstVisitStamp = BeginTopologyVertexVisit();
            int firstNeighborCount = CollectTopologyNeighborsDirect(
                ref vert0, vertex1, firstVisitStamp, topologyNeighborBuffer0);
            int secondVisitStamp = BeginTopologyVertexVisit();
            CollectTopologyNeighborsDirect(ref vert1, vertex0, secondVisitStamp, null);

            int commonNeighborCount = 0;
            for (int i = 0; i < firstNeighborCount; i++)
            {
                int neighbor = topologyNeighborBuffer0[i];
                if (topologyVertexVisitStamps[neighbor] != secondVisitStamp)
                    continue;

                commonNeighborCount++;
                if (neighbor != opposite0 && neighbor != opposite1)
                    return false;
            }

            return commonNeighborCount == sharedTriangleCount;
        }
        #endregion

        #region Remove Vertex Pass
        /// <summary>
        /// Remove vertices and mark deleted triangles
        /// </summary>
        private void RemoveVertexPass(int startTrisCount, int targetTrisCount, double threshold, ResizableArray<bool> deleted0, ResizableArray<bool> deleted1, ref int deletedTris, CancellationToken cancellationToken)
        {
            Triangle[] triangleData = triangles.Data;
            int triangleCount = triangles.Length;
            Vertex[] vertexData = vertices.Data;

            bool preserveBorderEdges = simplificationOptions.PreserveBorderEdges;
            bool preserveUVSeamEdges = simplificationOptions.PreserveUVSeamEdges;
            bool preserveUVFoldoverEdges = simplificationOptions.PreserveUVFoldoverEdges;
            bool preserveSubMeshEdges = simplificationOptions.PreserveSubMeshEdges;
            bool preserveBoneWeightSeams = simplificationOptions.PreserveBoneWeightSeams;

            CollapsePlacement placement;
            int sweepIndex = collapseSweepIndex++;
            bool lowIndexFirst = (sweepIndex & 1) == 0;
            for (int scanIndex = 0; scanIndex < triangleCount; scanIndex++)
            {
                int pairIndex = scanIndex >> 1;
                bool useLowIndex = ((scanIndex & 1) == 0) == lowIndexFirst;
                int tid = useLowIndex ? pairIndex : triangleCount - 1 - pairIndex;

                if ((scanIndex & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                ref Triangle triangle = ref triangleData[tid];
                if (triangle.dirty || triangle.deleted || triangle.err3 > threshold)
                    continue;

                for (int edgeIndex = 0; edgeIndex < TriangleEdgeCount; edgeIndex++)
                {
                    double edgeError;
                    int i0, i1, i2;
                    int ia0, ia1, ia2;
                    if (edgeIndex == 0)
                    {
                        edgeError = triangle.err0;
                        i0 = triangle.v0;
                        i1 = triangle.v1;
                        i2 = triangle.v2;
                        ia0 = triangle.va0;
                        ia1 = triangle.va1;
                        ia2 = triangle.va2;
                    }
                    else if (edgeIndex == 1)
                    {
                        edgeError = triangle.err1;
                        i0 = triangle.v1;
                        i1 = triangle.v2;
                        i2 = triangle.v0;
                        ia0 = triangle.va1;
                        ia1 = triangle.va2;
                        ia2 = triangle.va0;
                    }
                    else
                    {
                        edgeError = triangle.err2;
                        i0 = triangle.v2;
                        i1 = triangle.v0;
                        i2 = triangle.v1;
                        ia0 = triangle.va2;
                        ia1 = triangle.va0;
                        ia2 = triangle.va1;
                    }

                    if (edgeError > threshold)
                        continue;
                    cancellationToken.ThrowIfCancellationRequested();

                    ref Vertex vertex0 = ref vertexData[i0];
                    ref Vertex vertex1 = ref vertexData[i1];

                    if (vertex0.borderEdge != vertex1.borderEdge ||
                        vertex0.uvSeamEdge != vertex1.uvSeamEdge ||
                        vertex0.uvFoldoverEdge != vertex1.uvFoldoverEdge ||
                        vertex0.subMeshEdge != vertex1.subMeshEdge ||
                        vertex0.boneWeightSeamEdge != vertex1.boneWeightSeamEdge)
                    {
                        continue;
                    }
                    if ((preserveBorderEdges && vertex0.borderEdge) ||
                        (preserveUVSeamEdges && vertex0.uvSeamEdge) ||
                        (preserveUVFoldoverEdges && vertex0.uvFoldoverEdge) ||
                        (preserveSubMeshEdges && vertex0.subMeshEdge) ||
                        (preserveBoneWeightSeams && vertex0.boneWeightSeamEdge))
                    {
                        continue;
                    }

                    if (!PreservesManifoldTopology(i0, i1, ref vertex0, ref vertex1))
                        continue;

                    deleted0.Resize(vertex0.tcount);
                    deleted1.Resize(vertex1.tcount);

                    if (!TryResolveCollapsePlacement(
                        i0,
                        i1,
                        i2,
                        ia0,
                        ia1,
                        ia2,
                        ref vertex0,
                        ref vertex1,
                        deleted0.Data,
                        deleted1.Data,
                        out placement))
                    {
                        continue;
                    }

                    if (!AreAttributeIndicesValid(
                        ia0,
                        placement.attribute0,
                        placement.attribute1,
                        placement.attribute2))
                    {
                        invalidAttributePlacementRejections++;
                        continue;
                    }

                    vertex0.p = placement.position;
                    vertex0.q += vertex1.q;
                    vertex0.referenceComponent = MergeReferenceComponent(
                        vertex0.referenceComponent,
                        vertex1.referenceComponent);
                    vertex0.referenceBoundaryComponent = MergeReferenceComponent(
                        vertex0.referenceBoundaryComponent,
                        vertex1.referenceBoundaryComponent);
                    vertex0.referenceTriangleHint = placement.ReferenceTriangleHint >= 0
                        ? placement.ReferenceTriangleHint
                        : MergeReferenceHint(
                            vertex0.referenceTriangleHint,
                            vertex1.referenceTriangleHint);
                    vertex0.referenceBoundarySegmentHint = placement.ReferenceBoundaryHint >= 0
                        ? placement.ReferenceBoundaryHint
                        : MergeReferenceHint(
                            vertex0.referenceBoundarySegmentHint,
                            vertex1.referenceBoundarySegmentHint);

                    InterpolateVertexAttributes(
                        ia0,
                        placement.attribute0,
                        placement.attribute1,
                        placement.attribute2,
                        ref placement.barycentric);

                    if (vertex0.uvSeamEdge || vertex0.uvFoldoverEdge ||
                        vertex0.subMeshEdge || vertex0.boneWeightSeamEdge)
                    {
                        ia0 = -1;
                    }

                    int tstart = refs.Length;
                    UpdateTriangles(i0, ia0, ref vertex0, deleted0, ref deletedTris);
                    UpdateTriangles(i0, ia0, ref vertex1, deleted1, ref deletedTris);

                    int tcount = refs.Length - tstart;
                    if (tcount <= vertex0.tcount)
                    {
                        if (tcount > 0)
                        {
                            Ref[] refsArr = refs.Data;
                            Array.Copy(refsArr, tstart, refsArr, vertex0.tstart, tcount);
                        }
                    }
                    else
                    {
                        vertex0.tstart = tstart;
                    }

                    vertex0.tcount = tcount;
                    break;
                }

                if ((startTrisCount - deletedTris) <= targetTrisCount)
                    break;
            }
        }
        #endregion

        private int BeginBoundaryNeighborVisit(int vertexCount)
        {
            if (boundaryNeighborVisitStamps.Length < vertexCount)
            {
                int capacity = Math.Max(
                    vertexCount,
                    boundaryNeighborVisitStamps.Length * 2 + 32);
                Array.Resize(ref boundaryNeighborVisitStamps, capacity);
                Array.Resize(ref boundaryNeighborCounts, capacity);
            }

            if (boundaryNeighborVisitStamp == int.MaxValue)
            {
                Array.Clear(boundaryNeighborVisitStamps, 0, boundaryNeighborVisitStamps.Length);
                boundaryNeighborVisitStamp = 1;
            }
            else
            {
                boundaryNeighborVisitStamp++;
                if (boundaryNeighborVisitStamp == 0)
                    boundaryNeighborVisitStamp = 1;
            }

            return boundaryNeighborVisitStamp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CountBoundaryNeighbor(
            int vertexIndex,
            int visitStamp,
            List<int> touchedVertices)
        {
            if ((uint)vertexIndex >= (uint)vertices.Length)
                return;

            if (boundaryNeighborVisitStamps[vertexIndex] != visitStamp)
            {
                boundaryNeighborVisitStamps[vertexIndex] = visitStamp;
                boundaryNeighborCounts[vertexIndex] = 1;
                touchedVertices.Add(vertexIndex);
            }
            else
            {
                boundaryNeighborCounts[vertexIndex]++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SymmetricMatrix CalculateTrianglePlaneQuadric(
            ref Triangle triangle,
            Vertex[] vertexData,
            out Vector3d normal)
        {
            Vector3d p0 = vertexData[triangle.v0].p;
            Vector3d p1 = vertexData[triangle.v1].p;
            Vector3d p2 = vertexData[triangle.v2].p;
            Vector3d p10 = p1 - p0;
            Vector3d p20 = p2 - p0;
            Vector3d.Cross(ref p10, ref p20, out normal);
            normal.Normalize();
            return new SymmetricMatrix(
                normal.x,
                normal.y,
                normal.z,
                -Vector3d.Dot(ref normal, ref p0));
        }

        #region Update Mesh
        /// <summary>
        /// Compact triangles, compute edge error and build reference list.
        /// </summary>
        /// <param name="iteration">The iteration index.</param>
        private void UpdateMesh(int iteration, CancellationToken cancellationToken)
        {
            var triangles = this.triangles.Data;
            var vertices = this.vertices.Data;

            int triangleCount = this.triangles.Length;
            int vertexCount = this.vertices.Length;
            if (iteration > 0)
            {
                int dst = 0;
                for (int i = 0; i < triangleCount; i++)
                {
                    if (!triangles[i].deleted)
                    {
                        if (dst != i)
                        {
                            triangles[dst] = triangles[i];
                            triangles[dst].index = dst;
                        }
                        dst++;
                    }
                }
                this.triangles.Resize(dst);
                triangles = this.triangles.Data;
                triangleCount = dst;
            }

            UpdateReferences();

            if (iteration == 0)
            {
                var refs = this.refs.Data;

                List<int> touchedVertices = boundaryTouchedVertexBuffer;
                if (CanUseParallelCheapLoop(vertexCount))
                {
                    Parallel.For(
                        0,
                        vertexCount,
                        CreateParallelOptions(cancellationToken),
                        i =>
                        {
                            Vertex vertex = vertices[i];
                            vertex.borderEdge = false;
                            vertex.uvSeamEdge = false;
                            vertex.uvFoldoverEdge = false;
                            vertex.subMeshEdge = false;
                            vertex.boneWeightSeamEdge = false;
                            vertices[i] = vertex;
                        });
                }
                else
                {
                    for (int i = 0; i < vertexCount; i++)
                    {
                        vertices[i].borderEdge = false;
                        vertices[i].uvSeamEdge = false;
                        vertices[i].uvFoldoverEdge = false;
                        vertices[i].subMeshEdge = false;
                        vertices[i].boneWeightSeamEdge = false;
                    }
                }

                int id;
                int borderVertexCount = 0;
                double borderMinX = double.MaxValue;
                double borderMaxX = double.MinValue;
                double borderMinY = double.MaxValue;
                double borderMaxY = double.MinValue;
                double borderMinZ = double.MaxValue;
                double borderMaxZ = double.MinValue;
                var vertexLinkDistanceSqr = simplificationOptions.VertexLinkDistance * simplificationOptions.VertexLinkDistance;
                for (int i = 0; i < vertexCount; i++)
                {
                    int tstart = vertices[i].tstart;
                    int tcount = vertices[i].tcount;
                    int visitStamp = BeginBoundaryNeighborVisit(vertexCount);
                    touchedVertices.Clear();

                    for (int j = 0; j < tcount; j++)
                    {
                        int tid = refs[tstart + j].tid;
                        if ((uint)tid >= (uint)triangleCount)
                            continue;

                        Triangle triangle = triangles[tid];
                        CountBoundaryNeighbor(triangle.v0, visitStamp, touchedVertices);
                        CountBoundaryNeighbor(triangle.v1, visitStamp, touchedVertices);
                        CountBoundaryNeighbor(triangle.v2, visitStamp, touchedVertices);
                    }

                    for (int j = 0; j < touchedVertices.Count; j++)
                    {
                        id = touchedVertices[j];
                        if (boundaryNeighborCounts[id] != 1)
                            continue;

                        vertices[id].borderEdge = true;
                        ++borderVertexCount;

                        if (simplificationOptions.EnableSmartLink)
                        {
                            Vector3d borderPoint = vertices[id].p;
                            if (borderPoint.x < borderMinX) borderMinX = borderPoint.x;
                            if (borderPoint.x > borderMaxX) borderMaxX = borderPoint.x;
                            if (borderPoint.y < borderMinY) borderMinY = borderPoint.y;
                            if (borderPoint.y > borderMaxY) borderMaxY = borderPoint.y;
                            if (borderPoint.z < borderMinZ) borderMinZ = borderPoint.z;
                            if (borderPoint.z > borderMaxZ) borderMaxZ = borderPoint.z;
                        }
                    }
                }

                MarkSubMeshBoundaryEdges();

                if (simplificationOptions.EnableSmartLink)
                {
                    var borderVertices = new BorderVertex[borderVertexCount];
                    int borderIndexCount = 0;
                    double widthX = borderMaxX - borderMinX;
                    double widthY = borderMaxY - borderMinY;
                    double widthZ = borderMaxZ - borderMinZ;
                    int hashAxis = widthY > widthX ? 1 : 0;
                    if (widthZ > (hashAxis == 0 ? widthX : widthY))
                        hashAxis = 2;

                    double borderMin = hashAxis == 0 ? borderMinX : (hashAxis == 1 ? borderMinY : borderMinZ);
                    double borderAreaWidth = hashAxis == 0 ? widthX : (hashAxis == 1 ? widthY : widthZ);
                    bool hasHashRange = borderAreaWidth > 1e-30;

                    for (int i = 0; i < vertexCount; i++)
                    {
                        if (vertices[i].borderEdge)
                        {
                            int vertexHash = 0;
                            if (hasHashRange)
                            {
                                Vector3d point = vertices[i].p;
                                double normalized = (GetAxisValue(ref point, hashAxis) - borderMin) / borderAreaWidth;
                                normalized = Math.Max(0.0, Math.Min(1.0, normalized));
                                vertexHash = (int)Math.Round(normalized * int.MaxValue);
                            }

                            borderVertices[borderIndexCount] = new BorderVertex(i, vertexHash);
                            ++borderIndexCount;
                        }
                    }

                    Array.Sort(borderVertices, 0, borderIndexCount, BorderVertexComparer.instance);

                    double vertexLinkDistance = Math.Sqrt(vertexLinkDistanceSqr);
                    int hashMaxDistance;
                    if (!hasHashRange || vertexLinkDistance >= borderAreaWidth)
                    {
                        hashMaxDistance = int.MaxValue;
                    }
                    else
                    {
                        hashMaxDistance = Math.Max((int)Math.Ceiling((vertexLinkDistance / borderAreaWidth) * int.MaxValue), 1);
                    }

                    for (int i = 0; i < borderIndexCount; i++)
                    {
                        int myIndex = borderVertices[i].index;
                        if (myIndex == -1)
                            continue;

                        var myPoint = vertices[myIndex].p;
                        for (int j = i + 1; j < borderIndexCount; j++)
                        {
                            int otherIndex = borderVertices[j].index;
                            if (otherIndex == -1)
                                continue;
                            else if ((borderVertices[j].hash - borderVertices[i].hash) > hashMaxDistance)
                                break;

                            var otherPoint = vertices[otherIndex].p;
                            var sqrX = ((myPoint.x - otherPoint.x) * (myPoint.x - otherPoint.x));
                            var sqrY = ((myPoint.y - otherPoint.y) * (myPoint.y - otherPoint.y));
                            var sqrZ = ((myPoint.z - otherPoint.z) * (myPoint.z - otherPoint.z));
                            var sqrMagnitude = sqrX + sqrY + sqrZ;

                            if (sqrMagnitude <= vertexLinkDistanceSqr)
                            {
                                borderVertices[j].index = -1;
                                vertices[myIndex].borderEdge = false;
                                vertices[otherIndex].borderEdge = false;

                                if (AreAllUVsTheSame(myIndex, otherIndex))
                                {
                                    vertices[myIndex].uvFoldoverEdge = true;
                                    vertices[otherIndex].uvFoldoverEdge = true;
                                }
                                else
                                {
                                    vertices[myIndex].uvSeamEdge = true;
                                    vertices[otherIndex].uvSeamEdge = true;
                                }

                                if (!AreBoneWeightsTheSame(myIndex, otherIndex))
                                {
                                    vertices[myIndex].boneWeightSeamEdge = true;
                                    vertices[otherIndex].boneWeightSeamEdge = true;
                                }

                                if (!HaveSameSubMeshMembership(myIndex, otherIndex))
                                {
                                    vertices[myIndex].subMeshEdge = true;
                                    vertices[otherIndex].subMeshEdge = true;
                                }

                                int otherTriangleCount = vertices[otherIndex].tcount;
                                int otherTriangleStart = vertices[otherIndex].tstart;
                                Ref[] referenceData = refs;
                                for (int k = 0; k < otherTriangleCount; k++)
                                {
                                    Ref r = referenceData[otherTriangleStart + k];
                                    ref Triangle linkedTriangle = ref triangles[r.tid];
                                    if (r.tvertex == 0) linkedTriangle.v0 = myIndex;
                                    else if (r.tvertex == 1) linkedTriangle.v1 = myIndex;
                                    else linkedTriangle.v2 = myIndex;
                                }
                            }
                        }
                    }

                    UpdateReferences();
                }

                if (CanUseParallelCheapLoop(vertexCount))
                {
                    Parallel.For(
                        0,
                        vertexCount,
                        CreateParallelOptions(cancellationToken),
                        i => vertices[i].q = new SymmetricMatrix());
                }
                else
                {
                    for (int i = 0; i < vertexCount; i++)
                        vertices[i].q = new SymmetricMatrix();
                }

                if (CanUseParallelExpensiveLoop(triangleCount))
                {
                    if (triangleQuadricBuffer.Length < triangleCount)
                        Array.Resize(ref triangleQuadricBuffer, Math.Max(triangleCount, triangleQuadricBuffer.Length * 2 + 32));

                    SymmetricMatrix[] quadricData = triangleQuadricBuffer;
                    Parallel.For(
                        0,
                        triangleCount,
                        CreateParallelOptions(cancellationToken),
                        i =>
                        {
                            Triangle triangle = triangles[i];
                            Vector3d normal;
                            quadricData[i] = CalculateTrianglePlaneQuadric(
                                ref triangle, vertices, out normal);
                            triangle.n = normal;
                            triangles[i] = triangle;
                        });

                    for (int i = 0; i < triangleCount; i++)
                    {
                        Triangle triangle = triangles[i];
                        SymmetricMatrix quadric = quadricData[i];
                        vertices[triangle.v0].q += quadric;
                        vertices[triangle.v1].q += quadric;
                        vertices[triangle.v2].q += quadric;
                    }
                }
                else
                {
                    for (int i = 0; i < triangleCount; i++)
                    {
                        Triangle triangle = triangles[i];
                        Vector3d normal;
                        SymmetricMatrix quadric = CalculateTrianglePlaneQuadric(
                            ref triangle, vertices, out normal);
                        triangle.n = normal;
                        triangles[i] = triangle;
                        vertices[triangle.v0].q += quadric;
                        vertices[triangle.v1].q += quadric;
                        vertices[triangle.v2].q += quadric;
                    }
                }

                if (CanUseParallelExpensiveLoop(triangleCount))
                {
                    int requiredErrorValues = checked(triangleCount * 4);
                    if (triangleErrorBuffer.Length < requiredErrorValues)
                    {
                        Array.Resize(
                            ref triangleErrorBuffer,
                            Math.Max(requiredErrorValues, triangleErrorBuffer.Length * 2 + 128));
                    }

                    double[] errorData = triangleErrorBuffer;
                    Parallel.For(
                        0,
                        triangleCount,
                        CreateParallelOptions(cancellationToken),
                        i =>
                        {
                            Vector3d localPosition;
                            Triangle triangle = triangles[i];
                            int errorOffset = i * 4;
                            double error0 = CalculateConstrainedEdgeError(
                                triangle.v0, triangle.v1,
                                ref vertices[triangle.v0], ref vertices[triangle.v1],
                                out localPosition);
                            double error1 = CalculateConstrainedEdgeError(
                                triangle.v1, triangle.v2,
                                ref vertices[triangle.v1], ref vertices[triangle.v2],
                                out localPosition);
                            double error2 = CalculateConstrainedEdgeError(
                                triangle.v2, triangle.v0,
                                ref vertices[triangle.v2], ref vertices[triangle.v0],
                                out localPosition);
                            errorData[errorOffset] = error0;
                            errorData[errorOffset + 1] = error1;
                            errorData[errorOffset + 2] = error2;
                            errorData[errorOffset + 3] = MathHelper.Min(
                                error0, error1, error2);
                        });

                    for (int i = 0; i < triangleCount; i++)
                    {
                        int errorOffset = i * 4;
                        Triangle triangle = triangles[i];
                        triangle.err0 = errorData[errorOffset];
                        triangle.err1 = errorData[errorOffset + 1];
                        triangle.err2 = errorData[errorOffset + 2];
                        triangle.err3 = errorData[errorOffset + 3];
                        triangles[i] = triangle;
                    }
                }
                else
                {
                    Vector3d dummy;
                    for (int i = 0; i < triangleCount; i++)
                    {
                        if ((i & 1023) == 0)
                            cancellationToken.ThrowIfCancellationRequested();

                        Triangle triangle = triangles[i];
                        triangle.err0 = CalculateConstrainedEdgeError(
                            triangle.v0, triangle.v1,
                            ref vertices[triangle.v0], ref vertices[triangle.v1],
                            out dummy);
                        triangle.err1 = CalculateConstrainedEdgeError(
                            triangle.v1, triangle.v2,
                            ref vertices[triangle.v1], ref vertices[triangle.v2],
                            out dummy);
                        triangle.err2 = CalculateConstrainedEdgeError(
                            triangle.v2, triangle.v0,
                            ref vertices[triangle.v2], ref vertices[triangle.v0],
                            out dummy);
                        triangle.err3 = MathHelper.Min(
                            triangle.err0, triangle.err1, triangle.err2);
                        triangles[i] = triangle;
                    }
                }
            }
        }
        #endregion

        #region Update References
        private void UpdateReferences()
        {
            int triangleCount = this.triangles.Length;
            int vertexCount = this.vertices.Length;
            var triangles = this.triangles.Data;
            var vertices = this.vertices.Data;

            for (int i = 0; i < vertexCount; i++)
            {
                vertices[i].tstart = 0;
                vertices[i].tcount = 0;
            }

            for (int i = 0; i < triangleCount; i++)
            {
                ++vertices[triangles[i].v0].tcount;
                ++vertices[triangles[i].v1].tcount;
                ++vertices[triangles[i].v2].tcount;
            }

            int tstart = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                vertices[i].tstart = tstart;
                tstart += vertices[i].tcount;
                vertices[i].tcount = 0;
            }

            this.refs.Resize(tstart);
            var refs = this.refs.Data;
            for (int i = 0; i < triangleCount; i++)
            {
                int v0 = triangles[i].v0;
                int v1 = triangles[i].v1;
                int v2 = triangles[i].v2;
                int start0 = vertices[v0].tstart;
                int count0 = vertices[v0].tcount++;
                int start1 = vertices[v1].tstart;
                int count1 = vertices[v1].tcount++;
                int start2 = vertices[v2].tstart;
                int count2 = vertices[v2].tcount++;

                refs[start0 + count0].Set(i, 0);
                refs[start1 + count1].Set(i, 1);
                refs[start2 + count2].Set(i, 2);
            }
        }
        #endregion

        #region Compact Mesh
        /// <summary>
        /// Finally compact mesh before exiting.
        /// </summary>
        private void CompactMesh()
        {
            int dst = 0;
            var vertices = this.vertices.Data;
            int vertexCount = this.vertices.Length;
            for (int i = 0; i < vertexCount; i++)
            {
                vertices[i].tcount = 0;
            }

            var vertNormals = (this.vertNormals != null ? this.vertNormals.Data : null);
            var vertTangents = (this.vertTangents != null ? this.vertTangents.Data : null);
            var vertUV2D = (this.vertUV2D != null ? this.vertUV2D.Data : null);
            var vertUV3D = (this.vertUV3D != null ? this.vertUV3D.Data : null);
            var vertUV4D = (this.vertUV4D != null ? this.vertUV4D.Data : null);
            var vertColors = (this.vertColors != null ? this.vertColors.Data : null);
            var vertBoneWeights = (this.vertBoneWeights != null ? this.vertBoneWeights.Data : null);
            var blendShapes = (this.blendShapes != null ? this.blendShapes.Data : null);

            int lastSubMeshIndex = -1;
            subMeshOffsets = new int[subMeshCount];

            var triangles = this.triangles.Data;
            int triangleCount = this.triangles.Length;
            for (int i = 0; i < triangleCount; i++)
            {
                var triangle = triangles[i];
                if (!triangle.deleted)
                {
                    if (triangle.va0 != triangle.v0)
                    {
                        int iDest = triangle.va0;
                        int iSrc = triangle.v0;
                        vertices[iDest].p = vertices[iSrc].p;
                        triangle.v0 = triangle.va0;
                    }
                    if (triangle.va1 != triangle.v1)
                    {
                        int iDest = triangle.va1;
                        int iSrc = triangle.v1;
                        vertices[iDest].p = vertices[iSrc].p;
                        triangle.v1 = triangle.va1;
                    }
                    if (triangle.va2 != triangle.v2)
                    {
                        int iDest = triangle.va2;
                        int iSrc = triangle.v2;
                        vertices[iDest].p = vertices[iSrc].p;
                        triangle.v2 = triangle.va2;
                    }
                    int newTriangleIndex = dst++;
                    triangles[newTriangleIndex] = triangle;
                    triangles[newTriangleIndex].index = newTriangleIndex;

                    vertices[triangle.v0].tcount = 1;
                    vertices[triangle.v1].tcount = 1;
                    vertices[triangle.v2].tcount = 1;

                    if (triangle.subMeshIndex > lastSubMeshIndex)
                    {
                        for (int j = lastSubMeshIndex + 1; j < triangle.subMeshIndex; j++)
                        {
                            subMeshOffsets[j] = newTriangleIndex;
                        }
                        subMeshOffsets[triangle.subMeshIndex] = newTriangleIndex;
                        lastSubMeshIndex = triangle.subMeshIndex;
                    }
                }
            }

            triangleCount = dst;
            for (int i = lastSubMeshIndex + 1; i < subMeshCount; i++)
            {
                subMeshOffsets[i] = triangleCount;
            }

            this.triangles.Resize(triangleCount);
            triangles = this.triangles.Data;

            dst = 0;
            var vertexRemap = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                vertexRemap[i] = -1;

            for (int i = 0; i < vertexCount; i++)
            {
                Vertex vert = vertices[i];
                if (vert.tcount <= 0)
                    continue;

                vertexRemap[i] = dst;
                Vertex compactedVertex = vert;
                compactedVertex.index = dst;
                compactedVertex.tstart = 0;
                compactedVertex.tcount = 0;
                vertices[dst] = compactedVertex;

                if (dst != i)
                {
                    if (vertNormals != null) vertNormals[dst] = vertNormals[i];
                    if (vertTangents != null) vertTangents[dst] = vertTangents[i];
                    if (vertUV2D != null)
                    {
                        for (int j = 0; j < UVChannelCount; j++)
                        {
                            var vertUV = vertUV2D[j];
                            if (vertUV != null)
                                vertUV[dst] = vertUV[i];
                        }
                    }
                    if (vertUV3D != null)
                    {
                        for (int j = 0; j < UVChannelCount; j++)
                        {
                            var vertUV = vertUV3D[j];
                            if (vertUV != null)
                                vertUV[dst] = vertUV[i];
                        }
                    }
                    if (vertUV4D != null)
                    {
                        for (int j = 0; j < UVChannelCount; j++)
                        {
                            var vertUV = vertUV4D[j];
                            if (vertUV != null)
                                vertUV[dst] = vertUV[i];
                        }
                    }
                    if (vertColors != null) vertColors[dst] = vertColors[i];
                    if (vertBoneWeights != null) vertBoneWeights[dst] = vertBoneWeights[i];

                    if (blendShapes != null)
                    {
                        for (int shapeIndex = 0; shapeIndex < this.blendShapes.Length; shapeIndex++)
                            blendShapes[shapeIndex].MoveVertexElement(dst, i);
                    }
                }

                dst++;
            }

            for (int i = 0; i < triangleCount; i++)
            {
                Triangle triangle = triangles[i];
                int remapped0 = vertexRemap[triangle.v0];
                int remapped1 = vertexRemap[triangle.v1];
                int remapped2 = vertexRemap[triangle.v2];
                if (remapped0 < 0 || remapped1 < 0 || remapped2 < 0)
                    throw new InvalidOperationException("Mesh compaction encountered a triangle that references a removed vertex.");

                triangle.v0 = remapped0;
                triangle.v1 = remapped1;
                triangle.v2 = remapped2;
                triangle.va0 = remapped0;
                triangle.va1 = remapped1;
                triangle.va2 = remapped2;
                triangles[i] = triangle;
            }

            vertexCount = dst;
            this.vertices.Resize(vertexCount);
            if (vertNormals != null) this.vertNormals.Resize(vertexCount, true);
            if (vertTangents != null) this.vertTangents.Resize(vertexCount, true);
            if (vertUV2D != null) this.vertUV2D.Resize(vertexCount, true);
            if (vertUV3D != null) this.vertUV3D.Resize(vertexCount, true);
            if (vertUV4D != null) this.vertUV4D.Resize(vertexCount, true);
            if (vertColors != null) this.vertColors.Resize(vertexCount, true);
            if (vertBoneWeights != null) this.vertBoneWeights.Resize(vertexCount, true);

            if (blendShapes != null)
            {
                for (int i = 0; i < this.blendShapes.Length; i++)
                {
                    blendShapes[i].Resize(vertexCount, true);
                }
            }
        }
        #endregion

        #region Calculate Sub Mesh Offsets
        private void CalculateSubMeshOffsets()
        {
            int lastSubMeshIndex = -1;
            subMeshOffsets = new int[subMeshCount];

            var triangles = this.triangles.Data;
            int triangleCount = this.triangles.Length;
            for (int i = 0; i < triangleCount; i++)
            {
                var triangle = triangles[i];
                if (triangle.subMeshIndex > lastSubMeshIndex)
                {
                    for (int j = lastSubMeshIndex + 1; j < triangle.subMeshIndex; j++)
                    {
                        subMeshOffsets[j] = i;
                    }
                    subMeshOffsets[triangle.subMeshIndex] = i;
                    lastSubMeshIndex = triangle.subMeshIndex;
                }
            }

            for (int i = lastSubMeshIndex + 1; i < subMeshCount; i++)
            {
                subMeshOffsets[i] = triangleCount;
            }
        }
        #endregion

        #endregion

        #region Public Methods
        #region Sub-Meshes
        /// <summary>
        /// Returns the triangle indices for all sub-meshes.
        /// </summary>
        /// <returns>The triangle indices for all sub-meshes.</returns>
        public int[][] GetAllSubMeshTriangles()
        {
            EnsureNotSimplifying();
            var indices = new int[subMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                indices[subMeshIndex] = GetSubMeshTriangles(subMeshIndex);
            }
            return indices;
        }

        /// <summary>
        /// Returns the triangle indices for a specific sub-mesh.
        /// </summary>
        /// <param name="subMeshIndex">The sub-mesh index.</param>
        /// <returns>The triangle indices.</returns>
        public int[] GetSubMeshTriangles(int subMeshIndex)
        {
            EnsureNotSimplifying();
            if (subMeshIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(subMeshIndex), "The sub-mesh index is negative.");

            if (subMeshOffsets == null)
            {
                CalculateSubMeshOffsets();
            }

            if (subMeshIndex >= subMeshOffsets.Length)
                throw new ArgumentOutOfRangeException(nameof(subMeshIndex), "The sub-mesh index is greater than or equals to the sub mesh count.");
            else if (subMeshOffsets.Length != subMeshCount)
                throw new InvalidOperationException("The sub-mesh triangle offsets array is not the same size as the count of sub-meshes. This should not be possible to happen.");

            var triangles = this.triangles.Data;
            int triangleCount = this.triangles.Length;

            int startOffset = subMeshOffsets[subMeshIndex];
            if (startOffset == triangleCount)
                return new int[0];
            if (startOffset < 0 || startOffset > triangleCount)
                throw new InvalidOperationException(string.Format("The start offset for sub-mesh {0} is invalid ({1}).", subMeshIndex, startOffset));

            int endOffset = ((subMeshIndex + 1) < subMeshCount ? subMeshOffsets[subMeshIndex + 1] : triangleCount);
            if (endOffset < startOffset || endOffset > triangleCount)
                throw new InvalidOperationException(string.Format("The end offset for sub-mesh {0} is invalid ({1}).", subMeshIndex, endOffset));
            int subMeshTriangleCount = endOffset - startOffset;
            int[] subMeshIndices = new int[subMeshTriangleCount * 3];

            for (int triangleIndex = startOffset; triangleIndex < endOffset; triangleIndex++)
            {
                var triangle = triangles[triangleIndex];
                int offset = (triangleIndex - startOffset) * 3;
                subMeshIndices[offset] = triangle.v0;
                subMeshIndices[offset + 1] = triangle.v1;
                subMeshIndices[offset + 2] = triangle.v2;
            }

            return subMeshIndices;
        }

        /// <summary>
        /// Clears out all sub-meshes.
        /// </summary>
        public void ClearSubMeshes()
        {
            EnsureNotSimplifying();
            subMeshCount = 0;
            subMeshOffsets = null;
            referenceMesh = null;
            triangles.Resize(0);
        }

        /// <summary>
        /// Adds a sub-mesh triangle indices for a specific sub-mesh.
        /// </summary>
        /// <param name="triangles">The triangle indices.</param>
        public void AddSubMeshTriangles(int[] triangles)
        {
            EnsureNotSimplifying();
            if (triangles == null)
                throw new ArgumentNullException(nameof(triangles));
            else if ((triangles.Length % TriangleVertexCount) != 0)
                throw new ArgumentException("The index array length must be a multiple of 3 in order to represent triangles.", nameof(triangles));

            ValidateTriangleIndices(triangles, nameof(triangles));
            referenceMesh = null;
            subMeshOffsets = null;
            int subMeshIndex = subMeshCount;
            subMeshCount = checked(subMeshCount + 1);
            int triangleIndexStart = this.triangles.Length;
            int subMeshTriangleCount = triangles.Length / TriangleVertexCount;
            this.triangles.Resize(checked(this.triangles.Length + subMeshTriangleCount));
            var trisArr = this.triangles.Data;
            for (int i = 0; i < subMeshTriangleCount; i++)
            {
                int offset = i * 3;
                int v0 = triangles[offset];
                int v1 = triangles[offset + 1];
                int v2 = triangles[offset + 2];
                int triangleIndex = triangleIndexStart + i;
                trisArr[triangleIndex] = new Triangle(triangleIndex, v0, v1, v2, subMeshIndex);
            }
        }

        /// <summary>
        /// Adds several sub-meshes at once with their triangle indices for each sub-mesh.
        /// </summary>
        /// <param name="triangles">The triangle indices for each sub-mesh.</param>
        public void AddSubMeshTriangles(int[][] triangles)
        {
            EnsureNotSimplifying();
            if (triangles == null)
                throw new ArgumentNullException(nameof(triangles));

            int totalTriangleCount = 0;
            for (int i = 0; i < triangles.Length; i++)
            {
                if (triangles[i] == null)
                    throw new ArgumentException(string.Format("The index array at index {0} is null.", i));
                else if ((triangles[i].Length % TriangleVertexCount) != 0)
                    throw new ArgumentException(string.Format("The index array length at index {0} must be a multiple of 3 in order to represent triangles.", i), nameof(triangles));

                ValidateTriangleIndices(triangles[i], nameof(triangles));
                totalTriangleCount = checked(totalTriangleCount + (triangles[i].Length / TriangleVertexCount));
            }

            subMeshOffsets = null;
            int triangleIndexStart = this.triangles.Length;
            this.triangles.Resize(checked(this.triangles.Length + totalTriangleCount));
            var trisArr = this.triangles.Data;

            for (int i = 0; i < triangles.Length; i++)
            {
                int subMeshIndex = subMeshCount;
                subMeshCount = checked(subMeshCount + 1);
                var subMeshTriangles = triangles[i];
                int subMeshTriangleCount = subMeshTriangles.Length / TriangleVertexCount;
                for (int j = 0; j < subMeshTriangleCount; j++)
                {
                    int offset = j * 3;
                    int v0 = subMeshTriangles[offset];
                    int v1 = subMeshTriangles[offset + 1];
                    int v2 = subMeshTriangles[offset + 2];
                    int triangleIndex = triangleIndexStart + j;
                    trisArr[triangleIndex] = new Triangle(triangleIndex, v0, v1, v2, subMeshIndex);
                }

                triangleIndexStart += subMeshTriangleCount;
            }
        }
        #endregion

        #region UV Sets
        #region Getting
        /// <summary>
        /// Returns the UVs (2D) from a specific channel.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <returns>The UVs.</returns>
        public Vector2[] GetUVs2D(int channel)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            return vertUV2D != null && vertUV2D[channel] != null ? vertUV2D[channel].ToArray() : null;
        }

        /// <summary>
        /// Returns the UVs (3D) from a specific channel.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <returns>The UVs.</returns>
        public Vector3[] GetUVs3D(int channel)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            return vertUV3D != null && vertUV3D[channel] != null ? vertUV3D[channel].ToArray() : null;
        }

        /// <summary>
        /// Returns the UVs (4D) from a specific channel.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <returns>The UVs.</returns>
        public Vector4[] GetUVs4D(int channel)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            return vertUV4D != null && vertUV4D[channel] != null ? vertUV4D[channel].ToArray() : null;
        }

        /// <summary>
        /// Returns the UVs (2D) from a specific channel.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <param name="uvs">The UVs.</param>
        public void GetUVs(int channel, List<Vector2> uvs)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));
            else if (uvs == null)
                throw new ArgumentNullException(nameof(uvs));

            uvs.Clear();
            if (vertUV2D != null && vertUV2D[channel] != null)
            {
                uvs.AddRange(vertUV2D[channel].ToArray());
            }
        }

        /// <summary>
        /// Returns the UVs (3D) from a specific channel.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <param name="uvs">The UVs.</param>
        public void GetUVs(int channel, List<Vector3> uvs)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));
            else if (uvs == null)
                throw new ArgumentNullException(nameof(uvs));

            uvs.Clear();
            if (vertUV3D != null && vertUV3D[channel] != null)
            {
                uvs.AddRange(vertUV3D[channel].ToArray());
            }
        }

        /// <summary>
        /// Returns the UVs (4D) from a specific channel.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <param name="uvs">The UVs.</param>
        public void GetUVs(int channel, List<Vector4> uvs)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));
            else if (uvs == null)
                throw new ArgumentNullException(nameof(uvs));

            uvs.Clear();
            if (vertUV4D != null && vertUV4D[channel] != null)
            {
                uvs.AddRange(vertUV4D[channel].ToArray());
            }
        }
        #endregion

        #region Setting
        /// <summary>
        /// Sets the UVs (2D) for a specific channel.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <param name="uvs">The UVs.</param>
        public void SetUVs(int channel, IList<Vector2> uvs)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            ValidateUVCount(uvs);
            ValidateFiniteUVs(uvs, nameof(uvs));
            if (uvs != null && uvs.Count > 0)
            {
                if (vertUV2D == null)
                    vertUV2D = new UVChannels<Vector2>();

                int uvCount = uvs.Count;
                var uvSet = vertUV2D[channel];
                if (uvSet != null)
                {
                    uvSet.Resize(uvCount);
                }
                else
                {
                    uvSet = new ResizableArray<Vector2>(uvCount, uvCount);
                    vertUV2D[channel] = uvSet;
                }

                var uvData = uvSet.Data;
                uvs.CopyTo(uvData, 0);
            }
            else
            {
                if (vertUV2D != null)
                {
                    vertUV2D[channel] = null;
                }
            }

            if (vertUV3D != null)
            {
                vertUV3D[channel] = null;
            }
            if (vertUV4D != null)
            {
                vertUV4D[channel] = null;
            }
        }

        /// <summary>
        /// Sets the UVs (3D) for a specific channel.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <param name="uvs">The UVs.</param>
        public void SetUVs(int channel, IList<Vector3> uvs)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            ValidateUVCount(uvs);
            ValidateFiniteUVs(uvs, nameof(uvs));
            if (uvs != null && uvs.Count > 0)
            {
                if (vertUV3D == null)
                    vertUV3D = new UVChannels<Vector3>();

                int uvCount = uvs.Count;
                var uvSet = vertUV3D[channel];
                if (uvSet != null)
                {
                    uvSet.Resize(uvCount);
                }
                else
                {
                    uvSet = new ResizableArray<Vector3>(uvCount, uvCount);
                    vertUV3D[channel] = uvSet;
                }

                var uvData = uvSet.Data;
                uvs.CopyTo(uvData, 0);
            }
            else
            {
                if (vertUV3D != null)
                {
                    vertUV3D[channel] = null;
                }
            }

            if (vertUV2D != null)
            {
                vertUV2D[channel] = null;
            }
            if (vertUV4D != null)
            {
                vertUV4D[channel] = null;
            }
        }

        /// <summary>
        /// Sets the UVs (4D) for a specific channel.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <param name="uvs">The UVs.</param>
        public void SetUVs(int channel, IList<Vector4> uvs)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            ValidateUVCount(uvs);
            ValidateFiniteUVs(uvs, nameof(uvs));
            if (uvs != null && uvs.Count > 0)
            {
                if (vertUV4D == null)
                    vertUV4D = new UVChannels<Vector4>();

                int uvCount = uvs.Count;
                var uvSet = vertUV4D[channel];
                if (uvSet != null)
                {
                    uvSet.Resize(uvCount);
                }
                else
                {
                    uvSet = new ResizableArray<Vector4>(uvCount, uvCount);
                    vertUV4D[channel] = uvSet;
                }

                var uvData = uvSet.Data;
                uvs.CopyTo(uvData, 0);
            }
            else
            {
                if (vertUV4D != null)
                {
                    vertUV4D[channel] = null;
                }
            }

            if (vertUV2D != null)
            {
                vertUV2D[channel] = null;
            }
            if (vertUV3D != null)
            {
                vertUV3D[channel] = null;
            }
        }

        /// <summary>
        /// Sets the UVs for a specific channel with a specific count of UV components.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <param name="uvs">The UVs.</param>
        /// <param name="uvComponentCount">The count of UV components.</param>
        public void SetUVs(int channel, IList<Vector4> uvs, int uvComponentCount)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));
            else if (uvComponentCount < 0 || uvComponentCount > 4 || uvComponentCount == 1)
                throw new ArgumentOutOfRangeException(nameof(uvComponentCount), "UV channels must use 0, 2, 3, or 4 components.");

            ValidateUVCount(uvs);
            ValidateFiniteUVs(uvs, nameof(uvs));
            if (uvs != null && uvs.Count > 0 && uvComponentCount > 0)
            {
                if (uvComponentCount <= 2)
                {
                    var uv2D = MeshUtils.ConvertUVsTo2D(uvs);
                    SetUVs(channel, uv2D);
                }
                else if (uvComponentCount == 3)
                {
                    var uv3D = MeshUtils.ConvertUVsTo3D(uvs);
                    SetUVs(channel, uv3D);
                }
                else
                {
                    SetUVs(channel, uvs);
                }
            }
            else
            {
                if (vertUV2D != null)
                {
                    vertUV2D[channel] = null;
                }
                if (vertUV3D != null)
                {
                    vertUV3D[channel] = null;
                }
                if (vertUV4D != null)
                {
                    vertUV4D[channel] = null;
                }
            }
        }

        /// <summary>
        /// Sets the UVs for a specific channel and automatically detects the used components.
        /// </summary>
        /// <param name="channel">The channel index.</param>
        /// <param name="uvs">The UVs.</param>
        public void SetUVsAuto(int channel, IList<Vector4> uvs)
        {
            EnsureNotSimplifying();
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            int uvComponentCount = 0;
            if (uvs != null && uvs.Count > 0)
            {
                uvComponentCount = 2;
                for (int i = 0; i < uvs.Count; i++)
                {
                    Vector4 uv = uvs[i];
                    if (uv.w != 0f)
                    {
                        uvComponentCount = 4;
                        break;
                    }
                    if (uv.z != 0f)
                        uvComponentCount = 3;
                }
            }
            SetUVs(channel, uvs, uvComponentCount);
        }
        #endregion
        #endregion

        #region Blend Shapes
        /// <summary>
        /// Returns deep copies of all blend shapes.
        /// </summary>
        public BlendShape[] GetAllBlendShapes()
        {
            EnsureNotSimplifying();
            if (blendShapes == null)
                return null;

            var results = new BlendShape[blendShapes.Length];
            for (int i = 0; i < results.Length; i++)
                results[i] = blendShapes[i].ToBlendShape();
            return results;
        }

        /// <summary>Returns a deep copy of a specific blend shape.</summary>
        public BlendShape GetBlendShape(int blendShapeIndex)
        {
            EnsureNotSimplifying();
            if (blendShapes == null || blendShapeIndex < 0 || blendShapeIndex >= blendShapes.Length)
                throw new ArgumentOutOfRangeException(nameof(blendShapeIndex));

            return blendShapes[blendShapeIndex].ToBlendShape();
        }

        /// <summary>Clears all blend shapes.</summary>
        public void ClearBlendShapes()
        {
            EnsureNotSimplifying();
            if (blendShapes != null)
            {
                blendShapes.Clear();
                blendShapes = null;
            }
        }

        /// <summary>Adds a blend shape after validating all avatar data.</summary>
        public void AddBlendShape(BlendShape blendShape)
        {
            EnsureNotSimplifying();
            BlendShape preparedBlendShape = CloneAndValidateBlendShape(blendShape, nameof(blendShape), 0);

            if (blendShapes == null)
                blendShapes = new ResizableArray<BlendShapeContainer>(4, 0);

            blendShapes.Add(new BlendShapeContainer(preparedBlendShape));
        }

        /// <summary>Adds several validated blend shapes.</summary>
        public void AddBlendShapes(BlendShape[] blendShapes)
        {
            EnsureNotSimplifying();
            if (blendShapes == null)
                throw new ArgumentNullException(nameof(blendShapes));

            var preparedBlendShapes = new BlendShape[blendShapes.Length];
            for (int i = 0; i < blendShapes.Length; i++)
                preparedBlendShapes[i] = CloneAndValidateBlendShape(blendShapes[i], nameof(blendShapes), i);

            if (this.blendShapes == null)
                this.blendShapes = new ResizableArray<BlendShapeContainer>(Math.Max(4, blendShapes.Length), 0);

            for (int i = 0; i < preparedBlendShapes.Length; i++)
                this.blendShapes.Add(new BlendShapeContainer(preparedBlendShapes[i]));
        }
        #endregion

        #region Initialize
        /// <summary>
        /// Snapshots a Unity mesh into managed data. The source mesh is never modified.
        /// Unity Mesh access must happen on the thread that created this simplifier.
        /// </summary>
        public void Initialize(Mesh mesh)
        {
            EnsureNotSimplifying();
            EnsureUnityThread();
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (Application.isPlaying && !mesh.isReadable)
                throw new InvalidOperationException("The source mesh is not readable. Enable Read/Write on the mesh import settings before simplifying it at runtime.");

            if (!mesh.HasVertexAttribute(VertexAttribute.Position) || mesh.GetVertexAttributeDimension(VertexAttribute.Position) != 3)
                throw new NotSupportedException("The source mesh must provide a three-dimensional position stream.");
            if (mesh.HasVertexAttribute(VertexAttribute.Normal) && mesh.GetVertexAttributeDimension(VertexAttribute.Normal) != 3)
                throw new NotSupportedException("The source mesh normal stream must be three-dimensional.");
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent) && mesh.GetVertexAttributeDimension(VertexAttribute.Tangent) != 4)
                throw new NotSupportedException("The source mesh tangent stream must be four-dimensional.");
            if (mesh.HasVertexAttribute(VertexAttribute.Color) && mesh.GetVertexAttributeDimension(VertexAttribute.Color) != 4)
                throw new NotSupportedException("The source mesh color stream must be four-dimensional to round-trip without changing its layout.");

            int meshSubMeshCount = mesh.subMeshCount;
            for (int subMeshIndex = 0; subMeshIndex < meshSubMeshCount; subMeshIndex++)
            {
                if (mesh.GetTopology(subMeshIndex) != MeshTopology.Triangles)
                    throw new NotSupportedException(string.Format("Sub-mesh {0} uses {1}. Only triangle topology can be simplified safely.", subMeshIndex, mesh.GetTopology(subMeshIndex)));
            }

            sourceMeshName = string.IsNullOrEmpty(mesh.name) ? "Mesh" : mesh.name;
            referenceMesh = null;
            referenceConstraintRejectedCollapses = 0;
            invalidAttributePlacementRejections = 0;
            lastSimplificationIterationCount = 0;
            ClearSubMeshes();
            ClearVertexAttributes();

            Vertices = mesh.vertices;
            Normals = mesh.normals;
            Tangents = mesh.tangents;
            Colors = mesh.colors;
            BindPoses = mesh.bindposes;
            BoneWeights1 = MeshUtils.GetMeshBoneWeights(mesh);
            ValidateBoneWeightBindposes();

            for (int channel = 0; channel < UVChannelCount; channel++)
            {
                int componentCount = simplificationOptions.ManualUVComponentCount
                    ? simplificationOptions.UVComponentCount
                    : MeshUtils.GetMeshUVChannelDimension(mesh, channel);

                switch (componentCount)
                {
                    case 0:
                        SetUVs(channel, (IList<Vector4>)null, 0);
                        break;
                    case 2:
                        SetUVs(channel, MeshUtils.GetMeshUVs2D(mesh, channel));
                        break;
                    case 1:
                        throw new NotSupportedException(string.Format(
                            "UV channel {0} uses one component. Unity's safe GetUVs/SetUVs API only round-trips 2D, 3D, or 4D UV data, so this mesh is rejected rather than silently changing its vertex layout.",
                            channel));
                    case 3:
                        SetUVs(channel, MeshUtils.GetMeshUVs3D(mesh, channel));
                        break;
                    case 4:
                        SetUVs(channel, MeshUtils.GetMeshUVs(mesh, channel));
                        break;
                    default:
                        throw new InvalidOperationException(string.Format("UV channel {0} reports unsupported dimension {1}.", channel, componentCount));
                }
            }

            ClearBlendShapes();
            BlendShape[] sourceBlendShapes = MeshUtils.GetMeshBlendShapes(mesh);
            if (sourceBlendShapes != null && sourceBlendShapes.Length > 0)
                AddBlendShapes(sourceBlendShapes);

            var subMeshTriangles = new int[meshSubMeshCount][];
            for (int subMeshIndex = 0; subMeshIndex < meshSubMeshCount; subMeshIndex++)
                subMeshTriangles[subMeshIndex] = mesh.GetTriangles(subMeshIndex, true);
            AddSubMeshTriangles(subMeshTriangles);
            RemoveDegenerateTriangles();
        }
        #endregion

        #region Simplify Mesh
        /// <summary>Simplifies the managed mesh data to the requested quality.</summary>
        public void SimplifyMesh(float quality)
        {
            BeginSimplification();
            try
            {
                SimplifyMeshInternal(quality, CancellationToken.None);
            }
            finally
            {
                EndSimplification();
                if (Thread.CurrentThread.ManagedThreadId == unityThreadId)
                    FlushVerboseMessages();
            }
        }

        /// <summary>
        /// Runs the pure managed simplification pass on a worker thread. Initialize and ToMesh still must run on Unity's main thread.
        /// </summary>
        public async Task SimplifyMeshAsync(float quality, CancellationToken cancellationToken = default(CancellationToken))
        {
            BeginSimplification();
            try
            {
                await Task.Run(() => SimplifyMeshInternal(quality, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                EndSimplification();
            }
        }

        private void ReportProgress(
            SimplificationProgressStage stage,
            int passIndex,
            int passCount,
            int iterationIndex,
            int iterationCount,
            int startTriangleCount,
            int currentTriangleCount,
            int targetTriangleCount)
        {
            Action<SimplificationProgress> callback = progressChanged;
            if (callback == null)
                return;

            try
            {
                callback(new SimplificationProgress(
                    stage,
                    passIndex,
                    passCount,
                    iterationIndex,
                    iterationCount,
                    startTriangleCount,
                    currentTriangleCount,
                    targetTriangleCount,
                    referenceConstraintRejectedCollapses,
                    invalidAttributePlacementRejections));
            }
            catch
            {
                
            }
        }

        private void GetLiveEdgeErrorRangeAbove(
            double threshold,
            out double minimumAboveThreshold,
            out double maximum)
        {
            minimumAboveThreshold = double.MaxValue;
            maximum = 0.0;
            Triangle[] triangleData = triangles.Data;
            int triangleCount = triangles.Length;
            for (int i = 0; i < triangleCount; i++)
            {
                Triangle triangle = triangleData[i];
                if (triangle.deleted)
                    continue;

                AccumulateLiveEdgeErrorRange(
                    triangle.err0,
                    threshold,
                    ref minimumAboveThreshold,
                    ref maximum);
                AccumulateLiveEdgeErrorRange(
                    triangle.err1,
                    threshold,
                    ref minimumAboveThreshold,
                    ref maximum);
                AccumulateLiveEdgeErrorRange(
                    triangle.err2,
                    threshold,
                    ref minimumAboveThreshold,
                    ref maximum);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AccumulateLiveEdgeErrorRange(
            double error,
            double threshold,
            ref double minimumAboveThreshold,
            ref double maximum)
        {
            if (double.IsNaN(error) || double.IsInfinity(error))
                return;
            if (error > maximum)
                maximum = error;
            if (error > threshold && error < minimumAboveThreshold)
                minimumAboveThreshold = error;
        }

        private void SimplifyMeshInternal(float quality, CancellationToken cancellationToken)
        {
            if (float.IsNaN(quality) || float.IsInfinity(quality))
                throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be a finite value between 0 and 1.");

            quality = Math.Max(0f, Math.Min(1f, quality));
            int startTriangleCount = triangles.Length;
            int targetTriangleCount = (int)Math.Round(startTriangleCount * quality, MidpointRounding.AwayFromZero);
            SimplifyMeshToTriangleCountInternal(targetTriangleCount, cancellationToken, 1, 1);
        }

        /// <summary>Simplifies the managed mesh data toward an explicit remaining triangle count.</summary>
        public void SimplifyMeshToTriangleCount(int targetTriangleCount)
        {
            BeginSimplification();
            try
            {
                SimplifyMeshToTriangleCountInternal(targetTriangleCount, CancellationToken.None, 1, 1);
            }
            finally
            {
                EndSimplification();
                if (Thread.CurrentThread.ManagedThreadId == unityThreadId)
                    FlushVerboseMessages();
            }
        }

        /// <summary>
        /// Runs the pure managed simplification pass toward an explicit triangle count on a worker thread.
        /// Initialize and ToMesh still must run on Unity's main thread.
        /// </summary>
        public async Task SimplifyMeshToTriangleCountAsync(int targetTriangleCount, CancellationToken cancellationToken = default(CancellationToken))
        {
            BeginSimplification();
            try
            {
                await Task.Run(() => SimplifyMeshToTriangleCountInternal(targetTriangleCount, cancellationToken, 1, 1), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                EndSimplification();
            }
        }

        /// <summary>
        /// Repeats the triangle-target pass up to the requested number of times. Rebuilding the error
        /// state between passes can reach targets that a single threshold sweep stops slightly above.
        /// The loop exits early when the target is reached or a pass makes no further progress.
        /// </summary>
        public void SimplifyMeshToTriangleCount(int targetTriangleCount, int passCount)
        {
            BeginSimplification();
            try
            {
                SimplifyMeshToTriangleCountPassesInternal(targetTriangleCount, passCount, CancellationToken.None);
            }
            finally
            {
                EndSimplification();
                if (Thread.CurrentThread.ManagedThreadId == unityThreadId)
                    FlushVerboseMessages();
            }
        }

        /// <summary>
        /// Worker-thread version of the repeated triangle-target pass. Unity Mesh access still stays on
        /// the owning thread through Initialize and ToMesh.
        /// </summary>
        public async Task SimplifyMeshToTriangleCountAsync(
            int targetTriangleCount,
            int passCount,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            BeginSimplification();
            try
            {
                await Task.Run(
                    () => SimplifyMeshToTriangleCountPassesInternal(targetTriangleCount, passCount, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                EndSimplification();
            }
        }

        private void SimplifyMeshToTriangleCountPassesInternal(
            int targetTriangleCount,
            int passCount,
            CancellationToken cancellationToken)
        {
            if (passCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(passCount), "The simplification pass count must be positive.");

            int completedPasses = 0;
            int initialTriangleCount = triangles.Length;
            for (int pass = 0; pass < passCount; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int before = triangles.Length;
                if (before <= targetTriangleCount)
                    break;

                referenceConstraintRejectedCollapses = 0;
                invalidAttributePlacementRejections = 0;
                ReportProgress(
                    SimplificationProgressStage.StartingPass,
                    pass + 1,
                    passCount,
                    0,
                    simplificationOptions.MaxIterationCount,
                    before,
                    before,
                    targetTriangleCount);
                SimplifyMeshToTriangleCountInternal(targetTriangleCount, cancellationToken, pass + 1, passCount);
                int after = triangles.Length;
                completedPasses = pass + 1;
                ReportProgress(
                    SimplificationProgressStage.CompletedPass,
                    pass + 1,
                    passCount,
                    lastSimplificationIterationCount,
                    simplificationOptions.MaxIterationCount,
                    before,
                    after,
                    targetTriangleCount);
                LogVerbose(
                    "triangle-target pass {0}/{1}: {2} -> {3}; reference rejections {4}",
                    pass + 1,
                    passCount,
                    before,
                    after,
                    referenceConstraintRejectedCollapses);
                if (after <= targetTriangleCount || after >= before)
                    break;
            }

            ReportProgress(
                SimplificationProgressStage.Completed,
                completedPasses,
                passCount,
                lastSimplificationIterationCount,
                simplificationOptions.MaxIterationCount,
                initialTriangleCount,
                triangles.Length,
                targetTriangleCount);
        }

        private void SimplifyMeshToTriangleCountInternal(
            int targetTriangleCount,
            CancellationToken cancellationToken,
            int passIndex,
            int passCount)
        {
            if (targetTriangleCount < 0)
                throw new ArgumentOutOfRangeException(nameof(targetTriangleCount), "The target triangle count cannot be negative.");

            if (NeedsReferenceMesh() && referenceMesh == null)
                BuildReferenceMesh(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            int deletedTris = 0;
            ResizableArray<bool> deleted0 = deletedTriangleBuffer0;
            ResizableArray<bool> deleted1 = deletedTriangleBuffer1;
            deleted0.Resize(0);
            deleted1.Resize(0);
            int startTrisCount = triangles.Length;
            int targetTrisCount = Math.Min(targetTriangleCount, startTrisCount);
            lastSimplificationIterationCount = 0;

            try
            {
                for (int iteration = 0; iteration < simplificationOptions.MaxIterationCount; iteration++)
                {
                    lastSimplificationIterationCount = iteration + 1;
                    cancellationToken.ThrowIfCancellationRequested();
                    int currentTriangleCount = startTrisCount - deletedTris;
                    if (currentTriangleCount <= targetTrisCount)
                        break;

                    if ((iteration % 5) == 0)
                        UpdateMesh(iteration, cancellationToken);

                    Triangle[] triangleData = triangles.Data;
                    int triangleCount = triangles.Length;
                    if (CanUseParallelCheapLoop(triangleCount))
                    {
                        Parallel.For(
                            0,
                            triangleCount,
                            CreateParallelOptions(cancellationToken),
                            i => triangleData[i].dirty = false);
                    }
                    else
                    {
                        for (int i = 0; i < triangleCount; i++)
                            triangleData[i].dirty = false;
                    }

                    double threshold = cachedIterationThresholds[iteration];
                    int deletedBeforeIteration = deletedTris;
                    LogVerbose("iteration {0} - triangles {1} target {2} threshold {3}", iteration, currentTriangleCount, targetTrisCount, threshold);
                    RemoveVertexPass(startTrisCount, targetTrisCount, threshold, deleted0, deleted1, ref deletedTris, cancellationToken);
                    currentTriangleCount = startTrisCount - deletedTris;
                    ReportProgress(
                        SimplificationProgressStage.Iterating,
                        passIndex,
                        passCount,
                        iteration + 1,
                        simplificationOptions.MaxIterationCount,
                        startTrisCount,
                        currentTriangleCount,
                        targetTrisCount);

                    if (deletedTris == deletedBeforeIteration)
                    {
                        double minimumLiveEdgeErrorAboveThreshold;
                        double maximumLiveEdgeError;
                        GetLiveEdgeErrorRangeAbove(
                            threshold,
                            out minimumLiveEdgeErrorAboveThreshold,
                            out maximumLiveEdgeError);
                        if (threshold >= maximumLiveEdgeError ||
                            minimumLiveEdgeErrorAboveThreshold == double.MaxValue)
                        {
                            LogVerbose(
                                "Stopping pass after a complete zero-reduction sweep. Threshold {0}, maximum live edge error {1}.",
                                threshold,
                                maximumLiveEdgeError);
                            break;
                        }

                        int nextIteration = iteration + 1;
                        while (nextIteration < simplificationOptions.MaxIterationCount &&
                               cachedIterationThresholds[nextIteration] <
                               minimumLiveEdgeErrorAboveThreshold)
                        {
                            nextIteration++;
                        }

                        if (nextIteration > iteration + 1)
                        {
                            LogVerbose(
                                "Skipping empty threshold iterations {0}-{1}; the next live edge error is {2}.",
                                iteration + 1,
                                nextIteration - 1,
                                minimumLiveEdgeErrorAboveThreshold);
                            iteration = nextIteration - 1;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                CompactMesh();
                throw;
            }

            CompactMesh();
            LogVerbose("Finished simplification with triangle count {0} (requested target {1})", triangles.Length, targetTrisCount);
        }

        /// <summary>Simplifies without the normal target-quality loss budget.</summary>
        public void SimplifyMeshLossless()
        {
            BeginSimplification();
            try
            {
                SimplifyMeshLosslessInternal(CancellationToken.None);
            }
            finally
            {
                EndSimplification();
                if (Thread.CurrentThread.ManagedThreadId == unityThreadId)
                    FlushVerboseMessages();
            }
        }

        /// <summary>Runs lossless simplification on a worker thread.</summary>
        public async Task SimplifyMeshLosslessAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            BeginSimplification();
            try
            {
                await Task.Run(() => SimplifyMeshLosslessInternal(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                EndSimplification();
            }
        }

        private void SimplifyMeshLosslessInternal(CancellationToken cancellationToken)
        {
            if (NeedsReferenceMesh() && referenceMesh == null)
                BuildReferenceMesh(cancellationToken);

            int deletedTris = 0;
            ResizableArray<bool> deleted0 = deletedTriangleBuffer0;
            ResizableArray<bool> deleted1 = deletedTriangleBuffer1;
            deleted0.Resize(0);
            deleted1.Resize(0);
            int startTrisCount = triangles.Length;

            try
            {
                for (int iteration = 0; iteration < 9999; iteration++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    UpdateMesh(iteration, cancellationToken);

                    Triangle[] triangleData = triangles.Data;
                    int triangleCount = triangles.Length;
                    if (CanUseParallelCheapLoop(triangleCount))
                    {
                        Parallel.For(
                            0,
                            triangleCount,
                            CreateParallelOptions(cancellationToken),
                            i => triangleData[i].dirty = false);
                    }
                    else
                    {
                        for (int i = 0; i < triangleCount; i++)
                            triangleData[i].dirty = false;
                    }

                    LogVerbose("Lossless iteration {0} - triangles {1}", iteration, triangleCount);
                    RemoveVertexPass(startTrisCount, 0, DoubleEpsilon, deleted0, deleted1, ref deletedTris, cancellationToken);
                    if (deletedTris <= 0)
                        break;
                    deletedTris = 0;
                }
            }
            catch (OperationCanceledException)
            {
                CompactMesh();
                throw;
            }

            CompactMesh();
            LogVerbose("Finished simplification with triangle count {0}", triangles.Length);
        }
        #endregion

        #region To Mesh
        /// <summary>Creates a new Unity Mesh. It never returns or mutates the source mesh.</summary>
        public Mesh ToMesh()
        {
            EnsureNotSimplifying();
            EnsureUnityThread();
            FlushVerboseMessages();
            ValidateBoneWeightBindposes();

            Vector3[] outputVertices = Vertices;
            Vector3[] normals = Normals;
            Vector4[] tangents = Tangents;
            Color[] colors = Colors;
            BoneWeight1[][] boneWeights = BoneWeights1;
            int[][] indices = GetAllSubMeshTriangles();
            BlendShape[] outputBlendShapes = GetAllBlendShapes();

            List<Vector2>[] uvs2D = null;
            List<Vector3>[] uvs3D = null;
            List<Vector4>[] uvs4D = null;

            if (vertUV2D != null)
            {
                uvs2D = new List<Vector2>[UVChannelCount];
                for (int channel = 0; channel < UVChannelCount; channel++)
                {
                    if (vertUV2D[channel] != null)
                    {
                        var values = new List<Vector2>(outputVertices.Length);
                        GetUVs(channel, values);
                        uvs2D[channel] = values;
                    }
                }
            }

            if (vertUV3D != null)
            {
                uvs3D = new List<Vector3>[UVChannelCount];
                for (int channel = 0; channel < UVChannelCount; channel++)
                {
                    if (vertUV3D[channel] != null)
                    {
                        var values = new List<Vector3>(outputVertices.Length);
                        GetUVs(channel, values);
                        uvs3D[channel] = values;
                    }
                }
            }

            if (vertUV4D != null)
            {
                uvs4D = new List<Vector4>[UVChannelCount];
                for (int channel = 0; channel < UVChannelCount; channel++)
                {
                    if (vertUV4D[channel] != null)
                    {
                        var values = new List<Vector4>(outputVertices.Length);
                        GetUVs(channel, values);
                        uvs4D[channel] = values;
                    }
                }
            }

            Mesh result = MeshUtils.CreateMesh(outputVertices, indices, normals, tangents, colors, boneWeights, uvs2D, uvs3D, uvs4D, bindposes, outputBlendShapes);
            result.name = sourceMeshName + " (Simplified Copy)";
            return result;
        }

        /// <summary>Convenience API that snapshots, simplifies, and returns a new mesh.</summary>
        public static Mesh SimplifyMeshCopy(Mesh sourceMesh, float quality)
        {
            return SimplifyMeshCopy(sourceMesh, quality, SimplificationOptions.Avatar);
        }

        /// <summary>Convenience API that snapshots, simplifies, and returns a new mesh.</summary>
        public static Mesh SimplifyMeshCopy(Mesh sourceMesh, float quality, SimplificationOptions options)
        {
            ValidateOptions(options);
            var simplifier = new MeshSimplifier();
            simplifier.SimplificationOptions = options;
            simplifier.MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);
            simplifier.Initialize(sourceMesh);
            simplifier.SimplifyMesh(quality);
            return simplifier.ToMesh();
        }

        /// <summary>Convenience API that snapshots, simplifies toward an explicit triangle count, and returns a new mesh.</summary>
        public static Mesh SimplifyMeshCopyToTriangleCount(Mesh sourceMesh, int targetTriangleCount)
        {
            return SimplifyMeshCopyToTriangleCount(sourceMesh, targetTriangleCount, SimplificationOptions.Avatar);
        }

        /// <summary>Convenience API that snapshots, simplifies toward an explicit triangle count, and returns a new mesh.</summary>
        public static Mesh SimplifyMeshCopyToTriangleCount(Mesh sourceMesh, int targetTriangleCount, SimplificationOptions options)
        {
            if (sourceMesh == null)
                throw new ArgumentNullException(nameof(sourceMesh));
            if (targetTriangleCount < 0)
                throw new ArgumentOutOfRangeException(nameof(targetTriangleCount), "The target triangle count cannot be negative.");
            ValidateOptions(options);

            var simplifier = new MeshSimplifier();
            simplifier.SimplificationOptions = options;
            simplifier.MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);
            simplifier.Initialize(sourceMesh);
            simplifier.SimplifyMeshToTriangleCount(targetTriangleCount);
            return simplifier.ToMesh();
        }

        /// <summary>
        /// Snapshots a mesh, simplifies toward an explicit triangle count on a worker thread, then creates
        /// a new mesh on the captured Unity synchronization context. Await this method; do not block the main thread.
        /// </summary>
        public static Task<Mesh> SimplifyMeshCopyToTriangleCountAsync(Mesh sourceMesh, int targetTriangleCount, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SimplifyMeshCopyToTriangleCountAsync(sourceMesh, targetTriangleCount, SimplificationOptions.Avatar, cancellationToken);
        }

        /// <summary>
        /// Snapshots a mesh, simplifies toward an explicit triangle count on a worker thread, then creates
        /// a new mesh on the captured Unity synchronization context. Await this method; do not block the main thread.
        /// </summary>
        public static async Task<Mesh> SimplifyMeshCopyToTriangleCountAsync(Mesh sourceMesh, int targetTriangleCount, SimplificationOptions options, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sourceMesh == null)
                throw new ArgumentNullException(nameof(sourceMesh));
            if (targetTriangleCount < 0)
                throw new ArgumentOutOfRangeException(nameof(targetTriangleCount), "The target triangle count cannot be negative.");
            ValidateOptions(options);

            var simplifier = new MeshSimplifier();
            simplifier.SimplificationOptions = options;
            simplifier.MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);
            simplifier.Initialize(sourceMesh);
            await simplifier.SimplifyMeshToTriangleCountAsync(targetTriangleCount, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return simplifier.ToMesh();
        }

        /// <summary>
        /// Snapshots a mesh, simplifies its managed data on a worker thread, then creates a new mesh
        /// on the captured Unity synchronization context. Await this method; do not block the main thread.
        /// </summary>
        public static Task<Mesh> SimplifyMeshCopyAsync(Mesh sourceMesh, float quality, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SimplifyMeshCopyAsync(sourceMesh, quality, SimplificationOptions.Avatar, cancellationToken);
        }

        /// <summary>
        /// Snapshots a mesh, simplifies its managed data on a worker thread, then creates a new mesh
        /// on the captured Unity synchronization context. Await this method; do not block the main thread.
        /// </summary>
        public static async Task<Mesh> SimplifyMeshCopyAsync(Mesh sourceMesh, float quality, SimplificationOptions options, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sourceMesh == null)
                throw new ArgumentNullException(nameof(sourceMesh));
            ValidateOptions(options);

            var simplifier = new MeshSimplifier();
            simplifier.SimplificationOptions = options;
            simplifier.MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);
            simplifier.Initialize(sourceMesh);
            await simplifier.SimplifyMeshAsync(quality, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return simplifier.ToMesh();
        }

        /// <summary>
        /// Simplifies independent meshes concurrently using the conservative avatar preset.
        /// Unity Mesh reads and writes remain on the owning Unity thread.
        /// </summary>
        public static Task<Mesh[]> SimplifyMeshCopiesAsync(IList<Mesh> sourceMeshes, float quality, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SimplifyMeshCopiesAsync(sourceMeshes, quality, SimplificationOptions.Avatar, cancellationToken);
        }

        /// <summary>
        /// Snapshots meshes on the owning Unity thread, simplifies independent meshes concurrently on the
        /// thread pool, then creates new meshes back on the captured Unity synchronization context.
        /// This is the safe way to use multiple CPU cores; one individual edge-collapse pass remains sequential.
        /// </summary>
        public static async Task<Mesh[]> SimplifyMeshCopiesAsync(IList<Mesh> sourceMeshes, float quality, SimplificationOptions options, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sourceMeshes == null)
                throw new ArgumentNullException(nameof(sourceMeshes));
            ValidateOptions(options);
            if (float.IsNaN(quality) || float.IsInfinity(quality))
                throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be a finite value between 0 and 1.");

            int meshCount = sourceMeshes.Count;
            int automaticWorkerCount = Math.Max(1, Environment.ProcessorCount);
            int activeMeshWorkers = Math.Max(1, Math.Min(automaticWorkerCount, Math.Max(1, meshCount)));
            int baseInternalWorkers = Math.Max(1, automaticWorkerCount / activeMeshWorkers);
            int extraWorkerSlots = Math.Max(0, automaticWorkerCount - (baseInternalWorkers * activeMeshWorkers));
            var simplifiers = new MeshSimplifier[meshCount];
            for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Mesh sourceMesh = sourceMeshes[meshIndex];
                if (sourceMesh == null)
                    throw new ArgumentException(string.Format("The source mesh at index {0} is null.", meshIndex), nameof(sourceMeshes));

                var simplifier = new MeshSimplifier();
                simplifier.SimplificationOptions = options;
                simplifier.MaxDegreeOfParallelism =
                    baseInternalWorkers + (meshIndex < extraWorkerSlots ? 1 : 0);
                simplifier.Initialize(sourceMesh);
                simplifiers[meshIndex] = simplifier;
            }

            var tasks = new Task[meshCount];
            for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
                tasks[meshIndex] = simplifiers[meshIndex].SimplifyMeshAsync(quality, cancellationToken);

            await Task.WhenAll(tasks);
            cancellationToken.ThrowIfCancellationRequested();

            var results = new Mesh[meshCount];
            for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
                results[meshIndex] = simplifiers[meshIndex].ToMesh();
            return results;
        }

        /// <summary>
        /// Simplifies a skinned renderer's shared mesh into a new mesh and assigns it while preserving
        /// renderer-owned blend-shape weights and local bounds. Bones, root bone, materials, and renderer settings are untouched.
        /// </summary>
        public static Mesh SimplifyRendererMeshCopy(SkinnedMeshRenderer renderer, float quality)
        {
            return SimplifyRendererMeshCopy(renderer, quality, SimplificationOptions.Avatar);
        }

        /// <summary>
        /// Simplifies a skinned renderer's shared mesh into a new mesh and assigns it while preserving
        /// renderer-owned blend-shape weights and local bounds.
        /// </summary>
        public static Mesh SimplifyRendererMeshCopy(SkinnedMeshRenderer renderer, float quality, SimplificationOptions options)
        {
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));
            Mesh sourceMesh = renderer.sharedMesh;
            if (sourceMesh == null)
                throw new ArgumentException("The skinned mesh renderer has no shared mesh.", nameof(renderer));
            if (renderer.GetComponent<Cloth>() != null)
                throw new NotSupportedException(
                    "This renderer has a Cloth component. Cloth coefficients and constraints are vertex-indexed and cannot be safely preserved through topology simplification without an explicit cloth-data remap.");

            Matrix4x4[] sourceBindposes = sourceMesh.bindposes;
            Transform[] rendererBones = renderer.bones;
            bool hasSkinning = sourceMesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                               sourceMesh.HasVertexAttribute(VertexAttribute.BlendIndices);
            bool[] validBoneSlots = null;
            int fallbackBoneIndex = -1;
            bool hasMissingBoneSlots = false;
            if (hasSkinning)
            {
                int bindposeCount = sourceBindposes != null ? sourceBindposes.Length : 0;
                if (bindposeCount <= 0)
                    throw new InvalidOperationException("The skinned mesh has bone influences but no bindposes.");

                validBoneSlots = new bool[bindposeCount];
                for (int boneIndex = 0; boneIndex < bindposeCount; boneIndex++)
                {
                    bool valid = rendererBones != null && boneIndex < rendererBones.Length && rendererBones[boneIndex] != null;
                    validBoneSlots[boneIndex] = valid;
                    if (valid)
                    {
                        if (fallbackBoneIndex < 0)
                            fallbackBoneIndex = boneIndex;
                    }
                    else
                    {
                        hasMissingBoneSlots = true;
                    }
                }
            }

            int sourceBlendShapeCount = sourceMesh.blendShapeCount;
            var blendShapeWeights = new float[sourceBlendShapeCount];
            for (int i = 0; i < sourceBlendShapeCount; i++)
                blendShapeWeights[i] = renderer.GetBlendShapeWeight(i);

            Bounds localBounds = renderer.localBounds;
            var simplifier = new MeshSimplifier();
            simplifier.SimplificationOptions = options;
            simplifier.Initialize(sourceMesh);
            if (hasMissingBoneSlots)
                simplifier.FilterBoneInfluences(validBoneSlots, fallbackBoneIndex);
            simplifier.SimplifyMesh(quality);
            Mesh result = simplifier.ToMesh();
            renderer.sharedMesh = result;
            renderer.localBounds = localBounds;

            int restoreCount = Math.Min(blendShapeWeights.Length, result.blendShapeCount);
            for (int i = 0; i < restoreCount; i++)
                renderer.SetBlendShapeWeight(i, blendShapeWeights[i]);

            return result;
        }
        #endregion

        #region Validate Options
        /// <summary>Validates simplification options.</summary>
        public static void ValidateOptions(SimplificationOptions options)
        {
            if (!Enum.IsDefined(typeof(VertexPlacementMode), options.VertexPlacement))
                throw new ValidateSimplificationOptionsException(nameof(options.VertexPlacement), "The vertex placement mode is not supported.");
            if (float.IsNaN(options.FeatureAngleDegrees) || float.IsInfinity(options.FeatureAngleDegrees) || options.FeatureAngleDegrees < 0f || options.FeatureAngleDegrees > 180f)
                throw new ValidateSimplificationOptionsException(nameof(options.FeatureAngleDegrees), "The feature angle must be zero (compatibility default) or a finite value between 0 and 180 degrees.");
            if (float.IsNaN(options.MaxTriangleNormalDeviationDegrees) || float.IsInfinity(options.MaxTriangleNormalDeviationDegrees) || options.MaxTriangleNormalDeviationDegrees < 0f || options.MaxTriangleNormalDeviationDegrees >= 90f)
                throw new ValidateSimplificationOptionsException(nameof(options.MaxTriangleNormalDeviationDegrees), "The maximum triangle-normal deviation must be zero (compatibility default) or a finite value below 90 degrees.");
            if (double.IsNaN(options.MaxSurfaceDeviation) || double.IsInfinity(options.MaxSurfaceDeviation) || options.MaxSurfaceDeviation < 0.0)
                throw new ValidateSimplificationOptionsException(nameof(options.MaxSurfaceDeviation), "The maximum reference-surface deviation must be finite and non-negative. Zero selects the automatic scale-relative tolerance.");
            if (double.IsNaN(options.MaxBoundaryDeviation) || double.IsInfinity(options.MaxBoundaryDeviation) || options.MaxBoundaryDeviation < 0.0)
                throw new ValidateSimplificationOptionsException(nameof(options.MaxBoundaryDeviation), "The maximum reference-boundary deviation must be finite and non-negative. Zero selects the automatic scale-relative tolerance.");
            if (options.EnableSmartLink && (double.IsNaN(options.VertexLinkDistance) || double.IsInfinity(options.VertexLinkDistance) || options.VertexLinkDistance < 0.0))
                throw new ValidateSimplificationOptionsException(nameof(options.VertexLinkDistance), "The vertex link distance must be finite and non-negative when smart linking is enabled.");
            if (options.MaxIterationCount <= 0)
                throw new ValidateSimplificationOptionsException(nameof(options.MaxIterationCount), "The max iteration count must be positive.");
            if (double.IsNaN(options.Agressiveness) || double.IsInfinity(options.Agressiveness) || options.Agressiveness <= 0.0)
                throw new ValidateSimplificationOptionsException(nameof(options.Agressiveness), "The aggressiveness must be finite and above zero. Around 7 is the balanced default; lower values are more conservative.");
            if (options.ManualUVComponentCount && (options.UVComponentCount < 0 || options.UVComponentCount > 4 || options.UVComponentCount == 1))
                throw new ValidateSimplificationOptionsException(nameof(options.UVComponentCount), "The UV component count must be 0, 2, 3, or 4.");
            if (options.MaxBoneWeightsPerVertex < 0 || options.MaxBoneWeightsPerVertex > byte.MaxValue)
                throw new ValidateSimplificationOptionsException(nameof(options.MaxBoneWeightsPerVertex), "The maximum bone influences per vertex must be 0 (preserve all) or between 1 and 255.");
            if (float.IsNaN(options.BoneWeightThreshold) || float.IsInfinity(options.BoneWeightThreshold) || options.BoneWeightThreshold < 0f)
                throw new ValidateSimplificationOptionsException(nameof(options.BoneWeightThreshold), "The bone weight threshold must be finite and non-negative.");
        }
        #endregion
        #endregion
    }
}
