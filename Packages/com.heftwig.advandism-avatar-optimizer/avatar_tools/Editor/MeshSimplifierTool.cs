#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

    /// <summary>
    /// Small avatar-oriented editor front-end for selecting and simplifying meshes below a root GameObject.
    /// Source meshes are never modified. Generated meshes are saved as independent assets before assignment.
    /// </summary>
    public sealed class MeshSimplifierTool
    {
        private const string DefaultOutputFolder = "Assets/Generated/SimplifiedMeshes";
        private const string OutputFolderPreferenceKey = "AvatarTools.MeshSimplifier.OutputFolder";
        private const string CleanupPreferenceKey = "AvatarTools.MeshSimplifier.DeleteUnusedMeshes";
        private const string AssignPreferenceKey = "AvatarTools.MeshSimplifier.AssignGeneratedMeshes";
        private const string ColliderPreferenceKey = "AvatarTools.MeshSimplifier.UpdateMatchingColliders";
        private const float MinRemainingPercentage = 0.01f;
        private const int CombinedTargetMinimumTrianglesPerMesh = 32;
        private const double CombinedTargetMinimumFraction = 0.01;

        private enum TargetInputMode
        {
            TriangleCount,
            Percentage
        }

        private enum GeometryProtectionLevel
        {
            Stage1,
            Stage2,
            Stage3,
            Stage4
        }

        private enum JobState
        {
            Waiting,
            Snapshotting,
            Simplifying,
            Finalizing,
            Completed,
            Cancelled,
            Failed
        }

        private struct JobProgressSnapshot
        {
            public JobState State;
            public int SourceTriangles;
            public int TargetTriangles;
            public int CurrentTriangles;
            public int PassIndex;
            public int PassCount;
            public int IterationIndex;
            public int IterationCount;
            public int ReferenceRejections;
            public int InvalidAttributeRejections;
            public global::UnityMeshSimplifier.SimplificationProgressStage ProgressStage;
            public string Message;
        }

        private sealed class JobProgressState
        {
            private readonly object sync = new object();
            private JobProgressSnapshot snapshot;
            private int redistributionPassIndex;
            private int redistributionPassCount;

            public void Reset(int sourceTriangles, int targetTriangles)
            {
                lock (sync)
                {
                    redistributionPassIndex = 0;
                    redistributionPassCount = 0;
                    snapshot = new JobProgressSnapshot
                    {
                        State = JobState.Waiting,
                        SourceTriangles = sourceTriangles,
                        TargetTriangles = targetTriangles,
                        CurrentTriangles = sourceTriangles,
                        PassIndex = 0,
                        PassCount = 0,
                        IterationIndex = 0,
                        IterationCount = 0,
                        Message = "Waiting for a worker..."
                    };
                }
            }

            public void BeginRedistributionPass(int passIndex, int passCount)
            {
                lock (sync)
                {
                    redistributionPassIndex = Math.Max(1, passIndex);
                    redistributionPassCount = Math.Max(redistributionPassIndex, passCount);
                    snapshot.State = JobState.Simplifying;
                    snapshot.ProgressStage = global::UnityMeshSimplifier.SimplificationProgressStage.StartingPass;
                    snapshot.PassIndex = redistributionPassIndex;
                    snapshot.PassCount = redistributionPassCount;
                    snapshot.IterationIndex = 0;
                    snapshot.IterationCount = 0;
                    snapshot.Message = string.Format(
                        "Starting redistribution pass {0}/{1}...",
                        redistributionPassIndex,
                        redistributionPassCount);
                }
            }

            public void CompleteRedistributionPass(int passIndex, int passCount)
            {
                lock (sync)
                {
                    redistributionPassIndex = Math.Max(1, passIndex);
                    redistributionPassCount = Math.Max(redistributionPassIndex, passCount);
                    snapshot.State = JobState.Simplifying;
                    snapshot.ProgressStage = global::UnityMeshSimplifier.SimplificationProgressStage.CompletedPass;
                    snapshot.PassIndex = redistributionPassIndex;
                    snapshot.PassCount = redistributionPassCount;
                    snapshot.IterationIndex = Math.Max(snapshot.IterationIndex, snapshot.IterationCount);
                    snapshot.Message = string.Format(
                        "Redistribution pass {0}/{1} complete; waiting for the other meshes...",
                        redistributionPassIndex,
                        redistributionPassCount);
                }
            }

            public void SetState(JobState state, string message)
            {
                lock (sync)
                {
                    snapshot.State = state;
                    snapshot.Message = message;
                    if (state == JobState.Completed)
                        snapshot.CurrentTriangles = Math.Max(0, snapshot.CurrentTriangles);
                }
            }

            public void SetCurrentTriangles(int triangleCount)
            {
                lock (sync)
                    snapshot.CurrentTriangles = Math.Max(0, triangleCount);
            }

            public void SetTargetTriangles(int triangleCount, string message)
            {
                lock (sync)
                {
                    snapshot.TargetTriangles = Math.Max(0, triangleCount);
                    if (!string.IsNullOrEmpty(message))
                        snapshot.Message = message;
                }
            }

            public void Apply(global::UnityMeshSimplifier.SimplificationProgress progress)
            {
                lock (sync)
                {
                    int displayPassIndex = redistributionPassIndex > 0
                        ? redistributionPassIndex
                        : progress.PassIndex;
                    int displayPassCount = redistributionPassCount > 0
                        ? redistributionPassCount
                        : progress.PassCount;

                    snapshot.State = JobState.Simplifying;
                    snapshot.ProgressStage =
                        progress.Stage == global::UnityMeshSimplifier.SimplificationProgressStage.Completed
                            ? global::UnityMeshSimplifier.SimplificationProgressStage.CompletedPass
                            : progress.Stage;
                    snapshot.PassIndex = displayPassIndex;
                    snapshot.PassCount = displayPassCount;
                    snapshot.IterationIndex = progress.IterationIndex;
                    snapshot.IterationCount = progress.IterationCount;
                    snapshot.CurrentTriangles = Math.Max(0, progress.CurrentTriangleCount);
                    snapshot.TargetTriangles = Math.Max(0, progress.TargetTriangleCount);
                    snapshot.ReferenceRejections = Math.Max(0, progress.ReferenceRejectedCollapses);
                    snapshot.InvalidAttributeRejections = Math.Max(0, progress.InvalidAttributePlacementRejections);
                    snapshot.Message = BuildProgressMessage(
                        progress,
                        displayPassIndex,
                        displayPassCount);
                }
            }

            public JobProgressSnapshot Read()
            {
                lock (sync)
                    return snapshot;
            }

            private static string BuildProgressMessage(
                global::UnityMeshSimplifier.SimplificationProgress progress,
                int displayPassIndex,
                int displayPassCount)
            {
                switch (progress.Stage)
                {
                    case global::UnityMeshSimplifier.SimplificationProgressStage.StartingPass:
                        return string.Format(
                            "Starting redistribution pass {0}/{1}...",
                            displayPassIndex,
                            displayPassCount);
                    case global::UnityMeshSimplifier.SimplificationProgressStage.CompletedPass:
                    case global::UnityMeshSimplifier.SimplificationProgressStage.Completed:
                        return string.Format(
                            "Redistribution pass {0}/{1} complete.",
                            displayPassIndex,
                            displayPassCount);
                    default:
                        return string.Format(
                            "Redistribution pass {0}/{1}, iteration {2}/{3}",
                            displayPassIndex,
                            displayPassCount,
                            progress.IterationIndex,
                            progress.IterationCount);
                }
            }
        }

        [Serializable]
        private sealed class MeshEntry
        {
            public Component Owner;
            public Mesh SourceMesh;
            public bool Selected;
            public string HierarchyPath;
            public string RendererKind;
            public string Warning;
            public int VertexCount;
            public long TriangleCount;
            public int MissingBoneSlotCount;
            public int BindposeCount;

        }


        private sealed class SimplificationJob
        {
            public Mesh SourceMesh;
            public readonly List<MeshEntry> Entries = new List<MeshEntry>();
            public bool[] ValidBoneSlots;
            public int FallbackBoneIndex = -1;
            public int TargetTriangleCount;
            public int MinimumTriangleCount;
            public string VariantLabel;
            public bool TimeLimitReached;
            public readonly JobProgressState Progress = new JobProgressState();

            public bool FiltersMissingBones
            {
                get { return ValidBoneSlots != null; }
            }
        }

        private GameObject rootObject;
        private bool includeInactive = true;
        private bool includeMeshFilters = true;
        private bool assignGeneratedMeshes = true;
        private bool updateMatchingMeshColliders = true;
        private bool cleanUnusedGeneratedMeshes = true;
        private GeometryProtectionLevel geometryProtection = GeometryProtectionLevel.Stage4;
        private TargetInputMode targetInputMode = TargetInputMode.TriangleCount;
        private int targetTriangleCount = 70000;
        private float remainingPercentage = 70f;
        private int targetPassCount = 3;
        private float maximumSimplificationTimeSeconds = 0f;
        private string outputFolder = DefaultOutputFolder;
        private Vector2 scrollPosition;
        private readonly List<MeshEntry> meshEntries = new List<MeshEntry>();

        private CancellationTokenSource cancellationSource;
        private bool isProcessing;
        private List<SimplificationJob> activeJobs;
        private readonly Dictionary<MeshEntry, SimplificationJob> activeJobByEntry = new Dictionary<MeshEntry, SimplificationJob>();
        private bool automaticRefreshPending;
        private bool selectionSyncPending;
        private string statusMessage = "Select a root GameObject.";
        private Action repaintHandler;
        private static readonly HashSet<string> UndoProtectedMeshPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void OnEnable()
        {
            outputFolder = EditorPrefs.GetString(
                OutputFolderPreferenceKey,
                DefaultOutputFolder);
            cleanUnusedGeneratedMeshes = EditorPrefs.GetBool(
                CleanupPreferenceKey,
                true);
            assignGeneratedMeshes = EditorPrefs.GetBool(
                AssignPreferenceKey,
                true);
            updateMatchingMeshColliders = EditorPrefs.GetBool(
                ColliderPreferenceKey,
                true);

            SyncRootToCurrentSelection();
            ScanHierarchy(true);
        }

        public void OnDisable()
        {
            if (cancellationSource != null)
                cancellationSource.Cancel();

            repaintHandler = null;
        }

        public bool RequiresContinuousRepaint
        {
            get { return isProcessing || automaticRefreshPending; }
        }

        public void SetRepaintHandler(Action handler)
        {
            repaintHandler = handler;
        }

        private void RequestRepaint()
        {
            if (repaintHandler != null)
                repaintHandler();
        }

        public void OnUndoRedoPerformed()
        {
            QueueAutomaticRefresh(false);
        }

        public void OnInspectorUpdate()
        {
            if (!isProcessing && automaticRefreshPending)
                PerformAutomaticRefresh();

            if (isProcessing || automaticRefreshPending)
                RequestRepaint();
        }

        public void OnSelectionChange()
        {
            if (isProcessing)
                return;

            QueueAutomaticRefresh(true);
        }

        public void OnHierarchyChange()
        {
            QueueAutomaticRefresh(false);
        }

        public void OnProjectChange()
        {
            QueueAutomaticRefresh(false);
        }

        private void QueueAutomaticRefresh(bool synchronizeSelection)
        {
            automaticRefreshPending = true;
            selectionSyncPending |= synchronizeSelection;
            RequestRepaint();
        }

        private void PerformAutomaticRefresh()
        {
            if (isProcessing)
                return;

            bool synchronizeSelection = selectionSyncPending;
            automaticRefreshPending = false;
            selectionSyncPending = false;
            if (synchronizeSelection)
                SyncRootToCurrentSelection();
            ScanHierarchy(true);
        }

        private void SyncRootToCurrentSelection()
        {
            GameObject selectedObject = Selection.activeGameObject;
            if (selectedObject == null)
                return;

            if (rootObject != null &&
                (selectedObject == rootObject || selectedObject.transform.IsChildOf(rootObject.transform)))
            {
                return;
            }

            rootObject = selectedObject;
        }

        public void Draw()
        {
            if (!isProcessing && automaticRefreshPending)
                PerformAutomaticRefresh();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            try
            {
                DrawSourceSection();
                EditorGUILayout.Space(6f);
                DrawSettingsSection();
                Dictionary<Mesh, long> previewTargets = BuildCurrentPreviewTargets();
                EditorGUILayout.Space(8f);
                DrawMeshList(previewTargets);
                EditorGUILayout.Space(8f);
                DrawActionSection(previewTargets);
                EditorGUILayout.Space(8f);
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawSourceSection()
        {
            DrawSectionTitle("Target");

            using (new EditorGUI.DisabledScope(isProcessing))
            {
                EditorGUI.BeginChangeCheck();
                GameObject selectedRoot = (GameObject)EditorGUILayout.ObjectField(
                    "Root",
                    rootObject,
                    typeof(GameObject),
                    true);
                if (EditorGUI.EndChangeCheck())
                {
                    rootObject = selectedRoot;
                    ScanHierarchy(true);
                }

                EditorGUI.BeginChangeCheck();
                includeInactive = EditorGUILayout.ToggleLeft(
                    "Include inactive objects",
                    includeInactive);
                includeMeshFilters = EditorGUILayout.ToggleLeft(
                    "Include MeshFilter meshes",
                    includeMeshFilters);
                if (EditorGUI.EndChangeCheck())
                    ScanHierarchy(true);
            }
        }

        private void DrawSettingsSection()
        {
            DrawSectionTitle("Simplification");

            using (new EditorGUI.DisabledScope(isProcessing))
            {
                targetInputMode = (TargetInputMode)EditorGUILayout.EnumPopup(
                    "Target Mode",
                    targetInputMode);

                if (targetInputMode == TargetInputMode.TriangleCount)
                {
                    targetTriangleCount = Mathf.Max(
                        1,
                        EditorGUILayout.DelayedIntField(
                            "Combined Target Triangles",
                            targetTriangleCount));
                }
                else
                {
                    remainingPercentage = EditorGUILayout.Slider(
                        "Remaining Percentage",
                        remainingPercentage,
                        MinRemainingPercentage,
                        100f);
                }

                geometryProtection = (GeometryProtectionLevel)EditorGUILayout.EnumPopup(
                    "Geometry Protection",
                    geometryProtection);
                targetPassCount = Mathf.Clamp(
                    EditorGUILayout.DelayedIntField(
                        "Redistribution Passes",
                        targetPassCount),
                    1,
                    32);
                maximumSimplificationTimeSeconds = Mathf.Clamp(
                    EditorGUILayout.DelayedFloatField(
                        "Maximum Time (Seconds)",
                        maximumSimplificationTimeSeconds),
                    0f,
                    86400f);
            }

            EditorGUILayout.Space(4f);
            DrawSectionTitle("Output");

            using (new EditorGUI.DisabledScope(isProcessing))
            {
                DrawOutputFolderField();

                EditorGUI.BeginChangeCheck();
                assignGeneratedMeshes = EditorGUILayout.ToggleLeft(
                    "Assign generated meshes",
                    assignGeneratedMeshes);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetBool(AssignPreferenceKey, assignGeneratedMeshes);

                using (new EditorGUI.DisabledScope(!assignGeneratedMeshes))
                {
                    EditorGUI.BeginChangeCheck();
                    updateMatchingMeshColliders = EditorGUILayout.ToggleLeft(
                        "Update matching MeshColliders",
                        updateMatchingMeshColliders);
                    if (EditorGUI.EndChangeCheck())
                        EditorPrefs.SetBool(ColliderPreferenceKey, updateMatchingMeshColliders);
                }

                EditorGUI.BeginChangeCheck();
                cleanUnusedGeneratedMeshes = EditorGUILayout.ToggleLeft(
                    "Delete unused output meshes",
                    cleanUnusedGeneratedMeshes);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetBool(CleanupPreferenceKey, cleanUnusedGeneratedMeshes);

                using (new EditorGUI.DisabledScope(!IsValidAssetFolder(outputFolder)))
                {
                    if (GUILayout.Button("Delete Unused Output Meshes"))
                        DeleteUnusedOutputMeshesNow();
                }
            }
        }

        private static global::UnityMeshSimplifier.SimplificationOptions BuildSimplificationOptions(GeometryProtectionLevel level)
        {
            global::UnityMeshSimplifier.SimplificationOptions options = global::UnityMeshSimplifier.SimplificationOptions.Avatar;
            switch (level)
            {
                case GeometryProtectionLevel.Stage1:
                    options.VertexPlacement = global::UnityMeshSimplifier.VertexPlacementMode.SurfaceProjected;
                    options.FeatureAngleDegrees = 50f;
                    options.MaxTriangleNormalDeviationDegrees = 60f;
                    break;
                case GeometryProtectionLevel.Stage3:
                    options.PreserveBorderEdges = true;
                    options.PreserveSurfaceEnvelope = true;
                    options.PreserveBilateralSymmetry = true;
                    options.VertexPlacement = global::UnityMeshSimplifier.VertexPlacementMode.AvatarHybrid;
                    options.FeatureAngleDegrees = 20f;
                    options.MaxTriangleNormalDeviationDegrees = 28f;
                    options.MaxIterationCount = 240;
                    options.Agressiveness = 6.0;
                    break;
                case GeometryProtectionLevel.Stage4:
                    options.PreserveBorderEdges = true;
                    options.PreserveSurfaceEnvelope = true;
                    options.PreserveBilateralSymmetry = true;
                    options.VertexPlacement = global::UnityMeshSimplifier.VertexPlacementMode.ReferenceAccurate;
                    options.FeatureAngleDegrees = 20f;
                    options.MaxTriangleNormalDeviationDegrees = 24f;
                    options.MaxSurfaceDeviation = 0.0;
                    options.MaxBoundaryDeviation = 0.0;
                    options.MaxIterationCount = 300;
                    options.Agressiveness = 6.0;
                    break;
                default:
                    options.VertexPlacement = global::UnityMeshSimplifier.VertexPlacementMode.AvatarHybrid;
                    options.FeatureAngleDegrees = 35f;
                    options.MaxTriangleNormalDeviationDegrees = 45f;
                    break;
            }

            return options;
        }

        private void DrawOutputFolderField()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            string editedFolder = EditorGUILayout.TextField(
                "Save Folder",
                outputFolder);
            if (EditorGUI.EndChangeCheck())
            {
                outputFolder = NormalizeAssetPath(editedFolder);
                EditorPrefs.SetString(OutputFolderPreferenceKey, outputFolder);
            }

            if (GUILayout.Button("Browse", GUILayout.Width(68f)))
            {
                string initialFolder = Application.dataPath;
                if (IsValidAssetFolder(outputFolder))
                {
                    string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                    initialFolder = Path.Combine(projectRoot, outputFolder);
                }

                string absoluteFolder = EditorUtility.OpenFolderPanel(
                    "Choose Simplified Mesh Output Folder",
                    initialFolder,
                    string.Empty);
                if (!string.IsNullOrEmpty(absoluteFolder))
                {
                    string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
                    string normalizedAbsolute = absoluteFolder.Replace('\\', '/').TrimEnd('/');
                    if (normalizedAbsolute.Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        outputFolder = "Assets";
                    }
                    else if (normalizedAbsolute.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        outputFolder = normalizedAbsolute.Substring(projectRoot.Length + 1);
                    }

                    outputFolder = NormalizeAssetPath(outputFolder);
                    EditorPrefs.SetString(OutputFolderPreferenceKey, outputFolder);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawSectionTitle(string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return DefaultOutputFolder;

            return path.Trim().Replace('\\', '/').TrimEnd('/');
        }

        private static bool IsValidAssetFolder(string path)
        {
            string normalized = NormalizeAssetPath(path);
            return normalized.Equals("Assets", StringComparison.Ordinal) ||
                   normalized.StartsWith("Assets/", StringComparison.Ordinal);
        }

        private void DeleteUnusedOutputMeshesNow()
        {
            try
            {
                string normalizedFolder = NormalizeAndCreateAssetFolder(outputFolder);
                int deletedCount = CleanupUnusedGeneratedMeshAssets(
                    normalizedFolder,
                    UndoProtectedMeshPaths);
                AssetDatabase.Refresh();
                statusMessage = string.Format(
                    "Deleted {0} unused output mesh asset(s).",
                    deletedCount);
                RequestRepaint();
            }
            catch (Exception exception)
            {
                statusMessage = exception.Message;
                Debug.LogException(exception);
            }
        }

        private void DrawMeshList(Dictionary<Mesh, long> previewTargets)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Meshes", EditorStyles.miniBoldLabel);
            GUILayout.FlexibleSpace();

            int selectedCount = 0;
            for (int i = 0; i < meshEntries.Count; i++)
            {
                if (meshEntries[i].Selected)
                    selectedCount++;
            }

            GUILayout.Label(
                selectedCount + " / " + meshEntries.Count,
                EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(isProcessing || meshEntries.Count == 0))
            {
                if (GUILayout.Button("All", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                    SetAllSelections(true);
                if (GUILayout.Button("None", EditorStyles.toolbarButton, GUILayout.Width(46f)))
                    SetAllSelections(false);
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(58f)))
                    ScanHierarchy(false);
            }
            EditorGUILayout.EndHorizontal();

            if (meshEntries.Count == 0)
            {
                EditorGUILayout.Space(12f);
                EditorGUILayout.LabelField(
                    "No supported meshes found.",
                    EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.Space(12f);
                return;
            }

            for (int i = 0; i < meshEntries.Count; i++)
                DrawMeshEntry(meshEntries[i], previewTargets);
        }

        private void DrawMeshEntry(MeshEntry entry, Dictionary<Mesh, long> previewTargets)
        {
            bool missing = entry.Owner == null || entry.SourceMesh == null;
            bool blocked = !string.IsNullOrEmpty(entry.Warning);
            if (blocked)
                entry.Selected = false;

            Rect rowRect = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(isProcessing || missing || blocked))
                entry.Selected = EditorGUILayout.Toggle(entry.Selected, GUILayout.Width(18f));

            EditorGUILayout.ObjectField(
                entry.SourceMesh,
                typeof(Mesh),
                false);
            GUILayout.Label(
                entry.RendererKind,
                EditorStyles.miniLabel,
                GUILayout.Width(120f));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                entry.HierarchyPath ?? string.Empty,
                EditorStyles.miniLabel);

            string meshDetails = string.Format(
                "{0:N0} vertices   {1:N0} triangles   {2} submeshes",
                entry.VertexCount,
                entry.TriangleCount,
                entry.SourceMesh != null ? entry.SourceMesh.subMeshCount : 0);

            long target;
            if (!missing && entry.TriangleCount > 0 &&
                previewTargets != null &&
                previewTargets.TryGetValue(entry.SourceMesh, out target))
            {
                meshDetails += string.Format("   → {0:N0}", target);
            }

            EditorGUILayout.LabelField(meshDetails, EditorStyles.miniLabel);

            if (entry.MissingBoneSlotCount > 0)
            {
                EditorGUILayout.LabelField(
                    string.Format(
                        "Missing bone slots: {0} / {1}",
                        entry.MissingBoneSlotCount,
                        entry.BindposeCount),
                    EditorStyles.miniLabel);
            }

            if (blocked)
                EditorGUILayout.LabelField(entry.Warning, EditorStyles.miniLabel);

            SimplificationJob activeJob;
            if (isProcessing && activeJobByEntry.TryGetValue(entry, out activeJob))
                DrawJobProgress(activeJob);

            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.ContextClick && rowRect.Contains(Event.current.mousePosition))
                Event.current.Use();
        }

        private static float CalculateJobProgressFraction(JobProgressSnapshot progress)
        {
            if (progress.State == JobState.Finalizing ||
                progress.State == JobState.Completed)
            {
                return 1f;
            }

            if (progress.State == JobState.Waiting ||
                progress.State == JobState.Snapshotting ||
                progress.State == JobState.Cancelled ||
                progress.State == JobState.Failed)
            {
                return 0f;
            }

            int passCount = Math.Max(0, progress.PassCount);
            if (passCount <= 0)
                return 0f;

            int passIndex = Mathf.Clamp(progress.PassIndex, 1, passCount);
            float completedPasses = passIndex - 1;

            switch (progress.ProgressStage)
            {
                case global::UnityMeshSimplifier.SimplificationProgressStage.Completed:
                    return 1f;

                case global::UnityMeshSimplifier.SimplificationProgressStage.CompletedPass:
                    completedPasses = passIndex;
                    break;

                case global::UnityMeshSimplifier.SimplificationProgressStage.Iterating:
                    int iterationCount = Math.Max(0, progress.IterationCount);
                    float iterationFraction = iterationCount > 0
                        ? Mathf.Clamp01((float)progress.IterationIndex / iterationCount)
                        : 0f;
                    completedPasses += iterationFraction;
                    break;

                case global::UnityMeshSimplifier.SimplificationProgressStage.StartingPass:
                default:
                    break;
            }

            return Mathf.Clamp01(completedPasses / passCount);
        }

        private static void DrawJobProgress(SimplificationJob job)
        {
            JobProgressSnapshot progress = job.Progress.Read();
            int source = Math.Max(0, progress.SourceTriangles);
            int target = Math.Max(0, progress.TargetTriangles);
            int current = Math.Max(0, progress.CurrentTriangles);
            int completedReduction = Math.Max(0, source - current);
            float fraction = CalculateJobProgressFraction(progress);

            string label = string.Format(
                "{0}   {1:N0} / {2:N0} triangles",
                string.IsNullOrEmpty(progress.Message) ? progress.State.ToString() : progress.Message,
                current,
                target);
            Rect progressRect = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.ProgressBar(progressRect, fraction, label);

            float reductionPercentage = source > 0
                ? Mathf.Clamp01((float)completedReduction / source) * 100f
                : 0f;
            EditorGUILayout.LabelField(
                string.Format(
                    "Reduced by {0:N0} triangle(s): {1:N0} -> {2:N0} ({3:0.##}% reduction)",
                    completedReduction,
                    source,
                    current,
                    reductionPercentage),
                EditorStyles.miniLabel);

            if (progress.ReferenceRejections > 0 || progress.InvalidAttributeRejections > 0)
            {
                EditorGUILayout.LabelField(
                    string.Format(
                        "Safety rejections: {0:N0} Stage 4 constraint",
                        progress.ReferenceRejections,
                        progress.InvalidAttributeRejections),
                    EditorStyles.miniLabel);
            }
        }

        private void DrawActionSection(Dictionary<Mesh, long> previewTargets)
        {
            int selectedEntryCount = 0;
            var uniqueMeshes = new HashSet<Mesh>();
            for (int i = 0; i < meshEntries.Count; i++)
            {
                MeshEntry entry = meshEntries[i];
                if (entry.Selected && entry.Owner != null && entry.SourceMesh != null && string.IsNullOrEmpty(entry.Warning))
                {
                    selectedEntryCount++;
                    uniqueMeshes.Add(entry.SourceMesh);
                }
            }

            long selectedSourceTriangles = 0L;
            long selectedTargetTriangles = 0L;
            foreach (Mesh mesh in uniqueMeshes)
            {
                long sourceTriangles = GetTriangleCount(mesh);
                long target;
                selectedSourceTriangles += sourceTriangles;
                selectedTargetTriangles += previewTargets.TryGetValue(mesh, out target)
                    ? target
                    : sourceTriangles;
            }

            if (isProcessing && activeJobs != null && activeJobs.Count > 0)
                DrawOverallProgress(activeJobs);

            EditorGUILayout.LabelField(statusMessage, EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                string.Format(
                    "{0} renderer(s), {1} unique mesh(es)   {2:N0} → {3:N0} triangles",
                    selectedEntryCount,
                    uniqueMeshes.Count,
                    selectedSourceTriangles,
                    selectedTargetTriangles),
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (isProcessing)
            {
                if (GUILayout.Button("Cancel", GUILayout.Height(28f)) && cancellationSource != null)
                {
                    statusMessage = "Cancellation requested.";
                    cancellationSource.Cancel();
                }
            }
            else
            {
                bool canSimplify = selectedEntryCount > 0 &&
                                   rootObject != null &&
                                   IsValidAssetFolder(outputFolder);
                using (new EditorGUI.DisabledScope(!canSimplify))
                {
                    if (GUILayout.Button("Simplify Selected", GUILayout.Height(30f)))
                        SimplifySelectedAsync();
                }
            }
        }

        private static void DrawOverallProgress(List<SimplificationJob> jobs)
        {
            long requestedReduction = 0L;
            long completedReduction = 0L;
            int completedJobs = 0;

            for (int i = 0; i < jobs.Count; i++)
            {
                JobProgressSnapshot progress = jobs[i].Progress.Read();
                int source = Math.Max(0, progress.SourceTriangles);
                int target = Math.Max(0, progress.TargetTriangles);
                int current = Math.Max(0, progress.CurrentTriangles);
                long jobRequestedReduction = Math.Max(0, source - target);
                long jobCompletedReduction = Math.Max(0, source - current);

                requestedReduction += jobRequestedReduction;
                completedReduction += Math.Min(jobRequestedReduction, jobCompletedReduction);
                if (progress.State == JobState.Completed)
                    completedJobs++;
            }

            float fraction;
            if (completedJobs == jobs.Count && jobs.Count > 0)
            {
                fraction = 1f;
            }
            else if (requestedReduction > 0L)
            {
                fraction = Mathf.Clamp01((float)((double)completedReduction / requestedReduction));
            }
            else
            {
                fraction = jobs.Count > 0
                    ? Mathf.Clamp01((float)completedJobs / jobs.Count)
                    : 0f;
            }

            Rect rect = EditorGUILayout.GetControlRect(false, 20f);
            EditorGUI.ProgressBar(
                rect,
                fraction,
                string.Format("Overall: {0}/{1} mesh jobs complete", completedJobs, jobs.Count));
        }

        private void ScanHierarchy(bool selectAllNewEntries)
        {
            var oldSelections = new Dictionary<int, bool>();
            for (int i = 0; i < meshEntries.Count; i++)
            {
                Component owner = meshEntries[i].Owner;
                if (owner != null)
                    oldSelections[owner.GetInstanceID()] = meshEntries[i].Selected;
            }

            meshEntries.Clear();
            if (rootObject == null)
            {
                statusMessage = "Select a GameObject in the Hierarchy. Its supported child meshes are scanned automatically.";
                RequestRepaint();
                return;
            }

            SkinnedMeshRenderer[] skinnedRenderers = rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinnedRenderers[i];
                AddEntry(renderer, renderer.sharedMesh, "SkinnedMeshRenderer", oldSelections, selectAllNewEntries);
            }

            if (includeMeshFilters)
            {
                MeshFilter[] meshFilters = rootObject.GetComponentsInChildren<MeshFilter>(includeInactive);
                for (int i = 0; i < meshFilters.Length; i++)
                {
                    MeshFilter filter = meshFilters[i];
                    AddEntry(filter, filter.sharedMesh, "MeshFilter", oldSelections, selectAllNewEntries);
                }
            }

            meshEntries.Sort(CompareEntries);
            statusMessage = string.Format("Found {0} supported renderer component(s).", meshEntries.Count);
            RequestRepaint();
        }

        private void AddEntry(
            Component owner,
            Mesh sourceMesh,
            string rendererKind,
            Dictionary<int, bool> oldSelections,
            bool selectAllNewEntries)
        {
            bool previousSelection;
            bool hasPreviousSelection = oldSelections.TryGetValue(owner.GetInstanceID(), out previousSelection);
            var entry = new MeshEntry
            {
                Owner = owner,
                SourceMesh = sourceMesh,
                RendererKind = rendererKind,
                HierarchyPath = GetHierarchyPath(rootObject.transform, owner.transform),
                Selected = hasPreviousSelection ? previousSelection : selectAllNewEntries
            };

            if (sourceMesh == null)
            {
                entry.Warning = "This component has no shared mesh.";
            }
            else
            {
                entry.VertexCount = sourceMesh.vertexCount;
                entry.TriangleCount = GetTriangleCount(sourceMesh);
                entry.Warning = GetCompatibilityWarning(owner, sourceMesh);

                SkinnedMeshRenderer skinned = owner as SkinnedMeshRenderer;
                if (skinned != null && MeshHasSkinning(sourceMesh))
                {
                    int fallbackBoneIndex;
                    int missingBoneCount;
                    BuildValidBoneSlots(skinned, sourceMesh, out fallbackBoneIndex, out missingBoneCount);
                    entry.MissingBoneSlotCount = missingBoneCount;
                    Matrix4x4[] bindposes = sourceMesh.bindposes;
                    entry.BindposeCount = bindposes != null ? bindposes.Length : 0;
                }
            }

            meshEntries.Add(entry);
        }

        private static int CompareEntries(MeshEntry left, MeshEntry right)
        {
            int triangleCompare = right.TriangleCount.CompareTo(left.TriangleCount);
            if (triangleCompare != 0)
                return triangleCompare;

            int pathCompare = string.Compare(left.HierarchyPath, right.HierarchyPath, StringComparison.OrdinalIgnoreCase);
            if (pathCompare != 0)
                return pathCompare;
            return string.Compare(left.RendererKind, right.RendererKind, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetHierarchyPath(Transform rootTransform, Transform current)
        {
            if (current == rootTransform)
                return rootTransform.name;

            var names = new List<string>();
            Transform cursor = current;
            while (cursor != null)
            {
                names.Add(cursor.name);
                if (cursor == rootTransform)
                    break;
                cursor = cursor.parent;
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static long GetTriangleCount(Mesh mesh)
        {
            if (mesh == null)
                return 0L;

            long total = 0L;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) == MeshTopology.Triangles)
                    total += (long)mesh.GetIndexCount(subMesh) / 3L;
            }
            return total;
        }

        private Dictionary<Mesh, long> BuildCurrentPreviewTargets()
        {
            var sourceMeshes = new List<Mesh>();
            var seen = new HashSet<Mesh>();
            for (int i = 0; i < meshEntries.Count; i++)
            {
                MeshEntry entry = meshEntries[i];
                if (entry.Selected && entry.Owner != null && entry.SourceMesh != null && string.IsNullOrEmpty(entry.Warning) && seen.Add(entry.SourceMesh))
                    sourceMeshes.Add(entry.SourceMesh);
            }

            var result = new Dictionary<Mesh, long>(sourceMeshes.Count);
            if (sourceMeshes.Count == 0)
                return result;

            try
            {
                int[] targets = BuildTriangleTargets(
                    sourceMeshes,
                    targetInputMode,
                    targetTriangleCount,
                    remainingPercentage);
                for (int i = 0; i < sourceMeshes.Count; i++)
                    result.Add(sourceMeshes[i], targets[i]);
            }
            catch
            {

            }

            return result;
        }

        private static string GetCompatibilityWarning(Component owner, Mesh mesh)
        {
            if (mesh.vertexCount == 0)
                return "The mesh has no vertices.";
            if (mesh.subMeshCount == 0)
                return "The mesh has no submeshes.";
            long triangleCount = GetTriangleCount(mesh);
            if (triangleCount <= 0L)
                return "The mesh has no triangle primitives.";
            if (triangleCount > int.MaxValue)
                return "The mesh exceeds the supported triangle-count range.";
            if (!mesh.HasVertexAttribute(VertexAttribute.Position) || mesh.GetVertexAttributeDimension(VertexAttribute.Position) != 3)
                return "The mesh must have a three-dimensional position stream.";
            if (mesh.HasVertexAttribute(VertexAttribute.Normal) && mesh.GetVertexAttributeDimension(VertexAttribute.Normal) != 3)
                return "The normal stream is not three-dimensional.";
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent) && mesh.GetVertexAttributeDimension(VertexAttribute.Tangent) != 4)
                return "The tangent stream is not four-dimensional.";
            if (mesh.HasVertexAttribute(VertexAttribute.Color) && mesh.GetVertexAttributeDimension(VertexAttribute.Color) != 4)
                return "The color stream is not four-dimensional and cannot be round-tripped by this managed path.";

            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                MeshTopology topology = mesh.GetTopology(subMesh);
                if (topology != MeshTopology.Triangles)
                    return string.Format("Submesh {0} uses {1}; only triangle topology is supported.", subMesh, topology);
            }

            for (int channel = 0; channel < 8; channel++)
            {
                VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (mesh.HasVertexAttribute(attribute) && mesh.GetVertexAttributeDimension(attribute) == 1)
                    return string.Format("UV channel {0} is one-dimensional and cannot be round-tripped safely.", channel);
            }

            SkinnedMeshRenderer skinned = owner as SkinnedMeshRenderer;
            if (skinned != null)
            {
                if (skinned.GetComponent<Cloth>() != null)
                    return "A Cloth component is attached. Cloth coefficients and constraints require a dedicated vertex remap and cannot be simplified safely.";

                bool hasSkinning = MeshHasSkinning(mesh);
                if (hasSkinning)
                {
                    Matrix4x4[] bindposes = mesh.bindposes;
                    int bindposeCount = bindposes != null ? bindposes.Length : 0;
                    if (bindposeCount <= 0)
                        return "The mesh has skinning influences but no bindposes.";

                }
            }

            return null;
        }

        private static bool MeshHasSkinning(Mesh mesh)
        {
            return mesh != null &&
                   (mesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                    mesh.HasVertexAttribute(VertexAttribute.BlendIndices));
        }

        private static bool[] BuildValidBoneSlots(
            SkinnedMeshRenderer renderer,
            Mesh mesh,
            out int fallbackBoneIndex,
            out int missingBoneCount)
        {
            fallbackBoneIndex = -1;
            missingBoneCount = 0;

            Matrix4x4[] bindposes = mesh != null ? mesh.bindposes : null;
            int bindposeCount = bindposes != null ? bindposes.Length : 0;
            var validSlots = new bool[bindposeCount];
            Transform[] bones = renderer != null ? renderer.bones : null;

            for (int boneIndex = 0; boneIndex < bindposeCount; boneIndex++)
            {
                bool valid = bones != null && boneIndex < bones.Length && bones[boneIndex] != null;
                validSlots[boneIndex] = valid;
                if (valid)
                {
                    if (fallbackBoneIndex < 0)
                        fallbackBoneIndex = boneIndex;
                }
                else
                {
                    missingBoneCount++;
                }
            }

            return validSlots;
        }

        private static string BuildBoneMaskKey(bool[] validBoneSlots)
        {
            if (validBoneSlots == null || validBoneSlots.Length == 0)
                return "unskinned";

            char[] key = new char[validBoneSlots.Length];
            for (int i = 0; i < validBoneSlots.Length; i++)
                key[i] = validBoneSlots[i] ? '1' : '0';
            return new string(key);
        }

        private void SetAllSelections(bool selected)
        {
            for (int i = 0; i < meshEntries.Count; i++)
            {
                MeshEntry entry = meshEntries[i];
                entry.Selected = selected && entry.Owner != null && entry.SourceMesh != null && string.IsNullOrEmpty(entry.Warning);
            }
            RequestRepaint();
        }

        private async void SimplifySelectedAsync()
        {
            if (isProcessing)
                return;

            List<MeshEntry> selectedEntries = GetSelectedEntries();
            if (selectedEntries.Count == 0)
            {
                EditorUtility.DisplayDialog("Nothing selected", "Select at least one compatible mesh.", "OK");
                return;
            }

            string staleReason;
            if (!ValidateCurrentSelections(selectedEntries, out staleReason))
            {
                ScanHierarchy(false);
                EditorUtility.DisplayDialog("Hierarchy changed", staleReason + " The list has been refreshed; review the selection and run again.", "OK");
                return;
            }

            TargetInputMode requestedTargetMode = targetInputMode;
            int requestedTriangleCount = targetTriangleCount;
            float requestedRemainingPercentage = remainingPercentage;
            int requestedPassCount = targetPassCount;
            float requestedMaximumTimeSeconds = maximumSimplificationTimeSeconds;
            GeometryProtectionLevel requestedGeometryProtection = geometryProtection;
            int maximumParallelJobs = Mathf.Max(1, SystemInfo.processorCount);
            bool requestedAssignment = assignGeneratedMeshes;
            bool requestedUnusedMeshCleanup = cleanUnusedGeneratedMeshes;

            List<Mesh> uniqueSources = BuildUniqueSourceList(selectedEntries);
            ProtectMeshAssetsForUndo(uniqueSources);
            List<SimplificationJob> jobs;
            string normalizedFolder;
            int[] requestedTargets;
            try
            {
                ValidateTargetSettings(requestedTargetMode, requestedTriangleCount, requestedRemainingPercentage);
                if (requestedPassCount < 1 || requestedPassCount > 32)
                    throw new ArgumentOutOfRangeException(nameof(requestedPassCount), "The maximum target pass count must be between 1 and 32.");
                if (float.IsNaN(requestedMaximumTimeSeconds) || float.IsInfinity(requestedMaximumTimeSeconds) || requestedMaximumTimeSeconds < 0f || requestedMaximumTimeSeconds > 86400f)
                    throw new ArgumentOutOfRangeException(nameof(requestedMaximumTimeSeconds), "Maximum time must be 0 (unlimited) or a finite value between 0 and 86400 seconds.");

                requestedTargets = BuildTriangleTargets(
                    uniqueSources,
                    requestedTargetMode,
                    requestedTriangleCount,
                    requestedRemainingPercentage);

                var targetBySource = new Dictionary<Mesh, int>(uniqueSources.Count);
                for (int i = 0; i < uniqueSources.Count; i++)
                    targetBySource.Add(uniqueSources[i], requestedTargets[i]);

                jobs = BuildSimplificationJobs(selectedEntries, targetBySource);
                normalizedFolder = NormalizeAndCreateAssetFolder(outputFolder);
                global::UnityMeshSimplifier.MeshSimplifier.ValidateOptions(BuildSimplificationOptions(requestedGeometryProtection));
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("Invalid settings", exception.Message, "OK");
                return;
            }

            Mesh[] generatedMeshes = null;
            var createdAssetPaths = new List<string>();
            int undoGroup = -1;

            isProcessing = true;
            cancellationSource = new CancellationTokenSource();
            activeJobs = jobs;
            activeJobByEntry.Clear();
            for (int jobIndex = 0; jobIndex < jobs.Count; jobIndex++)
            {
                SimplificationJob job = jobs[jobIndex];
                job.TimeLimitReached = false;
                job.Progress.Reset((int)Math.Min(int.MaxValue, GetTriangleCount(job.SourceMesh)), job.TargetTriangleCount);
                for (int entryIndex = 0; entryIndex < job.Entries.Count; entryIndex++)
                    activeJobByEntry[job.Entries[entryIndex]] = job;
            }
            statusMessage = string.Format(
                "Preparing {0} simplification job(s) from {1} unique source mesh(es)...",
                jobs.Count,
                uniqueSources.Count);
            RequestRepaint();

            try
            {
                CancellationToken token = cancellationSource.Token;
                long combinedTargetForJobs = 0L;
                for (int jobIndex = 0; jobIndex < jobs.Count; jobIndex++)
                    combinedTargetForJobs += Math.Max(0, jobs[jobIndex].TargetTriangleCount);

                generatedMeshes = await SimplifyWithMaximumConcurrencyAsync(
                    jobs,
                    BuildSimplificationOptions(requestedGeometryProtection),
                    requestedPassCount,
                    maximumParallelJobs,
                    combinedTargetForJobs,
                    requestedMaximumTimeSeconds,
                    token);

                token.ThrowIfCancellationRequested();
                statusMessage = "Saving generated mesh assets...";
                RequestRepaint();

                Dictionary<MeshEntry, Mesh> resultByEntry = SaveGeneratedAssets(
                    jobs,
                    generatedMeshes,
                    normalizedFolder,
                    createdAssetPaths);
                for (int createdPathIndex = 0;
                     createdPathIndex < createdAssetPaths.Count;
                     createdPathIndex++)
                {
                    UndoProtectedMeshPaths.Add(
                        NormalizeAssetPath(createdAssetPaths[createdPathIndex]));
                }

                if (requestedAssignment)
                {
                    undoGroup = Undo.GetCurrentGroup();
                    Undo.IncrementCurrentGroup();
                    undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Assign simplified mesh copies");
                    AssignResults(selectedEntries, resultByEntry);
                    Undo.CollapseUndoOperations(undoGroup);
                }

                AssetDatabase.SaveAssets();

                int cleanedUnusedMeshCount = 0;
                if (requestedUnusedMeshCleanup)
                {
                    statusMessage = "Checking the output folder for unused generated meshes...";
                    RequestRepaint();

                    try
                    {
                        cleanedUnusedMeshCount = CleanupUnusedGeneratedMeshAssets(
                            normalizedFolder,
                            UndoProtectedMeshPaths);
                    }
                    catch (Exception cleanupException)
                    {
                        Debug.LogWarning(
                            "[Avatar Mesh Simplifier] Automatic unused-mesh cleanup was skipped because it failed: " +
                            cleanupException.Message,
                            rootObject);
                    }
                }

                AssetDatabase.Refresh();

                if (generatedMeshes.Length > 0 && generatedMeshes[0] != null)
                    EditorGUIUtility.PingObject(generatedMeshes[0]);

                long requestedTotal = SumTriangleTargets(requestedTargets);
                long generatedTotal = SumTriangleCounts(generatedMeshes);
                int filteredJobCount = 0;
                int timeLimitedJobCount = 0;
                for (int i = 0; i < jobs.Count; i++)
                {
                    if (jobs[i].FiltersMissingBones)
                        filteredJobCount++;
                    if (jobs[i].TimeLimitReached)
                        timeLimitedJobCount++;
                }

                ScanHierarchy(false);
                statusMessage = string.Format(
                    "Completed: {0} mesh asset(s) for {1} renderer(s), using up to {2} redistribution pass(es). Requested {3:N0} triangles across unique sources; generated {4:N0} across output variants.{5}{6}{7}",
                    generatedMeshes.Length,
                    selectedEntries.Count,
                    requestedPassCount,
                    requestedTotal,
                    generatedTotal,
                    timeLimitedJobCount > 0
                        ? string.Format(" The {0:0.###}-second maximum was reached; {1} job(s) kept and saved their current valid progress.", requestedMaximumTimeSeconds, timeLimitedJobCount)
                        : string.Empty,
                    filteredJobCount > 0 ? string.Format(" Missing-bone influences were filtered in {0} job(s).", filteredJobCount) : string.Empty,
                    requestedUnusedMeshCleanup && cleanedUnusedMeshCount > 0
                        ? string.Format(" Removed {0} unused generated mesh asset(s) from the output folder.", cleanedUnusedMeshCount)
                        : string.Empty);
                Debug.Log("[Avatar Mesh Simplifier] " + statusMessage, rootObject);
            }
            catch (OperationCanceledException)
            {
                CleanupFailedOperation(generatedMeshes, createdAssetPaths, undoGroup);
                statusMessage = "Simplification cancelled. No partial assignment was kept.";
            }
            catch (Exception exception)
            {
                CleanupFailedOperation(generatedMeshes, createdAssetPaths, undoGroup);
                statusMessage = "Simplification failed. See the Console for details.";
                Debug.LogException(exception, rootObject);
                EditorUtility.DisplayDialog("Mesh simplification failed", exception.Message, "OK");
            }
            finally
            {
                if (cancellationSource != null)
                {
                    cancellationSource.Dispose();
                    cancellationSource = null;
                }
                isProcessing = false;
                activeJobs = null;
                activeJobByEntry.Clear();
                RequestRepaint();
            }
        }

        private List<MeshEntry> GetSelectedEntries()
        {
            var selected = new List<MeshEntry>();
            for (int i = 0; i < meshEntries.Count; i++)
            {
                MeshEntry entry = meshEntries[i];
                if (entry.Selected && entry.Owner != null && entry.SourceMesh != null && string.IsNullOrEmpty(entry.Warning))
                    selected.Add(entry);
            }
            return selected;
        }

        private static bool ValidateCurrentSelections(List<MeshEntry> selectedEntries, out string reason)
        {
            for (int i = 0; i < selectedEntries.Count; i++)
            {
                MeshEntry entry = selectedEntries[i];
                if (entry.Owner == null)
                {
                    reason = "A selected renderer was removed.";
                    return false;
                }

                Mesh currentMesh = null;
                SkinnedMeshRenderer skinned = entry.Owner as SkinnedMeshRenderer;
                if (skinned != null)
                    currentMesh = skinned.sharedMesh;
                else
                {
                    MeshFilter filter = entry.Owner as MeshFilter;
                    if (filter != null)
                        currentMesh = filter.sharedMesh;
                }

                if (currentMesh != entry.SourceMesh)
                {
                    reason = string.Format("The mesh on '{0}' changed after the last scan.", entry.HierarchyPath);
                    return false;
                }

                string warning = GetCompatibilityWarning(entry.Owner, currentMesh);
                if (!string.IsNullOrEmpty(warning))
                {
                    reason = string.Format("'{0}' is no longer compatible: {1}", entry.HierarchyPath, warning);
                    return false;
                }
            }

            reason = null;
            return true;
        }

        private static void ProtectMeshAssetsForUndo(IEnumerable<Mesh> meshes)
        {
            if (meshes == null)
                return;

            foreach (Mesh mesh in meshes)
            {
                if (mesh == null)
                    continue;

                string assetPath = AssetDatabase.GetAssetPath(mesh);
                if (!string.IsNullOrEmpty(assetPath))
                    UndoProtectedMeshPaths.Add(NormalizeAssetPath(assetPath));
            }
        }

        private static List<Mesh> BuildUniqueSourceList(List<MeshEntry> selectedEntries)
        {
            var result = new List<Mesh>();
            var seen = new HashSet<Mesh>();
            for (int i = 0; i < selectedEntries.Count; i++)
            {
                Mesh source = selectedEntries[i].SourceMesh;
                if (seen.Add(source))
                    result.Add(source);
            }
            return result;
        }

        private static List<SimplificationJob> BuildSimplificationJobs(
            List<MeshEntry> selectedEntries,
            Dictionary<Mesh, int> targetBySource)
        {
            var jobs = new List<SimplificationJob>();
            var jobsByKey = new Dictionary<string, SimplificationJob>();

            for (int entryIndex = 0; entryIndex < selectedEntries.Count; entryIndex++)
            {
                MeshEntry entry = selectedEntries[entryIndex];
                Mesh source = entry.SourceMesh;
                bool[] validBoneSlots = null;
                int fallbackBoneIndex = -1;
                int missingBoneCount = 0;

                SkinnedMeshRenderer renderer = entry.Owner as SkinnedMeshRenderer;
                if (renderer != null && MeshHasSkinning(source))
                {
                    bool[] rendererValidSlots = BuildValidBoneSlots(
                        renderer,
                        source,
                        out fallbackBoneIndex,
                        out missingBoneCount);
                    if (missingBoneCount > 0)
                        validBoneSlots = rendererValidSlots;
                }

                string key = source.GetInstanceID().ToString();
                if (validBoneSlots != null)
                    key += "|bones:" + BuildBoneMaskKey(validBoneSlots);
                else
                    key += "|unfiltered";

                SimplificationJob job;
                if (!jobsByKey.TryGetValue(key, out job))
                {
                    int target;
                    if (!targetBySource.TryGetValue(source, out target))
                        throw new InvalidOperationException("A triangle target could not be matched to a selected source mesh.");

                    int sourceTriangleCount = (int)Math.Min(int.MaxValue, GetTriangleCount(source));
                    job = new SimplificationJob
                    {
                        SourceMesh = source,
                        ValidBoneSlots = validBoneSlots,
                        FallbackBoneIndex = fallbackBoneIndex,
                        TargetTriangleCount = target,
                        MinimumTriangleCount = GetCombinedTriangleFloor(source, sourceTriangleCount),
                        VariantLabel = validBoneSlots != null ? "MissingBonesFiltered" : string.Empty
                    };
                    jobsByKey.Add(key, job);
                    jobs.Add(job);
                }

                job.Entries.Add(entry);
            }

            return jobs;
        }

        private static void ValidateTargetSettings(
            TargetInputMode mode,
            int requestedTriangleCount,
            float requestedRemainingPercentage)
        {
            switch (mode)
            {
                case TargetInputMode.TriangleCount:
                    if (requestedTriangleCount < 1)
                        throw new ArgumentOutOfRangeException(nameof(requestedTriangleCount), "The target triangle count must be at least 1.");
                    break;

                case TargetInputMode.Percentage:
                    if (float.IsNaN(requestedRemainingPercentage) || float.IsInfinity(requestedRemainingPercentage) ||
                        requestedRemainingPercentage < MinRemainingPercentage || requestedRemainingPercentage > 100f)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(requestedRemainingPercentage),
                            string.Format("The remaining percentage must be between {0} and 100.", MinRemainingPercentage));
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), "Unknown target input mode.");
            }
        }

        private static int GetCombinedTriangleFloor(Mesh source, int sourceTriangleCount)
        {
            if (sourceTriangleCount <= 0)
                return 0;

            int percentageFloor = (int)Math.Ceiling(sourceTriangleCount * CombinedTargetMinimumFraction);
            int subMeshFloor = Math.Max(1, source != null ? source.subMeshCount : 1) * 4;
            int floor = Math.Max(CombinedTargetMinimumTrianglesPerMesh, Math.Max(percentageFloor, subMeshFloor));
            return Math.Min(sourceTriangleCount, floor);
        }

        private static int[] BuildTriangleTargets(
            List<Mesh> sourceMeshes,
            TargetInputMode mode,
            int requestedTriangleCount,
            float requestedRemainingPercentage)
        {
            if (sourceMeshes == null)
                throw new ArgumentNullException(nameof(sourceMeshes));

            ValidateTargetSettings(mode, requestedTriangleCount, requestedRemainingPercentage);

            int meshCount = sourceMeshes.Count;
            var sourceCounts = new int[meshCount];
            var combinedFloors = new int[meshCount];
            var targets = new int[meshCount];
            long totalSourceTriangles = 0L;
            long totalCombinedFloor = 0L;

            for (int i = 0; i < meshCount; i++)
            {
                Mesh source = sourceMeshes[i];
                long sourceTriangleCount = GetTriangleCount(source);
                if (sourceTriangleCount <= 0L)
                    throw new InvalidOperationException(string.Format("Mesh '{0}' has no triangle primitives.", source != null ? source.name : "<null>"));
                if (sourceTriangleCount > int.MaxValue)
                    throw new NotSupportedException(string.Format("Mesh '{0}' exceeds the supported triangle-count range.", source.name));

                sourceCounts[i] = (int)sourceTriangleCount;
                combinedFloors[i] = GetCombinedTriangleFloor(source, sourceCounts[i]);
                totalSourceTriangles += sourceTriangleCount;
                totalCombinedFloor += combinedFloors[i];
            }

            if (meshCount == 0)
                return targets;

            long requestedCombinedTarget;
            if (mode == TargetInputMode.Percentage)
            {
                double percentageFraction = requestedRemainingPercentage / 100.0;
                requestedCombinedTarget = (long)Math.Round(
                    totalSourceTriangles * percentageFraction,
                    MidpointRounding.AwayFromZero);
            }
            else
            {
                requestedCombinedTarget = requestedTriangleCount;
            }

            long combinedTarget = Math.Max(totalCombinedFloor, Math.Min(totalSourceTriangles, requestedCombinedTarget));
            if (combinedTarget >= totalSourceTriangles)
            {
                Array.Copy(sourceCounts, targets, meshCount);
                return targets;
            }

            long remainingBudget = combinedTarget - totalCombinedFloor;
            long totalCapacity = totalSourceTriangles - totalCombinedFloor;
            var fractionalRemainders = new double[meshCount];
            long assigned = totalCombinedFloor;

            for (int i = 0; i < meshCount; i++)
            {
                targets[i] = combinedFloors[i];
                int capacity = sourceCounts[i] - combinedFloors[i];
                if (capacity <= 0 || remainingBudget <= 0L || totalCapacity <= 0L)
                    continue;

                double idealExtra = (double)remainingBudget * capacity / totalCapacity;
                int extra = Math.Min(capacity, (int)Math.Floor(idealExtra));
                targets[i] += extra;
                assigned += extra;
                fractionalRemainders[i] = idealExtra - extra;
            }

            long leftovers = combinedTarget - assigned;
            while (leftovers > 0L)
            {
                int bestIndex = -1;
                double bestRemainder = double.NegativeInfinity;
                for (int i = 0; i < meshCount; i++)
                {
                    if (targets[i] >= sourceCounts[i])
                        continue;

                    double remainder = fractionalRemainders[i];
                    if (remainder > bestRemainder ||
                        (Math.Abs(remainder - bestRemainder) <= 1e-15 && (bestIndex < 0 || sourceCounts[i] > sourceCounts[bestIndex])))
                    {
                        bestRemainder = remainder;
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0)
                    throw new InvalidOperationException("Could not distribute the combined triangle target across the selected meshes.");

                targets[bestIndex]++;
                fractionalRemainders[bestIndex] = 0.0;
                leftovers--;
            }

            return targets;
        }

        private static long SumTriangleTargets(int[] targets)
        {
            if (targets == null)
                return 0L;

            long total = 0L;
            for (int i = 0; i < targets.Length; i++)
                total += targets[i];
            return total;
        }

        private static long SumTriangleCounts(Mesh[] meshes)
        {
            if (meshes == null)
                return 0L;

            long total = 0L;
            for (int i = 0; i < meshes.Length; i++)
                total += GetTriangleCount(meshes[i]);
            return total;
        }

        private static async Task RunSimplificationJobAsync(
            SimplificationJob job,
            global::UnityMeshSimplifier.MeshSimplifier simplifier,
            int redistributionPassIndex,
            int redistributionPassCount,
            CancellationTokenSource cancelAll,
            CancellationToken timeLimitToken)
        {
            if (timeLimitToken.IsCancellationRequested && !cancelAll.Token.IsCancellationRequested)
            {
                job.TimeLimitReached = true;
                job.Progress.SetState(JobState.Finalizing, "Maximum time reached; keeping the current valid mesh.");
                return;
            }

            job.Progress.BeginRedistributionPass(redistributionPassIndex, redistributionPassCount);
            try
            {
                using (CancellationTokenSource jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancelAll.Token, timeLimitToken))
                {
                    await simplifier.SimplifyMeshToTriangleCountAsync(
                        job.TargetTriangleCount,
                        1,
                        jobCancellation.Token).ConfigureAwait(false);
                }

                job.Progress.CompleteRedistributionPass(
                    redistributionPassIndex,
                    redistributionPassCount);
            }
            catch (OperationCanceledException) when (timeLimitToken.IsCancellationRequested && !cancelAll.Token.IsCancellationRequested)
            {
                job.TimeLimitReached = true;
                job.Progress.SetState(JobState.Finalizing, "Maximum time reached; keeping the current valid mesh.");
            }
            catch (OperationCanceledException)
            {
                job.Progress.SetState(JobState.Cancelled, "Cancelled.");
                throw;
            }
            catch
            {
                job.Progress.SetState(JobState.Failed, "Failed. See the Console for details.");
                cancelAll.Cancel();
                throw;
            }
        }

        private static long SumCurrentJobTriangles(List<SimplificationJob> jobs)
        {
            long total = 0L;
            for (int i = 0; i < jobs.Count; i++)
                total += Math.Max(0, jobs[i].Progress.Read().CurrentTriangles);
            return total;
        }

        private static List<int> BuildCombinedRebalanceTargets(
            List<SimplificationJob> jobs,
            long combinedTargetTriangleCount,
            bool[] stalledJobs)
        {
            int jobCount = jobs.Count;
            var newTargets = new int[jobCount];
            long currentTotal = 0L;
            long availableCapacity = 0L;

            for (int i = 0; i < jobCount; i++)
            {
                JobProgressSnapshot progress = jobs[i].Progress.Read();
                int current = Math.Max(0, progress.CurrentTriangles);
                newTargets[i] = current;
                currentTotal += current;

                bool reachedPreviousTarget = current <= jobs[i].TargetTriangleCount;
                int minimum = Math.Max(1, jobs[i].MinimumTriangleCount);
                if (!stalledJobs[i] && reachedPreviousTarget && current > minimum)
                    availableCapacity += current - minimum;
            }

            long reductionNeeded = currentTotal - combinedTargetTriangleCount;
            if (reductionNeeded <= 0L || availableCapacity <= 0L)
                return new List<int>();

            long distributableReduction = Math.Min(reductionNeeded, availableCapacity);
            var fractionalRemainders = new double[jobCount];
            long assignedReduction = 0L;

            for (int i = 0; i < jobCount; i++)
            {
                JobProgressSnapshot progress = jobs[i].Progress.Read();
                int current = Math.Max(0, progress.CurrentTriangles);
                bool reachedPreviousTarget = current <= jobs[i].TargetTriangleCount;
                int minimum = Math.Max(1, jobs[i].MinimumTriangleCount);
                long capacity = !stalledJobs[i] && reachedPreviousTarget && current > minimum
                    ? current - minimum
                    : 0L;
                if (capacity <= 0L)
                    continue;

                double idealReduction = (double)distributableReduction * capacity / availableCapacity;
                int reduction = (int)Math.Min(capacity, (long)Math.Floor(idealReduction));
                if (reduction > 0)
                {
                    newTargets[i] = current - reduction;
                    assignedReduction += reduction;
                }
                fractionalRemainders[i] = idealReduction - reduction;
            }

            long leftovers = distributableReduction - assignedReduction;
            while (leftovers > 0L)
            {
                int bestIndex = -1;
                double bestRemainder = double.NegativeInfinity;
                int bestCurrent = -1;

                for (int i = 0; i < jobCount; i++)
                {
                    JobProgressSnapshot progress = jobs[i].Progress.Read();
                    int current = Math.Max(0, progress.CurrentTriangles);
                    bool reachedPreviousTarget = current <= jobs[i].TargetTriangleCount;
                    int minimum = Math.Max(1, jobs[i].MinimumTriangleCount);
                    if (stalledJobs[i] || !reachedPreviousTarget || newTargets[i] <= minimum)
                        continue;

                    double remainder = fractionalRemainders[i];
                    if (remainder > bestRemainder ||
                        (Math.Abs(remainder - bestRemainder) <= 1e-15 && current > bestCurrent))
                    {
                        bestIndex = i;
                        bestRemainder = remainder;
                        bestCurrent = current;
                    }
                }

                if (bestIndex < 0)
                    break;

                newTargets[bestIndex]--;
                fractionalRemainders[bestIndex] = 0.0;
                leftovers--;
            }

            var changedJobs = new List<int>();
            for (int i = 0; i < jobCount; i++)
            {
                int current = Math.Max(0, jobs[i].Progress.Read().CurrentTriangles);
                if (newTargets[i] < current)
                {
                    jobs[i].TargetTriangleCount = newTargets[i];
                    jobs[i].Progress.SetTargetTriangles(
                        newTargets[i],
                        string.Format("Rebalanced combined target: {0:N0} triangles.", newTargets[i]));
                    changedJobs.Add(i);
                }
            }

            return changedJobs;
        }

        private static async Task RunJobIndicesWithMaximumConcurrencyAsync(
            List<SimplificationJob> jobs,
            global::UnityMeshSimplifier.MeshSimplifier[] simplifiers,
            IList<int> jobIndices,
            int redistributionPassIndex,
            int redistributionPassCount,
            int maximumParallelJobs,
            CancellationTokenSource cancelAll,
            CancellationToken timeLimitToken)
        {
            if (jobIndices == null || jobIndices.Count == 0)
                return;

            int workerCount = Math.Max(1, Math.Min(maximumParallelJobs, jobIndices.Count));
            int baseInternalWorkers = Math.Max(1, maximumParallelJobs / workerCount);
            int extraWorkerSlots = Math.Max(0, maximumParallelJobs - (baseInternalWorkers * workerCount));
            for (int i = 0; i < jobIndices.Count; i++)
            {
                int jobIndex = jobIndices[i];
                simplifiers[jobIndex].MaxDegreeOfParallelism =
                    baseInternalWorkers + (i < extraWorkerSlots ? 1 : 0);
            }

            int nextWorkItem = -1;
            var workers = new Task[workerCount];
            Func<Task> runWorker = async () =>
            {
                while (true)
                {
                    cancelAll.Token.ThrowIfCancellationRequested();
                    int workItem = Interlocked.Increment(ref nextWorkItem);
                    if (workItem >= jobIndices.Count)
                        return;

                    int jobIndex = jobIndices[workItem];
                    await RunSimplificationJobAsync(
                        jobs[jobIndex],
                        simplifiers[jobIndex],
                        redistributionPassIndex,
                        redistributionPassCount,
                        cancelAll,
                        timeLimitToken).ConfigureAwait(false);
                }
            };

            for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
                workers[workerIndex] = runWorker();

            await Task.WhenAll(workers);
        }

        private async Task<Mesh[]> SimplifyWithMaximumConcurrencyAsync(
            List<SimplificationJob> jobs,
            global::UnityMeshSimplifier.SimplificationOptions options,
            int targetPassCount,
            int maximumParallelJobs,
            long combinedTargetTriangleCount,
            float maximumSimplificationTimeSeconds,
            CancellationToken token)
        {
            if (jobs == null)
                throw new ArgumentNullException(nameof(jobs));
            if (targetPassCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetPassCount), "The redistribution pass count must be positive.");
            if (float.IsNaN(maximumSimplificationTimeSeconds) || float.IsInfinity(maximumSimplificationTimeSeconds) || maximumSimplificationTimeSeconds < 0f)
                throw new ArgumentOutOfRangeException(nameof(maximumSimplificationTimeSeconds), "The maximum simplification time must be zero or positive.");

            var results = new Mesh[jobs.Count];
            var simplifiers = new global::UnityMeshSimplifier.MeshSimplifier[jobs.Count];
            var allJobIndices = new int[jobs.Count];
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationTokenSource timeLimitCancellation = null;

            try
            {
                statusMessage = string.Format("Snapshotting {0} mesh job(s)...", jobs.Count);
                RequestRepaint();
                for (int i = 0; i < jobs.Count; i++)
                {
                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    SimplificationJob job = jobs[i];
                    job.Progress.SetState(JobState.Snapshotting, "Snapshotting mesh data...");

                    var simplifier = new global::UnityMeshSimplifier.MeshSimplifier();
                    simplifier.SimplificationOptions = options;
                    simplifier.ProgressChanged = job.Progress.Apply;
                    simplifier.Initialize(job.SourceMesh);
                    if (job.FiltersMissingBones)
                        simplifier.FilterBoneInfluences(job.ValidBoneSlots, job.FallbackBoneIndex);

                    simplifiers[i] = simplifier;
                    allJobIndices[i] = i;

                    if ((i & 1) != 0)
                        await Task.Yield();
                }

                CancellationToken timeLimitToken = CancellationToken.None;
                if (maximumSimplificationTimeSeconds > 0f)
                {
                    timeLimitCancellation = new CancellationTokenSource();
                    timeLimitCancellation.CancelAfter(TimeSpan.FromSeconds(maximumSimplificationTimeSeconds));
                    timeLimitToken = timeLimitCancellation.Token;
                }

                var stalledJobs = new bool[jobs.Count];
                long previousTotal = SumCurrentJobTriangles(jobs);

                for (int redistributionPass = 1;
                     redistributionPass <= targetPassCount;
                     redistributionPass++)
                {
                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    if (timeLimitToken.IsCancellationRequested)
                        break;

                    IList<int> jobsForPass;
                    if (redistributionPass == 1)
                    {
                        jobsForPass = allJobIndices;
                    }
                    else
                    {
                        if (combinedTargetTriangleCount <= 0L ||
                            previousTotal <= combinedTargetTriangleCount)
                        {
                            break;
                        }

                        List<int> rebalancedJobs = BuildCombinedRebalanceTargets(
                            jobs,
                            combinedTargetTriangleCount,
                            stalledJobs);
                        if (rebalancedJobs.Count == 0)
                            break;

                        jobsForPass = rebalancedJobs;
                    }

                    var beforeCounts = new int[jobsForPass.Count];
                    for (int i = 0; i < jobsForPass.Count; i++)
                    {
                        int jobIndex = jobsForPass[i];
                        beforeCounts[i] = Math.Max(
                            0,
                            jobs[jobIndex].Progress.Read().CurrentTriangles);
                    }

                    statusMessage = maximumSimplificationTimeSeconds > 0f
                        ? string.Format(
                            "Redistribution pass {0}/{1}: processing {2} mesh job(s) with up to {3} worker(s) and a shared {4:0.###}-second maximum...",
                            redistributionPass,
                            targetPassCount,
                            jobsForPass.Count,
                            Math.Max(1, Math.Min(maximumParallelJobs, jobsForPass.Count)),
                            maximumSimplificationTimeSeconds)
                        : string.Format(
                            "Redistribution pass {0}/{1}: processing {2} mesh job(s) with up to {3} worker(s)...",
                            redistributionPass,
                            targetPassCount,
                            jobsForPass.Count,
                            Math.Max(1, Math.Min(maximumParallelJobs, jobsForPass.Count)));
                    RequestRepaint();

                    await RunJobIndicesWithMaximumConcurrencyAsync(
                        jobs,
                        simplifiers,
                        jobsForPass,
                        redistributionPass,
                        targetPassCount,
                        maximumParallelJobs,
                        linkedCancellation,
                        timeLimitToken);
                    linkedCancellation.Token.ThrowIfCancellationRequested();

                    if (timeLimitToken.IsCancellationRequested)
                        break;

                    for (int i = 0; i < jobsForPass.Count; i++)
                    {
                        int jobIndex = jobsForPass[i];
                        int after = Math.Max(
                            0,
                            jobs[jobIndex].Progress.Read().CurrentTriangles);
                        if (after >= beforeCounts[i])
                            stalledJobs[jobIndex] = true;
                    }

                    long currentTotal = SumCurrentJobTriangles(jobs);
                    if (combinedTargetTriangleCount > 0L &&
                        currentTotal <= combinedTargetTriangleCount)
                    {
                        previousTotal = currentTotal;
                        break;
                    }

                    if (currentTotal >= previousTotal)
                        break;

                    previousTotal = currentTotal;
                }

                linkedCancellation.Token.ThrowIfCancellationRequested();
                bool reachedMaximumTime = false;
                for (int i = 0; i < jobs.Count; i++)
                {
                    if (jobs[i].TimeLimitReached)
                    {
                        reachedMaximumTime = true;
                        break;
                    }
                }
                statusMessage = reachedMaximumTime
                    ? "Maximum time reached. Creating and saving the best valid mesh state so far..."
                    : "Creating Unity mesh copies...";
                RequestRepaint();
                for (int i = 0; i < jobs.Count; i++)
                {
                    SimplificationJob job = jobs[i];
                    job.Progress.SetState(
                        JobState.Finalizing,
                        job.TimeLimitReached
                            ? "Maximum time reached; creating mesh from saved progress..."
                            : "Creating Unity mesh copy...");
                    RequestRepaint();

                    Mesh result = simplifiers[i].ToMesh();
                    results[i] = result;
                    job.Progress.SetCurrentTriangles((int)Math.Min(int.MaxValue, GetTriangleCount(result)));
                    job.Progress.SetState(
                        JobState.Completed,
                        job.TimeLimitReached ? "Completed at maximum time." : "Completed.");
                    RequestRepaint();
                    await Task.Yield();
                }

                return results;
            }
            catch (OperationCanceledException)
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    JobProgressSnapshot progress = jobs[i].Progress.Read();
                    if (progress.State != JobState.Completed && progress.State != JobState.Failed)
                        jobs[i].Progress.SetState(JobState.Cancelled, "Cancelled.");
                }
                DestroyTemporaryMeshes(results);
                throw;
            }
            catch
            {
                DestroyTemporaryMeshes(results);
                throw;
            }
            finally
            {
                if (timeLimitCancellation != null)
                    timeLimitCancellation.Dispose();
                linkedCancellation.Dispose();
            }
        }

        private static int CleanupUnusedGeneratedMeshAssets(
            string folder,
            IEnumerable<string> protectedAssetPaths)
        {
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                return 0;

            string normalizedFolder = NormalizeAssetPath(folder);
            var protectedPaths = new HashSet<string>(
                UndoProtectedMeshPaths,
                StringComparer.OrdinalIgnoreCase);
            if (protectedAssetPaths != null)
            {
                foreach (string path in protectedAssetPaths)
                {
                    if (!string.IsNullOrEmpty(path))
                        protectedPaths.Add(NormalizeAssetPath(path));
                }
            }

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] meshGuids = AssetDatabase.FindAssets(
                "t:Mesh",
                new[] { normalizedFolder });
            for (int meshIndex = 0; meshIndex < meshGuids.Length; meshIndex++)
            {
                string assetPath = NormalizeAssetPath(
                    AssetDatabase.GUIDToAssetPath(meshGuids[meshIndex]));
                if (!IsPathInsideFolder(assetPath, normalizedFolder) ||
                    !assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ||
                    !(AssetDatabase.LoadMainAssetAtPath(assetPath) is Mesh))
                {
                    continue;
                }

                candidates.Add(assetPath);
            }

            if (candidates.Count == 0)
                return 0;

            var usedPaths = new HashSet<string>(
                protectedPaths,
                StringComparer.OrdinalIgnoreCase);
            MarkLoadedMeshReferences(candidates, usedPaths);

            string[] projectPaths = AssetDatabase.GetAllAssetPaths();
            for (int pathIndex = 0;
                 pathIndex < projectPaths.Length && usedPaths.Count < candidates.Count;
                 pathIndex++)
            {
                string ownerPath = NormalizeAssetPath(projectPaths[pathIndex]);
                if (string.IsNullOrEmpty(ownerPath) ||
                    candidates.Contains(ownerPath) ||
                    AssetDatabase.IsValidFolder(ownerPath))
                {
                    continue;
                }

                string[] dependencies;
                try
                {
                    dependencies = AssetDatabase.GetDependencies(ownerPath, false);
                }
                catch
                {
                    continue;
                }

                for (int dependencyIndex = 0;
                     dependencyIndex < dependencies.Length;
                     dependencyIndex++)
                {
                    string dependencyPath = NormalizeAssetPath(
                        dependencies[dependencyIndex]);
                    if (candidates.Contains(dependencyPath))
                        usedPaths.Add(dependencyPath);
                }
            }

            int deletedCount = 0;
            foreach (string candidatePath in candidates)
            {
                if (usedPaths.Contains(candidatePath))
                    continue;

                if (AssetDatabase.MoveAssetToTrash(candidatePath))
                    deletedCount++;
            }

            if (deletedCount > 0)
                AssetDatabase.SaveAssets();

            return deletedCount;
        }

        private static bool IsPathInsideFolder(string assetPath, string folder)
        {
            if (string.Equals(assetPath, folder, StringComparison.OrdinalIgnoreCase))
                return true;

            return assetPath.StartsWith(
                folder + "/",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void MarkLoadedMeshReferences(
            HashSet<string> candidates,
            HashSet<string> usedPaths)
        {
            SkinnedMeshRenderer[] skinnedRenderers =
                Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>();
            for (int i = 0; i < skinnedRenderers.Length; i++)
                MarkMeshAssetPathUsed(skinnedRenderers[i].sharedMesh, candidates, usedPaths);

            MeshFilter[] meshFilters = Resources.FindObjectsOfTypeAll<MeshFilter>();
            for (int i = 0; i < meshFilters.Length; i++)
                MarkMeshAssetPathUsed(meshFilters[i].sharedMesh, candidates, usedPaths);

            MeshCollider[] meshColliders = Resources.FindObjectsOfTypeAll<MeshCollider>();
            for (int i = 0; i < meshColliders.Length; i++)
                MarkMeshAssetPathUsed(meshColliders[i].sharedMesh, candidates, usedPaths);
        }

        private static void MarkMeshAssetPathUsed(
            Mesh mesh,
            HashSet<string> candidates,
            HashSet<string> usedPaths)
        {
            if (mesh == null)
                return;

            string path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path))
                return;

            path = path.Replace('\\', '/');
            if (candidates.Contains(path))
                usedPaths.Add(path);
        }

        private static Dictionary<MeshEntry, Mesh> SaveGeneratedAssets(
            List<SimplificationJob> jobs,
            Mesh[] generatedMeshes,
            string folder,
            List<string> createdAssetPaths)
        {
            if (generatedMeshes == null || jobs == null || generatedMeshes.Length != jobs.Count)
                throw new InvalidOperationException("Generated mesh count does not match simplification job count.");

            var resultByEntry = new Dictionary<MeshEntry, Mesh>();
            var assetPaths = new string[jobs.Count];

            for (int i = 0; i < jobs.Count; i++)
            {
                SimplificationJob job = jobs[i];
                Mesh generated = generatedMeshes[i];
                if (generated == null)
                    throw new InvalidOperationException(string.Format("Simplification returned a null mesh for '{0}'.", job.SourceMesh.name));

                ValidateGeneratedMesh(job.SourceMesh, generated, job.ValidBoneSlots);
                string sourceName = SanitizeFileName(job.SourceMesh.name);
                bool alreadyEndsWithSimplified = sourceName.EndsWith(
                    "_Simplified",
                    StringComparison.OrdinalIgnoreCase);

                string suffix;
                if (string.IsNullOrEmpty(job.VariantLabel))
                {
                    suffix = alreadyEndsWithSimplified ? string.Empty : "_Simplified";
                }
                else
                {
                    suffix = alreadyEndsWithSimplified
                        ? "_" + job.VariantLabel
                        : "_Simplified_" + job.VariantLabel;
                }

                string fileName = sourceName + suffix + ".asset";
                assetPaths[i] = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + fileName);
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    SimplificationJob job = jobs[i];
                    Mesh generated = generatedMeshes[i];
                    generated.name = Path.GetFileNameWithoutExtension(assetPaths[i]);
                    createdAssetPaths.Add(assetPaths[i]);
                    AssetDatabase.CreateAsset(generated, assetPaths[i]);

                    for (int entryIndex = 0; entryIndex < job.Entries.Count; entryIndex++)
                        resultByEntry.Add(job.Entries[entryIndex], generated);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            return resultByEntry;
        }

        private static bool HasAnyValidBoneSlot(bool[] validBoneSlots)
        {
            if (validBoneSlots == null)
                return true;
            for (int i = 0; i < validBoneSlots.Length; i++)
            {
                if (validBoneSlots[i])
                    return true;
            }
            return false;
        }

        private static void ValidateGeneratedMesh(Mesh source, Mesh generated, bool[] validBoneSlots)
        {
            if (ReferenceEquals(source, generated))
                throw new InvalidOperationException("The simplifier returned the source mesh instead of an independent copy.");
            if (generated.vertexCount <= 0)
                throw new InvalidOperationException(string.Format("Simplifying '{0}' produced an empty mesh. Raise the triangle target or remaining percentage.", source.name));
            if (generated.vertexCount > source.vertexCount)
                throw new InvalidOperationException(string.Format("Simplifying '{0}' unexpectedly increased its vertex count.", source.name));
            if (generated.subMeshCount != source.subMeshCount)
            {
                throw new InvalidOperationException(string.Format(
                    "Submesh count changed for '{0}' ({1} -> {2}).",
                    source.name,
                    source.subMeshCount,
                    generated.subMeshCount));
            }

            for (int subMesh = 0; subMesh < source.subMeshCount; subMesh++)
            {
                if (generated.GetTopology(subMesh) != source.GetTopology(subMesh))
                {
                    throw new InvalidOperationException(string.Format(
                        "Submesh {0} topology changed for '{1}'.",
                        subMesh,
                        source.name));
                }
            }

            ValidateAttributePresenceAndDimension(source, generated, VertexAttribute.Position, "position");
            ValidateAttributePresenceAndDimension(source, generated, VertexAttribute.Normal, "normal");
            ValidateAttributePresenceAndDimension(source, generated, VertexAttribute.Tangent, "tangent");
            ValidateAttributePresenceAndDimension(source, generated, VertexAttribute.Color, "color");

            bool hasAnyValidBoneSlot = HasAnyValidBoneSlot(validBoneSlots);
            bool allRendererBonesMissing = validBoneSlots != null && !hasAnyValidBoneSlot;
            if (!allRendererBonesMissing)
            {
                ValidateAttributePresence(source, generated, VertexAttribute.BlendWeight, "blend weight");
                ValidateAttributePresence(source, generated, VertexAttribute.BlendIndices, "blend index");
            }

            for (int channel = 0; channel < 8; channel++)
            {
                VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                ValidateAttributePresenceAndDimension(source, generated, attribute, "UV" + channel);
            }

            long sourceTriangleCount = GetTriangleCount(source);
            long generatedTriangleCount = GetTriangleCount(generated);
            if (generatedTriangleCount > sourceTriangleCount)
            {
                throw new InvalidOperationException(string.Format(
                    "Simplifying '{0}' unexpectedly increased its triangle count ({1} -> {2}).",
                    source.name,
                    sourceTriangleCount,
                    generatedTriangleCount));
            }

            Matrix4x4[] sourceBindposes = source.bindposes;
            Matrix4x4[] generatedBindposes = generated.bindposes;
            int sourceBindposeCount = sourceBindposes != null ? sourceBindposes.Length : 0;
            int generatedBindposeCount = generatedBindposes != null ? generatedBindposes.Length : 0;
            if (sourceBindposeCount != generatedBindposeCount)
            {
                throw new InvalidOperationException(string.Format(
                    "Bindpose count changed for '{0}' ({1} -> {2}).",
                    source.name,
                    sourceBindposeCount,
                    generatedBindposeCount));
            }
            for (int bindpose = 0; bindpose < sourceBindposeCount; bindpose++)
            {
                for (int component = 0; component < 16; component++)
                {
                    if (!Mathf.Approximately(sourceBindposes[bindpose][component], generatedBindposes[bindpose][component]))
                    {
                        throw new InvalidOperationException(string.Format(
                            "Bindpose {0} changed for mesh '{1}'.",
                            bindpose,
                            source.name));
                    }
                }
            }

            ValidateGeneratedBoneWeights(generated, generatedBindposeCount, validBoneSlots);

            if (source.blendShapeCount != generated.blendShapeCount)
            {
                throw new InvalidOperationException(string.Format(
                    "Blend-shape count changed for '{0}' ({1} -> {2}).",
                    source.name,
                    source.blendShapeCount,
                    generated.blendShapeCount));
            }

            for (int shape = 0; shape < source.blendShapeCount; shape++)
            {
                string sourceName = source.GetBlendShapeName(shape);
                string generatedName = generated.GetBlendShapeName(shape);
                if (!string.Equals(sourceName, generatedName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(string.Format(
                        "Blend-shape {0} name changed for '{1}' ('{2}' -> '{3}').",
                        shape,
                        source.name,
                        sourceName,
                        generatedName));
                }

                int sourceFrameCount = source.GetBlendShapeFrameCount(shape);
                int generatedFrameCount = generated.GetBlendShapeFrameCount(shape);
                if (sourceFrameCount != generatedFrameCount)
                {
                    throw new InvalidOperationException(string.Format(
                        "Blend-shape '{0}' frame count changed for mesh '{1}' ({2} -> {3}).",
                        sourceName,
                        source.name,
                        sourceFrameCount,
                        generatedFrameCount));
                }

                for (int frame = 0; frame < sourceFrameCount; frame++)
                {
                    float sourceWeight = source.GetBlendShapeFrameWeight(shape, frame);
                    float generatedWeight = generated.GetBlendShapeFrameWeight(shape, frame);
                    if (!Mathf.Approximately(sourceWeight, generatedWeight))
                    {
                        throw new InvalidOperationException(string.Format(
                            "Blend-shape '{0}' frame {1} weight changed for mesh '{2}' ({3} -> {4}).",
                            sourceName,
                            frame,
                            source.name,
                            sourceWeight,
                            generatedWeight));
                    }
                }
            }
        }

        private static void ValidateGeneratedBoneWeights(Mesh mesh, int bindposeCount, bool[] validBoneSlots)
        {
            bool hasWeights = mesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                              mesh.HasVertexAttribute(VertexAttribute.BlendIndices);
            if (!hasWeights)
                return;
            if (bindposeCount <= 0)
                throw new InvalidOperationException(string.Format("Mesh '{0}' has bone weights but no bindposes.", mesh.name));

            var bonesPerVertex = mesh.GetBonesPerVertex();
            var allWeights = mesh.GetAllBoneWeights();
            try
            {
                if (bonesPerVertex.Length != mesh.vertexCount)
                {
                    throw new InvalidOperationException(string.Format(
                        "Mesh '{0}' has an invalid bones-per-vertex stream length.",
                        mesh.name));
                }

                int weightOffset = 0;
                for (int vertex = 0; vertex < bonesPerVertex.Length; vertex++)
                {
                    int influenceCount = bonesPerVertex[vertex];
                    if (weightOffset + influenceCount > allWeights.Length)
                    {
                        throw new InvalidOperationException(string.Format(
                            "Mesh '{0}' has a truncated bone-weight stream at vertex {1}.",
                            mesh.name,
                            vertex));
                    }

                    float sum = 0f;
                    float previousWeight = float.PositiveInfinity;
                    for (int influence = 0; influence < influenceCount; influence++)
                    {
                        BoneWeight1 boneWeight = allWeights[weightOffset++];
                        if (float.IsNaN(boneWeight.weight) || float.IsInfinity(boneWeight.weight) || boneWeight.weight <= 0f)
                        {
                            throw new InvalidOperationException(string.Format(
                                "Mesh '{0}' has an invalid bone weight at vertex {1}.",
                                mesh.name,
                                vertex));
                        }
                        if (boneWeight.boneIndex < 0 || boneWeight.boneIndex >= bindposeCount)
                        {
                            throw new InvalidOperationException(string.Format(
                                "Mesh '{0}' has bone index {1} outside its bindpose range at vertex {2}.",
                                mesh.name,
                                boneWeight.boneIndex,
                                vertex));
                        }
                        if (validBoneSlots != null &&
                            (boneWeight.boneIndex >= validBoneSlots.Length || !validBoneSlots[boneWeight.boneIndex]))
                        {
                            throw new InvalidOperationException(string.Format(
                                "Mesh '{0}' still references missing renderer bone slot {1} at vertex {2}.",
                                mesh.name,
                                boneWeight.boneIndex,
                                vertex));
                        }
                        if (boneWeight.weight > previousWeight + 0.000001f)
                        {
                            throw new InvalidOperationException(string.Format(
                                "Mesh '{0}' has bone influences that are not strongest-first at vertex {1}.",
                                mesh.name,
                                vertex));
                        }

                        previousWeight = boneWeight.weight;
                        sum += boneWeight.weight;
                    }

                    if (influenceCount > 0 && !Mathf.Approximately(sum, 1f))
                    {
                        throw new InvalidOperationException(string.Format(
                            "Mesh '{0}' has non-normalized bone weights at vertex {1} (sum {2}).",
                            mesh.name,
                            vertex,
                            sum));
                    }
                }

                if (weightOffset != allWeights.Length)
                {
                    throw new InvalidOperationException(string.Format(
                        "Mesh '{0}' has unused trailing bone weights.",
                        mesh.name));
                }
            }
            finally
            {
                if (bonesPerVertex.IsCreated)
                    bonesPerVertex.Dispose();
                if (allWeights.IsCreated)
                    allWeights.Dispose();
            }
        }

        private static void ValidateAttributePresence(
            Mesh source,
            Mesh generated,
            VertexAttribute attribute,
            string label)
        {
            if (source.HasVertexAttribute(attribute) != generated.HasVertexAttribute(attribute))
            {
                throw new InvalidOperationException(string.Format(
                    "The {0} stream presence changed for mesh '{1}'.",
                    label,
                    source.name));
            }
        }

        private static void ValidateAttributePresenceAndDimension(
            Mesh source,
            Mesh generated,
            VertexAttribute attribute,
            string label)
        {
            bool sourceHasAttribute = source.HasVertexAttribute(attribute);
            bool generatedHasAttribute = generated.HasVertexAttribute(attribute);
            if (sourceHasAttribute != generatedHasAttribute)
            {
                throw new InvalidOperationException(string.Format(
                    "The {0} stream presence changed for mesh '{1}'.",
                    label,
                    source.name));
            }

            if (sourceHasAttribute)
            {
                int sourceDimension = source.GetVertexAttributeDimension(attribute);
                int generatedDimension = generated.GetVertexAttributeDimension(attribute);
                if (sourceDimension != generatedDimension)
                {
                    throw new InvalidOperationException(string.Format(
                        "The {0} stream dimension changed for mesh '{1}' ({2} -> {3}).",
                        label,
                        source.name,
                        sourceDimension,
                        generatedDimension));
                }
            }
        }

        private void AssignResults(List<MeshEntry> selectedEntries, Dictionary<MeshEntry, Mesh> resultByEntry)
        {
            for (int i = 0; i < selectedEntries.Count; i++)
            {
                MeshEntry entry = selectedEntries[i];
                Mesh generated;
                if (!resultByEntry.TryGetValue(entry, out generated))
                    throw new InvalidOperationException("A generated mesh could not be matched to its selected renderer entry.");

                SkinnedMeshRenderer skinned = entry.Owner as SkinnedMeshRenderer;
                if (skinned != null)
                {
                    AssignToSkinnedRenderer(skinned, generated);
                    continue;
                }

                MeshFilter filter = entry.Owner as MeshFilter;
                if (filter != null)
                    AssignToMeshFilter(filter, entry.SourceMesh, generated);
            }
        }

        private static void AssignToSkinnedRenderer(SkinnedMeshRenderer renderer, Mesh generated)
        {
            Mesh source = renderer.sharedMesh;
            Bounds originalBounds = renderer.localBounds;
            int sourceShapeCount = source != null ? source.blendShapeCount : 0;
            var blendShapeNames = new string[sourceShapeCount];
            var blendShapeWeights = new float[sourceShapeCount];

            for (int i = 0; i < sourceShapeCount; i++)
            {
                blendShapeNames[i] = source.GetBlendShapeName(i);
                blendShapeWeights[i] = renderer.GetBlendShapeWeight(i);
            }

            Undo.RecordObject(renderer, "Assign simplified skinned mesh");
            renderer.sharedMesh = generated;
            renderer.localBounds = originalBounds;

            for (int i = 0; i < sourceShapeCount; i++)
            {
                int newIndex = -1;
                if (i < generated.blendShapeCount &&
                    string.Equals(generated.GetBlendShapeName(i), blendShapeNames[i], StringComparison.Ordinal))
                {
                    newIndex = i;
                }
                else
                {
                    newIndex = generated.GetBlendShapeIndex(blendShapeNames[i]);
                }

                if (newIndex >= 0)
                    renderer.SetBlendShapeWeight(newIndex, blendShapeWeights[i]);
            }

            EditorUtility.SetDirty(renderer);
            if (PrefabUtility.IsPartOfPrefabInstance(renderer))
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }

        private void AssignToMeshFilter(MeshFilter filter, Mesh source, Mesh generated)
        {
            Undo.RecordObject(filter, "Assign simplified mesh");
            filter.sharedMesh = generated;
            EditorUtility.SetDirty(filter);
            if (PrefabUtility.IsPartOfPrefabInstance(filter))
                PrefabUtility.RecordPrefabInstancePropertyModifications(filter);

            if (!updateMatchingMeshColliders)
                return;

            MeshCollider collider = filter.GetComponent<MeshCollider>();
            if (collider != null && collider.sharedMesh == source)
            {
                Undo.RecordObject(collider, "Assign simplified collider mesh");
                collider.sharedMesh = generated;
                EditorUtility.SetDirty(collider);
                if (PrefabUtility.IsPartOfPrefabInstance(collider))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
            }
        }

        private static void DestroyTemporaryMeshes(Mesh[] meshes)
        {
            if (meshes == null)
                return;

            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh mesh = meshes[i];
                if (mesh != null && !AssetDatabase.Contains(mesh))
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                    meshes[i] = null;
                }
            }
        }

        private static void CleanupFailedOperation(Mesh[] generatedMeshes, List<string> createdAssetPaths, int undoGroup)
        {
            if (undoGroup >= 0)
                Undo.RevertAllDownToGroup(undoGroup);

            for (int i = createdAssetPaths.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrEmpty(createdAssetPaths[i]))
                {
                    UndoProtectedMeshPaths.Remove(NormalizeAssetPath(createdAssetPaths[i]));
                    AssetDatabase.DeleteAsset(createdAssetPaths[i]);
                }
            }

            if (generatedMeshes != null)
            {
                for (int i = 0; i < generatedMeshes.Length; i++)
                {
                    Mesh mesh = generatedMeshes[i];
                    if (mesh != null && !AssetDatabase.Contains(mesh))
                        UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static string NormalizeAndCreateAssetFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("The output folder is empty.");

            string normalized = folder.Trim().Replace('\\', '/').TrimEnd('/');
            if (normalized.Length == 0)
                normalized = "Assets";

            if (!normalized.Equals("Assets", StringComparison.Ordinal) &&
                !normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new ArgumentException("The output folder must be inside the project's Assets directory.");
            }

            if (normalized.Equals("Assets", StringComparison.Ordinal))
                return normalized;

            if (normalized.Equals("Assets/StreamingAssets", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Assets/StreamingAssets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Unity native mesh assets cannot be created inside Assets/StreamingAssets.");
            }

            string[] segments = normalized.Split('/');
            string current = "Assets";
            for (int i = 1; i < segments.Length; i++)
            {
                if (string.IsNullOrEmpty(segments[i]))
                    continue;
                if (segments[i] == "." || segments[i] == "..")
                    throw new ArgumentException("The output folder cannot contain '.' or '..' path segments.");

                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, segments[i]);
                    if (string.IsNullOrEmpty(guid))
                        throw new IOException("Could not create output folder: " + next);
                }
                current = next;
            }

            return current;
        }

        private static string SanitizeFileName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value) ? "Mesh" : value.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                result = result.Replace(invalid[i], '_');
            result = result.Replace('/', '_').Replace('\\', '_');
            return string.IsNullOrEmpty(result) ? "Mesh" : result;
        }
    }
#endif
