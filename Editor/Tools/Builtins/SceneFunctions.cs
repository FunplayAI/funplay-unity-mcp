// Copyright (C) Funplay. Licensed under MIT.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

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
            var scenes = new System.Collections.Generic.List<object>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                var rootObjects = scene.GetRootGameObjects();
                var rootTree = new System.Collections.Generic.List<object>();
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
                    rootObjects = rootTree
                });
            }

            return Response.Success($"{scenes.Count} loaded scene(s)", new { count = scenes.Count, scenes });
        }

        [Description("List all scenes in the project")]
        [ReadOnlyTool]
        public static object ListScenes()
        {
            var guids = AssetDatabase.FindAssets("t:Scene");
            var scenes = new System.Collections.Generic.List<string>();

            foreach (var guid in guids)
            {
                scenes.Add(AssetDatabase.GUIDToAssetPath(guid));
            }

            return Response.Success($"Found {scenes.Count} scenes", new { count = scenes.Count, scenes });
        }

        [Description("Additively load a scene alongside the currently loaded scene(s) without unloading them.")]
        [SceneEditingTool]
        public static object LoadSceneAdditive(
            [ToolParam("Path to the scene asset (e.g. 'Assets/Scenes/Main.unity')")] string path)
        {
            if (!System.IO.File.Exists(path))
                return Response.Error("SCENE_FILE_NOT_FOUND", new { path });

            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            return Response.Success($"Additively loaded scene '{scene.name}'", new
            {
                loaded = new { name = scene.name, path = scene.path },
                loadedSceneCount = SceneManager.sceneCount
            });
        }

        [Description("Unload (close and remove) an additively loaded scene by name or path. " +
                     "Refuses to unload the last remaining loaded scene. Refuses to unload a scene with " +
                     "unsaved changes unless force='true' (which permanently discards them).")]
        [SceneEditingTool]
        public static object UnloadScene(
            [ToolParam("Name or path of the loaded scene to unload")] string name_or_path,
            [ToolParam("Set 'true' to discard unsaved changes and unload anyway (default false)", Required = false)] string force = null)
        {
            // Count only LOADED scenes: SceneManager.sceneCount also includes scenes present in the
            // Hierarchy but unloaded (isLoaded=false, e.g. right-click "Unload Scene"). Guarding on the
            // raw count would let this pass while only one loaded scene remains and then close it.
            int loadedCount = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isLoaded) loadedCount++;
            if (loadedCount <= 1)
                return Response.Error("CANNOT_UNLOAD_LAST_SCENE", new { loadedSceneCount = loadedCount });

            Scene target = default;
            bool found = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue; // only a currently-loaded scene can be unloaded
                if (scene.name == name_or_path || scene.path == name_or_path)
                {
                    target = scene;
                    found = true;
                    break;
                }
            }

            if (!found)
                return Response.Error("SCENE_NOT_LOADED", new { name_or_path });

            // Guard against silently discarding unsaved edits: CloseScene(scene, true) drops them with
            // no prompt, unlike the editor UI. Require an explicit force to proceed on a dirty scene.
            bool forced = force != null && (force.ToLowerInvariant() == "true" || force == "1");
            bool wasDirty = target.isDirty;
            if (wasDirty && !forced)
                return Response.Error("SCENE_HAS_UNSAVED_CHANGES", new
                {
                    name_or_path,
                    hint = "Scene has unsaved changes. Save first (save_all_scenes) or pass force='true' to discard them and unload."
                });

            var unloadedName = target.name;
            var unloadedPath = target.path;
            EditorSceneManager.CloseScene(target, true);
            return Response.Success($"Unloaded scene '{unloadedName}'", new
            {
                unloaded = new { name = unloadedName, path = unloadedPath },
                discardedUnsavedChanges = wasDirty,
                loadedSceneCount = loadedCount - 1
            });
        }

        [Description("List all loaded scenes that have unsaved changes (isDirty).")]
        [ReadOnlyTool]
        public static object ListDirtyScenes()
        {
            var dirty = CollectDirtyScenes();
            return Response.Success($"{dirty.Count} dirty scene(s)", new { count = dirty.Count, scenes = dirty });
        }

        [Description("Save all open (loaded) scenes that have unsaved changes.")]
        [SceneEditingTool]
        public static object SaveAllScenes()
        {
            var toSave = CollectDirtyScenes();

            bool saved = EditorSceneManager.SaveOpenScenes();
            if (!saved)
                return Response.Error("SAVE_OPEN_SCENES_FAILED", new { attempted = toSave });

            return Response.Success($"Saved {toSave.Count} scene(s)", new { count = toSave.Count, scenes = toSave });
        }

        [Description("Enter play mode in the editor")]
        [SceneEditingTool]
        public static object EnterPlayMode()
        {
            if (EditorApplication.isPlaying)
                return Response.Success("Already in play mode", new { wasPlaying = true, domain_reload_expected = false });

            EditorApplication.isPlaying = true;
            return Response.Success("Entering play mode", new
            {
                wasPlaying = false,
                domain_reload_expected = true,
                next = "poll get_reload_recovery_status until ready"
            });
        }

        [Description("Exit play mode in the editor")]
        [SceneEditingTool]
        public static object ExitPlayMode()
        {
            if (!EditorApplication.isPlaying)
                return Response.Success("Not in play mode", new { wasPlaying = false, domain_reload_expected = false });

            EditorApplication.isPlaying = false;
            return Response.Success("Exiting play mode", new
            {
                wasPlaying = true,
                domain_reload_expected = true,
                next = "poll get_reload_recovery_status until ready"
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

        private static System.Collections.Generic.List<object> CollectDirtyScenes()
        {
            var dirty = new System.Collections.Generic.List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                if (scene.isDirty)
                    dirty.Add(new { name = scene.name, path = scene.path });
            }
            return dirty;
        }

        private static object BuildHierarchy(Transform t, int depth, int maxDepth)
        {
            var components = t.GetComponents<Component>();
            var compNames = new System.Collections.Generic.List<string>();
            foreach (var c in components)
            {
                if (c != null && !(c is Transform))
                    compNames.Add(c.GetType().Name);
            }

            var children = new System.Collections.Generic.List<object>();
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
