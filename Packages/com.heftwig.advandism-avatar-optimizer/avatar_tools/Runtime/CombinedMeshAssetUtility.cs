using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Saves combined meshes as standalone assets and removes unused standalone mesh assets
/// from a user-selected folder. Keep this file inside an Editor folder.
/// </summary>
public static class CombinedMeshAssetUtility
{
    public const string DefaultSaveFolder = "Assets/CombinedMeshes";

    private const string GeneratedMeshLabel = "SkinnedMeshCombiner.GeneratedMesh";

    public static bool IsValidAssetFolder(string folderPath)
    {
        string normalizedPath = NormalizeAssetPath(folderPath);
        return normalizedPath == "Assets" || normalizedPath.StartsWith("Assets/", StringComparison.Ordinal);
    }

    public static string NormalizeAssetPath(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return string.Empty;

        return assetPath.Trim().Replace('\\', '/').TrimEnd('/');
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

        if (!normalizedAbsolutePath.Equals(normalizedAssetsPath, StringComparison.OrdinalIgnoreCase) &&
            !normalizedAbsolutePath.StartsWith(
                normalizedAssetsPath + "/",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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
            errorMessage = "The save folder must be inside this project's Assets folder.";
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
                string createdGuid = AssetDatabase.CreateFolder(currentFolder, folderParts[partIndex]);
                if (string.IsNullOrEmpty(createdGuid))
                {
                    errorMessage = $"Unity could not create the folder '{nextFolder}'.";
                    return false;
                }
            }

            currentFolder = nextFolder;
        }

        return AssetDatabase.IsValidFolder(normalizedFolderPath);
    }

    public static string SaveCombinedMesh(
        Mesh combinedMesh,
        string folderPath,
        string preferredAssetName)
    {
        if (combinedMesh == null)
            throw new ArgumentNullException(nameof(combinedMesh));

        string normalizedFolderPath = NormalizeAssetPath(folderPath);
        if (!EnsureFolderExists(normalizedFolderPath, out string errorMessage))
            throw new InvalidOperationException(errorMessage);

        if (EditorUtility.IsPersistent(combinedMesh))
            return AssetDatabase.GetAssetPath(combinedMesh);

        string safeAssetName = SanitizeFileName(preferredAssetName);
        if (string.IsNullOrWhiteSpace(safeAssetName))
            safeAssetName = "CombinedMesh";

        string requestedAssetPath = normalizedFolderPath + "/" + safeAssetName + ".asset";
        string uniqueAssetPath = AssetDatabase.GenerateUniqueAssetPath(requestedAssetPath);

        AssetDatabase.CreateAsset(combinedMesh, uniqueAssetPath);
        AddGeneratedMeshLabel(combinedMesh);
        EditorUtility.SetDirty(combinedMesh);
        AssetDatabase.SaveAssets();

        return uniqueAssetPath;
    }

    public static CleanupResult MoveUnusedMeshAssetsToTrash(
        string folderPath,
        IEnumerable<string> protectedAssetPaths = null)
    {
        CleanupResult result = new CleanupResult();
        string normalizedFolderPath = NormalizeAssetPath(folderPath);

        if (!AssetDatabase.IsValidFolder(normalizedFolderPath))
            return result;

        HashSet<string> candidateMeshPaths = FindStandaloneMeshAssetPaths(normalizedFolderPath);
        if (protectedAssetPaths != null)
        {
            foreach (string protectedAssetPath in protectedAssetPaths)
            {
                string normalizedProtectedPath = NormalizeAssetPath(protectedAssetPath);
                if (!string.IsNullOrEmpty(normalizedProtectedPath))
                    candidateMeshPaths.Remove(normalizedProtectedPath);
            }
        }

        if (candidateMeshPaths.Count == 0)
            return result;

        HashSet<string> referencedMeshPaths = FindReferencedMeshPaths(candidateMeshPaths);

        foreach (string candidatePath in candidateMeshPaths)
        {
            if (referencedMeshPaths.Contains(candidatePath))
                continue;

            if (AssetDatabase.MoveAssetToTrash(candidatePath))
            {
                result.MovedToTrashPaths.Add(candidatePath);
            }
            else
            {
                result.FailedPaths.Add(candidatePath);
            }
        }

        if (result.MovedToTrashPaths.Count > 0)
        {
            AssetDatabase.Refresh();
            Debug.Log(
                $"Skinned Mesh Combiner moved {result.MovedToTrashPaths.Count} unused mesh " +
                $"asset(s) from '{normalizedFolderPath}' to the OS trash.");
        }

        if (result.FailedPaths.Count > 0)
        {
            Debug.LogWarning(
                "Skinned Mesh Combiner could not move these unused mesh assets to the trash:\n" +
                string.Join("\n", result.FailedPaths));
        }

        return result;
    }

    private static HashSet<string> FindStandaloneMeshAssetPaths(string folderPath)
    {
        HashSet<string> meshAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] meshGuids = AssetDatabase.FindAssets("t:Mesh", new[] { folderPath });

        for (int meshIndex = 0; meshIndex < meshGuids.Length; meshIndex++)
        {
            string meshAssetPath = NormalizeAssetPath(
                AssetDatabase.GUIDToAssetPath(meshGuids[meshIndex]));

            // Never delete model files such as FBX/OBJ. Cleanup is limited to standalone
            // Unity .asset files whose main asset is a Mesh.
            if (!meshAssetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!(AssetDatabase.LoadMainAssetAtPath(meshAssetPath) is Mesh))
                continue;

            meshAssetPaths.Add(meshAssetPath);
        }

        return meshAssetPaths;
    }

    private static HashSet<string> FindReferencedMeshPaths(HashSet<string> candidateMeshPaths)
    {
        HashSet<string> referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FindProjectAssetReferences(candidateMeshPaths, referencedPaths);
        FindLoadedObjectReferences(candidateMeshPaths, referencedPaths);
        return referencedPaths;
    }

    private static void FindProjectAssetReferences(
        HashSet<string> candidateMeshPaths,
        HashSet<string> referencedPaths)
    {
        string[] allAssetPaths = AssetDatabase.GetAllAssetPaths();
        for (int assetIndex = 0; assetIndex < allAssetPaths.Length; assetIndex++)
        {
            string assetPath = NormalizeAssetPath(allAssetPaths[assetIndex]);
            if (string.IsNullOrEmpty(assetPath) ||
                candidateMeshPaths.Contains(assetPath) ||
                AssetDatabase.IsValidFolder(assetPath))
            {
                continue;
            }

            string[] dependencies;
            try
            {
                dependencies = AssetDatabase.GetDependencies(assetPath, false);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"Could not inspect asset dependencies for '{assetPath}': {exception.Message}");
                continue;
            }

            for (int dependencyIndex = 0;
                 dependencyIndex < dependencies.Length;
                 dependencyIndex++)
            {
                string dependencyPath = NormalizeAssetPath(dependencies[dependencyIndex]);
                if (candidateMeshPaths.Contains(dependencyPath))
                    referencedPaths.Add(dependencyPath);
            }
        }
    }

    private static void FindLoadedObjectReferences(
        HashSet<string> candidateMeshPaths,
        HashSet<string> referencedPaths)
    {
        Component[] loadedComponents = Resources.FindObjectsOfTypeAll<Component>();
        for (int componentIndex = 0;
             componentIndex < loadedComponents.Length;
             componentIndex++)
        {
            Component component = loadedComponents[componentIndex];
            if (component == null || EditorUtility.IsPersistent(component))
                continue;
            if (component.gameObject == null || !component.gameObject.scene.IsValid())
                continue;

            SerializedObject serializedComponent;
            try
            {
                serializedComponent = new SerializedObject(component);
            }
            catch
            {
                continue;
            }

            SerializedProperty property = serializedComponent.GetIterator();
            bool enterChildren = true;
            while (property.Next(enterChildren))
            {
                enterChildren = false;
                if (property.propertyType != SerializedPropertyType.ObjectReference)
                    continue;
                if (!(property.objectReferenceValue is Mesh referencedMesh))
                    continue;

                string referencedPath = NormalizeAssetPath(
                    AssetDatabase.GetAssetPath(referencedMesh));
                if (candidateMeshPaths.Contains(referencedPath))
                    referencedPaths.Add(referencedPath);
            }
        }
    }

    private static void AddGeneratedMeshLabel(Mesh mesh)
    {
        List<string> labels = new List<string>(AssetDatabase.GetLabels(mesh));
        if (labels.Contains(GeneratedMeshLabel))
            return;

        labels.Add(GeneratedMeshLabel);
        AssetDatabase.SetLabels(mesh, labels.ToArray());
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        char[] characters = fileName.Trim().ToCharArray();
        for (int characterIndex = 0;
             characterIndex < characters.Length;
             characterIndex++)
        {
            char character = characters[characterIndex];
            if (character == '/' || character == '\\' ||
                Array.IndexOf(invalidCharacters, character) >= 0)
            {
                characters[characterIndex] = '_';
            }
        }

        return new string(characters);
    }

    public sealed class CleanupResult
    {
        public readonly List<string> MovedToTrashPaths = new List<string>();
        public readonly List<string> FailedPaths = new List<string>();
    }
}
