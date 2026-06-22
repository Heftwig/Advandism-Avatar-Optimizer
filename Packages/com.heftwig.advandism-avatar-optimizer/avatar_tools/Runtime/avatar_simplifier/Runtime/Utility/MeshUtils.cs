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

#if UNITY_2018_2_OR_NEWER
#define UNITY_8UV_SUPPORT
#endif

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityMeshSimplifier
{
    /// <summary>
    /// Contains utility methods for meshes.
    /// </summary>
    public static class MeshUtils
    {
        #region Static Read-Only
        /// <summary>
        /// The count of supported UV channels.
        /// </summary>
#if UNITY_8UV_SUPPORT
        public static readonly int UVChannelCount = 8;
#else
        public static readonly int UVChannelCount = 4;
#endif
        #endregion

        #region Public Methods
        /// <summary>
        /// Creates a new mesh.
        /// </summary>
        /// <param name="vertices">The mesh vertices.</param>
        /// <param name="indices">The mesh sub-mesh indices.</param>
        /// <param name="normals">The mesh normals.</param>
        /// <param name="tangents">The mesh tangents.</param>
        /// <param name="colors">The mesh colors.</param>
        /// <param name="boneWeights">The mesh bone-weights.</param>
        /// <param name="uvs">The mesh 2D UV sets.</param>
        /// <param name="bindposes">The mesh bindposes.</param>
        /// <returns>The created mesh.</returns>
        public static Mesh CreateMesh(Vector3[] vertices, int[][] indices, Vector3[] normals, Vector4[] tangents, Color[] colors, BoneWeight[] boneWeights, List<Vector2>[] uvs, Matrix4x4[] bindposes, BlendShape[] blendShapes)
        {
            return CreateMesh(vertices, indices, normals, tangents, colors, boneWeights, uvs, null, null, bindposes, blendShapes);
        }

        /// <summary>
        /// Creates a new mesh.
        /// </summary>
        /// <param name="vertices">The mesh vertices.</param>
        /// <param name="indices">The mesh sub-mesh indices.</param>
        /// <param name="normals">The mesh normals.</param>
        /// <param name="tangents">The mesh tangents.</param>
        /// <param name="colors">The mesh colors.</param>
        /// <param name="boneWeights">The mesh bone-weights.</param>
        /// <param name="uvs">The mesh 4D UV sets.</param>
        /// <param name="bindposes">The mesh bindposes.</param>
        /// <returns>The created mesh.</returns>
        public static Mesh CreateMesh(Vector3[] vertices, int[][] indices, Vector3[] normals, Vector4[] tangents, Color[] colors, BoneWeight[] boneWeights, List<Vector4>[] uvs, Matrix4x4[] bindposes, BlendShape[] blendShapes)
        {
            return CreateMesh(vertices, indices, normals, tangents, colors, boneWeights, null, null, uvs, bindposes, blendShapes);
        }

        /// <summary>
        /// Creates a new mesh.
        /// </summary>
        /// <param name="vertices">The mesh vertices.</param>
        /// <param name="indices">The mesh sub-mesh indices.</param>
        /// <param name="normals">The mesh normals.</param>
        /// <param name="tangents">The mesh tangents.</param>
        /// <param name="colors">The mesh colors.</param>
        /// <param name="boneWeights">The mesh bone-weights.</param>
        /// <param name="uvs2D">The mesh 2D UV sets.</param>
        /// <param name="uvs3D">The mesh 3D UV sets.</param>
        /// <param name="uvs4D">The mesh 4D UV sets.</param>
        /// <param name="bindposes">The mesh bindposes.</param>
        /// <returns>The created mesh.</returns>
        public static Mesh CreateMesh(Vector3[] vertices, int[][] indices, Vector3[] normals, Vector4[] tangents, Color[] colors, BoneWeight[] boneWeights, List<Vector2>[] uvs2D, List<Vector3>[] uvs3D, List<Vector4>[] uvs4D, Matrix4x4[] bindposes, BlendShape[] blendShapes)
        {
            return CreateMesh(vertices, indices, normals, tangents, colors, ConvertLegacyBoneWeights(boneWeights), uvs2D, uvs3D, uvs4D, bindposes, blendShapes);
        }

        /// <summary>
        /// Creates a new mesh while preserving Unity's variable-count bone influences.
        /// </summary>
        public static Mesh CreateMesh(Vector3[] vertices, int[][] indices, Vector3[] normals, Vector4[] tangents, Color[] colors, BoneWeight1[][] boneWeights, List<Vector2>[] uvs2D, List<Vector3>[] uvs3D, List<Vector4>[] uvs4D, Matrix4x4[] bindposes, BlendShape[] blendShapes)
        {
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));

            ValidateVertexAttributeLength(normals, vertices.Length, nameof(normals));
            ValidateVertexAttributeLength(tangents, vertices.Length, nameof(tangents));
            ValidateVertexAttributeLength(colors, vertices.Length, nameof(colors));
            if (boneWeights != null && boneWeights.Length != vertices.Length)
                throw new ArgumentException("The bone weight vertex count must match the mesh vertex count.", nameof(boneWeights));

            ValidateFinite(vertices, nameof(vertices));
            ValidateFinite(normals, nameof(normals));
            ValidateFinite(tangents, nameof(tangents));
            ValidateFinite(colors, nameof(colors));
            ValidateFinite(bindposes, nameof(bindposes));
            ValidateUVChannels(uvs2D, vertices.Length, nameof(uvs2D));
            ValidateUVChannels(uvs3D, vertices.Length, nameof(uvs3D));
            ValidateUVChannels(uvs4D, vertices.Length, nameof(uvs4D));
            ValidateFiniteUVChannels(uvs2D, nameof(uvs2D));
            ValidateFiniteUVChannels(uvs3D, nameof(uvs3D));
            ValidateFiniteUVChannels(uvs4D, nameof(uvs4D));
            ValidateNoConflictingUVChannels(uvs2D, uvs3D, uvs4D);

            int subMeshCount = indices.Length;
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] subMeshIndices = indices[subMeshIndex];
                if (subMeshIndices == null)
                    throw new ArgumentException("A sub-mesh index array cannot be null.", nameof(indices));
                if ((subMeshIndices.Length % 3) != 0)
                    throw new ArgumentException("Every sub-mesh index array must contain a multiple of three indices.", nameof(indices));

                for (int index = 0; index < subMeshIndices.Length; index++)
                {
                    int vertexIndex = subMeshIndices[index];
                    if (vertexIndex < 0 || vertexIndex >= vertices.Length)
                        throw new ArgumentOutOfRangeException(nameof(indices), "A triangle index is outside the vertex array.");
                }
            }

            var newMesh = new Mesh();

            IndexFormat indexFormat;
            var indexMinMax = GetSubMeshIndexMinMax(indices, out indexFormat);
            newMesh.indexFormat = indexFormat;
            newMesh.vertices = vertices;
            newMesh.subMeshCount = subMeshCount;

            if (bindposes != null && bindposes.Length > 0)
                newMesh.bindposes = (Matrix4x4[])bindposes.Clone();
            if (normals != null && normals.Length > 0)
                newMesh.normals = normals;
            if (tangents != null && tangents.Length > 0)
                newMesh.tangents = tangents;
            if (colors != null && colors.Length > 0)
                newMesh.colors = colors;

            if (uvs2D != null)
            {
                for (int uvChannel = 0; uvChannel < uvs2D.Length; uvChannel++)
                {
                    if (uvs2D[uvChannel] != null && uvs2D[uvChannel].Count > 0)
                        newMesh.SetUVs(uvChannel, uvs2D[uvChannel]);
                }
            }

            if (uvs3D != null)
            {
                for (int uvChannel = 0; uvChannel < uvs3D.Length; uvChannel++)
                {
                    if (uvs3D[uvChannel] != null && uvs3D[uvChannel].Count > 0)
                        newMesh.SetUVs(uvChannel, uvs3D[uvChannel]);
                }
            }

            if (uvs4D != null)
            {
                for (int uvChannel = 0; uvChannel < uvs4D.Length; uvChannel++)
                {
                    if (uvs4D[uvChannel] != null && uvs4D[uvChannel].Count > 0)
                        newMesh.SetUVs(uvChannel, uvs4D[uvChannel]);
                }
            }

            if (blendShapes != null)
                ApplyMeshBlendShapes(newMesh, blendShapes);

            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                int[] sourceTriangles = indices[subMeshIndex];
                Vector2Int minMax = indexMinMax[subMeshIndex];
                if (indexFormat == IndexFormat.UInt16 && sourceTriangles.Length > 0 && minMax.y > ushort.MaxValue)
                {
                    int baseVertex = minMax.x;
                    int[] localTriangles = new int[sourceTriangles.Length];
                    for (int index = 0; index < sourceTriangles.Length; index++)
                        localTriangles[index] = sourceTriangles[index] - baseVertex;

                    newMesh.SetTriangles(localTriangles, subMeshIndex, false, baseVertex);
                }
                else
                {
                    // SetTriangles copies the data; never rewrite the caller's arrays.
                    newMesh.SetTriangles(sourceTriangles, subMeshIndex, false, 0);
                }
            }

            if (boneWeights != null)
                ApplyMeshBoneWeights(newMesh, boneWeights);

            newMesh.RecalculateBounds();
            return newMesh;
        }

        /// <summary>
        /// Returns a deep copy of all bone influences for every vertex.
        /// </summary>
        public static BoneWeight1[][] GetMeshBoneWeights(Mesh mesh)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            int vertexCount = mesh.vertexCount;
            var result = new BoneWeight1[vertexCount][];
            NativeArray<byte> bonesPerVertex = mesh.GetBonesPerVertex();
            NativeArray<BoneWeight1> allWeights = mesh.GetAllBoneWeights();
            if (bonesPerVertex.Length == 0 && allWeights.Length == 0)
                return null;
            if (bonesPerVertex.Length != vertexCount)
                throw new InvalidOperationException("The mesh bone-count buffer does not match its vertex count.");
            if (allWeights.Length == 0)
            {
                for (int vertexIndex = 0; vertexIndex < bonesPerVertex.Length; vertexIndex++)
                {
                    if (bonesPerVertex[vertexIndex] != 0)
                        throw new InvalidOperationException("The mesh bone weight buffers are inconsistent.");
                }
                return null;
            }

            int weightOffset = 0;
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                int count = vertexIndex < bonesPerVertex.Length ? bonesPerVertex[vertexIndex] : 0;
                var vertexWeights = new BoneWeight1[count];
                for (int influenceIndex = 0; influenceIndex < count; influenceIndex++)
                {
                    if (weightOffset >= allWeights.Length)
                        throw new InvalidOperationException("The mesh bone weight buffers are inconsistent.");

                    vertexWeights[influenceIndex] = allWeights[weightOffset++];
                }
                result[vertexIndex] = vertexWeights;
            }

            if (weightOffset != allWeights.Length)
                throw new InvalidOperationException("The mesh bone weight buffers contain unused influences.");

            return result;
        }

        /// <summary>
        /// Applies variable-count bone influences to a mesh.
        /// </summary>
        public static void ApplyMeshBoneWeights(Mesh mesh, BoneWeight1[][] boneWeights)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (boneWeights == null)
                return;
            if (boneWeights.Length != mesh.vertexCount)
                throw new ArgumentException("The bone weight vertex count must match the mesh vertex count.", nameof(boneWeights));

            int bindposeCount = mesh.bindposes != null ? mesh.bindposes.Length : 0;
            var normalizedWeights = new BoneWeight1[boneWeights.Length][];
            var accumulatedWeights = new Dictionary<int, double>(16);
            var sortedWeights = new List<KeyValuePair<int, double>>(16);
            int totalWeightCount = 0;
            for (int vertexIndex = 0; vertexIndex < boneWeights.Length; vertexIndex++)
            {
                BoneWeight1[] source = boneWeights[vertexIndex] ?? Array.Empty<BoneWeight1>();
                accumulatedWeights.Clear();
                for (int influenceIndex = 0; influenceIndex < source.Length; influenceIndex++)
                {
                    BoneWeight1 influence = source[influenceIndex];
                    if (influence.boneIndex < 0)
                        throw new ArgumentException("Bone indices cannot be negative.", nameof(boneWeights));
                    if (float.IsNaN(influence.weight) || float.IsInfinity(influence.weight) || influence.weight < 0f)
                        throw new ArgumentException("Bone weights must be finite and non-negative.", nameof(boneWeights));
                    if (influence.weight == 0f)
                        continue;
                    if (influence.boneIndex >= bindposeCount)
                    {
                        throw new ArgumentException(string.Format(
                            "Vertex {0} references bone index {1}, but the mesh only has {2} bind poses.",
                            vertexIndex, influence.boneIndex, bindposeCount), nameof(boneWeights));
                    }

                    double existing;
                    if (accumulatedWeights.TryGetValue(influence.boneIndex, out existing))
                        accumulatedWeights[influence.boneIndex] = existing + influence.weight;
                    else
                        accumulatedWeights.Add(influence.boneIndex, influence.weight);
                }

                if (accumulatedWeights.Count == 0)
                {
                    normalizedWeights[vertexIndex] = Array.Empty<BoneWeight1>();
                    continue;
                }

                if (accumulatedWeights.Count > byte.MaxValue)
                    throw new ArgumentException("Unity supports at most 255 unique bone influences per vertex.", nameof(boneWeights));

                sortedWeights.Clear();
                double sum = 0.0;
                foreach (KeyValuePair<int, double> pair in accumulatedWeights)
                {
                    if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value) || pair.Value <= 0.0)
                        throw new ArgumentException("Accumulated bone weights are invalid.", nameof(boneWeights));

                    sortedWeights.Add(pair);
                    sum += pair.Value;
                }
                if (double.IsNaN(sum) || double.IsInfinity(sum) || sum <= 0.0)
                    throw new ArgumentException("Bone weights could not be normalized.", nameof(boneWeights));

                sortedWeights.Sort(CompareAccumulatedBoneWeightsDescending);
                var copy = new BoneWeight1[sortedWeights.Count];
                double invSum = 1.0 / sum;
                float normalizedSum = 0f;
                for (int influenceIndex = 0; influenceIndex < copy.Length; influenceIndex++)
                {
                    KeyValuePair<int, double> influence = sortedWeights[influenceIndex];
                    float normalizedWeight = (float)(influence.Value * invSum);
                    copy[influenceIndex] = new BoneWeight1
                    {
                        boneIndex = influence.Key,
                        weight = normalizedWeight
                    };
                    normalizedSum += normalizedWeight;
                }

                // Minimize float-rounding drift in the emitted normalized weights.
                BoneWeight1 strongest = copy[0];
                strongest.weight += 1f - normalizedSum;
                if (strongest.weight <= 0f || float.IsNaN(strongest.weight) || float.IsInfinity(strongest.weight))
                    throw new ArgumentException("Bone weights could not be normalized safely.", nameof(boneWeights));
                copy[0] = strongest;

                normalizedWeights[vertexIndex] = copy;
                checked { totalWeightCount += copy.Length; }
            }

            var bonesPerVertexData = new byte[normalizedWeights.Length];
            var allWeightsData = new BoneWeight1[totalWeightCount];
            int offset = 0;
            for (int vertexIndex = 0; vertexIndex < normalizedWeights.Length; vertexIndex++)
            {
                BoneWeight1[] vertexWeights = normalizedWeights[vertexIndex];
                bonesPerVertexData[vertexIndex] = (byte)vertexWeights.Length;
                Array.Copy(vertexWeights, 0, allWeightsData, offset, vertexWeights.Length);
                offset += vertexWeights.Length;
            }

            var bonesPerVertex = new NativeArray<byte>(bonesPerVertexData, Allocator.Temp);
            var allWeights = new NativeArray<BoneWeight1>(allWeightsData, Allocator.Temp);
            try
            {
                mesh.SetBoneWeights(bonesPerVertex, allWeights);
            }
            finally
            {
                allWeights.Dispose();
                bonesPerVertex.Dispose();
            }
        }

        /// <summary>
        /// Converts legacy four-influence bone weights into variable-count influences.
        /// </summary>
        public static BoneWeight1[][] ConvertLegacyBoneWeights(BoneWeight[] boneWeights)
        {
            if (boneWeights == null || boneWeights.Length == 0)
                return null;

            var result = new BoneWeight1[boneWeights.Length][];
            for (int vertexIndex = 0; vertexIndex < boneWeights.Length; vertexIndex++)
            {
                BoneWeight source = boneWeights[vertexIndex];
                var influences = new BoneWeight1[4];
                int count = 0;
                AddLegacyInfluence(influences, ref count, source.boneIndex0, source.weight0);
                AddLegacyInfluence(influences, ref count, source.boneIndex1, source.weight1);
                AddLegacyInfluence(influences, ref count, source.boneIndex2, source.weight2);
                AddLegacyInfluence(influences, ref count, source.boneIndex3, source.weight3);
                Array.Resize(ref influences, count);
                result[vertexIndex] = influences;
            }
            return result;
        }

        /// <summary>
        /// Converts variable-count influences to Unity's legacy four-influence representation.
        /// Influences beyond the strongest four are intentionally discarded.
        /// </summary>
        public static BoneWeight[] ConvertToLegacyBoneWeights(BoneWeight1[][] boneWeights)
        {
            if (boneWeights == null)
                return null;

            var result = new BoneWeight[boneWeights.Length];
            for (int vertexIndex = 0; vertexIndex < boneWeights.Length; vertexIndex++)
            {
                BoneWeight1[] source = boneWeights[vertexIndex] ?? Array.Empty<BoneWeight1>();
                BoneWeight1[] copy = (BoneWeight1[])source.Clone();
                Array.Sort(copy, CompareBoneWeightsDescending);

                int count = Math.Min(4, copy.Length);
                float sum = 0f;
                for (int i = 0; i < count; i++)
                    sum += Math.Max(0f, copy[i].weight);

                if (sum <= float.Epsilon)
                    continue;

                float invSum = 1f / sum;
                BoneWeight legacy = new BoneWeight();
                if (count > 0) { legacy.boneIndex0 = copy[0].boneIndex; legacy.weight0 = copy[0].weight * invSum; }
                if (count > 1) { legacy.boneIndex1 = copy[1].boneIndex; legacy.weight1 = copy[1].weight * invSum; }
                if (count > 2) { legacy.boneIndex2 = copy[2].boneIndex; legacy.weight2 = copy[2].weight * invSum; }
                if (count > 3) { legacy.boneIndex3 = copy[3].boneIndex; legacy.weight3 = copy[3].weight * invSum; }
                result[vertexIndex] = legacy;
            }
            return result;
        }

        /// <summary>
        /// Returns the declared dimensionality of a UV channel.
        /// </summary>
        public static int GetMeshUVChannelDimension(Mesh mesh, int channel)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            return mesh.GetVertexAttributeDimension(GetTexCoordAttribute(channel));
        }

        /// <summary>
        /// Returns the blend shapes of a mesh.
        /// </summary>
        /// <param name="mesh">The mesh.</param>
        /// <returns>The mesh blend shapes.</returns>
        public static BlendShape[] GetMeshBlendShapes(Mesh mesh)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            int vertexCount = mesh.vertexCount;
            int blendShapeCount = mesh.blendShapeCount;
            if (blendShapeCount == 0)
                return null;

            var blendShapes = new BlendShape[blendShapeCount];

            for (int blendShapeIndex = 0; blendShapeIndex < blendShapeCount; blendShapeIndex++)
            {
                string shapeName = mesh.GetBlendShapeName(blendShapeIndex);
                int frameCount = mesh.GetBlendShapeFrameCount(blendShapeIndex);
                var frames = new BlendShapeFrame[frameCount];

                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    float frameWeight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, frameIndex);

                    var deltaVertices = new Vector3[vertexCount];
                    var deltaNormals = new Vector3[vertexCount];
                    var deltaTangents = new Vector3[vertexCount];
                    mesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);

                    // Unity's getter returns zero-filled arrays even when an optional delta stream is
                    // effectively absent. Preserve that as null to avoid retaining two unnecessary
                    // avatar-sized arrays per frame.
                    frames[frameIndex] = new BlendShapeFrame(
                        frameWeight,
                        deltaVertices,
                        HasAnyNonZero(deltaNormals) ? deltaNormals : null,
                        HasAnyNonZero(deltaTangents) ? deltaTangents : null);
                }

                blendShapes[blendShapeIndex] = new BlendShape(shapeName, frames);
            }

            return blendShapes;
        }

        /// <summary>
        /// Applies and overrides the specified blend shapes on the specified mesh.
        /// </summary>
        /// <param name="mesh">The mesh.</param>
        /// <param name="blendShapes">The mesh blend shapes.</param>
        public static void ApplyMeshBlendShapes(Mesh mesh, BlendShape[] blendShapes)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            // Validate the entire set before modifying the destination mesh. This keeps the operation
            // transactional when a later shape or frame is malformed.
            ValidateBlendShapes(blendShapes, mesh.vertexCount, nameof(blendShapes));

            mesh.ClearBlendShapes();
            if (blendShapes == null || blendShapes.Length == 0)
                return;

            for (int blendShapeIndex = 0; blendShapeIndex < blendShapes.Length; blendShapeIndex++)
            {
                BlendShape blendShape = blendShapes[blendShapeIndex];
                for (int frameIndex = 0; frameIndex < blendShape.Frames.Length; frameIndex++)
                {
                    BlendShapeFrame frame = blendShape.Frames[frameIndex];
                    mesh.AddBlendShapeFrame(
                        blendShape.ShapeName,
                        frame.FrameWeight,
                        frame.DeltaVertices,
                        frame.DeltaNormals,
                        frame.DeltaTangents);
                }
            }
        }

        /// <summary>
        /// Returns the UV sets for a specific mesh.
        /// </summary>
        /// <param name="mesh">The mesh.</param>
        /// <returns>The UV sets.</returns>
        public static IList<Vector4>[] GetMeshUVs(Mesh mesh)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            var uvs = new IList<Vector4>[UVChannelCount];
            for (int channel = 0; channel < UVChannelCount; channel++)
            {
                uvs[channel] = GetMeshUVs(mesh, channel);
            }
            return uvs;
        }

        /// <summary>
        /// Returns the 2D UV list for a specific mesh and UV channel.
        /// </summary>
        /// <param name="mesh">The mesh.</param>
        /// <param name="channel">The UV channel.</param>
        /// <returns>The UV list.</returns>
        public static IList<Vector2> GetMeshUVs2D(Mesh mesh, int channel)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            else if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            var uvList = new List<Vector2>(mesh.vertexCount);
            mesh.GetUVs(channel, uvList);
            return uvList;
        }

        /// <summary>
        /// Returns the 3D UV list for a specific mesh and UV channel.
        /// </summary>
        /// <param name="mesh">The mesh.</param>
        /// <param name="channel">The UV channel.</param>
        /// <returns>The UV list.</returns>
        public static IList<Vector3> GetMeshUVs3D(Mesh mesh, int channel)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            else if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            var uvList = new List<Vector3>(mesh.vertexCount);
            mesh.GetUVs(channel, uvList);
            return uvList;
        }

        /// <summary>
        /// Returns the 4D UV list for a specific mesh and UV channel.
        /// </summary>
        /// <param name="mesh">The mesh.</param>
        /// <param name="channel">The UV channel.</param>
        /// <returns>The UV list.</returns>
        public static IList<Vector4> GetMeshUVs(Mesh mesh, int channel)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            else if (channel < 0 || channel >= UVChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channel));

            var uvList = new List<Vector4>(mesh.vertexCount);
            mesh.GetUVs(channel, uvList);
            return uvList;
        }

        /// <summary>
        /// Returns the number of used UV components in a UV set.
        /// </summary>
        /// <param name="uvs">The UV set.</param>
        /// <returns>The number of used UV components.</returns>
        [Obsolete("Value-based UV dimensionality detection is ambiguous. For Mesh data use GetMeshUVChannelDimension, or pass an explicit component count.", false)]
        public static int GetUsedUVComponents(IList<Vector4> uvs)
        {
            if (uvs == null || uvs.Count == 0)
                return 0;

            // Texture coordinates are overwhelmingly 2D, and an all-zero UV set is still a valid
            // 2D channel. Never discard it merely because every component happens to be zero.
            int usedComponents = 2;
            foreach (Vector4 uv in uvs)
            {
                if (uv.w != 0f)
                    return 4;
                if (uv.z != 0f)
                    usedComponents = 3;
            }
            return usedComponents;
        }

        /// <summary>
        /// Converts a list of 4D UVs into 2D.
        /// </summary>
        /// <param name="uvs">The list of UVs.</param>
        /// <returns>The array of 2D UVs.</returns>
        public static Vector2[] ConvertUVsTo2D(IList<Vector4> uvs)
        {
            if (uvs == null)
                return null;

            var uv2D = new Vector2[uvs.Count];
            for (int i = 0; i < uv2D.Length; i++)
            {
                var uv = uvs[i];
                uv2D[i] = new Vector2(uv.x, uv.y);
            }
            return uv2D;
        }

        /// <summary>
        /// Converts a list of 4D UVs into 3D.
        /// </summary>
        /// <param name="uvs">The list of UVs.</param>
        /// <returns>The array of 3D UVs.</returns>
        public static Vector3[] ConvertUVsTo3D(IList<Vector4> uvs)
        {
            if (uvs == null)
                return null;

            var uv3D = new Vector3[uvs.Count];
            for (int i = 0; i < uv3D.Length; i++)
            {
                var uv = uvs[i];
                uv3D[i] = new Vector3(uv.x, uv.y, uv.z);
            }
            return uv3D;
        }

        /// <summary>
        /// Returns the minimum and maximum indices for each submesh along with the needed index format.
        /// </summary>
        /// <param name="indices">The indices for the submeshes.</param>
        /// <param name="indexFormat">The output index format.</param>
        /// <returns>The minimum and maximum indices for each submesh.</returns>
        public static Vector2Int[] GetSubMeshIndexMinMax(int[][] indices, out IndexFormat indexFormat)
        {
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));

            var result = new Vector2Int[indices.Length];
            indexFormat = IndexFormat.UInt16;
            for (int subMeshIndex = 0; subMeshIndex < indices.Length; subMeshIndex++)
            {
                int minIndex, maxIndex;
                GetIndexMinMax(indices[subMeshIndex], out minIndex, out maxIndex);
                result[subMeshIndex] = new Vector2Int(minIndex, maxIndex);

                int indexRange = (maxIndex - minIndex);
                if (indexRange > ushort.MaxValue)
                {
                    indexFormat = IndexFormat.UInt32;
                }
            }
            return result;
        }
        #endregion

        #region Private Methods
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

        private static void AddLegacyInfluence(BoneWeight1[] destination, ref int count, int boneIndex, float weight)
        {
            if (float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0f)
                throw new ArgumentException("Legacy bone weights must be finite and non-negative.", nameof(weight));
            if (weight == 0f)
                return;
            if (boneIndex < 0)
                throw new ArgumentException("Legacy bone indices cannot be negative.", nameof(boneIndex));

            destination[count++] = new BoneWeight1
            {
                boneIndex = boneIndex,
                weight = weight
            };
        }

        private static VertexAttribute GetTexCoordAttribute(int channel)
        {
            switch (channel)
            {
                case 0: return VertexAttribute.TexCoord0;
                case 1: return VertexAttribute.TexCoord1;
                case 2: return VertexAttribute.TexCoord2;
                case 3: return VertexAttribute.TexCoord3;
#if UNITY_8UV_SUPPORT
                case 4: return VertexAttribute.TexCoord4;
                case 5: return VertexAttribute.TexCoord5;
                case 6: return VertexAttribute.TexCoord6;
                case 7: return VertexAttribute.TexCoord7;
#endif
                default: throw new ArgumentOutOfRangeException(nameof(channel));
            }
        }

        private static void ValidateBlendShapes(BlendShape[] blendShapes, int vertexCount, string parameterName)
        {
            if (blendShapes == null || blendShapes.Length == 0)
                return;

            var shapeNames = new HashSet<string>(StringComparer.Ordinal);
            for (int blendShapeIndex = 0; blendShapeIndex < blendShapes.Length; blendShapeIndex++)
            {
                BlendShape blendShape = blendShapes[blendShapeIndex];
                if (string.IsNullOrEmpty(blendShape.ShapeName))
                    throw new ArgumentException("Blend shape names cannot be null or empty.", parameterName);
                if (!shapeNames.Add(blendShape.ShapeName))
                    throw new ArgumentException(string.Format("Blend shape name '{0}' is duplicated.", blendShape.ShapeName), parameterName);
                if (blendShape.Frames == null || blendShape.Frames.Length == 0)
                    throw new ArgumentException(string.Format("Blend shape '{0}' has no frames.", blendShape.ShapeName), parameterName);

                float previousWeight = float.NegativeInfinity;
                for (int frameIndex = 0; frameIndex < blendShape.Frames.Length; frameIndex++)
                {
                    BlendShapeFrame frame = blendShape.Frames[frameIndex];
                    if (!IsFinite(frame.FrameWeight) || (frameIndex > 0 && frame.FrameWeight <= previousWeight))
                    {
                        throw new ArgumentException(string.Format(
                            "Blend shape '{0}' has invalid or non-increasing frame weights.", blendShape.ShapeName), parameterName);
                    }
                    previousWeight = frame.FrameWeight;

                    if (frame.DeltaVertices == null || frame.DeltaVertices.Length != vertexCount)
                        throw new ArgumentException(string.Format("Blend shape '{0}' delta vertices must match the mesh vertex count.", blendShape.ShapeName), parameterName);
                    if (frame.DeltaNormals != null && frame.DeltaNormals.Length != vertexCount)
                        throw new ArgumentException(string.Format("Blend shape '{0}' delta normals must be null or match the mesh vertex count.", blendShape.ShapeName), parameterName);
                    if (frame.DeltaTangents != null && frame.DeltaTangents.Length != vertexCount)
                        throw new ArgumentException(string.Format("Blend shape '{0}' delta tangents must be null or match the mesh vertex count.", blendShape.ShapeName), parameterName);

                    ValidateFinite(frame.DeltaVertices, parameterName);
                    ValidateFinite(frame.DeltaNormals, parameterName);
                    ValidateFinite(frame.DeltaTangents, parameterName);
                }
            }
        }

        private static bool HasAnyNonZero(Vector3[] values)
        {
            if (values == null)
                return false;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].x != 0f || values[i].y != 0f || values[i].z != 0f)
                    return true;
            }
            return false;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void ValidateFinite(Vector3[] values, string parameterName)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                Vector3 value = values[i];
                if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
                    throw new ArgumentException(string.Format("A non-finite value was found at index {0}.", i), parameterName);
            }
        }

        private static void ValidateFinite(Vector4[] values, string parameterName)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                Vector4 value = values[i];
                if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z) || !IsFinite(value.w))
                    throw new ArgumentException(string.Format("A non-finite value was found at index {0}.", i), parameterName);
            }
        }

        private static void ValidateFinite(Color[] values, string parameterName)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                Color value = values[i];
                if (!IsFinite(value.r) || !IsFinite(value.g) || !IsFinite(value.b) || !IsFinite(value.a))
                    throw new ArgumentException(string.Format("A non-finite value was found at index {0}.", i), parameterName);
            }
        }

        private static void ValidateFinite(Matrix4x4[] values, string parameterName)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        if (!IsFinite(values[i][row, column]))
                            throw new ArgumentException(string.Format("A non-finite matrix value was found at index {0}.", i), parameterName);
                    }
                }
            }
        }

        private static void ValidateFiniteUVChannels(List<Vector2>[] channels, string parameterName)
        {
            if (channels == null) return;
            for (int channel = 0; channel < channels.Length; channel++)
            {
                List<Vector2> values = channels[channel];
                if (values == null) continue;
                for (int i = 0; i < values.Count; i++)
                {
                    Vector2 value = values[i];
                    if (!IsFinite(value.x) || !IsFinite(value.y))
                        throw new ArgumentException(string.Format("UV channel {0} contains a non-finite value at index {1}.", channel, i), parameterName);
                }
            }
        }

        private static void ValidateFiniteUVChannels(List<Vector3>[] channels, string parameterName)
        {
            if (channels == null) return;
            for (int channel = 0; channel < channels.Length; channel++)
            {
                List<Vector3> values = channels[channel];
                if (values == null) continue;
                for (int i = 0; i < values.Count; i++)
                {
                    Vector3 value = values[i];
                    if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
                        throw new ArgumentException(string.Format("UV channel {0} contains a non-finite value at index {1}.", channel, i), parameterName);
                }
            }
        }

        private static void ValidateFiniteUVChannels(List<Vector4>[] channels, string parameterName)
        {
            if (channels == null) return;
            for (int channel = 0; channel < channels.Length; channel++)
            {
                List<Vector4> values = channels[channel];
                if (values == null) continue;
                for (int i = 0; i < values.Count; i++)
                {
                    Vector4 value = values[i];
                    if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z) || !IsFinite(value.w))
                        throw new ArgumentException(string.Format("UV channel {0} contains a non-finite value at index {1}.", channel, i), parameterName);
                }
            }
        }

        private static void ValidateNoConflictingUVChannels(List<Vector2>[] uvs2D, List<Vector3>[] uvs3D, List<Vector4>[] uvs4D)
        {
            for (int channel = 0; channel < UVChannelCount; channel++)
            {
                int populatedDimensions = 0;
                if (uvs2D != null && channel < uvs2D.Length && uvs2D[channel] != null && uvs2D[channel].Count > 0) populatedDimensions++;
                if (uvs3D != null && channel < uvs3D.Length && uvs3D[channel] != null && uvs3D[channel].Count > 0) populatedDimensions++;
                if (uvs4D != null && channel < uvs4D.Length && uvs4D[channel] != null && uvs4D[channel].Count > 0) populatedDimensions++;
                if (populatedDimensions > 1)
                    throw new ArgumentException(string.Format("UV channel {0} was supplied with more than one dimensionality.", channel));
            }
        }

        private static void ValidateVertexAttributeLength<T>(T[] values, int vertexCount, string parameterName)
        {
            if (values != null && values.Length != 0 && values.Length != vertexCount)
                throw new ArgumentException("Vertex attribute arrays must be empty or match the mesh vertex count.", parameterName);
        }

        private static void ValidateUVChannels<T>(List<T>[] channels, int vertexCount, string parameterName)
        {
            if (channels == null)
                return;
            if (channels.Length > UVChannelCount)
                throw new ArgumentException("Too many UV channels were supplied.", parameterName);

            for (int channel = 0; channel < channels.Length; channel++)
            {
                List<T> values = channels[channel];
                if (values != null && values.Count != 0 && values.Count != vertexCount)
                    throw new ArgumentException("UV channel lengths must be empty or match the mesh vertex count.", parameterName);
            }
        }

        private static void GetIndexMinMax(int[] indices, out int minIndex, out int maxIndex)
        {
            if (indices == null || indices.Length == 0)
            {
                minIndex = maxIndex = 0;
                return;
            }

            minIndex = int.MaxValue;
            maxIndex = int.MinValue;

            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] < minIndex)
                {
                    minIndex = indices[i];
                }
                if (indices[i] > maxIndex)
                {
                    maxIndex = indices[i];
                }
            }
        }
        #endregion
    }
}
