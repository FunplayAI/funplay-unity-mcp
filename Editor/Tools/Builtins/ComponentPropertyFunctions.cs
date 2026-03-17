// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using UnityEditor;
using UnityEngine;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("ComponentProperty")]
    internal static class ComponentPropertyFunctions
    {
        [Description("Get all properties and fields of a component on a GameObject. " +
                     "Returns a readable list of property names, types, and current values. " +
                     "Use find_mode='name' to search by name, 'tag' to search by tag, 'path' for hierarchy path.")]
        [ReadOnlyTool]
        public static string GetComponentProperties(
            [ToolParam("GameObject identifier (name, tag, or hierarchy path)")] string game_object,
            [ToolParam("Component type name (e.g. 'Rigidbody', 'Transform', 'PlayerController')")] string component,
            [ToolParam("How to find the GameObject: 'name', 'tag', or 'path'", Required = false)] string find_mode = "name")
        {
            try
            {
                var go = FindGameObject(game_object, find_mode);
                if (go == null)
                    return $"Error: GameObject '{game_object}' not found (mode: {find_mode})";

                var comp = FindComponent(go, component);
                if (comp == null)
                    return $"Error: Component '{component}' not found on '{go.name}'. " +
                           $"Available: {GetComponentList(go)}";

                var sb = new StringBuilder();
                sb.AppendLine($"[{comp.GetType().Name}] on '{go.name}':");

                var props = comp.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var prop in props)
                {
                    if (!prop.CanRead) continue;
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (prop.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                    try
                    {
                        var val = prop.GetValue(comp);
                        sb.AppendLine($"  {prop.Name} ({prop.PropertyType.Name}) = {FormatValue(val)}");
                    }
                    catch { }
                }

                var fields = comp.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                    try
                    {
                        var val = field.GetValue(comp);
                        sb.AppendLine($"  {field.Name} ({field.FieldType.Name}) = {FormatValue(val)}");
                    }
                    catch { }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [Description("Set a property or field value on a component. " +
                     "Supports common types: int, float, bool, string, Vector3 ('x,y,z'), Color ('r,g,b,a'), enums.")]
        [SceneEditingTool]
        public static string SetComponentProperty(
            [ToolParam("GameObject identifier (name, tag, or hierarchy path)")] string game_object,
            [ToolParam("Component type name (e.g. 'Rigidbody', 'Transform')")] string component,
            [ToolParam("Property or field name to set")] string property,
            [ToolParam("New value as string")] string value,
            [ToolParam("How to find the GameObject: 'name', 'tag', or 'path'", Required = false)] string find_mode = "name")
        {
            try
            {
                var go = FindGameObject(game_object, find_mode);
                if (go == null)
                    return $"Error: GameObject '{game_object}' not found (mode: {find_mode})";

                var comp = FindComponent(go, component);
                if (comp == null)
                    return $"Error: Component '{component}' not found on '{go.name}'";

                Undo.RecordObject(comp, $"Set {property} on {go.name}");

                var prop = comp.GetType().GetProperty(property,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null && prop.CanWrite)
                {
                    var converted = ConvertToType(value, prop.PropertyType);
                    prop.SetValue(comp, converted);
                    EditorUtility.SetDirty(comp);
                    return $"Set {comp.GetType().Name}.{prop.Name} = {FormatValue(prop.GetValue(comp))}";
                }

                var field = comp.GetType().GetField(property,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field != null)
                {
                    var converted = ConvertToType(value, field.FieldType);
                    field.SetValue(comp, converted);
                    EditorUtility.SetDirty(comp);
                    return $"Set {comp.GetType().Name}.{field.Name} = {FormatValue(field.GetValue(comp))}";
                }

                return $"Error: Property/field '{property}' not found on {comp.GetType().Name}. " +
                       $"Check spelling or use get_component_properties to list available properties.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [Description("List all components attached to a GameObject.")]
        [ReadOnlyTool]
        public static string ListComponents(
            [ToolParam("GameObject identifier (name, tag, or hierarchy path)")] string game_object,
            [ToolParam("How to find the GameObject: 'name', 'tag', or 'path'", Required = false)] string find_mode = "name")
        {
            try
            {
                var go = FindGameObject(game_object, find_mode);
                if (go == null)
                    return $"Error: GameObject '{game_object}' not found (mode: {find_mode})";

                var sb = new StringBuilder();
                sb.AppendLine($"Components on '{go.name}':");
                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    sb.AppendLine($"  - {comp.GetType().Name}");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [Description("Set multiple properties on a component in a single call. " +
                     "Properties are specified as a JSON object: {\"property1\":\"value1\",\"property2\":\"value2\"}. " +
                     "More efficient than calling set_component_property multiple times.")]
        [SceneEditingTool]
        public static string SetComponentProperties(
            [ToolParam("GameObject identifier (name, tag, or hierarchy path)")] string game_object,
            [ToolParam("Component type name (e.g. 'Rigidbody', 'Transform')")] string component,
            [ToolParam("JSON object of property-value pairs, e.g. {\"mass\":\"5\",\"isKinematic\":\"true\"}")] string properties,
            [ToolParam("How to find the GameObject: 'name', 'tag', or 'path'", Required = false)] string find_mode = "name")
        {
            try
            {
                var go = FindGameObject(game_object, find_mode);
                if (go == null)
                    return $"Error: GameObject '{game_object}' not found (mode: {find_mode})";

                var comp = FindComponent(go, component);
                if (comp == null)
                    return $"Error: Component '{component}' not found on '{go.name}'";

                var propDict = ParseJsonProperties(properties);
                if (propDict == null || propDict.Count == 0)
                    return "Error: Could not parse properties JSON. Expected format: {\"prop\":\"value\"}";

                Undo.RecordObject(comp, $"Set properties on {go.name}");

                var results = new StringBuilder();
                int successCount = 0;
                int failCount = 0;

                foreach (var kvp in propDict)
                {
                    string propName = kvp.Key;
                    string propValue = kvp.Value;

                    var prop = comp.GetType().GetProperty(propName,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (prop != null && prop.CanWrite)
                    {
                        try
                        {
                            var converted = ConvertToType(propValue, prop.PropertyType);
                            prop.SetValue(comp, converted);
                            results.AppendLine($"  OK: {prop.Name} = {FormatValue(prop.GetValue(comp))}");
                            successCount++;
                            continue;
                        }
                        catch (Exception ex2)
                        {
                            results.AppendLine($"  FAIL: {propName} - {ex2.Message}");
                            failCount++;
                            continue;
                        }
                    }

                    var field = comp.GetType().GetField(propName,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (field != null)
                    {
                        try
                        {
                            var converted = ConvertToType(propValue, field.FieldType);
                            field.SetValue(comp, converted);
                            results.AppendLine($"  OK: {field.Name} = {FormatValue(field.GetValue(comp))}");
                            successCount++;
                            continue;
                        }
                        catch (Exception ex2)
                        {
                            results.AppendLine($"  FAIL: {propName} - {ex2.Message}");
                            failCount++;
                            continue;
                        }
                    }

                    results.AppendLine($"  FAIL: {propName} - property/field not found");
                    failCount++;
                }

                EditorUtility.SetDirty(comp);
                return $"Set {successCount} properties on {comp.GetType().Name} ({failCount} failed):\n{results}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // --- Helper methods ---

        private static GameObject FindGameObject(string identifier, string mode)
        {
            switch (mode.ToLowerInvariant())
            {
                case "tag":
                    return GameObject.FindWithTag(identifier);

                case "path":
                    var obj = FindGameObjectIncludingInactive(identifier);
                    if (obj != null) return obj;
                    foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    {
                        var found = root.transform.Find(identifier);
                        if (found != null) return found.gameObject;
                    }
                    return null;

                case "name":
                default:
                    var go = FindGameObjectIncludingInactive(identifier);
                    if (go != null) return go;
                    foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    {
                        var result = FindInChildren(root.transform, identifier);
                        if (result != null) return result.gameObject;
                    }
                    return null;
            }
        }

        private static Transform FindInChildren(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var result = FindInChildren(parent.GetChild(i), name);
                if (result != null) return result;
            }
            return null;
        }

        private static GameObject FindGameObjectIncludingInactive(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                return null;

            var go = GameObject.Find(identifier);
            if (go != null)
                return go;

            foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!candidate.scene.IsValid())
                    continue;

                if (candidate.hideFlags == HideFlags.NotEditable || candidate.hideFlags == HideFlags.HideAndDontSave)
                    continue;

                if (candidate.name == identifier)
                    return candidate;
            }

            return null;
        }

        private static Component FindComponent(GameObject go, string componentName)
        {
            var components = go.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp == null) continue;
                if (string.Equals(comp.GetType().Name, componentName, StringComparison.OrdinalIgnoreCase))
                    return comp;
            }
            foreach (var comp in components)
            {
                if (comp == null) continue;
                if (comp.GetType().Name.IndexOf(componentName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return comp;
            }
            return null;
        }

        private static string GetComponentList(GameObject go)
        {
            var names = new List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null) names.Add(comp.GetType().Name);
            }
            return string.Join(", ", names);
        }

        private static object ConvertToType(string value, Type targetType)
        {
            if (targetType == typeof(string)) return value;
            if (targetType == typeof(int)) return int.Parse(value);
            if (targetType == typeof(float)) return float.Parse(value);
            if (targetType == typeof(double)) return double.Parse(value);
            if (targetType == typeof(bool)) return value.ToLower() == "true" || value == "1";
            if (targetType == typeof(Vector2))
            {
                var p = value.Trim('(', ')').Split(',');
                return new Vector2(float.Parse(p[0].Trim()), float.Parse(p[1].Trim()));
            }
            if (targetType == typeof(Vector3))
            {
                var p = value.Trim('(', ')').Split(',');
                return new Vector3(float.Parse(p[0].Trim()), float.Parse(p[1].Trim()), float.Parse(p[2].Trim()));
            }
            if (targetType == typeof(Color))
            {
                var p = value.Trim('(', ')').Split(',');
                if (p.Length >= 4)
                    return new Color(float.Parse(p[0].Trim()), float.Parse(p[1].Trim()),
                        float.Parse(p[2].Trim()), float.Parse(p[3].Trim()));
                if (p.Length >= 3)
                    return new Color(float.Parse(p[0].Trim()), float.Parse(p[1].Trim()),
                        float.Parse(p[2].Trim()));
                return Color.white;
            }
            if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
            return Convert.ChangeType(value, targetType);
        }

        /// <summary>
        /// Simple JSON object parser for property-value pairs. No external dependency needed.
        /// </summary>
        private static Dictionary<string, string> ParseJsonProperties(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return result;

            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}")) return result;
            json = json.Substring(1, json.Length - 2).Trim();
            if (string.IsNullOrEmpty(json)) return result;

            // Simple state-machine parser for {"key":"value", ...}
            int i = 0;
            while (i < json.Length)
            {
                // Skip whitespace
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length) break;

                // Parse key
                string key = ParseJsonString(json, ref i);
                if (key == null) break;

                // Skip colon
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] != ':') break;
                i++;

                // Parse value
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                string val = ParseJsonString(json, ref i);
                if (val == null) break;

                result[key] = val;

                // Skip comma
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i < json.Length && json[i] == ',') i++;
            }

            return result;
        }

        private static string ParseJsonString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"') return null;
            i++; // skip opening quote
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    sb.Append(json[i + 1]);
                    i += 2;
                }
                else if (json[i] == '"')
                {
                    i++; // skip closing quote
                    return sb.ToString();
                }
                else
                {
                    sb.Append(json[i]);
                    i++;
                }
            }
            return null;
        }

        private static string FormatValue(object val)
        {
            if (val == null) return "null";
            if (val is Vector3 v3) return $"({v3.x:F2}, {v3.y:F2}, {v3.z:F2})";
            if (val is Vector2 v2) return $"({v2.x:F2}, {v2.y:F2})";
            if (val is Quaternion q) return $"({q.eulerAngles.x:F1}, {q.eulerAngles.y:F1}, {q.eulerAngles.z:F1})";
            if (val is Color c) return $"({c.r:F2}, {c.g:F2}, {c.b:F2}, {c.a:F2})";
            if (val is bool b) return b ? "true" : "false";
            if (val is float f) return f.ToString("F3");
            if (val is double d) return d.ToString("F3");
            if (val is UnityEngine.Object uobj)
            {
                if (uobj == null) return "null (destroyed)";
                return uobj.name;
            }
            return val.ToString();
        }
    }
}
