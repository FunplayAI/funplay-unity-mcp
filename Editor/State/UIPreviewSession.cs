// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.State
{
    [Serializable] internal sealed class PreviewSceneEntry
    {
        public string path, hash;
        public bool loaded, active, dirty;
    }
    [Serializable] internal sealed class UIPreviewState
    {
        public int schema_version = 1;
        public string session_id, editor_session, request_key, request_signature, status, phase, error, created_at;
        public string owned_folder, scene_path, scene_hash, size_label, operation_id, focused_window;
        public PreviewSceneEntry[] original_scenes;
        public string[] selection;
        public PreviewViewState original_view, applied_view;
        public List<string> warnings = new List<string>();
        public bool discard_preview_changes, scenes_restored, view_restored, selection_restored, assets_cleaned;
        public int end_attempt;
        public bool view_restore_attempted;
        public bool IsClosed => status == "closed" || status == "closed_with_warnings";
    }

    [InitializeOnLoad]
    internal static class UIPreviewSession
    {
        internal static readonly string JournalPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library/FunplayMcp/Preview/session.json");
        private const string SessionKey = "Funplay.Preview.EditorSession";
        private const string AssetRoot = "Assets/FunplayMcpPreviews";
        private static UIPreviewState state;
        private static string journalError, editorSession;
        private static double settleUntil;
        static UIPreviewSession()
        {
            editorSession = SessionState.GetString(SessionKey, "");
            if (editorSession.Length == 0) { editorSession = Guid.NewGuid().ToString("N"); SessionState.SetString(SessionKey, editorSession); }
            try
            {
                if (File.Exists(JournalPath))
                {
                    state = JsonConvert.DeserializeObject<UIPreviewState>(File.ReadAllText(JournalPath));
                    if (!ValidState(state)) throw new InvalidDataException("Preview journal schema/ownership is invalid; inspect it before recovery.");
                    if (!state.IsClosed && state.editor_session != editorSession)
                    { state.status = "interrupted"; state.error = "Editor restarted. Inspect current scenes, then explicitly end this session to recover. Nothing was automatically closed."; Save(); }
                    else if (state.status == "setting_up")
                    { state.status = "needs_attention"; state.error = "Setup was interrupted; inspect the scene and owned paths before ending."; Save(); }
                }
            }
            catch (Exception ex) { journalError = ex.Message; }
            EditorApplication.update += Tick;
        }
        internal static bool ValidState(UIPreviewState s) => s != null && s.schema_version == 1 && Guid.TryParseExact(s.session_id, "N", out _) &&
            s.owned_folder == AssetRoot + "/" + s.session_id && s.scene_path == s.owned_folder + "/Preview.unity" && s.warnings != null &&
            s.original_scenes != null && s.original_scenes.Length > 0 && s.original_scenes.All(x => x != null && !string.IsNullOrEmpty(x.path) && x.path.StartsWith("Assets/", StringComparison.Ordinal) && !x.path.Contains(".."));
        // GetSceneManagerSetup throws in Play Mode, including after domain reload. Runtime
        // status must use SceneManager; retain the editor setup API for unloaded edit scenes.
        internal static PreviewSceneEntry[] CurrentScenes() => EditorApplication.isPlaying ? RuntimeScenes() : EditorSceneManager.GetSceneManagerSetup().Select(x => new PreviewSceneEntry
        {
            path = x.path, loaded = x.isLoaded, active = x.isActive,
            dirty = SceneManager.GetSceneByPath(x.path).IsValid() && SceneManager.GetSceneByPath(x.path).isDirty
        }).ToArray();
        internal static PreviewSceneEntry[] RuntimeScenes() => Enumerable.Range(0, SceneManager.sceneCount).Select(i =>
        {
            var scene = SceneManager.GetSceneAt(i);
            return new PreviewSceneEntry { path = scene.path, loaded = scene.isLoaded, active = scene == SceneManager.GetActiveScene(), dirty = scene.isDirty };
        }).ToArray();
        internal static string ValidateOriginalScenes(PreviewSceneEntry[] scenes)
        {
            if (scenes == null || scenes.Length == 0) return "NO_SCENE_SETUP";
            if (scenes.Any(x => string.IsNullOrEmpty(x.path))) return "UNSAVED_SCENE: Save/choose a scene explicitly first; preview never discards untitled scenes.";
            if (scenes.Any(x => x.dirty)) return "DIRTY_SCENE: Save or revert your scene changes deliberately first.";
            if (scenes.Any(x => !x.path.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(x.path))) return "SCENE_ASSET_UNAVAILABLE";
            return null;
        }
        internal static string EndGuard(PreviewSceneEntry[] current, UIPreviewState session, bool discard)
        {
            if (current == null || current.Length != 1 || current[0].path != session.scene_path) return "SCENE_SETUP_CHANGED: A non-session scene is open; it will not be closed automatically. Return to the preview scene or restore the original setup manually.";
            if (current[0].dirty && !discard) return "PREVIEW_HAS_UNSAVED_CHANGES: Save useful work outside the temporary folder, or explicitly end with discard_preview_changes=true.";
            return null;
        }
        internal static object Start(string prefabPaths, string sceneTemplate, bool play, int width, int height, string requestKey)
        {
            bool replacedScene = false;
            try
            {
                if (journalError != null) return Response.Error("PREVIEW_JOURNAL_UNAVAILABLE", new { detail = journalError, journal_path = JournalPath });
                string signature = JsonConvert.SerializeObject(new { prefabPaths, sceneTemplate, play, width, height });
                if (!string.IsNullOrEmpty(requestKey) && state?.request_key == requestKey)
                    return state.request_signature == signature ? Receipt() : Response.Error("PREVIEW_REQUEST_KEY_CONFLICT");
                if (state != null && !state.IsClosed) return Response.Error("PREVIEW_SESSION_ACTIVE", state);
                if (requestKey?.Length > 128) return Response.Error("INVALID_REQUEST_KEY");
                if (EditorApplication.isPlayingOrWillChangePlaymode || !EditorOperationService.Snapshot().ready || EditorOperationService.HasActive)
                    return Response.Error("EDITOR_NOT_IDLE", new { hint = "Finish prepare_editor targeting edit before starting a preview." });
                if (PrefabStageUtility.GetCurrentPrefabStage() != null) return Response.Error("PREFAB_STAGE_OPEN", new { hint = "Leave Prefab Mode deliberately before previewing." });
                var original = CurrentScenes();
                var sceneProblem = ValidateOriginalScenes(original);
                if (sceneProblem != null) return Response.Error("PREVIEW_SCENE_NOT_SAFE", new { detail = sceneProblem });
                string[] paths = string.IsNullOrWhiteSpace(prefabPaths) ? Array.Empty<string>() : JsonConvert.DeserializeObject<string[]>(prefabPaths);
                if (paths == null || paths.Length > 20) return Response.Error("INVALID_PREVIEW_PREFABS");
                foreach (var path in paths)
                {
                    UIAuditService.ValidateAssetPath(path);
                    if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) throw new ArgumentException("Not a prefab: " + path);
                }
                if (!string.IsNullOrEmpty(sceneTemplate))
                {
                    UIAuditService.ValidateAssetPath(sceneTemplate);
                    if (!sceneTemplate.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || AssetDatabase.LoadAssetAtPath<SceneAsset>(sceneTemplate) == null) throw new ArgumentException("scene_template must identify a saved scene.");
                }
                PreviewGameView.ValidateDimensions(width, height);
                foreach (var entry in original) entry.hash = Hash(entry.path);
                var id = Guid.NewGuid().ToString("N");
                state = new UIPreviewState { session_id = id, editor_session = editorSession, request_key = requestKey, request_signature = signature,
                    status = "setting_up", phase = "snapshot_saved", created_at = DateTime.UtcNow.ToString("O"),
                    owned_folder = AssetRoot + "/" + id, scene_path = AssetRoot + "/" + id + "/Preview.unity", size_label = "FP-" + id,
                    original_scenes = original, original_view = PreviewGameView.Capture(),
                    focused_window = ObjectIdHelper.GetSerializableId(EditorWindow.focusedWindow),
                    selection = Selection.objects.Select(x => GlobalObjectId.GetGlobalObjectIdSlow(x).ToString()).ToArray() };
                Save(); // The recovery journal must exist before any scene is closed.
                if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets", "FunplayMcpPreviews");
                AssetDatabase.CreateFolder(AssetRoot, id);
                if (!string.IsNullOrEmpty(sceneTemplate))
                {
                    if (!AssetDatabase.CopyAsset(sceneTemplate, state.scene_path)) throw new IOException("Could not copy preview scene template.");
                    replacedScene = true; EditorSceneManager.OpenScene(state.scene_path, OpenSceneMode.Single);
                }
                else
                {
                    replacedScene = true; EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
                state.phase = "creating_environment"; Save();
                var previewScene = SceneManager.GetActiveScene();
                GameObject first = null;
                foreach (var path in paths)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    Transform parent = null;
                    if (prefab.transform is RectTransform && prefab.GetComponent<Canvas>() == null)
                    {
                        var canvases = previewScene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<Canvas>(true)).Where(x => x.isRootCanvas).ToArray();
                        if (canvases.Length > 1) throw new InvalidOperationException("Preview scene has multiple root Canvases; use a scene_template with the prefab already placed explicitly.");
                        if (canvases.Length == 0)
                        {
                            var response = JObject.FromObject(UIConfigurationFunctions.CreateProjectUi("canvas", "PreviewCanvas"));
                            if (!(bool)response["success"]) throw new InvalidOperationException(response.ToString());
                            parent = ((GameObject)ObjectIdHelper.ToObject((string)response["data"]["object_id"])).transform;
                        }
                        else parent = canvases[0].transform;
                    }
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, previewScene);
                    if (parent != null) instance.transform.SetParent(parent, false);
                    if (first == null) first = instance;
                }
                if (!EditorSceneManager.SaveScene(previewScene, state.scene_path)) throw new IOException("Could not save owned preview scene.");
                state.scene_hash = Hash(state.scene_path);
                Selection.activeGameObject = first;
                PreviewGameView.Apply(state.original_view, width, height, state.size_label);
                state.applied_view = PreviewGameView.Capture();
                state.phase = "verifying"; state.status = "preparing"; settleUntil = EditorApplication.timeSinceStartup + .5; Save();
                if (play) StartOperation("play");
                return Receipt();
            }
            catch (Exception ex)
            {
                if (state?.status == "setting_up" || state?.status == "preparing")
                {
                    state.error = ex.GetBaseException().Message; state.status = "needs_attention";
                    // Synchronous setup failure: no user action can have intervened inside this call.
                    // Restore only the snapshot taken by this setup. On later recovery, use EndGuard.
                    if (replacedScene)
                    {
                        try { RestoreSceneSetup(); RestoreView(); RestoreSelection(); CleanupOwnedFiles(true); state.status = "closed_with_warnings"; state.warnings.Add("Preview setup failed and was rolled back: " + state.error); }
                        catch (Exception rollback) { state.warnings.Add("Rollback incomplete: " + rollback.GetBaseException().Message); }
                    }
                    try { Save(); } catch { }
                }
                return Response.Error("PREVIEW_START_FAILED", new { detail = ex.GetBaseException().Message, session = state });
            }
        }
        internal static object Get(string id, string requestKey = null)
        {
            if (id != null && state?.session_id != id) return Response.Error("PREVIEW_SESSION_NOT_FOUND");
            if (requestKey != null && state?.request_key != requestKey) return Response.Error("PREVIEW_SESSION_NOT_FOUND");
            return Receipt();
        }
        internal static object End(string id, bool discard)
        {
            if (state == null || state.session_id != id) return Response.Error("PREVIEW_SESSION_NOT_FOUND");
            if (state.IsClosed) return Receipt();
            if (journalError != null) return Response.Error("PREVIEW_JOURNAL_UNAVAILABLE");
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) return Response.Error("PREFAB_STAGE_OPEN");
            // Never exit an unrelated Play session or close unrelated scenes.
            if (!SameOriginalSetup())
            {
                var guard = EndGuard(CurrentScenes(), state, discard || EditorApplication.isPlaying);
                if (guard != null) return Response.Error("PREVIEW_RESTORE_BLOCKED", new { detail = guard, session = state });
            }
            if (state.status == "ending") return Receipt();
            if (EditorOperationService.HasActive && EditorOperationService.ActiveId != state.operation_id) return Response.Error("EDITOR_OPERATION_ACTIVE");
            state.discard_preview_changes = discard; state.status = "ending"; state.phase = "exit_play"; state.error = null; Save();
            if (state.operation_id != null) EditorOperationService.Cancel(state.operation_id);
            try { state.end_attempt++; StartOperation("edit"); return Receipt(); }
            catch (Exception ex)
            {
                state.status = "needs_attention"; state.error = ex.GetBaseException().Message; Save();
                return Response.Error("PREVIEW_END_FAILED", state);
            }
        }
        private static void StartOperation(string target)
        {
            var response = JObject.FromObject(EditorOperationService.Start(target, false, 120, "preview:" + state.session_id + ":" + target + ":" + state.end_attempt));
            if (!(bool)response["success"]) throw new InvalidOperationException(response.ToString());
            state.operation_id = (string)response["data"]["operation"]["operation_id"]; Save();
        }
        private static void Tick()
        {
            if (state == null || state.status != "preparing" && state.status != "ending") return;
            try
            {
                if (state.operation_id != null)
                {
                    var receipt = JObject.FromObject(EditorOperationService.Get(state.operation_id, null));
                    var operation = receipt["data"]?["operation"];
                    if (operation == null) throw new InvalidOperationException("Preview editor operation receipt was lost.");
                    string status = (string)operation["status"];
                    if (status == "running") return;
                    if (status != "ready") throw new InvalidOperationException("Preview editor operation " + status + ": " + operation["message"]);
                }
                if (!EditorOperationService.Snapshot().ready || EditorApplication.timeSinceStartup < settleUntil) return;
                if (state.status == "preparing")
                {
                    if (CurrentScenes().Length != 1 || CurrentScenes()[0].path != state.scene_path) throw new InvalidOperationException("Preview scene changed during preparation.");
                    state.applied_view = PreviewGameView.Capture(); state.status = "ready"; state.phase = "ready"; Save(); return;
                }
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (state.phase == "verify_view")
                {
                    if (state.view_restored && !PreviewGameView.Matches(state.original_view, PreviewGameView.Capture()))
                    {
                        state.view_restored = false;
                        state.warnings.Add("Game View readback changed after repaint; verify resolution/zoom manually. No further user settings were overwritten.");
                    }
                    state.status = state.warnings.Count == 0 ? "closed" : "closed_with_warnings"; state.phase = "finished"; Save(); return;
                }
                state.phase = "restoring"; Save();
                bool restoreSelection = Selection.objects.All(x => x is GameObject go ? go.scene.path == state.scene_path : x is Component c && c.gameObject.scene.path == state.scene_path);
                if (!SameOriginalSetup())
                {
                    var guard = EndGuard(CurrentScenes(), state, state.discard_preview_changes);
                    if (guard != null) throw new InvalidOperationException(guard);
                    if (!state.discard_preview_changes && File.Exists(state.scene_path) && state.scene_hash != Hash(state.scene_path))
                        throw new InvalidOperationException("PREVIEW_FILE_CHANGED: Keep useful changes outside the temporary folder or explicitly discard_preview_changes.");
                    RestoreSceneSetup();
                }
                else state.scenes_restored = true;
                RestoreView();
                if (restoreSelection) RestoreSelection(); else state.warnings.Add("Selection outside the preview was changed; preserved it.");
                CleanupOwnedFiles(state.discard_preview_changes);
                // A successful reflection write is not proof that Unity retained the setting.
                // Let the Game View repaint before accepting the restoration receipt.
                state.phase = "verify_view"; settleUntil = EditorApplication.timeSinceStartup + .5; Save();
            }
            catch (Exception ex)
            {
                state.status = "needs_attention"; state.error = ex.GetBaseException().Message;
                try { Save(); } catch { }
            }
        }
        private static bool SameOriginalSetup()
        {
            var current = CurrentScenes();
            return current.Length == state.original_scenes.Length && current.Select((x, i) =>
                x.path == state.original_scenes[i].path && x.loaded == state.original_scenes[i].loaded && x.active == state.original_scenes[i].active).All(x => x);
        }
        private static void RestoreSceneSetup()
        {
            foreach (var scene in state.original_scenes)
            {
                if (!File.Exists(scene.path)) throw new IOException("Original scene is missing: " + scene.path);
                if (scene.hash != Hash(scene.path)) state.warnings.Add("Original scene file changed during preview; loading latest file without overwriting it: " + scene.path);
            }
            EditorSceneManager.RestoreSceneManagerSetup(state.original_scenes.Select(x => new SceneSetup { path = x.path, isLoaded = x.loaded, isActive = x.active }).ToArray());
            state.scenes_restored = SameOriginalSetup();
            if (!state.scenes_restored) throw new InvalidOperationException("Scene setup readback did not match the original setup.");
            Save();
        }
        private static void RestoreView()
        {
            if (state.view_restore_attempted) return;
            var before = state.warnings.Count;
            PreviewGameView.Restore(state.original_view, state.applied_view, state.size_label, state.warnings);
            state.view_restore_attempted = true;
            state.view_restored = state.warnings.Count == before && PreviewGameView.Matches(state.original_view, PreviewGameView.Capture());
            if (!state.view_restored && state.warnings.Count == before) state.warnings.Add("Game View settings did not match the original snapshot after restoration.");
            Save();
        }
        private static void RestoreSelection()
        {
            var selection = new List<UnityEngine.Object>();
            foreach (var value in state.selection ?? Array.Empty<string>())
                if (GlobalObjectId.TryParse(value, out var global))
                {
                    var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global); if (obj != null) selection.Add(obj);
                    else state.warnings.Add("An originally selected object is no longer available.");
                }
            Selection.objects = selection.ToArray(); state.selection_restored = selection.Count == (state.selection?.Length ?? 0);
            var previousWindow = ObjectIdHelper.ToObject(state.focused_window) as EditorWindow;
            var focused = EditorWindow.focusedWindow;
            if (previousWindow != null && (focused == null || ObjectIdHelper.GetSerializableId(focused) == state.original_view?.window_id)) previousWindow.Focus();
        }
        private static void CleanupOwnedFiles(bool discard)
        {
            if (!ValidState(state)) throw new InvalidOperationException("Invalid preview ownership journal.");
            if (File.Exists(state.scene_path))
            {
                if (!discard && state.scene_hash != Hash(state.scene_path)) throw new InvalidOperationException("Owned preview file changed; preserved it.");
                if (!AssetDatabase.DeleteAsset(state.scene_path)) throw new IOException("Could not remove owned preview scene.");
            }
            // Delete only our own empty directory. Any project/user-created file is retained.
            if (Directory.Exists(state.owned_folder))
            {
                if (!Directory.EnumerateFileSystemEntries(state.owned_folder).Any()) AssetDatabase.DeleteAsset(state.owned_folder);
                else state.warnings.Add("Additional files in the preview folder were preserved: " + state.owned_folder);
            }
            state.assets_cleaned = !File.Exists(state.scene_path);
        }
        internal static string Hash(string path)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static object Receipt() => Response.Success("Preview session receipt; inspect status and restoration flags.", new
        {
            session = state, current_editor = EditorOperationService.CurrentEditor(), current_scenes = CurrentScenes(),
            journal_path = JournalPath, journal_error = journalError,
            boundaries = "Original scenes must be saved and clean. Only owned temporary scene/empty folder is removed. Business data, asset edits, network requests and save-game effects are not rolled back. A preview scene may run project lifecycle code."
        });
        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));
                var temp = JournalPath + ".tmp"; File.WriteAllText(temp, JsonConvert.SerializeObject(state, Formatting.Indented));
                if (File.Exists(JournalPath)) File.Replace(temp, JournalPath, null); else File.Move(temp, JournalPath);
            }
            catch (Exception ex) { journalError = ex.Message; throw; }
        }
    }
}
