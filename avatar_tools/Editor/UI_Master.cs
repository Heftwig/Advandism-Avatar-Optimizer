using UnityEditor;
using UnityEngine;

public sealed class AvatarToolsWindow : EditorWindow
{
    private const string SelectedTabPreferenceKey = "AvatarTools.SelectedTab";

    private static readonly string[] TabLabels =
    {
        "Mesh Simplifier",
        "Mesh Combiner",
        "Material Compiler"
    };

    private int selectedTab;

    private MeshSimplifierTool meshSimplifier;
    private MeshCombinerTool meshCombiner;
    private MaterialMergerTool materialMerger;

    [MenuItem("Tools/Avatar Tools")]
    public static void Open()
    {
        AvatarToolsWindow window =
            GetWindow<AvatarToolsWindow>("Avatar Tools");

        window.minSize = new Vector2(560f, 420f);
        window.Show();
    }

    private void OnEnable()
    {
        EnsureToolsCreated();

        selectedTab = Mathf.Clamp(
            EditorPrefs.GetInt(SelectedTabPreferenceKey, 0),
            0,
            TabLabels.Length - 1);

        meshSimplifier.SetRepaintHandler(Repaint);

        meshSimplifier.OnEnable();
        meshCombiner.OnEnable();
        materialMerger.OnEnable();

        Undo.undoRedoPerformed -= HandleUndoRedo;
        Undo.undoRedoPerformed += HandleUndoRedo;
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= HandleUndoRedo;

        meshSimplifier?.OnDisable();
        meshCombiner?.OnDisable();
        materialMerger?.OnDisable();
    }

    private void OnSelectionChange()
    {
        EnsureToolsCreated();

        meshSimplifier.OnSelectionChange();
        meshCombiner.OnSelectionChange();
        materialMerger.OnSelectionChange();

        Repaint();
    }

    private void OnHierarchyChange()
    {
        EnsureToolsCreated();

        meshSimplifier.OnHierarchyChange();
        meshCombiner.OnHierarchyChange();
        materialMerger.OnHierarchyChange();

        Repaint();
    }

    private void OnProjectChange()
    {
        EnsureToolsCreated();

        meshSimplifier.OnProjectChange();
        meshCombiner.OnProjectChange();
        materialMerger.OnProjectChange();

        Repaint();
    }

    private void OnInspectorUpdate()
    {
        if (meshSimplifier == null)
            return;

        meshSimplifier.OnInspectorUpdate();

        if (meshSimplifier.RequiresContinuousRepaint)
            Repaint();
    }

    private void HandleUndoRedo()
    {
        EnsureToolsCreated();

        meshSimplifier.OnUndoRedoPerformed();
        meshCombiner.OnUndoRedoPerformed();
        materialMerger.OnUndoRedoPerformed();

        Repaint();
    }

    private void OnGUI()
    {
        EnsureToolsCreated();

        int newSelectedTab = GUILayout.Toolbar(
            selectedTab,
            TabLabels,
            GUILayout.Height(24f));

        if (newSelectedTab != selectedTab)
        {
            selectedTab = newSelectedTab;
            EditorPrefs.SetInt(
                SelectedTabPreferenceKey,
                selectedTab);

            GUI.FocusControl(null);
        }

        EditorGUILayout.Space(8f);

        switch (selectedTab)
        {
            case 0:
                meshSimplifier.Draw();
                break;

            case 1:
                meshCombiner.Draw();
                break;

            case 2:
                materialMerger.Draw();
                break;
        }
    }

    private void EnsureToolsCreated()
    {
        if (meshSimplifier == null)
        {
            meshSimplifier = new MeshSimplifierTool();
            meshSimplifier.SetRepaintHandler(Repaint);
        }

        meshCombiner ??= new MeshCombinerTool();
        materialMerger ??= new MaterialMergerTool();
    }
}