// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_6000_4_OR_NEWER
using SerializableSceneHandle = System.UInt64;
#else
using SerializableSceneHandle = System.Int32;
#endif

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Scene")]
    internal static class SceneFunctions
    {
        [Description("Save the current scene")]
        [SceneEditingTool]
        public static string SaveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            bool saved = EditorSceneManager.SaveScene(scene);
            return saved ? $"Saved scene '{scene.name}'" : ToolResultFormatter.Error("SCENE_SAVE_FAILED", new { scene = scene.name });
        }

        [Description("Open an existing scene by path")]
        [SceneEditingTool]
        public static string OpenScene(
            [ToolParam("Path to the scene asset (e.g. 'Assets/Scenes/Main.unity')")] string path)
        {
            if (!System.IO.File.Exists(path))
                return ToolResultFormatter.Error("SCENE_FILE_NOT_FOUND", new { path });

            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            EditorSceneManager.OpenScene(path);
            return $"Opened scene: {path}";
        }

        [Description("Create a new empty scene")]
        [SceneEditingTool]
        public static string CreateNewScene(
            [ToolParam("Name for the new scene")] string name,
            [ToolParam("Path to save (e.g. 'Assets/Scenes/')", Required = false)] string save_path = "Assets/Scenes/")
        {
            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            if (!System.IO.Directory.Exists(save_path))
                System.IO.Directory.CreateDirectory(save_path);

            var fullPath = $"{save_path}{name}.unity";
            EditorSceneManager.SaveScene(scene, fullPath);
            return $"Created and saved new scene: {fullPath}";
        }

        [Description("Get information about every loaded scene (the active scene plus any additively loaded ones), " +
                     "including path, dirty state, and a shallow root-object hierarchy per scene.")]
        [ReadOnlyTool]
        public static object GetSceneInfo()
        {
            var activeScene = EditorSceneManager.GetActiveScene();
            var scenes = new List<object>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                var rootObjects = scene.GetRootGameObjects();
                var rootTree = new List<object>();
                foreach (var go in rootObjects)
                {
                    rootTree.Add(BuildHierarchy(go.transform, 1, 3));
                }

                scenes.Add(new
                {
                    name = scene.name,
                    path = scene.path,
                    active = scene == activeScene,
                    isDirty = scene.isDirty,
                    isLoaded = scene.isLoaded,
                    handle = GetSerializableSceneHandle(scene),
                    buildIndex = scene.buildIndex,
                    rootCount = rootObjects.Length,
                    rootObjects = rootTree
                });
            }

            return Response.Success($"{scenes.Count} loaded scene(s)", new { count = scenes.Count, scenes });
        }

        [Description("List all scenes in the project")]
        [ReadOnlyTool]
        public static object ListScenes()
        {
            var guids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            var scenes = new List<string>();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    scenes.Add(path);
            }
            scenes.Sort(StringComparer.Ordinal);

            return Response.Success($"Found {scenes.Count} scenes", new { count = scenes.Count, scenes });
        }

        [Description("Additively load a scene alongside the currently loaded scene(s) without unloading them.")]
        [SceneEditingTool]
        public static object LoadSceneAdditive(
            [ToolParam("Path to the scene asset (e.g. 'Assets/Scenes/Main.unity')")] string path)
        {
            var blocker = GetSceneMutationBlocker();
            if (blocker != null)
                return blocker;

            if (!TryResolveSceneAssetPath(path, out var assetPath, out var pathError))
                return pathError;

            var existing = FindOpenSceneByPath(assetPath);
            if (existing.IsValid() && existing.isLoaded)
                return BuildLoadedSceneResponse(existing, true);

            if (existing.IsValid() && !EditorSceneManager.CloseScene(existing, true))
            {
                return Response.Error("SCENE_PLACEHOLDER_REMOVE_FAILED", new
                {
                    path = assetPath,
                    hint = "The scene is present in the Hierarchy but unloaded, and Unity could not remove that placeholder before loading it."
                });
            }

            try
            {
                var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
                if (!scene.IsValid() || !scene.isLoaded)
                    return Response.Error("SCENE_LOAD_FAILED", new { path = assetPath });

                return BuildLoadedSceneResponse(scene, false);
            }
            catch (Exception ex)
            {
                return Response.Error("SCENE_LOAD_FAILED", new
                {
                    path = assetPath,
                    message = ex.Message,
                    exception_type = ex.GetType().FullName
                });
            }
        }

        [Description("Unload (close and remove) an additively loaded scene by name or path. " +
                     "Refuses to unload the last remaining loaded scene. Refuses to unload a scene with " +
                     "unsaved changes unless force=true (which permanently discards them). If multiple " +
                     "loaded scenes share a name, pass the exact Assets/... path.")]
        [SceneEditingTool]
        public static object UnloadScene(
            [ToolParam("Name or path of the loaded scene to unload")] string name_or_path,
            [ToolParam("Discard unsaved changes and unload anyway (default false)", Required = false)] bool force = false)
        {
            var blocker = GetSceneMutationBlocker();
            if (blocker != null)
                return blocker;

            if (string.IsNullOrWhiteSpace(name_or_path))
                return Response.Error("SCENE_NAME_OR_PATH_REQUIRED");

            if (!TryResolveLoadedScene(name_or_path, out var target, out var resolveError))
                return resolveError;

            // SceneManager.sceneCount includes AdditiveWithoutLoading placeholders, so guard and
            // response counts must use only scenes whose isLoaded flag is true.
            var loadedCount = CountLoadedScenes();
            if (loadedCount <= 1)
                return Response.Error("CANNOT_UNLOAD_LAST_SCENE", new
                {
                    name = target.name,
                    path = target.path,
                    loadedSceneCount = loadedCount
                });

            bool wasDirty = target.isDirty;
            if (wasDirty && !force)
                return Response.Error("SCENE_HAS_UNSAVED_CHANGES", new
                {
                    name = target.name,
                    path = target.path,
                    hint = "Scene has unsaved changes. Review list_dirty_scenes and save the intended scenes, or pass force=true to discard this scene."
                });

            var unloadedName = target.name;
            var unloadedPath = target.path;
            var unloadedHandle = GetSerializableSceneHandle(target);
            bool closed;
            try
            {
                closed = EditorSceneManager.CloseScene(target, true);
            }
            catch (Exception ex)
            {
                return Response.Error("SCENE_UNLOAD_FAILED", new
                {
                    name = unloadedName,
                    path = unloadedPath,
                    message = ex.Message,
                    exception_type = ex.GetType().FullName
                });
            }

            if (!closed || IsLoadedSceneHandle(unloadedHandle))
                return Response.Error("SCENE_UNLOAD_FAILED", new { name = unloadedName, path = unloadedPath });

            var remainingCount = CountLoadedScenes();
            var activeScene = SceneManager.GetActiveScene();
            return Response.Success($"Unloaded scene '{unloadedName}'", new
            {
                unloaded = new { name = unloadedName, path = unloadedPath, handle = unloadedHandle },
                discardedUnsavedChanges = wasDirty,
                loadedSceneCount = remainingCount,
                activeScene = activeScene.IsValid()
                    ? new
                    {
                        name = activeScene.name,
                        path = activeScene.path,
                        handle = GetSerializableSceneHandle(activeScene)
                    }
                    : null
            });
        }

        [Description("List all loaded scenes that have unsaved changes (isDirty).")]
        [ReadOnlyTool]
        public static object ListDirtyScenes()
        {
            var dirty = CollectDirtySceneSummaries();
            return Response.Success($"{dirty.Count} dirty scene(s)", new { count = dirty.Count, scenes = dirty });
        }

        [Description("Save all open (loaded) scenes that have unsaved changes. If more than one scene " +
                     "is dirty, confirm_all=true is required so unrelated changes are not saved accidentally.")]
        [SceneEditingTool]
        public static object SaveAllScenes(
            [ToolParam("Required when more than one loaded scene is dirty. Confirms every reported dirty scene should be saved.", Required = false)] bool confirm_all = false)
        {
            var blocker = GetSceneMutationBlocker();
            if (blocker != null)
                return blocker;

            var dirtyScenes = CollectDirtyScenes();
            var attempted = BuildSceneSummaries(dirtyScenes);
            if (dirtyScenes.Count == 0)
                return Response.Success("No dirty scenes to save", new { count = 0, scenes = attempted });

            var untitled = new List<object>();
            foreach (var scene in dirtyScenes)
            {
                if (string.IsNullOrEmpty(scene.path))
                    untitled.Add(BuildSceneSummary(scene));
            }
            if (untitled.Count > 0)
            {
                return Response.Error("UNTITLED_SCENE_REQUIRES_PATH", new
                {
                    scenes = untitled,
                    hint = "save_all_scenes never opens a Save As dialog. Save or create the untitled scene with an explicit Assets/... path first."
                });
            }

            if (dirtyScenes.Count > 1 && !confirm_all)
            {
                return Response.Error("MULTIPLE_DIRTY_SCENES_CONFIRMATION_REQUIRED", new
                {
                    count = attempted.Count,
                    scenes = attempted,
                    hint = "Review the dirty scene list, then pass confirm_all=true only if every listed scene should be saved."
                });
            }

            bool saved;
            try
            {
                saved = EditorSceneManager.SaveOpenScenes();
            }
            catch (Exception ex)
            {
                return Response.Error("SAVE_OPEN_SCENES_FAILED", new
                {
                    attempted,
                    message = ex.Message,
                    exception_type = ex.GetType().FullName
                });
            }

            var remaining = CollectDirtySceneSummaries();
            if (!saved || remaining.Count > 0)
                return Response.Error("SAVE_OPEN_SCENES_FAILED", new { attempted, remaining });

            var savedScenes = BuildSceneSummaries(dirtyScenes);
            return Response.Success($"Saved {savedScenes.Count} scene(s)", new
            {
                count = savedScenes.Count,
                scenes = savedScenes,
                remainingDirtyCount = 0
            });
        }

        [Description("Enter play mode in the editor")]
        [SceneEditingTool]
        public static object EnterPlayMode()
        {
            var domainReloadExpected = IsDomainReloadExpectedForPlayModeTransition(
                EditorSettings.enterPlayModeOptionsEnabled,
                EditorSettings.enterPlayModeOptions);

            if (EditorApplication.isPlaying)
                return Response.Success("Already in play mode", new
                {
                    wasPlaying = true,
                    transitionRequested = false,
                    domain_reload_expected = false
                });

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return Response.Success("Play Mode entry is already in progress", new
                {
                    wasPlaying = false,
                    transitionRequested = false,
                    domain_reload_expected = domainReloadExpected,
                    next = BuildPlayModeNextStep(true, domainReloadExpected)
                });
            }

            EditorApplication.isPlaying = true;
            return Response.Success("Entering play mode", new
            {
                wasPlaying = false,
                transitionRequested = true,
                domain_reload_expected = domainReloadExpected,
                next = BuildPlayModeNextStep(true, domainReloadExpected)
            });
        }

        [Description("Exit play mode in the editor")]
        [SceneEditingTool]
        public static object ExitPlayMode()
        {
            if (!EditorApplication.isPlaying)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    return Response.Error("PLAY_MODE_TRANSITION_IN_PROGRESS", new
                    {
                        hint = "Unity is currently entering Play Mode. Wait for the transition to finish, then call exit_play_mode again."
                    });
                }

                return Response.Success("Not in play mode", new
                {
                    wasPlaying = false,
                    transitionRequested = false,
                    domain_reload_expected = false
                });
            }

            var domainReloadExpected = IsDomainReloadExpectedForPlayModeTransition(
                EditorSettings.enterPlayModeOptionsEnabled,
                EditorSettings.enterPlayModeOptions);
            EditorApplication.isPlaying = false;
            return Response.Success("Exiting play mode", new
            {
                wasPlaying = true,
                transitionRequested = true,
                domain_reload_expected = domainReloadExpected,
                next = BuildPlayModeNextStep(false, domainReloadExpected)
            });
        }

        [Description("Set the game time scale. Use 0 to pause, 1 for normal speed, " +
                     "2 for double speed, etc. Useful for testing or slow-motion debugging.")]
        [SceneEditingTool]
        public static string SetTimeScale(
            [ToolParam("Time scale value (0=paused, 1=normal, 2=double speed, etc.)")] float scale)
        {
            if (scale < 0f)
                return ToolResultFormatter.Error("INVALID_TIME_SCALE", new { scale, min = 0f });
            if (scale > 100f)
                return ToolResultFormatter.Error("INVALID_TIME_SCALE", new { scale, max = 100f });

            float previousScale = UnityEngine.Time.timeScale;
            UnityEngine.Time.timeScale = scale;
            return $"Time.timeScale changed from {previousScale:F2} to {scale:F2}";
        }

        [Description("Get the current time scale and time information")]
        [ReadOnlyTool]
        public static string GetTimeScale()
        {
            return $"Time.timeScale={UnityEngine.Time.timeScale:F2}, Time.time={UnityEngine.Time.time:F2}, " +
                   $"Time.deltaTime={UnityEngine.Time.deltaTime:F4}, Time.fixedDeltaTime={UnityEngine.Time.fixedDeltaTime:F4}";
        }

        internal static bool IsDomainReloadExpectedForPlayModeTransition(
            bool optionsEnabled,
            EnterPlayModeOptions options)
        {
            return !optionsEnabled || (options & EnterPlayModeOptions.DisableDomainReload) == 0;
        }

        private static string BuildPlayModeNextStep(bool entering, bool domainReloadExpected)
        {
            var target = entering ? "isPlaying=true" : "isPlaying=false";
            return domainReloadExpected
                ? $"Wait for the MCP backend to reconnect after domain reload, then poll get_editor_state until {target}."
                : $"The MCP backend should remain connected; poll get_editor_state until {target}.";
        }

        private static object GetSceneMutationBlocker()
        {
            if (EditorApplication.isPlaying)
            {
                return Response.Error("PLAY_MODE_ACTIVE", new
                {
                    hint = "Exit Play Mode before loading, unloading, or saving editor scenes."
                });
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return Response.Error("PLAY_MODE_TRANSITION_IN_PROGRESS", new
                {
                    hint = "Wait for the Play Mode transition to finish before changing editor scenes."
                });
            }

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                return Response.Error("PREFAB_STAGE_ACTIVE", new
                {
                    prefabPath = prefabStage.assetPath,
                    hint = "Close Prefab Mode before loading, unloading, or saving project scenes."
                });
            }

            return null;
        }

        private static bool TryResolveSceneAssetPath(
            string requestedPath,
            out string assetPath,
            out object error)
        {
            assetPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                error = Response.Error("SCENE_PATH_REQUIRED");
                return false;
            }

            var normalized = requestedPath.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(normalized) ||
                !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                !normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                ContainsTraversalSegment(normalized))
            {
                error = Response.Error("INVALID_SCENE_PATH", new
                {
                    path = requestedPath,
                    expected = "A project-relative scene asset path under Assets/ ending in .unity"
                });
                return false;
            }

            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(normalized);
            if (sceneAsset == null)
            {
                error = Response.Error("SCENE_ASSET_NOT_FOUND", new { path = normalized });
                return false;
            }

            assetPath = AssetDatabase.GetAssetPath(sceneAsset);
            return true;
        }

        private static bool ContainsTraversalSegment(string path)
        {
            foreach (var segment in path.Split('/'))
            {
                if (segment == "." || segment == "..")
                    return true;
            }
            return false;
        }

        private static object BuildLoadedSceneResponse(Scene scene, bool alreadyLoaded)
        {
            var activeScene = SceneManager.GetActiveScene();
            return Response.Success(
                alreadyLoaded
                    ? $"Scene '{scene.name}' is already loaded"
                    : $"Additively loaded scene '{scene.name}'",
                new
                {
                    loaded = new
                    {
                        name = scene.name,
                        path = scene.path,
                        handle = GetSerializableSceneHandle(scene),
                        active = scene == activeScene,
                        alreadyLoaded
                    },
                    loadedSceneCount = CountLoadedScenes()
                });
        }

        private static Scene FindOpenSceneByPath(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase))
                    return scene;
            }
            return default;
        }

        private static bool TryResolveLoadedScene(
            string nameOrPath,
            out Scene target,
            out object error)
        {
            target = default;
            error = null;
            var query = nameOrPath.Trim().Replace('\\', '/');
            var pathLike = query.IndexOf('/') >= 0 ||
                           query.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
            var matches = new List<Scene>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;

                var match = pathLike
                    ? string.Equals(scene.path, query, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(scene.name, query, StringComparison.Ordinal);
                if (match)
                    matches.Add(scene);
            }

            if (matches.Count == 0)
            {
                error = Response.Error("SCENE_NOT_LOADED", new { name_or_path = nameOrPath });
                return false;
            }

            if (matches.Count > 1)
            {
                error = Response.Error("SCENE_NAME_AMBIGUOUS", new
                {
                    name = query,
                    matches = BuildSceneSummaries(matches),
                    hint = "Pass the exact Assets/... scene path."
                });
                return false;
            }

            target = matches[0];
            return true;
        }

        private static int CountLoadedScenes()
        {
            int count = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isLoaded)
                    count++;
            }
            return count;
        }

        private static SerializableSceneHandle GetSerializableSceneHandle(Scene scene)
        {
#if UNITY_6000_4_OR_NEWER
            return scene.handle.GetRawData();
#else
            return scene.handle;
#endif
        }

        private static bool IsLoadedSceneHandle(SerializableSceneHandle handle)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && GetSerializableSceneHandle(scene) == handle)
                    return true;
            }
            return false;
        }

        private static List<Scene> CollectDirtyScenes()
        {
            var dirty = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.isDirty)
                    dirty.Add(scene);
            }
            return dirty;
        }

        private static List<object> CollectDirtySceneSummaries()
        {
            return BuildSceneSummaries(CollectDirtyScenes());
        }

        private static List<object> BuildSceneSummaries(IEnumerable<Scene> scenes)
        {
            var summaries = new List<object>();
            foreach (var scene in scenes)
                summaries.Add(BuildSceneSummary(scene));
            return summaries;
        }

        private static object BuildSceneSummary(Scene scene)
        {
            var activeScene = SceneManager.GetActiveScene();
            return new
            {
                name = scene.name,
                path = scene.path,
                handle = GetSerializableSceneHandle(scene),
                active = scene == activeScene,
                isDirty = scene.isDirty
            };
        }

        private static object BuildHierarchy(Transform t, int depth, int maxDepth)
        {
            var components = t.GetComponents<Component>();
            var compNames = new List<string>();
            foreach (var c in components)
            {
                if (c != null && !(c is Transform))
                    compNames.Add(c.GetType().Name);
            }

            var children = new List<object>();
            if (depth < maxDepth)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    children.Add(BuildHierarchy(t.GetChild(i), depth + 1, maxDepth));
                }
            }

            return new { name = t.name, instanceId = ObjectIdHelper.GetSerializableId(t.gameObject), components = compNames, children };
        }
    }
}
