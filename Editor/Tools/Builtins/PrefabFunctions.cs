// Copyright (C) GameBooom. Licensed under GPLv3.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameBooom.Editor.Tools.Builtins
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
                return $"Error: GameObject '{game_object_name}' not found";

            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

            var fullPath = $"{save_path}{game_object_name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, fullPath, InteractionMode.UserAction);
            return prefab != null
                ? $"Created prefab at {fullPath}"
                : "Error: Failed to create prefab";
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
                return $"Error: Prefab not found at '{prefab_path}'";

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
                return "Error: Failed to instantiate prefab";

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
                return $"Error: GameObject '{game_object_name}' not found";

            if (!PrefabUtility.IsPartOfAnyPrefab(go))
                return $"Error: '{game_object_name}' is not a prefab instance";

            var unpackMode = mode == "outermost"
                ? PrefabUnpackMode.OutermostRoot
                : PrefabUnpackMode.Completely;

            PrefabUtility.UnpackPrefabInstance(go, unpackMode, InteractionMode.UserAction);
            return $"Unpacked prefab '{game_object_name}' ({mode})";
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
