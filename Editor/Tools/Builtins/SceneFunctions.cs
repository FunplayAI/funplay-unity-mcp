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
        public static string GetSceneInfo()
        {
            var activeScene = EditorSceneManager.GetActiveScene();
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                var rootObjects = scene.GetRootGameObjects();
                sb.AppendLine(scene == activeScene ? $"Scene: {scene.name} (active)" : $"Scene: {scene.name} (additive)");
                sb.AppendLine($"Path: {scene.path}");
                sb.AppendLine($"Is Dirty: {scene.isDirty}");
                sb.AppendLine($"Root Objects ({rootObjects.Length}):");

                foreach (var go in rootObjects)
                {
                    AppendHierarchy(sb, go.transform, 1, 3);
                }
            }

            return sb.ToString();
        }

        [Description("List all scenes in the project")]
        [ReadOnlyTool]
        public static string ListScenes()
        {
            var guids = AssetDatabase.FindAssets("t:Scene");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {guids.Length} scenes:");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                sb.AppendLine($"  - {path}");
            }

            return sb.ToString();
        }

        [Description("Enter play mode in the editor")]
        [SceneEditingTool]
        public static string EnterPlayMode()
        {
            if (EditorApplication.isPlaying)
                return "Already in play mode";

            EditorApplication.isPlaying = true;
            return "Entering play mode";
        }

        [Description("Exit play mode in the editor")]
        [SceneEditingTool]
        public static string ExitPlayMode()
        {
            if (!EditorApplication.isPlaying)
                return "Not in play mode";

            EditorApplication.isPlaying = false;
            return "Exiting play mode";
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

        private static void AppendHierarchy(System.Text.StringBuilder sb, Transform t, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            var indent = new string(' ', depth * 2);
            var components = t.GetComponents<Component>();
            var compNames = new System.Collections.Generic.List<string>();
            foreach (var c in components)
            {
                if (c != null && !(c is Transform))
                    compNames.Add(c.GetType().Name);
            }
            var compStr = compNames.Count > 0 ? $" [{string.Join(", ", compNames)}]" : "";
            sb.AppendLine($"{indent}- {t.name}{compStr}");

            for (int i = 0; i < t.childCount; i++)
            {
                AppendHierarchy(sb, t.GetChild(i), depth + 1, maxDepth);
            }
        }
    }
}
