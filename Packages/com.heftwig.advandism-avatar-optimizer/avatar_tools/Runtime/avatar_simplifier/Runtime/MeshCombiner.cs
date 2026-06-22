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

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityMeshSimplifier
{
    /// <summary>
    /// Contains methods for combining meshes.
    /// </summary>
    public static class MeshCombiner
    {
        #region Public Methods
        /// <summary>
        /// Combines an array of mesh renderers into one single mesh.
        /// </summary>
        /// <param name="rootTransform">The root transform to create the combine mesh based from, essentially the origin of the new mesh.</param>
        /// <param name="renderers">The array of mesh renderers to combine.</param>
        /// <param name="resultMaterials">The resulting materials for the combined mesh.</param>
        /// <returns>The combined mesh.</returns>
        public static Mesh CombineMeshes(Transform rootTransform, MeshRenderer[] renderers, out Material[] resultMaterials)
        {
            if (rootTransform == null)
                throw new System.ArgumentNullException(nameof(rootTransform));
            else if (renderers == null)
                throw new System.ArgumentNullException(nameof(renderers));

            var meshes = new Mesh[renderers.Length];
            var transforms = new Matrix4x4[renderers.Length];
            var materials = new Material[renderers.Length][];

            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                    throw new System.ArgumentException(string.Format("The renderer at index {0} is null.", i), nameof(renderers));

                var rendererTransform = renderer.transform;
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null)
                    throw new System.ArgumentException(string.Format("The renderer at index {0} has no mesh filter.", i), nameof(renderers));
                else if (meshFilter.sharedMesh == null)
                    throw new System.ArgumentException(string.Format("The mesh filter for renderer at index {0} has no mesh.", i), nameof(renderers));
                else if (!CanReadMesh(meshFilter.sharedMesh))
                    throw new System.ArgumentException(string.Format("The mesh in the mesh filter for renderer at index {0} is not readable.", i), nameof(renderers));

                meshes[i] = meshFilter.sharedMesh;
                transforms[i] = rootTransform.worldToLocalMatrix * rendererTransform.localToWorldMatrix;
                materials[i] = renderer.sharedMaterials;
            }

            return CombineMeshes(meshes, transforms, materials, out resultMaterials);
        }

        /// <summary>
        /// Combines an array of skinned mesh renderers into one single skinned mesh.
        /// </summary>
        /// <param name="rootTransform">The root transform to create the combine mesh based from, essentially the origin of the new mesh.</param>
        /// <param name="renderers">The array of skinned mesh renderers to combine.</param>
        /// <param name="resultMaterials">The resulting materials for the combined mesh.</param>
        /// <param name="resultBones">The resulting bones for the combined mesh.</param>
        /// <returns>The combined mesh.</returns>
        public static Mesh CombineMeshes(Transform rootTransform, SkinnedMeshRenderer[] renderers, out Material[] resultMaterials, out Transform[] resultBones)
        {
            if (rootTransform == null)
                throw new System.ArgumentNullException(nameof(rootTransform));
            else if (renderers == null)
                throw new System.ArgumentNullException(nameof(renderers));

            var meshes = new Mesh[renderers.Length];
            var transforms = new Matrix4x4[renderers.Length];
            var materials = new Material[renderers.Length][];
            var bones = new Transform[renderers.Length][];

            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                    throw new System.ArgumentException(string.Format("The renderer at index {0} is null.", i), nameof(renderers));
                else if (renderer.sharedMesh == null)
                    throw new System.ArgumentException(string.Format("The renderer at index {0} has no mesh.", i), nameof(renderers));
                else if (!CanReadMesh(renderer.sharedMesh))
                    throw new System.ArgumentException(string.Format("The mesh in the renderer at index {0} is not readable.", i), nameof(renderers));
                else if (renderer.GetComponent<Cloth>() != null)
                    throw new System.NotSupportedException(string.Format(
                        "The skinned renderer at index {0} has a Cloth component. Cloth coefficients and constraints are vertex-indexed and cannot be combined safely without an explicit remap.", i));

                var rendererTransform = renderer.transform;
                meshes[i] = renderer.sharedMesh;
                transforms[i] = rootTransform.worldToLocalMatrix * rendererTransform.localToWorldMatrix;
                materials[i] = renderer.sharedMaterials;
                bones[i] = renderer.bones;
            }

            return CombineMeshes(meshes, transforms, materials, bones, out resultMaterials, out resultBones);
        }

        /// <summary>
        /// Combines an array of meshes into a single mesh.
        /// </summary>
        /// <param name="meshes">The array of meshes to combine.</param>
        /// <param name="transforms">The array of transforms for the meshes.</param>
        /// <param name="materials">The array of materials for each mesh to combine.</param>
        /// <param name="resultMaterials">The resulting materials for the combined mesh.</param>
        /// <returns>The combined mesh.</returns>
        public static Mesh CombineMeshes(Mesh[] meshes, Matrix4x4[] transforms, Material[][] materials, out Material[] resultMaterials)
        {
            if (meshes == null)
                throw new System.ArgumentNullException(nameof(meshes));
            else if (transforms == null)
                throw new System.ArgumentNullException(nameof(transforms));
            else if (materials == null)
                throw new System.ArgumentNullException(nameof(materials));

            Transform[] resultBones;
            return CombineMeshes(meshes, transforms, materials, null, out resultMaterials, out resultBones);
        }

        /// <summary>
        /// Combines an array of meshes into a single mesh.
        /// </summary>
        /// <param name="meshes">The array of meshes to combine.</param>
        /// <param name="transforms">The array of transforms for the meshes.</param>
        /// <param name="materials">The array of materials for each mesh to combine.</param>
        /// <param name="bones">The array of bones for each mesh to combine.</param>
        /// <param name="resultMaterials">The resulting materials for the combined mesh.</param>
        /// <param name="resultBones">The resulting bones for the combined mesh.</param>
        /// <returns>The combined mesh.</returns>
        public static Mesh CombineMeshes(Mesh[] meshes, Matrix4x4[] transforms, Material[][] materials, Transform[][] bones, out Material[] resultMaterials, out Transform[] resultBones)
        {
            if (meshes == null)
                throw new System.ArgumentNullException(nameof(meshes));
            else if (transforms == null)
                throw new System.ArgumentNullException(nameof(transforms));
            else if (materials == null)
                throw new System.ArgumentNullException(nameof(materials));
            else if (transforms.Length != meshes.Length)
                throw new System.ArgumentException("The array of transforms doesn't have the same length as the array of meshes.", nameof(transforms));
            else if (materials.Length != meshes.Length)
                throw new System.ArgumentException("The array of materials doesn't have the same length as the array of meshes.", nameof(materials));
            else if (bones != null && bones.Length != meshes.Length)
                throw new System.ArgumentException("The array of bones doesn't have the same length as the array of meshes.", nameof(bones));

            int totalVertexCount = 0;
            int totalSubMeshCount = 0;
            bool? meshesHaveNormals = null;
            bool? meshesHaveTangents = null;
            var combinedUVDimensions = new int[MeshUtils.UVChannelCount];
            for (int meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
            {
                var mesh = meshes[meshIndex];
                if (mesh == null)
                    throw new System.ArgumentException(string.Format("The mesh at index {0} is null.", meshIndex), nameof(meshes));
                else if (!CanReadMesh(mesh))
                    throw new System.ArgumentException(string.Format("The mesh at index {0} is not readable.", meshIndex), nameof(meshes));
                else if (mesh.blendShapeCount > 0)
                    throw new System.NotSupportedException(string.Format(
                        "MeshCombiner cannot safely merge blend shapes yet. Mesh '{0}' contains {1} blend shape(s); combine it with a blend-shape-aware pipeline instead of silently losing avatar morph data.",
                        mesh.name, mesh.blendShapeCount));

                float transformDeterminant = transforms[meshIndex].determinant;
                if (float.IsNaN(transformDeterminant) || float.IsInfinity(transformDeterminant) || System.Math.Abs(transformDeterminant) <= 1e-12f)
                    throw new System.ArgumentException(string.Format("The transform for mesh at index {0} is singular or non-finite.", meshIndex), nameof(transforms));

                checked
                {
                    totalVertexCount += mesh.vertexCount;
                    totalSubMeshCount += mesh.subMeshCount;
                }

                if (!mesh.HasVertexAttribute(VertexAttribute.Position) || mesh.GetVertexAttributeDimension(VertexAttribute.Position) != 3)
                    throw new System.NotSupportedException(string.Format("Mesh '{0}' must provide a three-dimensional position stream.", mesh.name));
                if (mesh.HasVertexAttribute(VertexAttribute.Color) && mesh.GetVertexAttributeDimension(VertexAttribute.Color) != 4)
                    throw new System.NotSupportedException(string.Format("Mesh '{0}' has a color stream that is not four-dimensional.", mesh.name));

                bool hasNormals = mesh.HasVertexAttribute(VertexAttribute.Normal);
                bool hasTangents = mesh.HasVertexAttribute(VertexAttribute.Tangent);
                if (hasNormals && mesh.GetVertexAttributeDimension(VertexAttribute.Normal) != 3)
                    throw new System.NotSupportedException(string.Format("Mesh '{0}' has a normal stream that is not three-dimensional.", mesh.name));
                if (hasTangents && mesh.GetVertexAttributeDimension(VertexAttribute.Tangent) != 4)
                    throw new System.NotSupportedException(string.Format("Mesh '{0}' has a tangent stream that is not four-dimensional.", mesh.name));
                if (meshesHaveNormals.HasValue && meshesHaveNormals.Value != hasNormals)
                    throw new System.NotSupportedException("All meshes must either provide normals or omit normals. Mixing populated and missing normal streams would require guessing vertex data.");
                if (meshesHaveTangents.HasValue && meshesHaveTangents.Value != hasTangents)
                    throw new System.NotSupportedException("All meshes must either provide tangents or omit tangents. Mixing populated and missing tangent streams would require guessing vertex data.");
                meshesHaveNormals = hasNormals;
                meshesHaveTangents = hasTangents;

                for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
                {
                    MeshTopology topology = mesh.GetTopology(subMeshIndex);
                    if (topology != MeshTopology.Triangles)
                        throw new System.NotSupportedException(string.Format(
                            "Mesh '{0}' submesh {1} uses {2}; MeshCombiner only supports triangle topology.",
                            mesh.name, subMeshIndex, topology));
                }

                for (int channel = 0; channel < MeshUtils.UVChannelCount; channel++)
                {
                    int uvDimension = MeshUtils.GetMeshUVChannelDimension(mesh, channel);
                    if (uvDimension == 1)
                        throw new System.NotSupportedException(string.Format("Mesh '{0}' UV channel {1} uses an unsupported one-component layout.", mesh.name, channel));
                    combinedUVDimensions[channel] = System.Math.Max(combinedUVDimensions[channel], uvDimension);
                }

                // Validate the mesh materials
                var meshMaterials = materials[meshIndex];
                if (meshMaterials == null)
                    throw new System.ArgumentException(string.Format("The materials for mesh at index {0} is null.", meshIndex), nameof(materials));
                else if (meshMaterials.Length != mesh.subMeshCount)
                    throw new System.ArgumentException(
                        string.Format("The materials for mesh at index {0} doesn't match the submesh count ({1} != {2}).",
                            meshIndex, meshMaterials.Length, mesh.subMeshCount), nameof(materials));

                for (int materialIndex = 0; materialIndex < meshMaterials.Length; materialIndex++)
                {
                    if (meshMaterials[materialIndex] == null)
                        throw new System.ArgumentException(string.Format("The material at index {0} for mesh at index {1} is null.", materialIndex, meshIndex), nameof(materials));
                }

                // Validate the mesh bones
                if (bones != null)
                {
                    var meshBones = bones[meshIndex];
                    if (meshBones == null)
                        throw new System.ArgumentException(string.Format("The bones for mesh at index {0} is null.", meshIndex), nameof(meshBones));
                    if (mesh.bindposes == null || mesh.bindposes.Length != meshBones.Length)
                        throw new System.ArgumentException(string.Format(
                            "The bind pose count for mesh at index {0} does not match its renderer bone count ({1} != {2}).",
                            meshIndex, mesh.bindposes != null ? mesh.bindposes.Length : 0, meshBones.Length), nameof(bones));

                    for (int boneIndex = 0; boneIndex < meshBones.Length; boneIndex++)
                    {
                        if (meshBones[boneIndex] == null)
                            throw new System.ArgumentException(string.Format("The bone at index {0} for mesh at index {1} is null.", boneIndex, meshIndex), nameof(meshBones));
                    }
                }
            }

            var combinedVertices = new List<Vector3>(totalVertexCount);
            var combinedIndices = new List<List<int>>(totalSubMeshCount);
            List<Vector3> combinedNormals = null;
            List<Vector4> combinedTangents = null;
            List<Color> combinedColors = null;
            List<BoneWeight1[]> combinedBoneWeights = null;
            var combinedUV2D = new List<Vector2>[MeshUtils.UVChannelCount];
            var combinedUV3D = new List<Vector3>[MeshUtils.UVChannelCount];
            var combinedUV4D = new List<Vector4>[MeshUtils.UVChannelCount];

            List<Matrix4x4> usedBindposes = null;
            List<Transform> usedBones = null;
            var usedMaterials = new List<Material>(totalSubMeshCount);
            var materialMap = new Dictionary<Material, int>(totalSubMeshCount);

            int currentVertexCount = 0;
            for (int meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
            {
                var mesh = meshes[meshIndex];
                var meshTransform = transforms[meshIndex];
                var meshMaterials = materials[meshIndex];
                var meshBones = (bones != null ? bones[meshIndex] : null);

                int subMeshCount = mesh.subMeshCount;
                int meshVertexCount = mesh.vertexCount;
                var meshVertices = mesh.vertices;
                var meshNormals = mesh.normals;
                var meshTangents = mesh.tangents;
                var meshColors = mesh.colors;
                var meshBoneWeights = MeshUtils.GetMeshBoneWeights(mesh);
                var meshBindposes = mesh.bindposes;

                if (meshBones == null && meshBoneWeights != null)
                    throw new System.InvalidOperationException(string.Format("Mesh '{0}' contains skinning data but no bone array was supplied.", mesh.name));
                if (meshBones != null && meshVertexCount > 0 && (meshBones.Length == 0 || meshBoneWeights == null))
                    throw new System.InvalidOperationException(string.Format(
                        "Mesh '{0}' is being combined as a skinned mesh but has no usable bone influences. Assign valid skinning data before combining it.", mesh.name));

                // Convert bind poses from each source renderer's local space into the combined root space.
                if (meshBones != null && meshBoneWeights != null && meshBoneWeights.Length > 0)
                {
                    if (usedBindposes == null)
                    {
                        usedBindposes = new List<Matrix4x4>(meshBindposes.Length);
                        usedBones = new List<Transform>(meshBones.Length);
                    }

                    Matrix4x4 inverseMeshTransform = meshTransform.inverse;
                    int[] boneIndices = new int[meshBones.Length];
                    for (int i = 0; i < meshBones.Length; i++)
                    {
                        Matrix4x4 transformedBindpose = meshBindposes[i] * inverseMeshTransform;
                        int usedBoneIndex = FindBoneBindposeIndex(usedBones, usedBindposes, meshBones[i], transformedBindpose);
                        if (usedBoneIndex == -1)
                        {
                            usedBoneIndex = usedBones.Count;
                            usedBones.Add(meshBones[i]);
                            usedBindposes.Add(transformedBindpose);
                        }
                        boneIndices[i] = usedBoneIndex;
                    }

                    // Then we remap the bones
                    RemapBones(meshBoneWeights, boneIndices);
                }

                // Transform vertices, normals, and tangents into the combined root space.
                bool flipWinding = meshTransform.determinant < 0f;
                TransformVertices(meshVertices, ref meshTransform);
                TransformNormals(meshNormals, ref meshTransform);
                TransformTangents(meshTangents, meshNormals, ref meshTransform, flipWinding);

                // Copy vertex positions & attributes
                CopyVertexPositions(combinedVertices, meshVertices);
                CopyVertexAttributes(ref combinedNormals, meshNormals, currentVertexCount, meshVertexCount, totalVertexCount, new Vector3(1f, 0f, 0f));
                CopyVertexAttributes(ref combinedTangents, meshTangents, currentVertexCount, meshVertexCount, totalVertexCount, new Vector4(0f, 0f, 1f, 1f));
                CopyVertexAttributes(ref combinedColors, meshColors, currentVertexCount, meshVertexCount, totalVertexCount, new Color(1f, 1f, 1f, 1f));
                CopyVertexAttributes(ref combinedBoneWeights, meshBoneWeights, currentVertexCount, meshVertexCount, totalVertexCount, System.Array.Empty<BoneWeight1>());

                for (int channel = 0; channel < MeshUtils.UVChannelCount; channel++)
                {
                    switch (combinedUVDimensions[channel])
                    {
                        case 0:
                            break;
                        case 2:
                            CopyVertexAttributes(ref combinedUV2D[channel], MeshUtils.GetMeshUVs2D(mesh, channel), currentVertexCount, meshVertexCount, totalVertexCount, Vector2.zero);
                            break;
                        case 3:
                            CopyVertexAttributes(ref combinedUV3D[channel], MeshUtils.GetMeshUVs3D(mesh, channel), currentVertexCount, meshVertexCount, totalVertexCount, Vector3.zero);
                            break;
                        case 4:
                            CopyVertexAttributes(ref combinedUV4D[channel], MeshUtils.GetMeshUVs(mesh, channel), currentVertexCount, meshVertexCount, totalVertexCount, Vector4.zero);
                            break;
                        default:
                            throw new System.InvalidOperationException(string.Format("UV channel {0} has invalid dimension {1}.", channel, combinedUVDimensions[channel]));
                    }
                }

                for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                {
                    var subMeshMaterial = meshMaterials[subMeshIndex];
                    var subMeshIndices = mesh.GetTriangles(subMeshIndex, true);

                    if (flipWinding)
                    {
                        for (int triangleIndex = 0; triangleIndex < subMeshIndices.Length; triangleIndex += 3)
                        {
                            int temp = subMeshIndices[triangleIndex + 1];
                            subMeshIndices[triangleIndex + 1] = subMeshIndices[triangleIndex + 2];
                            subMeshIndices[triangleIndex + 2] = temp;
                        }
                    }

                    if (currentVertexCount > 0)
                    {
                        for (int index = 0; index < subMeshIndices.Length; index++)
                        {
                            subMeshIndices[index] += currentVertexCount;
                        }
                    }

                    int existingSubMeshIndex;
                    if (materialMap.TryGetValue(subMeshMaterial, out existingSubMeshIndex))
                    {
                        combinedIndices[existingSubMeshIndex].AddRange(subMeshIndices);
                    }
                    else
                    {
                        int materialIndex = combinedIndices.Count;
                        materialMap.Add(subMeshMaterial, materialIndex);
                        usedMaterials.Add(subMeshMaterial);
                        combinedIndices.Add(new List<int>(subMeshIndices));
                    }
                }

                currentVertexCount += meshVertexCount;
            }

            var resultVertices = combinedVertices.ToArray();
            var resultIndices = new int[combinedIndices.Count][];
            for (int subMeshIndex = 0; subMeshIndex < combinedIndices.Count; subMeshIndex++)
                resultIndices[subMeshIndex] = combinedIndices[subMeshIndex].ToArray();
            var resultNormals = (combinedNormals != null ? combinedNormals.ToArray() : null);
            var resultTangents = (combinedTangents != null ? combinedTangents.ToArray() : null);
            var resultColors = (combinedColors != null ? combinedColors.ToArray() : null);
            var resultBoneWeights = (combinedBoneWeights != null ? combinedBoneWeights.ToArray() : null);
            var resultBindposes = (usedBindposes != null ? usedBindposes.ToArray() : null);
            resultMaterials = usedMaterials.ToArray();
            resultBones = (usedBones != null ? usedBones.ToArray() : null);
            return MeshUtils.CreateMesh(resultVertices, resultIndices, resultNormals, resultTangents, resultColors, resultBoneWeights, combinedUV2D, combinedUV3D, combinedUV4D, resultBindposes, null);
        }
        #endregion

        #region Private Methods
        private static void CopyVertexPositions(List<Vector3> list, Vector3[] arr)
        {
            if (arr == null || arr.Length == 0)
                return;

            list.AddRange(arr);
        }

        private static void CopyVertexAttributes<T>(ref List<T> dest, IEnumerable<T> src, int previousVertexCount, int meshVertexCount, int totalVertexCount, T defaultValue)
        {
            ICollection<T> sourceCollection = src as ICollection<T>;
            if (src != null && sourceCollection == null)
            {
                // Avoid enumerating a one-shot source twice and make its length validation deterministic.
                sourceCollection = new List<T>(src);
            }

            int sourceCount = sourceCollection != null ? sourceCollection.Count : 0;
            if (sourceCount == 0)
            {
                if (dest != null)
                {
                    for (int i = 0; i < meshVertexCount; i++)
                        dest.Add(defaultValue);
                }
                return;
            }

            if (sourceCount != meshVertexCount)
                throw new System.InvalidOperationException(string.Format(
                    "A populated vertex attribute has {0} values, but the mesh has {1} vertices.", sourceCount, meshVertexCount));

            if (dest == null)
            {
                dest = new List<T>(totalVertexCount);
                for (int i = 0; i < previousVertexCount; i++)
                    dest.Add(defaultValue);
            }

            dest.AddRange(sourceCollection);
        }

        private static void TransformVertices(Vector3[] vertices, ref Matrix4x4 transform)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = transform.MultiplyPoint3x4(vertices[i]);
            }
        }

        private static void TransformNormals(Vector3[] normals, ref Matrix4x4 transform)
        {
            if (normals == null)
                return;

            Matrix4x4 normalTransform = transform.inverse.transpose;
            for (int i = 0; i < normals.Length; i++)
            {
                Vector3 normal = normalTransform.MultiplyVector(normals[i]);
                normals[i] = normal.sqrMagnitude > 1e-20f ? normal.normalized : Vector3.zero;
            }
        }

        private static void TransformTangents(Vector4[] tangents, Vector3[] transformedNormals, ref Matrix4x4 transform, bool flipHandedness)
        {
            if (tangents == null)
                return;

            for (int i = 0; i < tangents.Length; i++)
            {
                Vector4 source = tangents[i];
                Vector3 tangentDir = transform.MultiplyVector(new Vector3(source.x, source.y, source.z));

                // Non-uniform scale can make a transformed tangent non-orthogonal to its transformed
                // normal. Re-orthogonalize without inventing a fallback direction for zero data.
                if (transformedNormals != null && i < transformedNormals.Length)
                {
                    Vector3 normal = transformedNormals[i];
                    if (normal.sqrMagnitude > 1e-20f)
                        tangentDir -= normal * Vector3.Dot(normal, tangentDir);
                }

                if (tangentDir.sqrMagnitude > 1e-20f)
                    tangentDir.Normalize();
                else
                    tangentDir = Vector3.zero;

                float handedness = flipHandedness ? -source.w : source.w;
                if (handedness > 0f) handedness = 1f;
                else if (handedness < 0f) handedness = -1f;

                tangents[i] = new Vector4(tangentDir.x, tangentDir.y, tangentDir.z, handedness);
            }
        }

        private static int FindBoneBindposeIndex(List<Transform> bones, List<Matrix4x4> bindposes, Transform bone, Matrix4x4 bindpose)
        {
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i] == bone && bindposes[i] == bindpose)
                    return i;
            }
            return -1;
        }

        private static void RemapBones(BoneWeight1[][] boneWeights, int[] boneIndices)
        {
            for (int i = 0; i < boneWeights.Length; i++)
            {
                BoneWeight1[] vertexWeights = boneWeights[i];
                if (vertexWeights == null)
                    continue;

                for (int influenceIndex = 0; influenceIndex < vertexWeights.Length; influenceIndex++)
                {
                    BoneWeight1 influence = vertexWeights[influenceIndex];
                    if (influence.weight <= 0f)
                        continue;
                    if (influence.boneIndex < 0 || influence.boneIndex >= boneIndices.Length)
                        throw new System.InvalidOperationException(string.Format("A bone weight references invalid bone index {0}.", influence.boneIndex));

                    influence.boneIndex = boneIndices[influence.boneIndex];
                    vertexWeights[influenceIndex] = influence;
                }
            }
        }

        private static bool CanReadMesh(Mesh mesh)
        {
#if UNITY_EDITOR
            // Unity permits CPU access to some non-readable assets outside play mode, but that behavior
            // does not carry into a player build. Runtime combining therefore requires isReadable.
            if (!Application.isPlaying)
                return true;
#endif
            return mesh.isReadable;
        }

        #endregion
    }
}
