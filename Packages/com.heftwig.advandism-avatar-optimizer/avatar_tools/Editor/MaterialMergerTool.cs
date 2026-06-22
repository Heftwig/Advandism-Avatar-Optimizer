using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Material atlas merger UI that can be hosted by an EditorWindow or custom inspector.
/// Keep this file inside an Editor folder.
/// </summary>
public sealed class MaterialMergerTool
{
    private const string OutputFolderPreferenceKey =
        "MaterialMerger.OutputFolder";
    private const string DeleteUnusedPreferenceKey =
        "MaterialMerger.DeleteUnusedOutputAssets";

    private static readonly int[] ResolutionSizes =
    {
        32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384
    };

    private static readonly string[] ResolutionLabels =
    {
        "32 × 32",
        "64 × 64",
        "128 × 128",
        "256 × 256",
        "512 × 512",
        "1024 × 1024",
        "2048 × 2048",
        "4096 × 4096",
        "8192 × 8192",
        "16384 × 16384"
    };

    [Serializable]
    private sealed class PlatformSettings
    {
        public string ImporterName;
        public string DisplayName;
        public int ResolutionIndex;
        public int Compression;
        public bool IsExtended;
    }

    private readonly List<Material> availableMaterials = new List<Material>();
    private readonly Dictionary<Material, bool> selectedMaterials =
        new Dictionary<Material, bool>();
    private readonly List<PlatformSettings> platformSettings =
        new List<PlatformSettings>();

    private GameObject rootObject;
    private Vector2 materialScrollPosition;
    private Vector2 platformScrollPosition;
    private bool showTextureSettings;
    private bool showPlatformSettings;
    private bool showExtendedPlatforms;
    private bool refreshQueued;
    private int anisotropicLevel;
    private string outputFolderPath;
    private bool deleteUnusedOutputAssets;

    public void OnEnable()
    {
        SyncRootToSelection();
        InitializePlatformSettings();
        outputFolderPath = MaterialMergerAssetUtility.NormalizeAssetPath(
            EditorPrefs.GetString(
                OutputFolderPreferenceKey,
                MaterialMergerAssetUtility.DefaultOutputFolder));
        deleteUnusedOutputAssets = EditorPrefs.GetBool(
            DeleteUnusedPreferenceKey,
            true);

        RefreshMaterials();
    }

    public void OnDisable()
    {
    }

    private void SyncRootToSelection()
    {
        GameObject activeObject = Selection.activeGameObject;
        if (activeObject == null)
            return;

        bool selectionIsInsideCurrentRoot = rootObject != null &&
                                            (activeObject == rootObject ||
                                             activeObject.transform.IsChildOf(rootObject.transform));
        if (!selectionIsInsideCurrentRoot)
            rootObject = activeObject;
    }

    public void OnHierarchyChange()
    {
        refreshQueued = true;
    }

    public void OnProjectChange()
    {
        refreshQueued = true;
    }

    public void Draw()
    {
        if (refreshQueued)
        {
            refreshQueued = false;
            RefreshMaterials();
        }

        DrawRootSection();
        EditorGUILayout.Space(6f);
        DrawOutputSection();
        EditorGUILayout.Space(6f);
        DrawTextureSettings();
        EditorGUILayout.Space(8f);
        DrawSectionTitle("Materials");
        DrawMaterialSelectionToolbar();
        DrawMaterialList();
        EditorGUILayout.Space(8f);
        DrawMergeButton();
    }

    private void DrawRootSection()
    {
        DrawSectionTitle("Target");

        EditorGUI.BeginChangeCheck();
        GameObject newRootObject = (GameObject)EditorGUILayout.ObjectField(
            "Root",
            rootObject,
            typeof(GameObject),
            true);
        if (EditorGUI.EndChangeCheck())
        {
            rootObject = newRootObject;
            RefreshMaterials();
        }
    }

    private void DrawOutputSection()
    {
        DrawSectionTitle("Output");

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        string editedFolderPath = EditorGUILayout.TextField(
            "Save Folder",
            outputFolderPath);
        if (EditorGUI.EndChangeCheck())
        {
            outputFolderPath = MaterialMergerAssetUtility.NormalizeAssetPath(
                editedFolderPath);
            EditorPrefs.SetString(OutputFolderPreferenceKey, outputFolderPath);
        }

        if (GUILayout.Button("Browse", GUILayout.Width(68f)))
            BrowseForOutputFolder();
        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginChangeCheck();
        deleteUnusedOutputAssets = EditorGUILayout.ToggleLeft(
            "Delete unused output assets",
            deleteUnusedOutputAssets);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetBool(
                DeleteUnusedPreferenceKey,
                deleteUnusedOutputAssets);
        }
    }

    private void DrawTextureSettings()
    {
        showTextureSettings = EditorGUILayout.Foldout(
            showTextureSettings,
            "Texture Settings",
            true);
        if (!showTextureSettings)
            return;

        EditorGUI.indentLevel++;
        anisotropicLevel = EditorGUILayout.IntSlider(
            "Anisotropic Level",
            anisotropicLevel,
            0,
            16);

        EditorGUILayout.BeginHorizontal();
        showPlatformSettings = EditorGUILayout.Foldout(
            showPlatformSettings,
            "Platform Overrides",
            true);
        GUILayout.FlexibleSpace();
        showExtendedPlatforms = GUILayout.Toggle(
            showExtendedPlatforms,
            "Extended",
            EditorStyles.miniButton,
            GUILayout.Width(74f));
        EditorGUILayout.EndHorizontal();

        if (showPlatformSettings)
            DrawPlatformSettings();

        EditorGUI.indentLevel--;
    }

    private void DrawPlatformSettings()
    {
        int visiblePlatformCount = platformSettings.Count(
            settings => !settings.IsExtended || showExtendedPlatforms);
        float scrollHeight = Mathf.Min(visiblePlatformCount * 58f, 230f);

        platformScrollPosition = EditorGUILayout.BeginScrollView(
            platformScrollPosition,
            GUILayout.Height(scrollHeight));

        for (int platformIndex = 0;
             platformIndex < platformSettings.Count;
             platformIndex++)
        {
            PlatformSettings settings = platformSettings[platformIndex];
            if (settings.IsExtended && !showExtendedPlatforms)
                continue;

            EditorGUILayout.LabelField(
                settings.DisplayName,
                EditorStyles.miniBoldLabel);
            settings.ResolutionIndex = EditorGUILayout.Popup(
                "Max Size",
                settings.ResolutionIndex,
                ResolutionLabels);
            settings.Compression = EditorGUILayout.IntSlider(
                "Compression",
                settings.Compression,
                0,
                100);
            EditorGUILayout.Space(3f);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawMaterialSelectionToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("All", EditorStyles.toolbarButton, GUILayout.Width(42f)))
            SetAllSelections(true);
        if (GUILayout.Button("None", EditorStyles.toolbarButton, GUILayout.Width(46f)))
            SetAllSelections(false);
        if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(58f)))
            RefreshMaterials();

        GUILayout.FlexibleSpace();
        GUILayout.Label(
            CountSelectedMaterials() + " / " + availableMaterials.Count,
            EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }

    private void DrawMaterialList()
    {
        materialScrollPosition = EditorGUILayout.BeginScrollView(
            materialScrollPosition,
            GUILayout.MinHeight(120f));

        for (int materialIndex = 0;
             materialIndex < availableMaterials.Count;
             materialIndex++)
        {
            Material material = availableMaterials[materialIndex];
            if (material == null)
                continue;

            if (!selectedMaterials.ContainsKey(material))
                selectedMaterials[material] = true;

            Rect rowRect = EditorGUILayout.GetControlRect(
                false,
                EditorGUIUtility.singleLineHeight + 4f);
            rowRect.y += 2f;

            Rect toggleRect = new Rect(
                rowRect.x,
                rowRect.y,
                18f,
                EditorGUIUtility.singleLineHeight);
            bool showShaderName = rowRect.width >= 430f;
            Rect shaderRect = showShaderName
                ? new Rect(
                    rowRect.xMax - 170f,
                    rowRect.y,
                    170f,
                    EditorGUIUtility.singleLineHeight)
                : Rect.zero;
            float materialRight = showShaderName
                ? shaderRect.x - 6f
                : rowRect.xMax;
            Rect materialRect = new Rect(
                toggleRect.xMax + 2f,
                rowRect.y,
                Mathf.Max(40f, materialRight - toggleRect.xMax - 2f),
                EditorGUIUtility.singleLineHeight);

            selectedMaterials[material] = EditorGUI.Toggle(
                toggleRect,
                selectedMaterials[material]);
            EditorGUI.ObjectField(
                materialRect,
                material,
                typeof(Material),
                false);

            if (showShaderName)
            {
                string shaderName = material.shader != null
                    ? material.shader.name
                    : "Missing Shader";
                GUI.Label(shaderRect, shaderName, EditorStyles.miniLabel);
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawMergeButton()
    {
        bool canMerge = rootObject != null &&
                        CountSelectedMaterials() >= 2 &&
                        MaterialMergerAssetUtility.IsValidAssetFolder(
                            outputFolderPath);

        using (new EditorGUI.DisabledScope(!canMerge))
        {
            if (GUILayout.Button(
                    "Merge Selected Materials",
                    GUILayout.Height(30f)))
            {
                MergeSelectedMaterials();
            }
        }
    }

    private void BrowseForOutputFolder()
    {
        string initialAbsoluteFolder = Application.dataPath;
        if (MaterialMergerAssetUtility.IsValidAssetFolder(outputFolderPath))
        {
            string projectRootPath = Application.dataPath.Substring(
                0,
                Application.dataPath.Length - "Assets".Length);
            initialAbsoluteFolder = projectRootPath + outputFolderPath;
        }

        string selectedAbsoluteFolder = EditorUtility.OpenFolderPanel(
            "Choose Material Merge Output Folder",
            initialAbsoluteFolder,
            string.Empty);
        if (string.IsNullOrEmpty(selectedAbsoluteFolder))
            return;

        if (!MaterialMergerAssetUtility.TryConvertAbsoluteFolderToAssetPath(
                selectedAbsoluteFolder,
                out string selectedAssetFolder))
        {
            return;
        }

        outputFolderPath = selectedAssetFolder;
        EditorPrefs.SetString(OutputFolderPreferenceKey, outputFolderPath);
    }

    private void MergeSelectedMaterials()
    {
        if (rootObject == null)
            return;

        HashSet<Material> chosenMaterials = new HashSet<Material>();
        foreach (KeyValuePair<Material, bool> selection in selectedMaterials)
        {
            if (selection.Value && selection.Key != null)
                chosenMaterials.Add(selection.Key);
        }

        if (chosenMaterials.Count < 2)
            return;

        if (!MaterialMergerAssetUtility.EnsureFolderExists(
                outputFolderPath,
                out string folderError))
        {
            Debug.LogError(folderError);
            return;
        }

        List<TextureImporterPlatformSettings> importerSettings =
            BuildTextureImporterSettings();

        const string undoName = "Merge Materials";
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(undoName);
        Undo.RegisterFullObjectHierarchyUndo(rootObject, undoName);
        List<UnityEngine.Object> undoSourceAssets = RegisterSourceAssetUndo(
            chosenMaterials,
            undoName);
        MaterialMergerAssetUtility.ProtectAssetsForUndo(undoSourceAssets);

        try
        {
            MaterialMergerAssetUtility.Merge(
                rootObject,
                ResolutionSizes[platformSettings[0].ResolutionIndex],
                anisotropicLevel,
                importerSettings,
                chosenMaterials,
                outputFolderPath);

            if (rootObject != null)
            {
                EditorUtility.SetDirty(rootObject);
                if (rootObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(rootObject.scene);
            }

            if (deleteUnusedOutputAssets)
            {
                MaterialMergerAssetUtility.CleanupResult cleanupResult =
                    MaterialMergerAssetUtility.DeleteUnusedOutputAssets(
                        outputFolderPath);

                if (cleanupResult.FailedAssetPaths.Count > 0)
                {
                    Debug.LogWarning(
                        "Could not delete these unused output assets:\n" +
                        string.Join("\n", cleanupResult.FailedAssetPaths));
                }
            }

            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(undoGroup);

            refreshQueued = true;
            SceneView.RepaintAll();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            Undo.RevertAllInCurrentGroup();
        }
    }

    private List<TextureImporterPlatformSettings> BuildTextureImporterSettings()
    {
        List<TextureImporterPlatformSettings> importerSettings =
            new List<TextureImporterPlatformSettings>(platformSettings.Count);

        for (int platformIndex = 0;
             platformIndex < platformSettings.Count;
             platformIndex++)
        {
            PlatformSettings settings = platformSettings[platformIndex];
            importerSettings.Add(new TextureImporterPlatformSettings
            {
                name = settings.ImporterName,
                overridden = true,
                maxTextureSize = ResolutionSizes[settings.ResolutionIndex],
                compressionQuality = 100 - settings.Compression
            });
        }

        return importerSettings;
    }

    private List<UnityEngine.Object> RegisterSourceAssetUndo(
        HashSet<Material> chosenMaterials,
        string undoName)
    {
        List<UnityEngine.Object> sourceAssets = new List<UnityEngine.Object>();

        foreach (Material material in chosenMaterials)
        {
            if (material != null && !sourceAssets.Contains(material))
                sourceAssets.Add(material);

            string materialPath = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(materialPath))
                continue;

            string[] dependencyPaths = AssetDatabase.GetDependencies(materialPath, true);
            for (int dependencyIndex = 0;
                 dependencyIndex < dependencyPaths.Length;
                 dependencyIndex++)
            {
                string dependencyPath = dependencyPaths[dependencyIndex];
                Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(dependencyPath);
                if (texture != null && !sourceAssets.Contains(texture))
                    sourceAssets.Add(texture);

                if (AssetImporter.GetAtPath(dependencyPath) is TextureImporter)
                    Undo.RegisterImporterUndo(dependencyPath, undoName);
            }
        }

        MeshFilter[] meshFilters = rootObject.GetComponentsInChildren<MeshFilter>(true);
        for (int filterIndex = 0; filterIndex < meshFilters.Length; filterIndex++)
        {
            Mesh mesh = meshFilters[filterIndex].sharedMesh;
            if (mesh != null && !sourceAssets.Contains(mesh))
                sourceAssets.Add(mesh);
        }

        SkinnedMeshRenderer[] skinnedRenderers =
            rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int rendererIndex = 0;
             rendererIndex < skinnedRenderers.Length;
             rendererIndex++)
        {
            Mesh mesh = skinnedRenderers[rendererIndex].sharedMesh;
            if (mesh != null && !sourceAssets.Contains(mesh))
                sourceAssets.Add(mesh);
        }

        if (sourceAssets.Count > 0)
        {
            Undo.RegisterCompleteObjectUndo(
                sourceAssets.ToArray(),
                undoName);
        }

        return sourceAssets;
    }

    private void SetAllSelections(bool selected)
    {
        for (int materialIndex = 0;
             materialIndex < availableMaterials.Count;
             materialIndex++)
        {
            Material material = availableMaterials[materialIndex];
            if (material != null)
                selectedMaterials[material] = selected;
        }
    }

    private int CountSelectedMaterials()
    {
        int selectedCount = 0;
        foreach (KeyValuePair<Material, bool> selection in selectedMaterials)
        {
            if (selection.Key != null && selection.Value)
                selectedCount++;
        }

        return selectedCount;
    }

    private void RefreshMaterials()
    {
        Dictionary<Material, bool> previousSelections =
            new Dictionary<Material, bool>(selectedMaterials);

        availableMaterials.Clear();
        selectedMaterials.Clear();

        if (rootObject == null)
            return;

        Renderer[] renderers = rootObject.GetComponentsInChildren<Renderer>(true);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                continue;

            Material[] rendererMaterials = renderer.sharedMaterials;
            for (int materialIndex = 0;
                 materialIndex < rendererMaterials.Length;
                 materialIndex++)
            {
                Material material = rendererMaterials[materialIndex];
                if (material == null || availableMaterials.Contains(material))
                    continue;

                availableMaterials.Add(material);
                selectedMaterials[material] = previousSelections.TryGetValue(
                    material,
                    out bool wasSelected)
                    ? wasSelected
                    : true;
            }
        }

        availableMaterials.Sort(CompareMaterials);
    }

    private static int CompareMaterials(Material left, Material right)
    {
        string leftPath = left != null ? AssetDatabase.GetAssetPath(left) : string.Empty;
        string rightPath = right != null ? AssetDatabase.GetAssetPath(right) : string.Empty;
        int pathComparison = string.Compare(
            leftPath,
            rightPath,
            StringComparison.OrdinalIgnoreCase);
        if (pathComparison != 0)
            return pathComparison;

        return string.Compare(
            left != null ? left.name : string.Empty,
            right != null ? right.name : string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    public void OnSelectionChange()
    {
        SyncRootToSelection();
        RefreshMaterials();
    }

    public void OnUndoRedoPerformed()
    {
        refreshQueued = true;
        SceneView.RepaintAll();
    }

    private void InitializePlatformSettings()
    {
        if (platformSettings.Count > 0)
            return;

        platformSettings.Add(CreatePlatform(
            "Standalone", "PC, Mac & Linux", 7, false));
        platformSettings.Add(CreatePlatform(
            "Android", "Android", 6, false));
        platformSettings.Add(CreatePlatform(
            "iPhone", "iOS", 6, false));
        platformSettings.Add(CreatePlatform(
            "WebGL", "WebGL", 6, true));
        platformSettings.Add(CreatePlatform(
            "Windows Store Apps", "Windows Store", 6, true));
        platformSettings.Add(CreatePlatform(
            "PS4", "PlayStation", 6, true));
        platformSettings.Add(CreatePlatform(
            "XboxOne", "Xbox", 6, true));
        platformSettings.Add(CreatePlatform(
            "Nintendo Switch", "Nintendo Switch", 6, true));
        platformSettings.Add(CreatePlatform(
            "tvOS", "tvOS", 6, true));
    }

    private static PlatformSettings CreatePlatform(
        string importerName,
        string displayName,
        int resolutionIndex,
        bool isExtended)
    {
        return new PlatformSettings
        {
            ImporterName = importerName,
            DisplayName = displayName,
            ResolutionIndex = resolutionIndex,
            Compression = 0,
            IsExtended = isExtended
        };
    }

    private static void DrawSectionTitle(string title)
    {
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
    }
}
