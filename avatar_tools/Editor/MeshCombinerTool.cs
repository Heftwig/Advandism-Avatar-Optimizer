using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Mesh-combiner UI hosted by AvatarToolsWindow.
/// Keep this file inside an Editor folder.
/// </summary>
public sealed class MeshCombinerTool
{
    private const string SaveFolderPreferenceKey =
        "SkinnedMeshCombiner.SaveFolder";
    private const string AutoCleanupPreferenceKey =
        "SkinnedMeshCombiner.AutoCleanupUnusedMeshes";

    private const float RendererRowHeight = 44f;
    private const float MinimumSourceListHeight = 150f;
    private const float MaximumSourceListHeight = 360f;

    private readonly List<SkinnedMeshRenderer> skinnedRenderers =
        new List<SkinnedMeshRenderer>();
    private readonly List<MeshRenderer> staticRenderers =
        new List<MeshRenderer>();
    private readonly List<bool> selectedSkinnedRenderers =
        new List<bool>();
    private readonly List<bool> selectedStaticRenderers =
        new List<bool>();

    private GameObject rootObject;
    private Vector2 sourceScrollPosition;
    private SkinnedMeshMerger.BlendShapeNameMode blendShapeNameMode =
        SkinnedMeshMerger.BlendShapeNameMode.MergeMatchingNames;
    private SkinnedMeshMerger.SourceHandlingMode sourceHandlingMode =
        SkinnedMeshMerger.SourceHandlingMode.DeleteSourceObjects;
    private string saveFolderPath = CombinedMeshAssetUtility.DefaultSaveFolder;
    private bool automaticallyDeleteUnusedSavedMeshes = true;
    private bool isCombining;

    public void OnEnable()
    {
        saveFolderPath = CombinedMeshAssetUtility.NormalizeAssetPath(
            EditorPrefs.GetString(
                SaveFolderPreferenceKey,
                CombinedMeshAssetUtility.DefaultSaveFolder));
        automaticallyDeleteUnusedSavedMeshes = EditorPrefs.GetBool(
            AutoCleanupPreferenceKey,
            true);

        SyncRootToSelection();
        RefreshRendererLists();
    }

    public void OnDisable()
    {
    }

    public void OnSelectionChange()
    {
        if (isCombining)
            return;

        SyncRootToSelection();
        RefreshRendererLists();
    }

    public void OnUndoRedoPerformed()
    {
        RefreshRendererLists();
        SceneView.RepaintAll();
    }

    public void OnHierarchyChange()
    {
        if (!isCombining)
            RefreshRendererLists();
    }

    public void OnProjectChange()
    {
    }

    public void Draw()
    {
        using (new EditorGUI.DisabledScope(isCombining))
        {
            DrawTargetSection();

            if (rootObject == null)
            {
                EditorGUILayout.Space(12f);
                EditorGUILayout.LabelField(
                    "Select a root GameObject to scan its child renderers.",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUILayout.Space(7f);
            DrawCombineSettingsSection();

            EditorGUILayout.Space(7f);
            DrawOutputSection();

            EditorGUILayout.Space(9f);
            DrawSourceSection();

            EditorGUILayout.Space(9f);
            DrawPrimaryAction();
        }
    }

    private void DrawTargetSection()
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
            RefreshRendererLists();
        }
    }

    private void DrawCombineSettingsSection()
    {
        DrawSectionTitle("Combine Settings");

        blendShapeNameMode =
            (SkinnedMeshMerger.BlendShapeNameMode)EditorGUILayout.EnumPopup(
                "Blend Shape Names",
                blendShapeNameMode);
        sourceHandlingMode =
            (SkinnedMeshMerger.SourceHandlingMode)EditorGUILayout.EnumPopup(
                "Source Handling",
                sourceHandlingMode);
    }

    private void DrawOutputSection()
    {
        DrawSectionTitle("Output");

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        string editedSaveFolderPath = EditorGUILayout.DelayedTextField(
            "Save Folder",
            saveFolderPath);
        if (EditorGUI.EndChangeCheck())
        {
            saveFolderPath = CombinedMeshAssetUtility.NormalizeAssetPath(
                editedSaveFolderPath);
            EditorPrefs.SetString(SaveFolderPreferenceKey, saveFolderPath);
        }

        if (GUILayout.Button("Browse", GUILayout.Width(68f)))
            BrowseForSaveFolder();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        automaticallyDeleteUnusedSavedMeshes = EditorGUILayout.ToggleLeft(
            "Delete unused output meshes",
            automaticallyDeleteUnusedSavedMeshes);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetBool(
                AutoCleanupPreferenceKey,
                automaticallyDeleteUnusedSavedMeshes);
        }

        bool hasValidSaveFolder =
            CombinedMeshAssetUtility.IsValidAssetFolder(saveFolderPath);
        using (new EditorGUI.DisabledScope(!hasValidSaveFolder))
        {
            if (GUILayout.Button("Delete Unused Now", GUILayout.Width(128f)))
                CleanUnusedMeshAssets();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSourceSection()
    {
        DrawSectionTitle("Sources");
        DrawSelectionToolbar();

        int sourceCount = GetSourceCount();
        float listHeight = Mathf.Clamp(
            34f + sourceCount * RendererRowHeight,
            MinimumSourceListHeight,
            MaximumSourceListHeight);

        EditorGUILayout.BeginVertical(GUI.skin.box);
        sourceScrollPosition = EditorGUILayout.BeginScrollView(
            sourceScrollPosition,
            GUILayout.Height(listHeight));

        if (sourceCount == 0)
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                "No enabled mesh renderers were found.",
                EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
        }
        else
        {
            DrawRendererGroup(
                "Skinned Mesh Renderers",
                skinnedRenderers,
                selectedSkinnedRenderers);

            if (skinnedRenderers.Count > 0 && staticRenderers.Count > 0)
                EditorGUILayout.Space(5f);

            DrawRendererGroup(
                "Static Mesh Renderers",
                staticRenderers,
                selectedStaticRenderers);
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    private void DrawSelectionToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button(
                "All",
                EditorStyles.toolbarButton,
                GUILayout.Width(40f)))
        {
            SetAllSelections(true);
        }

        if (GUILayout.Button(
                "None",
                EditorStyles.toolbarButton,
                GUILayout.Width(44f)))
        {
            SetAllSelections(false);
        }

        if (GUILayout.Button(
                "Invert",
                EditorStyles.toolbarButton,
                GUILayout.Width(50f)))
        {
            InvertSelections();
        }

        if (GUILayout.Button(
                "Refresh",
                EditorStyles.toolbarButton,
                GUILayout.Width(58f)))
        {
            RefreshRendererLists();
        }

        GUILayout.FlexibleSpace();
        EditorGUILayout.LabelField(
            CountSelectedRenderers() + " / " + GetSourceCount(),
            EditorStyles.miniLabel,
            GUILayout.Width(64f));
        EditorGUILayout.EndHorizontal();

        GetSelectedMeshStatistics(
            out long selectedVertexCount,
            out long selectedTriangleCount,
            out int selectedSubMeshCount,
            out int selectedBlendShapeCount);

        EditorGUILayout.LabelField(
            string.Format(
                "{0:N0} vertices    {1:N0} triangles    {2:N0} submeshes    {3:N0} blend shapes",
                selectedVertexCount,
                selectedTriangleCount,
                selectedSubMeshCount,
                selectedBlendShapeCount),
            EditorStyles.miniLabel);
    }

    private void DrawRendererGroup<TRenderer>(
        string title,
        List<TRenderer> renderers,
        List<bool> selections)
        where TRenderer : Renderer
    {
        if (renderers.Count == 0)
            return;

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
        GUILayout.FlexibleSpace();
        EditorGUILayout.LabelField(
            renderers.Count.ToString(),
            EditorStyles.miniLabel,
            GUILayout.Width(28f));
        EditorGUILayout.EndHorizontal();

        for (int rendererIndex = 0;
             rendererIndex < renderers.Count;
             rendererIndex++)
        {
            TRenderer renderer = renderers[rendererIndex];
            Mesh mesh = GetRendererMesh(renderer);
            selections[rendererIndex] = DrawRendererRow(
                renderer,
                mesh,
                selections[rendererIndex],
                renderer is SkinnedMeshRenderer ? "Skinned" : "Static");
        }
    }

    private static bool DrawRendererRow(
        Renderer renderer,
        Mesh mesh,
        bool selected,
        string rendererKind)
    {
        Rect rowRect = GUILayoutUtility.GetRect(
            0f,
            RendererRowHeight,
            GUILayout.ExpandWidth(true));
        GUI.Box(rowRect, GUIContent.none);

        const float padding = 5f;
        const float toggleWidth = 18f;
        const float kindWidth = 58f;
        const float lineGap = 3f;

        Rect contentRect = new Rect(
            rowRect.x + padding,
            rowRect.y + padding,
            rowRect.width - padding * 2f,
            rowRect.height - padding * 2f);

        Rect toggleRect = new Rect(
            contentRect.x,
            contentRect.y,
            toggleWidth,
            EditorGUIUtility.singleLineHeight);
        Rect kindRect = new Rect(
            contentRect.xMax - kindWidth,
            contentRect.y,
            kindWidth,
            EditorGUIUtility.singleLineHeight);
        Rect rendererRect = new Rect(
            toggleRect.xMax + 2f,
            contentRect.y,
            Mathf.Max(40f, kindRect.x - toggleRect.xMax - 6f),
            EditorGUIUtility.singleLineHeight);

        selected = EditorGUI.Toggle(toggleRect, selected);
        EditorGUI.ObjectField(
            rendererRect,
            renderer,
            typeof(Renderer),
            true);
        GUI.Label(kindRect, rendererKind, EditorStyles.miniLabel);

        float secondLineY = contentRect.y +
                            EditorGUIUtility.singleLineHeight +
                            lineGap;
        bool showStatistics = contentRect.width >= 430f;
        float statisticsWidth = showStatistics ? 210f : 0f;
        Rect statisticsRect = showStatistics
            ? new Rect(
                contentRect.xMax - statisticsWidth,
                secondLineY,
                statisticsWidth,
                EditorGUIUtility.singleLineHeight)
            : Rect.zero;
        float meshRight = showStatistics
            ? statisticsRect.x - 6f
            : contentRect.xMax;
        Rect meshRect = new Rect(
            rendererRect.x,
            secondLineY,
            Mathf.Max(40f, meshRight - rendererRect.x),
            EditorGUIUtility.singleLineHeight);

        EditorGUI.ObjectField(meshRect, mesh, typeof(Mesh), false);

        if (showStatistics && mesh != null)
        {
            GUI.Label(
                statisticsRect,
                BuildMeshStatisticsLabel(mesh),
                EditorStyles.miniLabel);
        }

        return selected;
    }

    private void DrawPrimaryAction()
    {
        int selectedRendererCount = CountSelectedRenderers();
        bool canCombine = rootObject != null &&
                          selectedRendererCount >= 2 &&
                          CombinedMeshAssetUtility.IsValidAssetFolder(
                              saveFolderPath);

        using (new EditorGUI.DisabledScope(!canCombine))
        {
            string buttonLabel = selectedRendererCount > 0
                ? "Combine " + selectedRendererCount + " Selected Renderers"
                : "Combine Selected Renderers";

            if (GUILayout.Button(buttonLabel, GUILayout.Height(32f)))
                CombineSelectedRenderers();
        }
    }

    private void BrowseForSaveFolder()
    {
        string initialAbsoluteFolder = Application.dataPath;
        if (CombinedMeshAssetUtility.IsValidAssetFolder(saveFolderPath))
        {
            string projectRootPath = Application.dataPath.Substring(
                0,
                Application.dataPath.Length - "Assets".Length);
            initialAbsoluteFolder = projectRootPath + saveFolderPath;
        }

        string selectedAbsoluteFolder = EditorUtility.OpenFolderPanel(
            "Choose Combined Mesh Save Folder",
            initialAbsoluteFolder,
            string.Empty);

        if (string.IsNullOrEmpty(selectedAbsoluteFolder))
            return;

        if (!CombinedMeshAssetUtility.TryConvertAbsoluteFolderToAssetPath(
                selectedAbsoluteFolder,
                out string selectedAssetFolder))
        {
            return;
        }

        saveFolderPath = selectedAssetFolder;
        EditorPrefs.SetString(SaveFolderPreferenceKey, saveFolderPath);
    }

    private void CleanUnusedMeshAssets()
    {
        if (!CombinedMeshAssetUtility.EnsureFolderExists(
                saveFolderPath,
                out string folderError))
        {
            Debug.LogError(folderError);
            return;
        }

        CombinedMeshAssetUtility.CleanupResult cleanupResult =
            CombinedMeshAssetUtility.MoveUnusedMeshAssetsToTrash(saveFolderPath);
        Debug.Log(
            "Avatar Tools deleted " +
            cleanupResult.MovedToTrashPaths.Count +
            " unused combined mesh asset(s).");
    }

    private void SyncRootToSelection()
    {
        GameObject selectedObject = Selection.activeGameObject;
        if (selectedObject == null)
            return;

        bool selectionIsInsideCurrentRoot = rootObject != null &&
                                            (selectedObject == rootObject ||
                                             selectedObject.transform.IsChildOf(
                                                 rootObject.transform));
        if (!selectionIsInsideCurrentRoot)
            rootObject = selectedObject;
    }

    private void RefreshRendererLists()
    {
        Dictionary<int, bool> previousSelections =
            CaptureCurrentSelections();

        skinnedRenderers.Clear();
        staticRenderers.Clear();
        selectedSkinnedRenderers.Clear();
        selectedStaticRenderers.Clear();

        if (rootObject == null)
            return;

        SkinnedMeshRenderer[] foundSkinnedRenderers =
            rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int rendererIndex = 0;
             rendererIndex < foundSkinnedRenderers.Length;
             rendererIndex++)
        {
            SkinnedMeshRenderer renderer = foundSkinnedRenderers[rendererIndex];
            if (renderer == null ||
                renderer.sharedMesh == null ||
                !renderer.enabled)
            {
                continue;
            }

            skinnedRenderers.Add(renderer);
        }

        MeshRenderer[] foundStaticRenderers =
            rootObject.GetComponentsInChildren<MeshRenderer>(true);
        for (int rendererIndex = 0;
             rendererIndex < foundStaticRenderers.Length;
             rendererIndex++)
        {
            MeshRenderer renderer = foundStaticRenderers[rendererIndex];
            MeshFilter meshFilter = renderer != null
                ? renderer.GetComponent<MeshFilter>()
                : null;

            if (renderer == null ||
                !renderer.enabled ||
                meshFilter == null ||
                meshFilter.sharedMesh == null)
            {
                continue;
            }

            staticRenderers.Add(renderer);
        }

        skinnedRenderers.Sort(CompareRenderersByHierarchy);
        staticRenderers.Sort(CompareRenderersByHierarchy);

        RestoreSelections(
            skinnedRenderers,
            selectedSkinnedRenderers,
            previousSelections);
        RestoreSelections(
            staticRenderers,
            selectedStaticRenderers,
            previousSelections);
    }

    private Dictionary<int, bool> CaptureCurrentSelections()
    {
        Dictionary<int, bool> selections = new Dictionary<int, bool>();

        for (int index = 0; index < skinnedRenderers.Count; index++)
        {
            SkinnedMeshRenderer renderer = skinnedRenderers[index];
            if (renderer != null && index < selectedSkinnedRenderers.Count)
                selections[renderer.GetInstanceID()] = selectedSkinnedRenderers[index];
        }

        for (int index = 0; index < staticRenderers.Count; index++)
        {
            MeshRenderer renderer = staticRenderers[index];
            if (renderer != null && index < selectedStaticRenderers.Count)
                selections[renderer.GetInstanceID()] = selectedStaticRenderers[index];
        }

        return selections;
    }

    private static void RestoreSelections<TRenderer>(
        List<TRenderer> renderers,
        List<bool> selections,
        Dictionary<int, bool> previousSelections)
        where TRenderer : Renderer
    {
        for (int index = 0; index < renderers.Count; index++)
        {
            TRenderer renderer = renderers[index];
            bool wasSelected;
            selections.Add(
                renderer != null &&
                previousSelections.TryGetValue(
                    renderer.GetInstanceID(),
                    out wasSelected)
                    ? wasSelected
                    : true);
        }
    }

    private static int CompareRenderersByHierarchy(
        Renderer left,
        Renderer right)
    {
        string leftPath = GetHierarchyPath(left != null ? left.transform : null);
        string rightPath = GetHierarchyPath(right != null ? right.transform : null);
        return string.Compare(
            leftPath,
            rightPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetHierarchyPath(Transform transform)
    {
        if (transform == null)
            return string.Empty;

        string path = transform.name;
        Transform current = transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }

    private void SetAllSelections(bool selected)
    {
        for (int index = 0; index < selectedSkinnedRenderers.Count; index++)
            selectedSkinnedRenderers[index] = selected;
        for (int index = 0; index < selectedStaticRenderers.Count; index++)
            selectedStaticRenderers[index] = selected;
    }

    private void InvertSelections()
    {
        for (int index = 0; index < selectedSkinnedRenderers.Count; index++)
            selectedSkinnedRenderers[index] = !selectedSkinnedRenderers[index];
        for (int index = 0; index < selectedStaticRenderers.Count; index++)
            selectedStaticRenderers[index] = !selectedStaticRenderers[index];
    }

    private int GetSourceCount()
    {
        return skinnedRenderers.Count + staticRenderers.Count;
    }

    private int CountSelectedRenderers()
    {
        int selectedCount = 0;
        for (int index = 0; index < selectedSkinnedRenderers.Count; index++)
        {
            if (selectedSkinnedRenderers[index])
                selectedCount++;
        }

        for (int index = 0; index < selectedStaticRenderers.Count; index++)
        {
            if (selectedStaticRenderers[index])
                selectedCount++;
        }

        return selectedCount;
    }

    private void GetSelectedMeshStatistics(
        out long vertexCount,
        out long triangleCount,
        out int subMeshCount,
        out int blendShapeCount)
    {
        vertexCount = 0L;
        triangleCount = 0L;
        subMeshCount = 0;
        blendShapeCount = 0;

        for (int index = 0; index < skinnedRenderers.Count; index++)
        {
            if (!selectedSkinnedRenderers[index])
                continue;

            AddMeshStatistics(
                skinnedRenderers[index] != null
                    ? skinnedRenderers[index].sharedMesh
                    : null,
                ref vertexCount,
                ref triangleCount,
                ref subMeshCount,
                ref blendShapeCount);
        }

        for (int index = 0; index < staticRenderers.Count; index++)
        {
            if (!selectedStaticRenderers[index])
                continue;

            AddMeshStatistics(
                GetRendererMesh(staticRenderers[index]),
                ref vertexCount,
                ref triangleCount,
                ref subMeshCount,
                ref blendShapeCount);
        }
    }

    private static void AddMeshStatistics(
        Mesh mesh,
        ref long vertexCount,
        ref long triangleCount,
        ref int subMeshCount,
        ref int blendShapeCount)
    {
        if (mesh == null)
            return;

        vertexCount += mesh.vertexCount;
        triangleCount += GetTriangleCount(mesh);
        subMeshCount += mesh.subMeshCount;
        blendShapeCount += mesh.blendShapeCount;
    }

    private static string BuildMeshStatisticsLabel(Mesh mesh)
    {
        return string.Format(
            "{0:N0} v   {1:N0} t   {2} sub   {3} shapes",
            mesh.vertexCount,
            GetTriangleCount(mesh),
            mesh.subMeshCount,
            mesh.blendShapeCount);
    }

    private static long GetTriangleCount(Mesh mesh)
    {
        if (mesh == null)
            return 0L;

        long triangleCount = 0L;
        for (int subMeshIndex = 0;
             subMeshIndex < mesh.subMeshCount;
             subMeshIndex++)
        {
            if (mesh.GetTopology(subMeshIndex) == MeshTopology.Triangles)
            {
                triangleCount +=
                    (long)mesh.GetIndexCount(subMeshIndex) / 3L;
            }
        }

        return triangleCount;
    }

    private static Mesh GetRendererMesh(Renderer renderer)
    {
        SkinnedMeshRenderer skinnedRenderer =
            renderer as SkinnedMeshRenderer;
        if (skinnedRenderer != null)
            return skinnedRenderer.sharedMesh;

        MeshRenderer meshRenderer = renderer as MeshRenderer;
        if (meshRenderer == null)
            return null;

        MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
        return meshFilter != null ? meshFilter.sharedMesh : null;
    }

    private HashSet<string> CollectSelectedMeshAssetPathsForUndo()
    {
        HashSet<string> meshAssetPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int rendererIndex = 0;
             rendererIndex < skinnedRenderers.Count;
             rendererIndex++)
        {
            if (!selectedSkinnedRenderers[rendererIndex])
                continue;

            SkinnedMeshRenderer renderer = skinnedRenderers[rendererIndex];
            AddMeshAssetPath(
                renderer != null ? renderer.sharedMesh : null,
                meshAssetPaths);
        }

        for (int rendererIndex = 0;
             rendererIndex < staticRenderers.Count;
             rendererIndex++)
        {
            if (!selectedStaticRenderers[rendererIndex])
                continue;

            AddMeshAssetPath(
                GetRendererMesh(staticRenderers[rendererIndex]),
                meshAssetPaths);
        }

        return meshAssetPaths;
    }

    private static void AddMeshAssetPath(
        Mesh mesh,
        HashSet<string> meshAssetPaths)
    {
        if (mesh == null)
            return;

        string meshAssetPath = CombinedMeshAssetUtility.NormalizeAssetPath(
            AssetDatabase.GetAssetPath(mesh));
        if (!string.IsNullOrEmpty(meshAssetPath))
            meshAssetPaths.Add(meshAssetPath);
    }

    private void CombineSelectedRenderers()
    {
        if (!CombinedMeshAssetUtility.EnsureFolderExists(
                saveFolderPath,
                out string folderError))
        {
            Debug.LogError(folderError);
            return;
        }

        List<GameObject> selectedObjects = new List<GameObject>();

        for (int rendererIndex = 0;
             rendererIndex < skinnedRenderers.Count;
             rendererIndex++)
        {
            if (selectedSkinnedRenderers[rendererIndex] &&
                skinnedRenderers[rendererIndex] != null)
            {
                selectedObjects.Add(
                    skinnedRenderers[rendererIndex].gameObject);
            }
        }

        for (int rendererIndex = 0;
             rendererIndex < staticRenderers.Count;
             rendererIndex++)
        {
            if (selectedStaticRenderers[rendererIndex] &&
                staticRenderers[rendererIndex] != null)
            {
                selectedObjects.Add(staticRenderers[rendererIndex].gameObject);
            }
        }

        HashSet<string> protectedMeshAssetPaths =
            CollectSelectedMeshAssetPathsForUndo();

        isCombining = true;
        try
        {
            SkinnedMeshRenderer combinedRenderer = SkinnedMeshMerger.Combine(
                rootObject,
                selectedObjects,
                blendShapeNameMode,
                sourceHandlingMode);

            if (combinedRenderer == null || combinedRenderer.sharedMesh == null)
                return;

            string savedMeshPath = CombinedMeshAssetUtility.SaveCombinedMesh(
                combinedRenderer.sharedMesh,
                saveFolderPath,
                combinedRenderer.sharedMesh.name);

            EditorUtility.SetDirty(combinedRenderer);
            AssetDatabase.SaveAssets();

            if (automaticallyDeleteUnusedSavedMeshes)
            {
                protectedMeshAssetPaths.Add(savedMeshPath);
                CombinedMeshAssetUtility.MoveUnusedMeshAssetsToTrash(
                    saveFolderPath,
                    protectedMeshAssetPaths);
            }

            Selection.activeGameObject = combinedRenderer.gameObject;
            EditorGUIUtility.PingObject(combinedRenderer.sharedMesh);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, rootObject);
            EditorUtility.DisplayDialog(
                "Skinned Mesh Combine Failed",
                exception.Message,
                "OK");
        }
        finally
        {
            isCombining = false;
            RefreshRendererLists();
        }
    }

    private static void DrawSectionTitle(string title)
    {
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
    }
}
