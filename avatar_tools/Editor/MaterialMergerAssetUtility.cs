using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Handles output folders, compatibility with older Material_Merger_Utility signatures,
/// generated-asset relocation, and unused output cleanup.
/// Keep this file inside an Editor folder.
/// </summary>
internal static class MaterialMergerAssetUtility
{
    public const string DefaultOutputFolder = "Assets/Atlas_merger";

    private static readonly Type[] GeneratedOutputTypes =
    {
        typeof(Material),
        typeof(Texture),
        typeof(Mesh)
    };

    // Assets required by scene Undo/Redo are not cleanup candidates for the
    // remainder of the current editor domain.
    private static readonly HashSet<string> UndoProtectedAssetPaths =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Outputs created or assigned by the most recent merge are protected from
    // the cleanup pass that immediately follows that merge. This prevents the
    // cleanup option from deleting the material/texture/mesh it just created.
    private static readonly HashSet<string> PendingMergeOutputAssetPaths =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public sealed class CleanupResult
    {
        public readonly List<string> DeletedAssetPaths = new List<string>();
        public readonly List<string> FailedAssetPaths = new List<string>();
    }

    public static string NormalizeAssetPath(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return string.Empty;

        return assetPath.Trim().Replace('\\', '/').TrimEnd('/');
    }

    public static bool IsValidAssetFolder(string folderPath)
    {
        string normalizedPath = NormalizeAssetPath(folderPath);
        return normalizedPath == "Assets" ||
               normalizedPath.StartsWith("Assets/", StringComparison.Ordinal);
    }

    public static bool TryConvertAbsoluteFolderToAssetPath(
        string absoluteFolderPath,
        out string assetFolderPath)
    {
        assetFolderPath = string.Empty;
        if (string.IsNullOrWhiteSpace(absoluteFolderPath))
            return false;

        string normalizedAbsolutePath = NormalizeAssetPath(Path.GetFullPath(absoluteFolderPath));
        string normalizedAssetsPath = NormalizeAssetPath(Path.GetFullPath(Application.dataPath));

        bool isAssetsFolder = normalizedAbsolutePath.Equals(
            normalizedAssetsPath,
            StringComparison.OrdinalIgnoreCase);
        bool isInsideAssetsFolder = normalizedAbsolutePath.StartsWith(
            normalizedAssetsPath + "/",
            StringComparison.OrdinalIgnoreCase);

        if (!isAssetsFolder && !isInsideAssetsFolder)
            return false;

        assetFolderPath = "Assets" + normalizedAbsolutePath.Substring(normalizedAssetsPath.Length);
        assetFolderPath = NormalizeAssetPath(assetFolderPath);
        return true;
    }

    public static bool EnsureFolderExists(string folderPath, out string errorMessage)
    {
        errorMessage = string.Empty;
        string normalizedFolderPath = NormalizeAssetPath(folderPath);

        if (!IsValidAssetFolder(normalizedFolderPath))
        {
            errorMessage = "The output folder must be inside the project's Assets folder.";
            return false;
        }

        if (AssetDatabase.IsValidFolder(normalizedFolderPath))
            return true;

        string[] folderParts = normalizedFolderPath.Split('/');
        string currentFolder = folderParts[0];

        for (int partIndex = 1; partIndex < folderParts.Length; partIndex++)
        {
            string nextFolder = currentFolder + "/" + folderParts[partIndex];
            if (!AssetDatabase.IsValidFolder(nextFolder))
            {
                string createdFolderGuid = AssetDatabase.CreateFolder(
                    currentFolder,
                    folderParts[partIndex]);

                if (string.IsNullOrEmpty(createdFolderGuid))
                {
                    errorMessage = "Unity could not create the folder '" + nextFolder + "'.";
                    return false;
                }
            }

            currentFolder = nextFolder;
        }

        return AssetDatabase.IsValidFolder(normalizedFolderPath);
    }

    public static void Merge(
        GameObject rootObject,
        int atlasSize,
        int anisotropicLevel,
        List<TextureImporterPlatformSettings> platformSettings,
        HashSet<Material> selectedMaterials,
        string outputFolder)
    {
        if (rootObject == null)
            throw new ArgumentNullException(nameof(rootObject));
        if (platformSettings == null)
            throw new ArgumentNullException(nameof(platformSettings));
        if (selectedMaterials == null)
            throw new ArgumentNullException(nameof(selectedMaterials));

        string normalizedOutputFolder = NormalizeAssetPath(outputFolder);
        if (!EnsureFolderExists(normalizedOutputFolder, out string folderError))
            throw new InvalidOperationException(folderError);

        HashSet<string> outputAssetsBeforeMerge = FindGeneratedOutputAssetPaths(
            normalizedOutputFolder);

        Type mergerUtilityType = FindMaterialMergerUtilityType();
        MethodInfo mergeMethod = FindBestMergeMethod(mergerUtilityType, out bool acceptsOutputFolder);
        object[] mergeArguments = BuildMergeArguments(
            mergeMethod,
            rootObject,
            atlasSize,
            anisotropicLevel,
            platformSettings,
            selectedMaterials,
            normalizedOutputFolder);

        try
        {
            mergeMethod.Invoke(null, mergeArguments);
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException ?? exception;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (!acceptsOutputFolder)
            MoveLegacyGeneratedAssets(normalizedOutputFolder);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        ProtectLatestMergeOutputs(
            rootObject,
            normalizedOutputFolder,
            outputAssetsBeforeMerge);
    }

    public static void ProtectAssetsForUndo(IEnumerable<UnityEngine.Object> assets)
    {
        if (assets == null)
            return;

        foreach (UnityEngine.Object asset in assets)
        {
            if (asset == null)
                continue;

            string assetPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(asset));
            if (string.IsNullOrEmpty(assetPath))
                continue;

            UndoProtectedAssetPaths.Add(assetPath);
            string[] dependencyPaths = AssetDatabase.GetDependencies(assetPath, true);
            for (int dependencyIndex = 0;
                 dependencyIndex < dependencyPaths.Length;
                 dependencyIndex++)
            {
                string dependencyPath = NormalizeAssetPath(dependencyPaths[dependencyIndex]);
                if (!string.IsNullOrEmpty(dependencyPath))
                    UndoProtectedAssetPaths.Add(dependencyPath);
            }
        }
    }

    public static CleanupResult DeleteUnusedOutputAssets(string outputFolder)
    {
        CleanupResult result = new CleanupResult();
        string normalizedOutputFolder = NormalizeAssetPath(outputFolder);

        if (!AssetDatabase.IsValidFolder(normalizedOutputFolder))
            return result;

        HashSet<string> candidateAssetPaths = FindGeneratedOutputAssetPaths(
            normalizedOutputFolder);

        HashSet<string> protectedAssetPaths = new HashSet<string>(
            UndoProtectedAssetPaths,
            StringComparer.OrdinalIgnoreCase);
        protectedAssetPaths.UnionWith(PendingMergeOutputAssetPaths);

        // Pending paths only need to survive the cleanup triggered by the
        // merge that created them. Future cleanup runs can evaluate them
        // normally using actual scene/project references.
        PendingMergeOutputAssetPaths.Clear();

        candidateAssetPaths.ExceptWith(protectedAssetPaths);
        if (candidateAssetPaths.Count == 0)
            return result;

        HashSet<string> referencedAssetPaths = FindReferencedCandidateAssets(
            candidateAssetPaths);

        List<string> unusedAssetPaths = candidateAssetPaths
            .Where(path => !referencedAssetPaths.Contains(path))
            .OrderByDescending(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AssetDatabase.StartAssetEditing();
        try
        {
            for (int assetIndex = 0; assetIndex < unusedAssetPaths.Count; assetIndex++)
            {
                string assetPath = unusedAssetPaths[assetIndex];
                if (AssetDatabase.DeleteAsset(assetPath))
                    result.DeletedAssetPaths.Add(assetPath);
                else
                    result.FailedAssetPaths.Add(assetPath);
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        if (result.DeletedAssetPaths.Count > 0)
            DeleteEmptyFolders(normalizedOutputFolder);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return result;
    }

    private static Type FindMaterialMergerUtilityType()
    {
        Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int assemblyIndex = 0; assemblyIndex < loadedAssemblies.Length; assemblyIndex++)
        {
            Type utilityType = loadedAssemblies[assemblyIndex].GetType(
                "Material_Merger_Utility",
                false);
            if (utilityType != null)
                return utilityType;
        }

        throw new InvalidOperationException(
            "Material_Merger_Utility was not found. Keep the original merger utility in the project.");
    }

    private static MethodInfo FindBestMergeMethod(
        Type utilityType,
        out bool acceptsOutputFolder)
    {
        MethodInfo[] mergeMethods = utilityType.GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        MethodInfo fallbackMethod = null;
        for (int methodIndex = 0; methodIndex < mergeMethods.Length; methodIndex++)
        {
            MethodInfo method = mergeMethods[methodIndex];
            if (!string.Equals(method.Name, "Merge", StringComparison.Ordinal))
                continue;

            if (!CanBuildMergeArguments(method.GetParameters(), out bool hasOutputFolder))
                continue;

            if (hasOutputFolder)
            {
                acceptsOutputFolder = true;
                return method;
            }

            fallbackMethod = method;
        }

        if (fallbackMethod != null)
        {
            acceptsOutputFolder = false;
            return fallbackMethod;
        }

        throw new MissingMethodException(
            utilityType.FullName,
            "Merge(GameObject, int, int, List<TextureImporterPlatformSettings>, HashSet<Material>[, string])");
    }

    private static bool CanBuildMergeArguments(
        ParameterInfo[] parameters,
        out bool hasOutputFolder)
    {
        hasOutputFolder = false;
        int gameObjectCount = 0;
        int integerCount = 0;
        int platformSettingsCount = 0;
        int materialSetCount = 0;

        for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
        {
            Type parameterType = parameters[parameterIndex].ParameterType;
            if (parameterType == typeof(GameObject))
            {
                gameObjectCount++;
            }
            else if (parameterType == typeof(int))
            {
                integerCount++;
            }
            else if (parameterType == typeof(string))
            {
                hasOutputFolder = true;
            }
            else if (parameterType.IsAssignableFrom(
                         typeof(List<TextureImporterPlatformSettings>)))
            {
                platformSettingsCount++;
            }
            else if (parameterType.IsAssignableFrom(typeof(HashSet<Material>)))
            {
                materialSetCount++;
            }
            else
            {
                return false;
            }
        }

        return gameObjectCount == 1 &&
               integerCount == 2 &&
               platformSettingsCount == 1 &&
               materialSetCount == 1 &&
               parameters.Length == (hasOutputFolder ? 6 : 5);
    }

    private static object[] BuildMergeArguments(
        MethodInfo mergeMethod,
        GameObject rootObject,
        int atlasSize,
        int anisotropicLevel,
        List<TextureImporterPlatformSettings> platformSettings,
        HashSet<Material> selectedMaterials,
        string outputFolder)
    {
        ParameterInfo[] parameters = mergeMethod.GetParameters();
        object[] arguments = new object[parameters.Length];
        int integerIndex = 0;

        for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
        {
            Type parameterType = parameters[parameterIndex].ParameterType;
            if (parameterType == typeof(GameObject))
            {
                arguments[parameterIndex] = rootObject;
            }
            else if (parameterType == typeof(int))
            {
                arguments[parameterIndex] = integerIndex++ == 0
                    ? atlasSize
                    : anisotropicLevel;
            }
            else if (parameterType == typeof(string))
            {
                arguments[parameterIndex] = outputFolder;
            }
            else if (parameterType.IsAssignableFrom(
                         typeof(List<TextureImporterPlatformSettings>)))
            {
                arguments[parameterIndex] = platformSettings;
            }
            else if (parameterType.IsAssignableFrom(typeof(HashSet<Material>)))
            {
                arguments[parameterIndex] = selectedMaterials;
            }
        }

        return arguments;
    }

    private static void MoveLegacyGeneratedAssets(string destinationFolder)
    {
        string normalizedDestinationFolder = NormalizeAssetPath(destinationFolder);
        string normalizedLegacyFolder = NormalizeAssetPath(DefaultOutputFolder);

        if (normalizedDestinationFolder.Equals(
                normalizedLegacyFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!AssetDatabase.IsValidFolder(normalizedLegacyFolder))
            return;

        List<string> legacyAssetPaths = FindGeneratedOutputAssetPaths(normalizedLegacyFolder)
            .Where(path => !IsPathInsideFolder(path, normalizedDestinationFolder))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int assetIndex = 0; assetIndex < legacyAssetPaths.Count; assetIndex++)
        {
            string sourceAssetPath = legacyAssetPaths[assetIndex];
            string relativeAssetPath = sourceAssetPath.Substring(
                normalizedLegacyFolder.Length).TrimStart('/');
            string relativeDirectory = Path.GetDirectoryName(relativeAssetPath);
            string destinationDirectory = string.IsNullOrEmpty(relativeDirectory)
                ? normalizedDestinationFolder
                : normalizedDestinationFolder + "/" + relativeDirectory.Replace('\\', '/');

            if (!EnsureFolderExists(destinationDirectory, out string folderError))
                throw new InvalidOperationException(folderError);

            string requestedDestinationPath = destinationDirectory + "/" +
                                              Path.GetFileName(sourceAssetPath);
            string destinationAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                requestedDestinationPath);
            string moveError = AssetDatabase.MoveAsset(
                sourceAssetPath,
                destinationAssetPath);

            if (!string.IsNullOrEmpty(moveError))
                throw new IOException(moveError);
        }

        DeleteEmptyFolders(normalizedLegacyFolder);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void ProtectLatestMergeOutputs(
        GameObject rootObject,
        string outputFolder,
        HashSet<string> outputAssetsBeforeMerge)
    {
        HashSet<string> outputAssetsAfterMerge = FindGeneratedOutputAssetPaths(
            outputFolder);

        foreach (string assetPath in outputAssetsAfterMerge)
        {
            if (outputAssetsBeforeMerge == null ||
                !outputAssetsBeforeMerge.Contains(assetPath))
            {
                PendingMergeOutputAssetPaths.Add(assetPath);
            }
        }

        if (rootObject == null)
            return;

        Renderer[] renderers = rootObject.GetComponentsInChildren<Renderer>(true);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            if (renderer == null)
                continue;

            Material[] sharedMaterials = renderer.sharedMaterials;
            for (int materialIndex = 0;
                 materialIndex < sharedMaterials.Length;
                 materialIndex++)
            {
                AddAssetAndDependenciesInsideFolder(
                    sharedMaterials[materialIndex],
                    outputFolder,
                    PendingMergeOutputAssetPaths);
            }

            SkinnedMeshRenderer skinnedRenderer = renderer as SkinnedMeshRenderer;
            if (skinnedRenderer != null)
            {
                AddAssetAndDependenciesInsideFolder(
                    skinnedRenderer.sharedMesh,
                    outputFolder,
                    PendingMergeOutputAssetPaths);
            }
        }

        MeshFilter[] meshFilters = rootObject.GetComponentsInChildren<MeshFilter>(true);
        for (int filterIndex = 0; filterIndex < meshFilters.Length; filterIndex++)
        {
            AddAssetAndDependenciesInsideFolder(
                meshFilters[filterIndex].sharedMesh,
                outputFolder,
                PendingMergeOutputAssetPaths);
        }

        MeshCollider[] meshColliders = rootObject.GetComponentsInChildren<MeshCollider>(true);
        for (int colliderIndex = 0; colliderIndex < meshColliders.Length; colliderIndex++)
        {
            AddAssetAndDependenciesInsideFolder(
                meshColliders[colliderIndex].sharedMesh,
                outputFolder,
                PendingMergeOutputAssetPaths);
        }
    }

    private static void AddAssetAndDependenciesInsideFolder(
        UnityEngine.Object asset,
        string outputFolder,
        HashSet<string> destination)
    {
        if (asset == null || destination == null)
            return;

        string assetPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(asset));
        if (string.IsNullOrEmpty(assetPath))
            return;

        if (IsPathInsideFolder(assetPath, outputFolder))
            destination.Add(assetPath);

        string[] dependencyPaths;
        try
        {
            dependencyPaths = AssetDatabase.GetDependencies(assetPath, true);
        }
        catch (Exception)
        {
            return;
        }

        for (int dependencyIndex = 0;
             dependencyIndex < dependencyPaths.Length;
             dependencyIndex++)
        {
            string dependencyPath = NormalizeAssetPath(
                dependencyPaths[dependencyIndex]);
            if (IsPathInsideFolder(dependencyPath, outputFolder))
                destination.Add(dependencyPath);
        }
    }

    private static HashSet<string> FindGeneratedOutputAssetPaths(string folderPath)
    {
        HashSet<string> assetPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        string[] assetGuids = AssetDatabase.FindAssets(string.Empty, new[] { folderPath });

        for (int guidIndex = 0; guidIndex < assetGuids.Length; guidIndex++)
        {
            string assetPath = NormalizeAssetPath(
                AssetDatabase.GUIDToAssetPath(assetGuids[guidIndex]));
            if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                continue;

            UnityEngine.Object mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (mainAsset == null)
                continue;

            Type mainAssetType = mainAsset.GetType();
            for (int typeIndex = 0; typeIndex < GeneratedOutputTypes.Length; typeIndex++)
            {
                if (!GeneratedOutputTypes[typeIndex].IsAssignableFrom(mainAssetType))
                    continue;

                assetPaths.Add(assetPath);
                break;
            }
        }

        return assetPaths;
    }

    private static HashSet<string> FindReferencedCandidateAssets(
        HashSet<string> candidateAssetPaths)
    {
        HashSet<string> referencedAssetPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        FindProjectAssetReferences(candidateAssetPaths, referencedAssetPaths);
        FindLoadedObjectReferences(candidateAssetPaths, referencedAssetPaths);
        return referencedAssetPaths;
    }

    private static void FindProjectAssetReferences(
        HashSet<string> candidateAssetPaths,
        HashSet<string> referencedAssetPaths)
    {
        string[] allAssetPaths = AssetDatabase.GetAllAssetPaths();
        for (int assetIndex = 0; assetIndex < allAssetPaths.Length; assetIndex++)
        {
            string assetPath = NormalizeAssetPath(allAssetPaths[assetIndex]);
            if (string.IsNullOrEmpty(assetPath) ||
                candidateAssetPaths.Contains(assetPath) ||
                AssetDatabase.IsValidFolder(assetPath))
            {
                continue;
            }

            AddCandidateDependencies(
                assetPath,
                candidateAssetPaths,
                referencedAssetPaths);
        }
    }

    private static void FindLoadedObjectReferences(
        HashSet<string> candidateAssetPaths,
        HashSet<string> referencedAssetPaths)
    {
        // Explicit renderer/mesh scans are more reliable than depending only
        // on SerializedObject traversal for native renderer properties.
        Renderer[] loadedRenderers = Resources.FindObjectsOfTypeAll<Renderer>();
        for (int rendererIndex = 0;
             rendererIndex < loadedRenderers.Length;
             rendererIndex++)
        {
            Renderer renderer = loadedRenderers[rendererIndex];
            if (renderer == null || EditorUtility.IsPersistent(renderer))
                continue;

            Material[] sharedMaterials = renderer.sharedMaterials;
            for (int materialIndex = 0;
                 materialIndex < sharedMaterials.Length;
                 materialIndex++)
            {
                Material material = sharedMaterials[materialIndex];
                if (material == null)
                    continue;

                string materialPath = NormalizeAssetPath(
                    AssetDatabase.GetAssetPath(material));
                if (!string.IsNullOrEmpty(materialPath))
                {
                    AddCandidateDependencies(
                        materialPath,
                        candidateAssetPaths,
                        referencedAssetPaths);
                }
            }

            SkinnedMeshRenderer skinnedRenderer = renderer as SkinnedMeshRenderer;
            if (skinnedRenderer != null && skinnedRenderer.sharedMesh != null)
            {
                string meshPath = NormalizeAssetPath(
                    AssetDatabase.GetAssetPath(skinnedRenderer.sharedMesh));
                if (!string.IsNullOrEmpty(meshPath))
                {
                    AddCandidateDependencies(
                        meshPath,
                        candidateAssetPaths,
                        referencedAssetPaths);
                }
            }
        }

        MeshFilter[] loadedMeshFilters = Resources.FindObjectsOfTypeAll<MeshFilter>();
        for (int filterIndex = 0;
             filterIndex < loadedMeshFilters.Length;
             filterIndex++)
        {
            MeshFilter filter = loadedMeshFilters[filterIndex];
            if (filter == null || EditorUtility.IsPersistent(filter) || filter.sharedMesh == null)
                continue;

            string meshPath = NormalizeAssetPath(
                AssetDatabase.GetAssetPath(filter.sharedMesh));
            if (!string.IsNullOrEmpty(meshPath))
            {
                AddCandidateDependencies(
                    meshPath,
                    candidateAssetPaths,
                    referencedAssetPaths);
            }
        }

        MeshCollider[] loadedMeshColliders = Resources.FindObjectsOfTypeAll<MeshCollider>();
        for (int colliderIndex = 0;
             colliderIndex < loadedMeshColliders.Length;
             colliderIndex++)
        {
            MeshCollider collider = loadedMeshColliders[colliderIndex];
            if (collider == null || EditorUtility.IsPersistent(collider) || collider.sharedMesh == null)
                continue;

            string meshPath = NormalizeAssetPath(
                AssetDatabase.GetAssetPath(collider.sharedMesh));
            if (!string.IsNullOrEmpty(meshPath))
            {
                AddCandidateDependencies(
                    meshPath,
                    candidateAssetPaths,
                    referencedAssetPaths);
            }
        }

        Component[] loadedComponents = Resources.FindObjectsOfTypeAll<Component>();
        for (int componentIndex = 0;
             componentIndex < loadedComponents.Length;
             componentIndex++)
        {
            Component component = loadedComponents[componentIndex];
            if (component == null || EditorUtility.IsPersistent(component))
                continue;

            AddSerializedObjectReferences(
                component,
                candidateAssetPaths,
                referencedAssetPaths);
        }

        ScriptableObject[] loadedScriptableObjects =
            Resources.FindObjectsOfTypeAll<ScriptableObject>();
        for (int objectIndex = 0;
             objectIndex < loadedScriptableObjects.Length;
             objectIndex++)
        {
            ScriptableObject scriptableObject = loadedScriptableObjects[objectIndex];
            if (scriptableObject == null || EditorUtility.IsPersistent(scriptableObject))
                continue;

            AddSerializedObjectReferences(
                scriptableObject,
                candidateAssetPaths,
                referencedAssetPaths);
        }
    }

    private static void AddSerializedObjectReferences(
        UnityEngine.Object owner,
        HashSet<string> candidateAssetPaths,
        HashSet<string> referencedAssetPaths)
    {
        try
        {
            SerializedObject serializedObject = new SerializedObject(owner);
            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;

            while (property.Next(enterChildren))
            {
                enterChildren = false;
                if (property.propertyType != SerializedPropertyType.ObjectReference)
                    continue;

                UnityEngine.Object referencedObject = property.objectReferenceValue;
                if (referencedObject == null)
                    continue;

                string referencedAssetPath = NormalizeAssetPath(
                    AssetDatabase.GetAssetPath(referencedObject));
                if (string.IsNullOrEmpty(referencedAssetPath))
                    continue;

                AddCandidateDependencies(
                    referencedAssetPath,
                    candidateAssetPaths,
                    referencedAssetPaths);
            }
        }
        catch (Exception)
        {
            // Some native editor objects cannot be serialized safely. They are ignored.
        }
    }

    private static void AddCandidateDependencies(
        string rootAssetPath,
        HashSet<string> candidateAssetPaths,
        HashSet<string> referencedAssetPaths)
    {
        string[] dependencyPaths;
        try
        {
            dependencyPaths = AssetDatabase.GetDependencies(rootAssetPath, true);
        }
        catch (Exception)
        {
            return;
        }

        for (int dependencyIndex = 0;
             dependencyIndex < dependencyPaths.Length;
             dependencyIndex++)
        {
            string dependencyPath = NormalizeAssetPath(dependencyPaths[dependencyIndex]);
            if (candidateAssetPaths.Contains(dependencyPath))
                referencedAssetPaths.Add(dependencyPath);
        }
    }

    private static bool IsPathInsideFolder(string assetPath, string folderPath)
    {
        string normalizedAssetPath = NormalizeAssetPath(assetPath);
        string normalizedFolderPath = NormalizeAssetPath(folderPath);
        return normalizedAssetPath.Equals(
                   normalizedFolderPath,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedAssetPath.StartsWith(
                   normalizedFolderPath + "/",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteEmptyFolders(string rootFolder)
    {
        string normalizedRootFolder = NormalizeAssetPath(rootFolder);
        if (!AssetDatabase.IsValidFolder(normalizedRootFolder))
            return;

        string absoluteRootFolder = AssetPathToAbsolutePath(normalizedRootFolder);
        if (!Directory.Exists(absoluteRootFolder))
            return;

        string[] directories = Directory.GetDirectories(
            absoluteRootFolder,
            "*",
            SearchOption.AllDirectories);

        Array.Sort(
            directories,
            (left, right) => right.Length.CompareTo(left.Length));

        for (int directoryIndex = 0; directoryIndex < directories.Length; directoryIndex++)
        {
            string directory = directories[directoryIndex];
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                continue;

            string assetFolderPath = AbsolutePathToAssetPath(directory);
            if (!string.IsNullOrEmpty(assetFolderPath))
                AssetDatabase.DeleteAsset(assetFolderPath);
        }
    }

    private static string AssetPathToAbsolutePath(string assetPath)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
    }

    private static string AbsolutePathToAssetPath(string absolutePath)
    {
        string normalizedAbsolutePath = NormalizeAssetPath(Path.GetFullPath(absolutePath));
        string normalizedProjectRoot = NormalizeAssetPath(
            Directory.GetParent(Application.dataPath).FullName);

        if (!normalizedAbsolutePath.StartsWith(
                normalizedProjectRoot + "/",
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return normalizedAbsolutePath.Substring(normalizedProjectRoot.Length + 1);
    }
}
