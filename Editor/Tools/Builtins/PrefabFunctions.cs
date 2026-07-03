// Copyright (C) Funplay. Licensed under MIT.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Prefab")]
    internal static class PrefabFunctions
    {
        [Description("Create a prefab from a GameObject in the scene")]
        [SceneEditingTool]
        public static string CreatePrefab(
            [ToolParam("Name of the GameObject to convert")] string game_object_name,
            [ToolParam("Path to save prefab (e.g. 'Assets/Prefabs/')", Required = false)] string save_path = "Assets/Prefabs/")
        {
            var go = GameObject.Find(game_object_name);
            if (go == null)
                return ToolResultFormatter.Error("GAME_OBJECT_NOT_FOUND", new { game_object_name });

            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

            var fullPath = $"{save_path}{game_object_name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, fullPath, InteractionMode.UserAction);
            return prefab != null
                ? $"Created prefab at {fullPath}"
                : ToolResultFormatter.Error("PREFAB_CREATE_FAILED", new { path = fullPath });
        }

        [Description("Instantiate a prefab in the scene")]
        [SceneEditingTool]
        public static string InstantiatePrefab(
            [ToolParam("Path to the prefab asset")] string prefab_path,
            [ToolParam("Name for the instance", Required = false)] string name = null,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = "0,0,0")
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null)
                return ToolResultFormatter.Error("PREFAB_NOT_FOUND", new { prefab_path });

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
                return ToolResultFormatter.Error("PREFAB_INSTANTIATE_FAILED", new { prefab_path });

            Undo.RegisterCreatedObjectUndo(instance, "Instantiate prefab");

            if (!string.IsNullOrEmpty(name))
                instance.name = name;

            instance.transform.position = ParseVector3(position);
            Selection.activeGameObject = instance;

            return $"Instantiated prefab '{prefab.name}' as '{instance.name}' at {instance.transform.position}";
        }

        [Description("Unpack a prefab instance in the scene")]
        [SceneEditingTool]
        public static string UnpackPrefab(
            [ToolParam("Name of the prefab instance")] string game_object_name,
            [ToolParam("Unpack mode: 'completely' or 'outermost'", Required = false)] string mode = "completely")
        {
            var go = GameObject.Find(game_object_name);
            if (go == null)
                return ToolResultFormatter.Error("GAME_OBJECT_NOT_FOUND", new { game_object_name });

            if (!PrefabUtility.IsPartOfAnyPrefab(go))
                return ToolResultFormatter.Error("NOT_PREFAB_INSTANCE", new { game_object_name });

            var unpackMode = mode == "outermost"
                ? PrefabUnpackMode.OutermostRoot
                : PrefabUnpackMode.Completely;

            PrefabUtility.UnpackPrefabInstance(go, unpackMode, InteractionMode.UserAction);
            return $"Unpacked prefab '{game_object_name}' ({mode})";
        }

        [Description("Open a prefab asset in Prefab Mode (an isolated prefab stage) for editing its contents directly, " +
                     "without instantiating it into a scene. While the stage is open, hierarchy/component tools and " +
                     "execute_code operate on the prefab contents. Persist edits with save_prefab_stage, then " +
                     "close_prefab_stage when done. If another prefab stage is already open with unsaved changes, " +
                     "this returns an error instead of silently discarding them.")]
        public static string OpenPrefabStage(
            [ToolParam("Path to the prefab asset (e.g. 'Assets/Prefabs/Item.prefab')")] string prefab_path)
        {
            if (string.IsNullOrEmpty(prefab_path))
                return ToolResultFormatter.Error("INVALID_ARGUMENT", new { prefab_path });

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null)
                return ToolResultFormatter.Error("PREFAB_NOT_FOUND", new { prefab_path });

            var current = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (current != null)
            {
                if (current.assetPath == prefab_path)
                    return FormatPrefabStageStatus(current, "already open");

                if (current.scene.isDirty)
                    return ToolResultFormatter.Error("ANOTHER_STAGE_DIRTY", new
                    {
                        open_stage = current.assetPath,
                        hint = "Call save_prefab_stage or close_prefab_stage(save=false) first."
                    });
            }

            var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(prefab_path);
            if (stage == null)
                return ToolResultFormatter.Error("PREFAB_STAGE_OPEN_FAILED", new { prefab_path });

            return FormatPrefabStageStatus(stage, "opened");
        }

        private static string FormatPrefabStageStatus(
            UnityEditor.SceneManagement.PrefabStage stage,
            string status)
        {
            var root = stage.prefabContentsRoot;
            return $"Prefab stage {status}: {stage.assetPath}\n" +
                   $"Root: {root.name} (instanceId={root.GetInstanceID()}), children: {root.transform.childCount}";
        }

        [Description("Save the currently open prefab stage back to its .prefab asset, without closing the stage. " +
                     "Use after editing prefab contents via component tools or execute_code inside an open prefab stage.")]
        public static string SavePrefabStage()
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return ToolResultFormatter.Error("NO_PREFAB_STAGE_OPEN", new { hint = "Call open_prefab_stage first." });

            var saved = PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath, out var success);
            if (!success || saved == null)
                return ToolResultFormatter.Error("PREFAB_STAGE_SAVE_FAILED", new { stage.assetPath });

            stage.ClearDirtiness();
            return $"Prefab stage saved: {stage.assetPath}";
        }

        [Description("Close the currently open prefab stage and return to the main stage. By default pending edits are " +
                     "saved first; pass save=false to DISCARD unsaved edits. Never shows a blocking save dialog: " +
                     "discarding clears the stage's dirty flag before closing.")]
        public static string ClosePrefabStage(
            [ToolParam("Save pending edits before closing (false discards them)", Required = false)] bool save = true)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return ToolResultFormatter.Error("NO_PREFAB_STAGE_OPEN", new { hint = "Nothing to close." });

            var assetPath = stage.assetPath;
            var wasDirty = stage.scene.isDirty;

            if (save && wasDirty)
            {
                PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, assetPath, out var success);
                if (!success)
                    return ToolResultFormatter.Error("PREFAB_STAGE_SAVE_FAILED", new { assetPath });
            }

            // Clear the dirty flag before leaving the stage so Unity never pops a modal
            // "save changes?" dialog (a modal would block the MCP request indefinitely).
            stage.ClearDirtiness();
            UnityEditor.SceneManagement.StageUtility.GoToMainStage();

            var action = !wasDirty ? "no pending edits" : (save ? "edits saved" : "edits discarded");
            return $"Prefab stage closed: {assetPath} ({action})";
        }

        private static Vector3 ParseVector3(string value)
        {
            if (string.IsNullOrEmpty(value)) return Vector3.zero;
            value = value.Trim('(', ')', ' ');
            var p = value.Split(',');
            if (p.Length >= 3)
                return new Vector3(
                    float.Parse(p[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(p[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            return Vector3.zero;
        }
    }
}
