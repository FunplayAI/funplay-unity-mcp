// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Read and write component properties through Unity's SerializedObject API.
    ///
    /// This is the path agents should take instead of raw reflection — it picks up
    /// <c>[SerializeField] private</c> fields, supports Object references (Material,
    /// Texture, GameObject, ScriptableObject…) via <c>{ "fileID": &lt;instanceId&gt; }</c>,
    /// and surfaces per-field success/failure so an LLM can recover from partial writes.
    /// </summary>
    public static class ComponentSerializer
    {
        // ---------- Reading ----------

        public sealed class PropertySnapshot
        {
            public string Name;
            public string DisplayName;
            public string Type;
            public object Value;
        }

        /// <summary>
        /// Returns every visible serialized property on the component. Skips Unity's "m_Script"
        /// field by default since it's noise for AI consumers.
        /// </summary>
        public static List<PropertySnapshot> ReadProperties(UnityEngine.Object component, bool includeScriptField = false)
        {
            var list = new List<PropertySnapshot>();
            if (component == null) return list;

            var so = new SerializedObject(component);
            var prop = so.GetIterator();
            // First call returns the root; pass true to descend into children
            if (prop.NextVisible(true))
            {
                do
                {
                    if (!includeScriptField && prop.name == "m_Script")
                        continue;

                    list.Add(new PropertySnapshot
                    {
                        Name = prop.name,
                        DisplayName = prop.displayName,
                        Type = prop.propertyType.ToString(),
                        Value = ReadPropertyValue(prop)
                    });
                }
                while (prop.NextVisible(false));
            }
            return list;
        }

        private static object ReadPropertyValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.intValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return p.floatValue;
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Color:
                    var c = p.colorValue;
                    return new { r = c.r, g = c.g, b = c.b, a = c.a };
                case SerializedPropertyType.Vector2:
                    var v2 = p.vector2Value; return new { x = v2.x, y = v2.y };
                case SerializedPropertyType.Vector3:
                    var v3 = p.vector3Value; return new { x = v3.x, y = v3.y, z = v3.z };
                case SerializedPropertyType.Vector4:
                    var v4 = p.vector4Value; return new { x = v4.x, y = v4.y, z = v4.z, w = v4.w };
                case SerializedPropertyType.Quaternion:
                    var q = p.quaternionValue; return new { x = q.x, y = q.y, z = q.z, w = q.w };
                case SerializedPropertyType.Rect:
                    var r = p.rectValue; return new { x = r.x, y = r.y, width = r.width, height = r.height };
                case SerializedPropertyType.Bounds:
                    var b = p.boundsValue;
                    return new { center = new { x = b.center.x, y = b.center.y, z = b.center.z },
                                 extents = new { x = b.extents.x, y = b.extents.y, z = b.extents.z } };
                case SerializedPropertyType.Enum:
                    return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length
                        ? (object)p.enumDisplayNames[p.enumValueIndex]
                        : p.enumValueIndex;
                case SerializedPropertyType.ObjectReference:
                    var o = p.objectReferenceValue;
                    return o == null
                        ? null
                        : (object)new { fileID = ObjectIdHelper.GetSerializableId(o), name = o.name, type = o.GetType().Name };
                case SerializedPropertyType.LayerMask:
                    return p.intValue;
                case SerializedPropertyType.AnimationCurve:
                    return "<AnimationCurve>";
                default:
                    if (p.isArray)
                        return $"<Array length={p.arraySize}>";
                    return p.propertyType.ToString();
            }
        }

        // ---------- Writing ----------

        public sealed class FieldResult
        {
            public string Field;
            public bool Success;
            public string Error;
        }

        /// <summary>
        /// Apply a JObject of property→value pairs to a component. Returns a per-field
        /// success report so the caller can surface "set 4 of 5 fields, X failed because Y".
        /// </summary>
        public static List<FieldResult> WriteProperties(UnityEngine.Object component, JObject properties, string undoLabel = null)
        {
            var results = new List<FieldResult>();
            if (component == null || properties == null) return results;

            Undo.RecordObject(component, undoLabel ?? $"Set properties on {component.GetType().Name}");

            var so = new SerializedObject(component);
            so.Update();

            foreach (var prop in properties.Properties())
            {
                var fr = new FieldResult { Field = prop.Name };
                try
                {
                    var serializedProperty = so.FindProperty(prop.Name);
                    if (serializedProperty == null)
                    {
                        // Reflection fallback for non-serialized public properties (e.g. Renderer.material, Time.timeScale-style)
                        if (TryWriteViaReflection(component, prop.Name, prop.Value, out var reflectionError))
                        {
                            fr.Success = true;
                        }
                        else
                        {
                            fr.Error = reflectionError ?? $"Property '{prop.Name}' not found.";
                        }
                    }
                    else if (TryWriteSerializedProperty(serializedProperty, prop.Value, out var writeError))
                    {
                        fr.Success = true;
                    }
                    else
                    {
                        fr.Error = writeError;
                    }
                }
                catch (Exception ex)
                {
                    fr.Error = ex.Message;
                }
                results.Add(fr);
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            return results;
        }

        private static bool TryWriteSerializedProperty(SerializedProperty p, JToken value, out string error)
        {
            error = null;
            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        p.intValue = value.ToObject<int>(); return true;
                    case SerializedPropertyType.Boolean:
                        p.boolValue = value.ToObject<bool>(); return true;
                    case SerializedPropertyType.Float:
                        p.floatValue = value.ToObject<float>(); return true;
                    case SerializedPropertyType.String:
                        p.stringValue = value.ToObject<string>() ?? string.Empty; return true;
                    case SerializedPropertyType.LayerMask:
                        p.intValue = value.ToObject<int>(); return true;
                    case SerializedPropertyType.Vector2:
                        if (TryParseVector(value, 2, out var v2arr))
                        { p.vector2Value = new Vector2(v2arr[0], v2arr[1]); return true; }
                        break;
                    case SerializedPropertyType.Vector3:
                        if (TryParseVector(value, 3, out var v3arr))
                        { p.vector3Value = new Vector3(v3arr[0], v3arr[1], v3arr[2]); return true; }
                        break;
                    case SerializedPropertyType.Vector4:
                        if (TryParseVector(value, 4, out var v4arr))
                        { p.vector4Value = new Vector4(v4arr[0], v4arr[1], v4arr[2], v4arr[3]); return true; }
                        break;
                    case SerializedPropertyType.Quaternion:
                        if (value is JObject qo)
                        {
                            if (qo.ContainsKey("w"))
                            {
                                p.quaternionValue = new Quaternion(
                                    qo.Value<float>("x"), qo.Value<float>("y"),
                                    qo.Value<float>("z"), qo.Value<float>("w"));
                            }
                            else
                            {
                                // Treat as Euler
                                p.quaternionValue = Quaternion.Euler(
                                    qo.Value<float>("x"), qo.Value<float>("y"), qo.Value<float>("z"));
                            }
                            return true;
                        }
                        break;
                    case SerializedPropertyType.Color:
                        if (value is JObject co)
                        {
                            float a = co.ContainsKey("a") ? co.Value<float>("a") : 1f;
                            p.colorValue = new Color(co.Value<float>("r"), co.Value<float>("g"),
                                                     co.Value<float>("b"), a);
                            return true;
                        }
                        break;
                    case SerializedPropertyType.Rect:
                        if (value is JObject ro)
                        {
                            p.rectValue = new Rect(ro.Value<float>("x"), ro.Value<float>("y"),
                                                   ro.Value<float>("width"), ro.Value<float>("height"));
                            return true;
                        }
                        break;
                    case SerializedPropertyType.Enum:
                        if (value.Type == JTokenType.Integer)
                        {
                            p.enumValueIndex = value.ToObject<int>();
                            return true;
                        }
                        if (value.Type == JTokenType.String)
                        {
                            var name = value.ToObject<string>();
                            for (int i = 0; i < p.enumDisplayNames.Length; i++)
                            {
                                if (string.Equals(p.enumDisplayNames[i], name, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(p.enumNames[i], name, StringComparison.OrdinalIgnoreCase))
                                {
                                    p.enumValueIndex = i;
                                    return true;
                                }
                            }
                            error = $"Enum value '{name}' not found in {string.Join(", ", p.enumDisplayNames)}";
                            return false;
                        }
                        break;
                    case SerializedPropertyType.ObjectReference:
                        return TryWriteObjectReference(p, value, out error);
                    case SerializedPropertyType.ArraySize:
                        p.arraySize = value.ToObject<int>(); return true;
                    default:
                        if (p.isArray && value is JArray arr)
                            return TryWriteArray(p, arr, out error);
                        error = $"Unsupported property type {p.propertyType}";
                        return false;
                }
                error = $"Could not parse value for {p.propertyType}: {value.Type}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryWriteObjectReference(SerializedProperty p, JToken value, out string error)
        {
            error = null;
            if (value.Type == JTokenType.Null)
            {
                p.objectReferenceValue = null;
                return true;
            }

            string instanceId = null;
            string assetPath = null;

            if (value.Type == JTokenType.Integer)
            {
                instanceId = value.ToString();
            }
            else if (value is JObject obj)
            {
                if (obj.TryGetValue("fileID", out var fid)) instanceId = fid.ToString();
                else if (obj.TryGetValue("instanceId", out var iid)) instanceId = iid.ToString();
                if (obj.TryGetValue("assetPath", out var ap)) assetPath = ap.ToObject<string>();
            }
            else if (value.Type == JTokenType.String)
            {
                assetPath = value.ToObject<string>();
            }

            UnityEngine.Object resolved = null;
            if (!string.IsNullOrWhiteSpace(instanceId))
                resolved = ObjectIdHelper.ToObject(instanceId);
            if (resolved == null && !string.IsNullOrEmpty(assetPath))
                resolved = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);

            if (resolved == null)
            {
                error = !string.IsNullOrWhiteSpace(instanceId)
                    ? $"No object with instanceId={instanceId}"
                    : $"Could not load asset at '{assetPath}'";
                return false;
            }

            p.objectReferenceValue = resolved;
            return true;
        }

        private static bool TryWriteArray(SerializedProperty arrayProp, JArray array, out string error)
        {
            error = null;
            arrayProp.arraySize = array.Count;
            for (int i = 0; i < array.Count; i++)
            {
                var element = arrayProp.GetArrayElementAtIndex(i);
                if (!TryWriteSerializedProperty(element, array[i], out var elemErr))
                {
                    error = $"element[{i}]: {elemErr}";
                    return false;
                }
            }
            return true;
        }

        private static bool TryParseVector(JToken value, int dims, out float[] components)
        {
            components = null;
            if (value is JArray arr && arr.Count >= dims)
            {
                components = new float[dims];
                for (int i = 0; i < dims; i++)
                    components[i] = arr[i].ToObject<float>();
                return true;
            }
            if (value is JObject obj)
            {
                components = new float[dims];
                string[] keys = { "x", "y", "z", "w" };
                for (int i = 0; i < dims; i++)
                {
                    if (!obj.ContainsKey(keys[i]))
                        return false;
                    components[i] = obj.Value<float>(keys[i]);
                }
                return true;
            }
            return false;
        }

        // ---------- Reflection fallback ----------

        private static bool TryWriteViaReflection(UnityEngine.Object component, string name, JToken value, out string error)
        {
            error = null;
            var type = component.GetType();
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    var converted = ConvertJTokenToType(value, prop.PropertyType);
                    prop.SetValue(component, converted);
                    return true;
                }
                catch (Exception ex) { error = ex.Message; return false; }
            }

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null)
            {
                try
                {
                    var converted = ConvertJTokenToType(value, field.FieldType);
                    field.SetValue(component, converted);
                    return true;
                }
                catch (Exception ex) { error = ex.Message; return false; }
            }
            return false;
        }

        private static object ConvertJTokenToType(JToken token, Type targetType)
        {
            if (targetType == typeof(string)) return token.ToObject<string>();
            if (targetType == typeof(int)) return token.ToObject<int>();
            if (targetType == typeof(long)) return token.ToObject<long>();
            if (targetType == typeof(float)) return token.ToObject<float>();
            if (targetType == typeof(double)) return token.ToObject<double>();
            if (targetType == typeof(bool)) return token.ToObject<bool>();
            if (targetType.IsEnum) return Enum.Parse(targetType, token.ToObject<string>(), ignoreCase: true);
            if (targetType == typeof(Vector2) && TryParseVector(token, 2, out var v2)) return new Vector2(v2[0], v2[1]);
            if (targetType == typeof(Vector3) && TryParseVector(token, 3, out var v3)) return new Vector3(v3[0], v3[1], v3[2]);
            if (targetType == typeof(Vector4) && TryParseVector(token, 4, out var v4)) return new Vector4(v4[0], v4[1], v4[2], v4[3]);
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                string id = null;
                if (token.Type == JTokenType.Integer) id = token.ToString();
                else if (token is JObject jo && jo.TryGetValue("fileID", out var f)) id = f.ToString();
                if (!string.IsNullOrWhiteSpace(id)) return ObjectIdHelper.ToObject(id);
            }
            return token.ToObject(targetType);
        }
    }
}
