// Copyright (C) GameBooom. Licensed under GPLv3.
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("GameObject")]
    internal static class GameObjectFunctions
    {
        [Description("Create a new empty GameObject in the scene")]
        [SceneEditingTool]
        public static string CreateGameObject(
            [ToolParam("Name of the new GameObject")] string name,
            [ToolParam("Parent GameObject name (optional)", Required = false)] string parent_name = null)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

            if (!string.IsNullOrEmpty(parent_name))
            {
                var parentGo = FindSceneGameObject(parent_name);
                if (parentGo != null)
                {
                    Undo.SetTransformParent(go.transform, parentGo.transform, $"Set parent of {name}");
                }
            }

            Selection.activeGameObject = go;
            return $"Created GameObject '{name}' (InstanceID: {go.GetInstanceID()})";
        }

        [Description("Create a primitive GameObject (Cube, Sphere, Capsule, Cylinder, Plane, Quad)")]
        [SceneEditingTool]
        public static string CreatePrimitive(
            [ToolParam("Name of the new object")] string name,
            [ToolParam("Primitive type: Cube, Sphere, Capsule, Cylinder, Plane, Quad")] string primitive_type,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = "0,0,0",
            [ToolParam("Scale as 'x,y,z'", Required = false)] string scale = "1,1,1")
        {
            PrimitiveType type;
            switch (primitive_type.ToLowerInvariant())
            {
                case "cube": type = PrimitiveType.Cube; break;
                case "sphere": type = PrimitiveType.Sphere; break;
                case "capsule": type = PrimitiveType.Capsule; break;
                case "cylinder": type = PrimitiveType.Cylinder; break;
                case "plane": type = PrimitiveType.Plane; break;
                case "quad": type = PrimitiveType.Quad; break;
                default: return $"Error: Unknown primitive type '{primitive_type}'";
            }

            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

            go.transform.position = ParseVector3(position);
            go.transform.localScale = ParseVector3(scale);

            Selection.activeGameObject = go;
            return $"Created {primitive_type} '{name}' at {go.transform.position}";
        }

        [Description("Delete a GameObject from the scene by name")]
        [SceneEditingTool]
        public static string DeleteGameObject(
            [ToolParam("Name of the GameObject to delete")] string name)
        {
            var go = FindSceneGameObject(name);
            if (go == null)
                return $"Error: GameObject '{name}' not found";

            Undo.DestroyObjectImmediate(go);
            return $"Deleted GameObject '{name}'";
        }

        [Description("Duplicate a GameObject")]
        [SceneEditingTool]
        public static string DuplicateGameObject(
            [ToolParam("Name of the GameObject to duplicate")] string name,
            [ToolParam("Name for the duplicate", Required = false)] string new_name = null)
        {
            var go = FindSceneGameObject(name);
            if (go == null)
                return $"Error: GameObject '{name}' not found";

            var duplicate = Object.Instantiate(go);
            Undo.RegisterCreatedObjectUndo(duplicate, $"Duplicate {name}");

            if (!string.IsNullOrEmpty(new_name))
                duplicate.name = new_name;
            else
                duplicate.name = name + " (Copy)";

            Selection.activeGameObject = duplicate;
            return $"Duplicated '{name}' as '{duplicate.name}'";
        }

        [Description("Rename a GameObject")]
        [SceneEditingTool]
        public static string RenameGameObject(
            [ToolParam("Current name of the GameObject")] string name,
            [ToolParam("New name for the GameObject")] string new_name)
        {
            var go = FindSceneGameObject(name);
            if (go == null)
                return $"Error: GameObject '{name}' not found";

            Undo.RecordObject(go, $"Rename {name} to {new_name}");
            go.name = new_name;
            return $"Renamed '{name}' to '{new_name}'";
        }

        [Description("Set a GameObject's position, rotation, and/or scale")]
        [SceneEditingTool]
        public static string SetTransform(
            [ToolParam("Name of the GameObject")] string name,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = null,
            [ToolParam("Rotation (euler angles) as 'x,y,z'", Required = false)] string rotation = null,
            [ToolParam("Scale as 'x,y,z'", Required = false)] string scale = null)
        {
            var go = FindSceneGameObject(name);
            if (go == null)
                return $"Error: GameObject '{name}' not found";

            Undo.RecordObject(go.transform, $"Set transform of {name}");

            if (!string.IsNullOrEmpty(position))
                go.transform.position = ParseVector3(position);
            if (!string.IsNullOrEmpty(rotation))
                go.transform.eulerAngles = ParseVector3(rotation);
            if (!string.IsNullOrEmpty(scale))
                go.transform.localScale = ParseVector3(scale);

            return $"Updated transform of '{name}': pos={go.transform.position}, rot={go.transform.eulerAngles}, scale={go.transform.localScale}";
        }

        [Description("Set the parent of a GameObject")]
        [SceneEditingTool]
        public static string SetParent(
            [ToolParam("Name of the child GameObject")] string child_name,
            [ToolParam("Name of the parent GameObject (empty to unparent)", Required = false)] string parent_name = null)
        {
            var child = FindSceneGameObject(child_name);
            if (child == null)
                return $"Error: GameObject '{child_name}' not found";

            if (string.IsNullOrEmpty(parent_name))
            {
                Undo.SetTransformParent(child.transform, null, $"Unparent {child_name}");
                return $"Unparented '{child_name}'";
            }

            var parent = FindSceneGameObject(parent_name);
            if (parent == null)
                return $"Error: Parent '{parent_name}' not found";

            Undo.SetTransformParent(child.transform, parent.transform, $"Parent {child_name} to {parent_name}");
            return $"Set parent of '{child_name}' to '{parent_name}'";
        }

        [Description("Add a component to a GameObject")]
        [SceneEditingTool]
        public static string AddComponent(
            [ToolParam("Name of the GameObject")] string name,
            [ToolParam("Full component type name (e.g. 'Rigidbody', 'BoxCollider', 'AudioSource')")] string component_type)
        {
            var go = FindSceneGameObject(name);
            if (go == null)
                return $"Error: GameObject '{name}' not found";

            var type = FindComponentType(component_type);
            if (type == null)
                return $"Error: Component type '{component_type}' not found";

            var comp = Undo.AddComponent(go, type);
            return comp != null
                ? $"Added {component_type} to '{name}'"
                : $"Error: Failed to add {component_type}";
        }

        [Description("Set tag and/or layer of a GameObject")]
        [SceneEditingTool]
        public static string SetTagAndLayer(
            [ToolParam("Name of the GameObject")] string name,
            [ToolParam("Tag to set", Required = false)] string tag = null,
            [ToolParam("Layer name to set", Required = false)] string layer = null)
        {
            var go = FindSceneGameObject(name);
            if (go == null)
                return $"Error: GameObject '{name}' not found";

            Undo.RecordObject(go, $"Set tag/layer of {name}");

            var result = new List<string>();
            if (!string.IsNullOrEmpty(tag))
            {
                go.tag = tag;
                result.Add($"tag={tag}");
            }
            if (!string.IsNullOrEmpty(layer))
            {
                int layerIndex = LayerMask.NameToLayer(layer);
                if (layerIndex >= 0)
                {
                    go.layer = layerIndex;
                    result.Add($"layer={layer}");
                }
                else
                {
                    result.Add($"layer '{layer}' not found");
                }
            }

            return $"Set {string.Join(", ", result)} on '{name}'";
        }

        [Description("Set active state of a GameObject")]
        [SceneEditingTool]
        public static string SetActive(
            [ToolParam("Name of the GameObject")] string name,
            [ToolParam("true to activate, false to deactivate")] string active)
        {
            var go = FindSceneGameObject(name);
            if (go == null)
                return $"Error: GameObject '{name}' not found";

            bool isActive = active == "true" || active == "1";
            Undo.RecordObject(go, $"Set active {name}");
            go.SetActive(isActive);
            return $"Set '{name}' active = {isActive}";
        }

        [Description("Find and list GameObjects by name pattern")]
        [ReadOnlyTool]
        public static string FindGameObjects(
            [ToolParam("Search query (name or partial name)")] string query)
        {
            var allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            var results = new List<string>();
            var lowerQuery = query.ToLowerInvariant();

            foreach (var go in allObjects)
            {
                if (go.name.ToLowerInvariant().Contains(lowerQuery))
                {
                    results.Add($"- {go.name} (pos: {go.transform.position}, active: {go.activeSelf})");
                }
                if (results.Count >= 50) break;
            }

            return results.Count > 0
                ? $"Found {results.Count} objects:\n{string.Join("\n", results)}"
                : "No objects found matching the query.";
        }

        [Description("Get detailed info about a specific GameObject")]
        [ReadOnlyTool]
        public static string GetGameObjectInfo(
            [ToolParam("Name of the GameObject")] string name)
        {
            var go = FindSceneGameObject(name);
            if (go == null)
                return $"Error: GameObject '{name}' not found";

            var info = new System.Text.StringBuilder();
            info.AppendLine($"Name: {go.name}");
            info.AppendLine($"Active: {go.activeSelf}");
            info.AppendLine($"Tag: {go.tag}");
            info.AppendLine($"Layer: {LayerMask.LayerToName(go.layer)}");
            info.AppendLine($"Position: {go.transform.position}");
            info.AppendLine($"Rotation: {go.transform.eulerAngles}");
            info.AppendLine($"Scale: {go.transform.localScale}");
            info.AppendLine($"Children: {go.transform.childCount}");
            info.AppendLine($"Components:");

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null)
                    info.AppendLine($"  - {comp.GetType().Name}");
            }

            return info.ToString();
        }

        private static GameObject FindSceneGameObject(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var go = GameObject.Find(name);
            if (go != null)
                return go;

            foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!candidate.scene.IsValid())
                    continue;

                if (candidate.hideFlags == HideFlags.NotEditable || candidate.hideFlags == HideFlags.HideAndDontSave)
                    continue;

                if (candidate.name == name)
                    return candidate;
            }

            return null;
        }

        private static Vector3 ParseVector3(string value)
        {
            if (string.IsNullOrEmpty(value)) return Vector3.zero;
            value = value.Trim('(', ')', ' ');
            var parts = value.Split(',');
            if (parts.Length >= 3)
            {
                return new Vector3(
                    float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }
            return Vector3.zero;
        }

        private static System.Type FindComponentType(string typeName)
        {
            // Try common Unity types first
            var type = System.Type.GetType($"UnityEngine.{typeName}, UnityEngine.CoreModule");
            if (type != null) return type;

            type = System.Type.GetType($"UnityEngine.{typeName}, UnityEngine.PhysicsModule");
            if (type != null) return type;

            type = System.Type.GetType($"UnityEngine.{typeName}, UnityEngine.AudioModule");
            if (type != null) return type;

            type = System.Type.GetType($"UnityEngine.{typeName}, UnityEngine.AnimationModule");
            if (type != null) return type;

            type = System.Type.GetType($"UnityEngine.{typeName}, UnityEngine.UIModule");
            if (type != null) return type;

            type = System.Type.GetType($"UnityEngine.UI.{typeName}, UnityEngine.UI");
            if (type != null) return type;

            // Search all loaded assemblies
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(typeName) ?? asm.GetType($"UnityEngine.{typeName}");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            return null;
        }
    }
}
