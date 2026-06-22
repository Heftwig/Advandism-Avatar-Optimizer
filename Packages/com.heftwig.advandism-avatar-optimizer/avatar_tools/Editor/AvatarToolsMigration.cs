using System;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
internal static class AvatarToolsMigration
{
    private static readonly string[] LegacyEmptyFolders =
    {
        "Assets/avatar_tools/Runtime/avatar_simplifier/Editor",
        "Assets/Editor"
    };

    static AvatarToolsMigration()
    {
        EditorApplication.delayCall -= RemoveLegacyEmptyFolders;
        EditorApplication.delayCall += RemoveLegacyEmptyFolders;
    }

    private static void RemoveLegacyEmptyFolders()
    {
        EditorApplication.delayCall -= RemoveLegacyEmptyFolders;

        bool changed = false;
        for (int folderIndex = 0; folderIndex < LegacyEmptyFolders.Length; folderIndex++)
            changed |= DeleteFolderWhenEmpty(LegacyEmptyFolders[folderIndex]);

        if (changed)
            AssetDatabase.Refresh();
    }

    private static bool DeleteFolderWhenEmpty(string assetFolderPath)
    {
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrEmpty(projectRoot))
            return false;

        string absoluteFolderPath = Path.Combine(
            projectRoot,
            assetFolderPath.Replace('/', Path.DirectorySeparatorChar));

        if (Directory.Exists(absoluteFolderPath))
        {
            try
            {
                using (var entries = Directory.EnumerateFileSystemEntries(absoluteFolderPath).GetEnumerator())
                {
                    if (entries.MoveNext())
                        return false;
                }
            }
            catch
            {
                return false;
            }

            if (AssetDatabase.IsValidFolder(assetFolderPath))
                return AssetDatabase.DeleteAsset(assetFolderPath);

            try
            {
                Directory.Delete(absoluteFolderPath, false);
            }
            catch
            {
                return false;
            }
        }

        string metaPath = absoluteFolderPath + ".meta";
        if (!File.Exists(metaPath))
            return false;

        try
        {
            File.Delete(metaPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
