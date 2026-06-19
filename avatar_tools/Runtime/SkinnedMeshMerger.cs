using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Combines enabled SkinnedMeshRenderers and MeshRenderers below a root object into
/// one SkinnedMeshRenderer. Supports variable bone influences, blend shapes,
/// vertex colors, UV0-UV7, multiple submeshes, and non-triangle topology.
///
/// Requires Unity 2020.3 or newer and readable source meshes.
/// </summary>
public static class SkinnedMeshMerger
{
    public enum BlendShapeNameMode
    {
        /// <summary>Blend shapes with the same name are combined into one output shape.</summary>
        MergeMatchingNames,

        /// <summary>Every source renderer gets its own prefixed blend-shape name.</summary>
        PrefixWithRendererName
    }

    public enum SourceHandlingMode
    {
        /// <summary>
        /// Deletes source GameObjects. If deleting an object would also delete an output bone
        /// or the selected root, only its renderer-related components are deleted instead.
        /// </summary>
        DeleteSourceObjects,

        /// <summary>Leaves source objects in place and disables their renderers.</summary>
        DisableSourceRenderers,

        /// <summary>Leaves source objects and renderers unchanged.</summary>
        KeepSourceRenderers
    }

    private sealed class SourceMeshData
    {
        public Renderer Renderer;
        public SkinnedMeshRenderer SkinnedRenderer;
        public MeshFilter MeshFilter;
        public Mesh Mesh;
        public int VertexOffset;
        public Matrix4x4 SourceToOutput;
        public Matrix4x4 NormalToOutput;
        public bool ReversesWinding;
    }

    private sealed class OutputSubMesh
    {
        public readonly List<int> Indices = new List<int>();
        public MeshTopology Topology;
        public Material Material;
    }

    private sealed class BlendShapeTrackFrame
    {
        public float Weight;
        public Vector3[] DeltaVertices;
        public Vector3[] DeltaNormals;
        public Vector3[] DeltaTangents;
    }

    private sealed class BlendShapeTrack
    {
        public int VertexOffset;
        public readonly List<BlendShapeTrackFrame> Frames =
            new List<BlendShapeTrackFrame>();
    }

    private sealed class BlendShapeData
    {
        public string Name;
        public float InitialWeight;
        public bool HasInitialWeight;
        public readonly List<BlendShapeTrack> Tracks =
            new List<BlendShapeTrack>();
        public readonly SortedSet<float> FrameWeights =
            new SortedSet<float>();
    }

    public static SkinnedMeshRenderer Combine(
        GameObject rootObject,
        List<GameObject> selectedObjects = null,
        BlendShapeNameMode blendShapeNameMode = BlendShapeNameMode.MergeMatchingNames,
        SourceHandlingMode sourceHandlingMode = SourceHandlingMode.DeleteSourceObjects)
    {
        if (rootObject == null)
            throw new ArgumentNullException(nameof(rootObject));

#if UNITY_EDITOR
        int undoGroup = -1;
        if (!Application.isPlaying)
        {
            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Combine skinned meshes");
        }
#endif

        try
        {
            return CombineInternal(
                rootObject,
                selectedObjects,
                blendShapeNameMode,
                sourceHandlingMode);
        }
        finally
        {
#if UNITY_EDITOR
            if (undoGroup >= 0)
            {
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(undoGroup);
            }

            SceneView.RepaintAll();
#endif
        }
    }

    /// <summary>Compatibility overload for callers using the earlier boolean option.</summary>
    public static SkinnedMeshRenderer Combine(
        GameObject rootObject,
        List<GameObject> selectedObjects,
        BlendShapeNameMode blendShapeNameMode,
        bool disableSourceRenderers)
    {
        return Combine(
            rootObject,
            selectedObjects,
            blendShapeNameMode,
            disableSourceRenderers
                ? SourceHandlingMode.DisableSourceRenderers
                : SourceHandlingMode.KeepSourceRenderers);
    }

    private static SkinnedMeshRenderer CombineInternal(
        GameObject rootObject,
        List<GameObject> selectedObjects,
        BlendShapeNameMode blendShapeNameMode,
        SourceHandlingMode sourceHandlingMode)
    {
        Transform outputTransform = rootObject.transform;
        List<SourceMeshData> sourceMeshes = CollectSources(rootObject, selectedObjects);

        if (sourceMeshes.Count < 2)
        {
            Debug.LogWarning("Select at least two enabled mesh renderers to combine.", rootObject);
            return null;
        }

        int totalVertexCount = 0;
        for (int sourceIndex = 0; sourceIndex < sourceMeshes.Count; sourceIndex++)
        {
            SourceMeshData source = sourceMeshes[sourceIndex];

            if (!source.Mesh.isReadable)
            {
                throw new InvalidOperationException(
                    $"Mesh '{source.Mesh.name}' on '{source.Renderer.name}' is not readable. " +
                    "Enable Read/Write in the model import settings.");
            }

            if (source.Renderer.GetComponent<Cloth>() != null)
            {
                throw new NotSupportedException(
                    $"'{source.Renderer.name}' has a Cloth component. Cloth constraints and " +
                    "per-vertex coefficients cannot be merged safely by a mesh-only combiner.");
            }

            source.VertexOffset = totalVertexCount;
            source.SourceToOutput =
                outputTransform.worldToLocalMatrix * source.Renderer.transform.localToWorldMatrix;
            source.NormalToOutput = source.SourceToOutput.inverse.transpose;
            source.ReversesWinding = source.SourceToOutput.determinant < 0.0f;
            totalVertexCount += source.Mesh.vertexCount;
        }

        List<Vector3> outputVertices = new List<Vector3>(totalVertexCount);
        List<Vector3> outputNormals = new List<Vector3>(totalVertexCount);
        List<Vector4> outputTangents = new List<Vector4>(totalVertexCount);
        List<Color32> outputColors = new List<Color32>(totalVertexCount);
        List<Vector4>[] outputUvChannels = CreateUvChannelLists(totalVertexCount);

        bool allMeshesHaveNormals = true;
        bool allMeshesHaveTangents = true;
        bool anyMeshHasColors = false;
        bool[] anyMeshHasUvChannel = new bool[8];

        List<Transform> outputBones = new List<Transform>();
        List<Matrix4x4> outputBindPoses = new List<Matrix4x4>();
        List<byte> outputBonesPerVertex = new List<byte>(totalVertexCount);
        List<BoneWeight1> outputBoneWeights = new List<BoneWeight1>();
        List<OutputSubMesh> outputSubMeshes = new List<OutputSubMesh>();

        Dictionary<string, BlendShapeData> blendShapesByName =
            new Dictionary<string, BlendShapeData>(StringComparer.Ordinal);
        List<BlendShapeData> blendShapeOrder = new List<BlendShapeData>();

        Bounds combinedLocalBounds = new Bounds();
        bool hasCombinedBounds = false;

        for (int sourceIndex = 0; sourceIndex < sourceMeshes.Count; sourceIndex++)
        {
            SourceMeshData source = sourceMeshes[sourceIndex];
            Mesh sourceMesh = source.Mesh;
            int sourceVertexCount = sourceMesh.vertexCount;

            AppendVertices(source, outputVertices);
            AppendNormals(source, outputNormals, ref allMeshesHaveNormals);
            AppendTangents(source, outputTangents, ref allMeshesHaveTangents);
            AppendColors(source, outputColors, ref anyMeshHasColors);
            AppendUvChannels(source, outputUvChannels, anyMeshHasUvChannel);

            int[] sourceBoneToOutputBone = BuildBoneMap(
                source,
                outputTransform,
                outputBones,
                outputBindPoses);

            AppendBoneWeights(
                source,
                sourceBoneToOutputBone,
                outputBonesPerVertex,
                outputBoneWeights);

            AppendSubMeshes(source, outputSubMeshes);
            CollectBlendShapes(
                source,
                blendShapeNameMode,
                blendShapesByName,
                blendShapeOrder);

            Bounds sourceLocalBounds = source.SkinnedRenderer != null
                ? source.SkinnedRenderer.localBounds
                : sourceMesh.bounds;
            Bounds transformedBounds = TransformBounds(sourceLocalBounds, source.SourceToOutput);

            if (!hasCombinedBounds)
            {
                combinedLocalBounds = transformedBounds;
                hasCombinedBounds = true;
            }
            else
            {
                combinedLocalBounds.Encapsulate(transformedBounds);
            }

            if (sourceVertexCount == 0)
                Debug.LogWarning($"Source mesh '{sourceMesh.name}' contains no vertices.", source.Renderer);
        }

        Mesh combinedMesh = new Mesh
        {
            name = rootObject.name + "_Combined",
            indexFormat = totalVertexCount > ushort.MaxValue
                ? IndexFormat.UInt32
                : IndexFormat.UInt16
        };
        // The mesh itself is intentionally not registered as an Undo-created object.
        // The Editor tool saves it as an asset after this method returns. Keeping the asset
        // alive allows Redo to restore the combined renderer with a valid mesh reference.

        combinedMesh.SetVertices(outputVertices);

        if (allMeshesHaveNormals)
            combinedMesh.SetNormals(outputNormals);

        if (!allMeshesHaveNormals)
            allMeshesHaveTangents = false;

        if (allMeshesHaveTangents)
            combinedMesh.SetTangents(outputTangents);

        if (anyMeshHasColors)
            combinedMesh.SetColors(outputColors);

        for (int uvChannel = 0; uvChannel < outputUvChannels.Length; uvChannel++)
        {
            if (anyMeshHasUvChannel[uvChannel])
                combinedMesh.SetUVs(uvChannel, outputUvChannels[uvChannel]);
        }

        combinedMesh.subMeshCount = outputSubMeshes.Count;
        for (int subMeshIndex = 0; subMeshIndex < outputSubMeshes.Count; subMeshIndex++)
        {
            OutputSubMesh outputSubMesh = outputSubMeshes[subMeshIndex];
            combinedMesh.SetIndices(
                outputSubMesh.Indices.ToArray(),
                outputSubMesh.Topology,
                subMeshIndex,
                false);
        }

        combinedMesh.bindposes = outputBindPoses.ToArray();

        using (NativeArray<byte> bonesPerVertex =
               new NativeArray<byte>(outputBonesPerVertex.ToArray(), Allocator.Temp))
        using (NativeArray<BoneWeight1> boneWeights =
               new NativeArray<BoneWeight1>(outputBoneWeights.ToArray(), Allocator.Temp))
        {
            combinedMesh.SetBoneWeights(bonesPerVertex, boneWeights);
        }

        if (!allMeshesHaveNormals)
            combinedMesh.RecalculateNormals();

        if (!allMeshesHaveTangents && anyMeshHasUvChannel[0] && HasTriangleSubMesh(outputSubMeshes))
            combinedMesh.RecalculateTangents();

        AddBlendShapes(combinedMesh, blendShapeOrder, totalVertexCount);
        combinedMesh.RecalculateBounds();
        combinedMesh.UploadMeshData(false);

#if UNITY_EDITOR
        if (!Application.isPlaying)
            Undo.FlushUndoRecordObjects();
#endif

        GameObject combinedObject = new GameObject(combinedMesh.name);
        SkinnedMeshRenderer combinedRenderer;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            Undo.RegisterCreatedObjectUndo(combinedObject, "Create combined skinned mesh");
            Undo.SetTransformParent(
                combinedObject.transform,
                outputTransform,
                "Parent combined skinned mesh");
            Undo.RegisterCompleteObjectUndo(
                combinedObject.transform,
                "Reset combined mesh transform");
            combinedObject.transform.localPosition = Vector3.zero;
            combinedObject.transform.localRotation = Quaternion.identity;
            combinedObject.transform.localScale = Vector3.one;

            combinedRenderer = Undo.AddComponent<SkinnedMeshRenderer>(combinedObject);
            Undo.RegisterCompleteObjectUndo(
                combinedRenderer,
                "Configure combined skinned mesh renderer");
        }
        else
#endif
        {
            combinedObject.transform.SetParent(outputTransform, false);
            combinedRenderer = combinedObject.AddComponent<SkinnedMeshRenderer>();
        }
        combinedRenderer.sharedMesh = combinedMesh;
        combinedRenderer.bones = outputBones.ToArray();
        combinedRenderer.rootBone = outputTransform;
        combinedRenderer.sharedMaterials = GetOutputMaterials(outputSubMeshes);

        CopyRendererSettings(sourceMeshes[0].Renderer, combinedRenderer);

        SkinnedMeshRenderer firstSkinnedRenderer = FindFirstSkinnedRenderer(sourceMeshes);
        if (firstSkinnedRenderer != null)
        {
            combinedRenderer.quality = firstSkinnedRenderer.quality;
            combinedRenderer.updateWhenOffscreen = firstSkinnedRenderer.updateWhenOffscreen;
            combinedRenderer.skinnedMotionVectors = firstSkinnedRenderer.skinnedMotionVectors;
        }

        if (hasCombinedBounds)
            combinedRenderer.localBounds = combinedLocalBounds;

        ApplyInitialBlendShapeWeights(combinedRenderer, blendShapeOrder);

        HandleSourcesAfterCombine(
            rootObject,
            sourceMeshes,
            outputBones,
            sourceHandlingMode);

#if UNITY_EDITOR
        Selection.activeGameObject = combinedObject;
        EditorUtility.SetDirty(combinedRenderer);
#endif

        return combinedRenderer;
    }

    private static List<SourceMeshData> CollectSources(
        GameObject rootObject,
        List<GameObject> selectedObjects)
    {
        List<SourceMeshData> sources = new List<SourceMeshData>();
        bool filterBySelection = selectedObjects != null && selectedObjects.Count > 0;

        SkinnedMeshRenderer[] skinnedRenderers =
            rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        for (int rendererIndex = 0; rendererIndex < skinnedRenderers.Length; rendererIndex++)
        {
            SkinnedMeshRenderer renderer = skinnedRenderers[rendererIndex];
            if (renderer == null || renderer.sharedMesh == null || !renderer.enabled)
                continue;
            if (filterBySelection && !IsIncluded(renderer.gameObject, selectedObjects))
                continue;

            sources.Add(new SourceMeshData
            {
                Renderer = renderer,
                SkinnedRenderer = renderer,
                Mesh = renderer.sharedMesh
            });
        }

        MeshRenderer[] meshRenderers = rootObject.GetComponentsInChildren<MeshRenderer>(true);
        for (int rendererIndex = 0; rendererIndex < meshRenderers.Length; rendererIndex++)
        {
            MeshRenderer renderer = meshRenderers[rendererIndex];
            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();

            if (renderer == null || !renderer.enabled ||
                meshFilter == null || meshFilter.sharedMesh == null)
                continue;
            if (renderer.GetComponent<SkinnedMeshRenderer>() != null)
                continue;
            if (filterBySelection && !IsIncluded(renderer.gameObject, selectedObjects))
                continue;

            sources.Add(new SourceMeshData
            {
                Renderer = renderer,
                MeshFilter = meshFilter,
                Mesh = meshFilter.sharedMesh
            });
        }

        return sources;
    }

    private static bool IsIncluded(GameObject rendererObject, List<GameObject> selectedObjects)
    {
        for (int selectedIndex = 0; selectedIndex < selectedObjects.Count; selectedIndex++)
        {
            GameObject selectedObject = selectedObjects[selectedIndex];
            if (selectedObject == null)
                continue;

            if (rendererObject == selectedObject ||
                rendererObject.transform.IsChildOf(selectedObject.transform))
                return true;
        }

        return false;
    }

    private static List<Vector4>[] CreateUvChannelLists(int capacity)
    {
        List<Vector4>[] channels = new List<Vector4>[8];
        for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
            channels[channelIndex] = new List<Vector4>(capacity);
        return channels;
    }

    private static void AppendVertices(SourceMeshData source, List<Vector3> outputVertices)
    {
        Vector3[] sourceVertices = source.Mesh.vertices;
        for (int vertexIndex = 0; vertexIndex < sourceVertices.Length; vertexIndex++)
            outputVertices.Add(source.SourceToOutput.MultiplyPoint3x4(sourceVertices[vertexIndex]));
    }

    private static void AppendNormals(
        SourceMeshData source,
        List<Vector3> outputNormals,
        ref bool allMeshesHaveNormals)
    {
        Vector3[] sourceNormals = source.Mesh.normals;
        int vertexCount = source.Mesh.vertexCount;
        bool hasNormals = sourceNormals != null && sourceNormals.Length == vertexCount;
        allMeshesHaveNormals &= hasNormals;

        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            Vector3 normal = hasNormals
                ? source.NormalToOutput.MultiplyVector(sourceNormals[vertexIndex]).normalized
                : Vector3.zero;
            outputNormals.Add(normal);
        }
    }

    private static void AppendTangents(
        SourceMeshData source,
        List<Vector4> outputTangents,
        ref bool allMeshesHaveTangents)
    {
        Vector4[] sourceTangents = source.Mesh.tangents;
        int vertexCount = source.Mesh.vertexCount;
        bool hasTangents = sourceTangents != null && sourceTangents.Length == vertexCount;
        allMeshesHaveTangents &= hasTangents;
        float handednessMultiplier = source.ReversesWinding ? -1.0f : 1.0f;

        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            if (!hasTangents)
            {
                outputTangents.Add(Vector4.zero);
                continue;
            }

            Vector4 sourceTangent = sourceTangents[vertexIndex];
            Vector3 tangentDirection = source.SourceToOutput.MultiplyVector(
                new Vector3(sourceTangent.x, sourceTangent.y, sourceTangent.z));
            tangentDirection.Normalize();

            outputTangents.Add(new Vector4(
                tangentDirection.x,
                tangentDirection.y,
                tangentDirection.z,
                sourceTangent.w * handednessMultiplier));
        }
    }

    private static void AppendColors(
        SourceMeshData source,
        List<Color32> outputColors,
        ref bool anyMeshHasColors)
    {
        Color32[] sourceColors = source.Mesh.colors32;
        int vertexCount = source.Mesh.vertexCount;
        bool hasColors = sourceColors != null && sourceColors.Length == vertexCount;
        anyMeshHasColors |= hasColors;

        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            outputColors.Add(hasColors ? sourceColors[vertexIndex] : new Color32(255, 255, 255, 255));
    }

    private static void AppendUvChannels(
        SourceMeshData source,
        List<Vector4>[] outputUvChannels,
        bool[] anyMeshHasUvChannel)
    {
        int vertexCount = source.Mesh.vertexCount;
        List<Vector4> sourceUvValues = new List<Vector4>(vertexCount);

        for (int uvChannel = 0; uvChannel < outputUvChannels.Length; uvChannel++)
        {
            sourceUvValues.Clear();
            source.Mesh.GetUVs(uvChannel, sourceUvValues);
            bool hasUvChannel = sourceUvValues.Count == vertexCount;
            anyMeshHasUvChannel[uvChannel] |= hasUvChannel;

            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                outputUvChannels[uvChannel].Add(
                    hasUvChannel ? sourceUvValues[vertexIndex] : Vector4.zero);
            }
        }
    }

    private static int[] BuildBoneMap(
        SourceMeshData source,
        Transform outputTransform,
        List<Transform> outputBones,
        List<Matrix4x4> outputBindPoses)
    {
        if (source.SkinnedRenderer == null)
        {
            Matrix4x4 staticBindPose =
                outputTransform.worldToLocalMatrix * outputTransform.localToWorldMatrix;
            int staticBoneIndex = FindOrAddBone(
                outputTransform,
                staticBindPose,
                outputBones,
                outputBindPoses);
            return new[] { staticBoneIndex };
        }

        Transform[] sourceBones = source.SkinnedRenderer.bones;
        Matrix4x4[] sourceBindPoses = source.Mesh.bindposes;
        int sourceBoneCount = Math.Max(sourceBones != null ? sourceBones.Length : 0,
                                       sourceBindPoses != null ? sourceBindPoses.Length : 0);

        if (sourceBoneCount == 0)
        {
            Matrix4x4 fallbackBindPose =
                outputTransform.worldToLocalMatrix * outputTransform.localToWorldMatrix;
            int fallbackBoneIndex = FindOrAddBone(
                outputTransform,
                fallbackBindPose,
                outputBones,
                outputBindPoses);
            return new[] { fallbackBoneIndex };
        }

        int[] sourceBoneToOutputBone = new int[sourceBoneCount];
        Matrix4x4 sourceWorldToLocal = source.Renderer.transform.worldToLocalMatrix;
        Matrix4x4 outputLocalToWorld = outputTransform.localToWorldMatrix;

        for (int sourceBoneIndex = 0; sourceBoneIndex < sourceBoneCount; sourceBoneIndex++)
        {
            Transform sourceBone =
                sourceBones != null && sourceBoneIndex < sourceBones.Length && sourceBones[sourceBoneIndex] != null
                    ? sourceBones[sourceBoneIndex]
                    : outputTransform;

            Matrix4x4 outputBindPose;
            if (sourceBindPoses != null && sourceBoneIndex < sourceBindPoses.Length)
            {
                // Preserve the imported bind pose. Rebuilding this from current bone transforms
                // breaks meshes when the character is not currently in its bind pose.
                outputBindPose =
                    sourceBindPoses[sourceBoneIndex] * sourceWorldToLocal * outputLocalToWorld;
            }
            else
            {
                outputBindPose = sourceBone.worldToLocalMatrix * outputLocalToWorld;
                Debug.LogWarning(
                    $"Mesh '{source.Mesh.name}' has no bind pose for bone index {sourceBoneIndex}. " +
                    "A current-pose fallback was used.",
                    source.Renderer);
            }

            sourceBoneToOutputBone[sourceBoneIndex] = FindOrAddBone(
                sourceBone,
                outputBindPose,
                outputBones,
                outputBindPoses);
        }

        return sourceBoneToOutputBone;
    }

    private static int FindOrAddBone(
        Transform bone,
        Matrix4x4 bindPose,
        List<Transform> outputBones,
        List<Matrix4x4> outputBindPoses)
    {
        for (int boneIndex = 0; boneIndex < outputBones.Count; boneIndex++)
        {
            if (outputBones[boneIndex] == bone &&
                MatricesApproximatelyEqual(outputBindPoses[boneIndex], bindPose))
                return boneIndex;
        }

        outputBones.Add(bone);
        outputBindPoses.Add(bindPose);
        return outputBones.Count - 1;
    }

    private static bool MatricesApproximatelyEqual(Matrix4x4 left, Matrix4x4 right)
    {
        const float tolerance = 0.00001f;
        for (int elementIndex = 0; elementIndex < 16; elementIndex++)
        {
            if (Mathf.Abs(left[elementIndex] - right[elementIndex]) > tolerance)
                return false;
        }
        return true;
    }

    private static void AppendBoneWeights(
        SourceMeshData source,
        int[] sourceBoneToOutputBone,
        List<byte> outputBonesPerVertex,
        List<BoneWeight1> outputBoneWeights)
    {
        int vertexCount = source.Mesh.vertexCount;

        if (source.SkinnedRenderer == null)
        {
            int staticBoneIndex = sourceBoneToOutputBone[0];
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                outputBonesPerVertex.Add(1);
                outputBoneWeights.Add(new BoneWeight1
                {
                    boneIndex = staticBoneIndex,
                    weight = 1.0f
                });
            }
            return;
        }

        using (NativeArray<byte> sourceBonesPerVertex = source.Mesh.GetBonesPerVertex())
        using (NativeArray<BoneWeight1> sourceBoneWeights = source.Mesh.GetAllBoneWeights())
        {
            if (sourceBonesPerVertex.Length != vertexCount || sourceBoneWeights.Length == 0)
            {
                AppendFallbackBoneWeights(
                    vertexCount,
                    sourceBoneToOutputBone[0],
                    outputBonesPerVertex,
                    outputBoneWeights);
                return;
            }

            int sourceWeightIndex = 0;
            Dictionary<int, float> accumulatedWeightsByBone =
                new Dictionary<int, float>();
            List<BoneWeight1> vertexWeights = new List<BoneWeight1>();

            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                accumulatedWeightsByBone.Clear();
                vertexWeights.Clear();

                int declaredInfluenceCount = sourceBonesPerVertex[vertexIndex];
                for (int influenceIndex = 0;
                     influenceIndex < declaredInfluenceCount &&
                     sourceWeightIndex < sourceBoneWeights.Length;
                     influenceIndex++)
                {
                    BoneWeight1 sourceWeight = sourceBoneWeights[sourceWeightIndex++];
                    if (sourceWeight.weight <= 0.0f)
                        continue;

                    int mappedBoneIndex = sourceWeight.boneIndex >= 0 &&
                                          sourceWeight.boneIndex < sourceBoneToOutputBone.Length
                        ? sourceBoneToOutputBone[sourceWeight.boneIndex]
                        : sourceBoneToOutputBone[0];

                    if (accumulatedWeightsByBone.TryGetValue(
                            mappedBoneIndex,
                            out float accumulatedWeight))
                    {
                        accumulatedWeightsByBone[mappedBoneIndex] =
                            accumulatedWeight + sourceWeight.weight;
                    }
                    else
                    {
                        accumulatedWeightsByBone.Add(
                            mappedBoneIndex,
                            sourceWeight.weight);
                    }
                }

                foreach (KeyValuePair<int, float> accumulatedWeight in accumulatedWeightsByBone)
                {
                    vertexWeights.Add(new BoneWeight1
                    {
                        boneIndex = accumulatedWeight.Key,
                        weight = accumulatedWeight.Value
                    });
                }

                if (vertexWeights.Count == 0)
                {
                    vertexWeights.Add(new BoneWeight1
                    {
                        boneIndex = sourceBoneToOutputBone[0],
                        weight = 1.0f
                    });
                }

                vertexWeights.Sort((left, right) => right.weight.CompareTo(left.weight));

                int retainedInfluenceCount = Mathf.Min(vertexWeights.Count, byte.MaxValue);
                float retainedWeightSum = 0.0f;
                for (int influenceIndex = 0;
                     influenceIndex < retainedInfluenceCount;
                     influenceIndex++)
                {
                    retainedWeightSum += vertexWeights[influenceIndex].weight;
                }

                if (retainedWeightSum <= Mathf.Epsilon)
                {
                    outputBonesPerVertex.Add(1);
                    outputBoneWeights.Add(new BoneWeight1
                    {
                        boneIndex = sourceBoneToOutputBone[0],
                        weight = 1.0f
                    });
                    continue;
                }

                outputBonesPerVertex.Add((byte)retainedInfluenceCount);
                for (int influenceIndex = 0;
                     influenceIndex < retainedInfluenceCount;
                     influenceIndex++)
                {
                    BoneWeight1 retainedWeight = vertexWeights[influenceIndex];
                    retainedWeight.weight /= retainedWeightSum;
                    outputBoneWeights.Add(retainedWeight);
                }
            }
        }
    }

    private static void AppendFallbackBoneWeights(
        int vertexCount,
        int fallbackBoneIndex,
        List<byte> outputBonesPerVertex,
        List<BoneWeight1> outputBoneWeights)
    {
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            outputBonesPerVertex.Add(1);
            outputBoneWeights.Add(new BoneWeight1
            {
                boneIndex = fallbackBoneIndex,
                weight = 1.0f
            });
        }
    }

    private static void AppendSubMeshes(
        SourceMeshData source,
        List<OutputSubMesh> outputSubMeshes)
    {
        Material[] sourceMaterials = source.Renderer.sharedMaterials;

        for (int sourceSubMeshIndex = 0;
             sourceSubMeshIndex < source.Mesh.subMeshCount;
             sourceSubMeshIndex++)
        {
            int[] sourceIndices = source.Mesh.GetIndices(sourceSubMeshIndex, true);
            MeshTopology topology = source.Mesh.GetTopology(sourceSubMeshIndex);

            if (source.ReversesWinding)
                ReverseWinding(sourceIndices, topology);

            OutputSubMesh outputSubMesh = new OutputSubMesh
            {
                Topology = topology,
                Material = sourceMaterials != null && sourceSubMeshIndex < sourceMaterials.Length
                    ? sourceMaterials[sourceSubMeshIndex]
                    : source.Renderer.sharedMaterial
            };

            for (int index = 0; index < sourceIndices.Length; index++)
                outputSubMesh.Indices.Add(sourceIndices[index] + source.VertexOffset);

            outputSubMeshes.Add(outputSubMesh);
        }
    }

    private static void ReverseWinding(int[] indices, MeshTopology topology)
    {
        if (topology == MeshTopology.Triangles)
        {
            for (int index = 0; index + 2 < indices.Length; index += 3)
            {
                int temporary = indices[index + 1];
                indices[index + 1] = indices[index + 2];
                indices[index + 2] = temporary;
            }
        }
        else if (topology == MeshTopology.Quads)
        {
            for (int index = 0; index + 3 < indices.Length; index += 4)
            {
                int temporary = indices[index + 1];
                indices[index + 1] = indices[index + 3];
                indices[index + 3] = temporary;
            }
        }
    }

    private static void CollectBlendShapes(
        SourceMeshData source,
        BlendShapeNameMode nameMode,
        Dictionary<string, BlendShapeData> blendShapesByName,
        List<BlendShapeData> blendShapeOrder)
    {
        Mesh sourceMesh = source.Mesh;
        if (sourceMesh.blendShapeCount == 0)
            return;

        int vertexCount = sourceMesh.vertexCount;
        Vector3[] sourceDeltaVertices = new Vector3[vertexCount];
        Vector3[] sourceDeltaNormals = new Vector3[vertexCount];
        Vector3[] sourceDeltaTangents = new Vector3[vertexCount];

        for (int shapeIndex = 0; shapeIndex < sourceMesh.blendShapeCount; shapeIndex++)
        {
            string sourceShapeName = sourceMesh.GetBlendShapeName(shapeIndex);
            string outputShapeName = nameMode == BlendShapeNameMode.PrefixWithRendererName
                ? source.Renderer.name + "_" + source.VertexOffset + "__" + sourceShapeName
                : sourceShapeName;

            if (!blendShapesByName.TryGetValue(outputShapeName, out BlendShapeData outputShape))
            {
                outputShape = new BlendShapeData { Name = outputShapeName };
                blendShapesByName.Add(outputShapeName, outputShape);
                blendShapeOrder.Add(outputShape);
            }

            if (source.SkinnedRenderer != null)
            {
                float sourceInitialWeight =
                    source.SkinnedRenderer.GetBlendShapeWeight(shapeIndex);

                if (!outputShape.HasInitialWeight)
                {
                    outputShape.InitialWeight = sourceInitialWeight;
                    outputShape.HasInitialWeight = true;
                }
                else if (nameMode == BlendShapeNameMode.MergeMatchingNames &&
                         !Mathf.Approximately(
                             outputShape.InitialWeight,
                             sourceInitialWeight))
                {
                    Debug.LogWarning(
                        $"Blend shape '{outputShapeName}' has different current weights on " +
                        "multiple source renderers. The first renderer's weight is used. " +
                        "Use PrefixWithRendererName to keep the controls independent.",
                        source.Renderer);
                }
            }

            int frameCount = sourceMesh.GetBlendShapeFrameCount(shapeIndex);
            if (frameCount == 0)
                continue;

            BlendShapeTrack track = new BlendShapeTrack
            {
                VertexOffset = source.VertexOffset
            };
            bool hasExplicitZeroFrame = false;

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                Array.Clear(sourceDeltaVertices, 0, sourceDeltaVertices.Length);
                Array.Clear(sourceDeltaNormals, 0, sourceDeltaNormals.Length);
                Array.Clear(sourceDeltaTangents, 0, sourceDeltaTangents.Length);

                sourceMesh.GetBlendShapeFrameVertices(
                    shapeIndex,
                    frameIndex,
                    sourceDeltaVertices,
                    sourceDeltaNormals,
                    sourceDeltaTangents);

                Vector3[] transformedDeltaVertices = new Vector3[vertexCount];
                Vector3[] transformedDeltaNormals = new Vector3[vertexCount];
                Vector3[] transformedDeltaTangents = new Vector3[vertexCount];

                for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                {
                    transformedDeltaVertices[vertexIndex] =
                        source.SourceToOutput.MultiplyVector(sourceDeltaVertices[vertexIndex]);
                    transformedDeltaNormals[vertexIndex] =
                        source.NormalToOutput.MultiplyVector(sourceDeltaNormals[vertexIndex]);
                    transformedDeltaTangents[vertexIndex] =
                        source.SourceToOutput.MultiplyVector(sourceDeltaTangents[vertexIndex]);
                }

                float frameWeight =
                    sourceMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                hasExplicitZeroFrame |= Mathf.Approximately(frameWeight, 0.0f);
                outputShape.FrameWeights.Add(frameWeight);
                track.Frames.Add(new BlendShapeTrackFrame
                {
                    Weight = frameWeight,
                    DeltaVertices = transformedDeltaVertices,
                    DeltaNormals = transformedDeltaNormals,
                    DeltaTangents = transformedDeltaTangents
                });
            }

            // Unity interpolates ordinary one-frame blend shapes from an undeformed state at
            // weight zero. Keeping that implicit zero frame in each source track lets merged
            // shapes with different frame weights be resampled correctly at the union of frames.
            if (!hasExplicitZeroFrame)
                track.Frames.Add(new BlendShapeTrackFrame { Weight = 0.0f });

            track.Frames.Sort((left, right) => left.Weight.CompareTo(right.Weight));
            outputShape.Tracks.Add(track);
        }
    }

    private static void AddBlendShapes(
        Mesh combinedMesh,
        List<BlendShapeData> blendShapes,
        int totalVertexCount)
    {
        Vector3[] combinedDeltaVertices = new Vector3[totalVertexCount];
        Vector3[] combinedDeltaNormals = new Vector3[totalVertexCount];
        Vector3[] combinedDeltaTangents = new Vector3[totalVertexCount];

        for (int shapeIndex = 0; shapeIndex < blendShapes.Count; shapeIndex++)
        {
            BlendShapeData shape = blendShapes[shapeIndex];
            foreach (float frameWeight in shape.FrameWeights)
            {
                Array.Clear(combinedDeltaVertices, 0, combinedDeltaVertices.Length);
                Array.Clear(combinedDeltaNormals, 0, combinedDeltaNormals.Length);
                Array.Clear(combinedDeltaTangents, 0, combinedDeltaTangents.Length);

                for (int trackIndex = 0;
                     trackIndex < shape.Tracks.Count;
                     trackIndex++)
                {
                    AccumulateBlendShapeTrack(
                        shape.Tracks[trackIndex],
                        frameWeight,
                        combinedDeltaVertices,
                        combinedDeltaNormals,
                        combinedDeltaTangents);
                }

                combinedMesh.AddBlendShapeFrame(
                    shape.Name,
                    frameWeight,
                    combinedDeltaVertices,
                    combinedDeltaNormals,
                    combinedDeltaTangents);
            }
        }
    }

    private static void AccumulateBlendShapeTrack(
        BlendShapeTrack track,
        float targetWeight,
        Vector3[] combinedDeltaVertices,
        Vector3[] combinedDeltaNormals,
        Vector3[] combinedDeltaTangents)
    {
        if (track.Frames.Count == 0)
            return;

        BlendShapeTrackFrame lowerFrame;
        BlendShapeTrackFrame upperFrame;

        if (track.Frames.Count == 1)
        {
            lowerFrame = track.Frames[0];
            upperFrame = track.Frames[0];
        }
        else if (targetWeight <= track.Frames[0].Weight)
        {
            lowerFrame = track.Frames[0];
            upperFrame = track.Frames[1];
        }
        else if (targetWeight >= track.Frames[track.Frames.Count - 1].Weight)
        {
            lowerFrame = track.Frames[track.Frames.Count - 2];
            upperFrame = track.Frames[track.Frames.Count - 1];
        }
        else
        {
            lowerFrame = track.Frames[0];
            upperFrame = track.Frames[1];

            for (int frameIndex = 1; frameIndex < track.Frames.Count; frameIndex++)
            {
                if (targetWeight > track.Frames[frameIndex].Weight)
                    continue;

                lowerFrame = track.Frames[frameIndex - 1];
                upperFrame = track.Frames[frameIndex];
                break;
            }
        }

        float frameWeightRange = upperFrame.Weight - lowerFrame.Weight;
        float interpolation = Mathf.Approximately(frameWeightRange, 0.0f)
            ? 0.0f
            : (targetWeight - lowerFrame.Weight) / frameWeightRange;

        int trackVertexCount = GetBlendShapeTrackVertexCount(lowerFrame, upperFrame);
        for (int vertexIndex = 0; vertexIndex < trackVertexCount; vertexIndex++)
        {
            int outputVertexIndex = track.VertexOffset + vertexIndex;

            combinedDeltaVertices[outputVertexIndex] += Vector3.LerpUnclamped(
                GetBlendShapeDelta(lowerFrame.DeltaVertices, vertexIndex),
                GetBlendShapeDelta(upperFrame.DeltaVertices, vertexIndex),
                interpolation);
            combinedDeltaNormals[outputVertexIndex] += Vector3.LerpUnclamped(
                GetBlendShapeDelta(lowerFrame.DeltaNormals, vertexIndex),
                GetBlendShapeDelta(upperFrame.DeltaNormals, vertexIndex),
                interpolation);
            combinedDeltaTangents[outputVertexIndex] += Vector3.LerpUnclamped(
                GetBlendShapeDelta(lowerFrame.DeltaTangents, vertexIndex),
                GetBlendShapeDelta(upperFrame.DeltaTangents, vertexIndex),
                interpolation);
        }
    }

    private static int GetBlendShapeTrackVertexCount(
        BlendShapeTrackFrame firstFrame,
        BlendShapeTrackFrame secondFrame)
    {
        if (firstFrame.DeltaVertices != null)
            return firstFrame.DeltaVertices.Length;
        if (secondFrame.DeltaVertices != null)
            return secondFrame.DeltaVertices.Length;
        return 0;
    }

    private static Vector3 GetBlendShapeDelta(Vector3[] deltas, int vertexIndex)
    {
        return deltas != null && vertexIndex < deltas.Length
            ? deltas[vertexIndex]
            : Vector3.zero;
    }

    private static Bounds TransformBounds(Bounds sourceBounds, Matrix4x4 transformMatrix)
    {
        Vector3 center = sourceBounds.center;
        Vector3 extents = sourceBounds.extents;
        Vector3 firstCorner = transformMatrix.MultiplyPoint3x4(center + new Vector3(
            -extents.x, -extents.y, -extents.z));
        Bounds transformedBounds = new Bounds(firstCorner, Vector3.zero);

        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
            transformedBounds.Encapsulate(transformMatrix.MultiplyPoint3x4(corner));
        }

        return transformedBounds;
    }

    private static bool HasTriangleSubMesh(List<OutputSubMesh> subMeshes)
    {
        for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
        {
            if (subMeshes[subMeshIndex].Topology == MeshTopology.Triangles)
                return true;
        }
        return false;
    }

    private static Material[] GetOutputMaterials(List<OutputSubMesh> outputSubMeshes)
    {
        Material[] materials = new Material[outputSubMeshes.Count];
        for (int materialIndex = 0; materialIndex < outputSubMeshes.Count; materialIndex++)
            materials[materialIndex] = outputSubMeshes[materialIndex].Material;
        return materials;
    }

    private static SkinnedMeshRenderer FindFirstSkinnedRenderer(List<SourceMeshData> sources)
    {
        for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            if (sources[sourceIndex].SkinnedRenderer != null)
                return sources[sourceIndex].SkinnedRenderer;
        }
        return null;
    }

    private static void CopyRendererSettings(Renderer source, SkinnedMeshRenderer destination)
    {
        destination.shadowCastingMode = source.shadowCastingMode;
        destination.receiveShadows = source.receiveShadows;
        destination.lightProbeUsage = source.lightProbeUsage;
        destination.reflectionProbeUsage = source.reflectionProbeUsage;
        destination.probeAnchor = source.probeAnchor;
        destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
        destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
        destination.sortingLayerID = source.sortingLayerID;
        destination.sortingOrder = source.sortingOrder;
    }

    private static void ApplyInitialBlendShapeWeights(
        SkinnedMeshRenderer renderer,
        List<BlendShapeData> blendShapes)
    {
        for (int shapeIndex = 0; shapeIndex < blendShapes.Count; shapeIndex++)
        {
            BlendShapeData shape = blendShapes[shapeIndex];
            if (!shape.HasInitialWeight)
                continue;

            int rendererShapeIndex = renderer.sharedMesh.GetBlendShapeIndex(shape.Name);
            if (rendererShapeIndex >= 0)
                renderer.SetBlendShapeWeight(rendererShapeIndex, shape.InitialWeight);
        }
    }

    private static void HandleSourcesAfterCombine(
        GameObject rootObject,
        List<SourceMeshData> sources,
        List<Transform> outputBones,
        SourceHandlingMode sourceHandlingMode)
    {
        switch (sourceHandlingMode)
        {
            case SourceHandlingMode.DeleteSourceObjects:
                DeleteSourceObjects(rootObject, sources, outputBones);
                break;

            case SourceHandlingMode.DisableSourceRenderers:
                DisableSourceRenderers(sources);
                break;

            case SourceHandlingMode.KeepSourceRenderers:
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(sourceHandlingMode),
                    sourceHandlingMode,
                    null);
        }
    }

    private static void DisableSourceRenderers(List<SourceMeshData> sources)
    {
        for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            Renderer renderer = sources[sourceIndex].Renderer;
            if (renderer == null)
                continue;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                Undo.RecordObject(renderer, "Disable source mesh renderer");
#endif
            renderer.enabled = false;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                EditorUtility.SetDirty(renderer);
#endif
        }
    }

    private static void DeleteSourceObjects(
        GameObject rootObject,
        List<SourceMeshData> sources,
        List<Transform> outputBones)
    {
        List<GameObject> objectsToDelete = new List<GameObject>();
        List<SourceMeshData> componentsToDelete = new List<SourceMeshData>();

        for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            SourceMeshData source = sources[sourceIndex];
            if (source.Renderer == null)
                continue;

            GameObject sourceObject = source.Renderer.gameObject;
            bool mustKeepTransform = sourceObject == rootObject ||
                                     ContainsRequiredBone(sourceObject.transform, outputBones);

            if (mustKeepTransform)
                componentsToDelete.Add(source);
            else if (!objectsToDelete.Contains(sourceObject))
                objectsToDelete.Add(sourceObject);
        }

        RemoveNestedDeletionTargets(objectsToDelete);

        for (int sourceIndex = 0; sourceIndex < componentsToDelete.Count; sourceIndex++)
            DeleteRendererComponents(componentsToDelete[sourceIndex]);

        for (int objectIndex = 0; objectIndex < objectsToDelete.Count; objectIndex++)
            DestroyWithUndo(objectsToDelete[objectIndex]);
    }

    private static bool ContainsRequiredBone(
        Transform sourceTransform,
        List<Transform> outputBones)
    {
        for (int boneIndex = 0; boneIndex < outputBones.Count; boneIndex++)
        {
            Transform bone = outputBones[boneIndex];
            if (bone != null &&
                (bone == sourceTransform || bone.IsChildOf(sourceTransform)))
                return true;
        }

        return false;
    }

    private static void RemoveNestedDeletionTargets(List<GameObject> objectsToDelete)
    {
        for (int childIndex = objectsToDelete.Count - 1; childIndex >= 0; childIndex--)
        {
            Transform childTransform = objectsToDelete[childIndex].transform;
            bool hasDeletedAncestor = false;

            for (int parentIndex = 0; parentIndex < objectsToDelete.Count; parentIndex++)
            {
                if (parentIndex == childIndex)
                    continue;

                Transform possibleParent = objectsToDelete[parentIndex].transform;
                if (childTransform.IsChildOf(possibleParent))
                {
                    hasDeletedAncestor = true;
                    break;
                }
            }

            if (hasDeletedAncestor)
                objectsToDelete.RemoveAt(childIndex);
        }
    }

    private static void DeleteRendererComponents(SourceMeshData source)
    {
        if (source.Renderer != null)
            DestroyWithUndo(source.Renderer);

        if (source.MeshFilter != null)
            DestroyWithUndo(source.MeshFilter);
    }

    private static void DestroyWithUndo(UnityEngine.Object objectToDestroy)
    {
        if (objectToDestroy == null)
            return;

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            Undo.DestroyObjectImmediate(objectToDestroy);
            return;
        }
#endif

        UnityEngine.Object.Destroy(objectToDestroy);
    }
}
