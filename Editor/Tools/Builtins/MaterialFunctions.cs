// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Globalization;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Material")]
    internal static class MaterialFunctions
    {
        [Description("Inspect a Material: shader name, enabled keywords, render queue, and every shader property " +
                     "with its type and current value. 'path' is preferred as an asset path (e.g. 'Assets/Foo.mat'); " +
                     "if it does not resolve to a material asset it is treated as a scene GameObject identifier and the " +
                     "first Renderer.sharedMaterial is used.")]
        [ReadOnlyTool]
        public static object GetMaterialProperties(
            [ToolParam("Material asset path (e.g. 'Assets/Materials/Foo.mat'), or a scene GameObject id/name/path whose Renderer.sharedMaterial is inspected.")] string path)
        {
            var mat = ResolveMaterial(path, out var source);
            if (mat == null)
                return Response.Error("MATERIAL_NOT_FOUND",
                    new { path, hint = "Provide a valid .mat asset path, or a scene GameObject with a Renderer that has a sharedMaterial." });

            var shader = mat.shader;
            var properties = new List<object>();
            if (shader != null)
            {
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    var pName = ShaderUtil.GetPropertyName(shader, i);
                    var pType = ShaderUtil.GetPropertyType(shader, i);
                    object value;
                    switch (pType)
                    {
                        case ShaderUtil.ShaderPropertyType.Color:
                        {
                            var c = mat.GetColor(pName);
                            value = new { r = c.r, g = c.g, b = c.b, a = c.a };
                            break;
                        }
                        case ShaderUtil.ShaderPropertyType.Vector:
                        {
                            var v = mat.GetVector(pName);
                            value = new { x = v.x, y = v.y, z = v.z, w = v.w };
                            break;
                        }
                        case ShaderUtil.ShaderPropertyType.Float:
                        case ShaderUtil.ShaderPropertyType.Range:
                            value = mat.GetFloat(pName);
                            break;
                        case ShaderUtil.ShaderPropertyType.TexEnv:
                        {
                            var tex = mat.GetTexture(pName);
                            value = tex != null ? AssetDatabase.GetAssetPath(tex) : null;
                            break;
                        }
                        default:
                            // ShaderPropertyType.Int (and any future kinds) — read as integer.
                            value = mat.GetInteger(pName);
                            break;
                    }
                    properties.Add(new { name = pName, type = pType.ToString(), value });
                }
            }

            return Response.Success($"Material '{mat.name}' ({properties.Count} shader properties).", new
            {
                material = mat.name,
                source,
                shader = shader != null ? shader.name : null,
                keywords = mat.shaderKeywords,
                renderQueue = mat.renderQueue,
                properties
            });
        }

        [Description("Set a single shader property on a Material and save the asset. type='auto' (default) detects the " +
                     "property kind from the shader; or force one of float|color|vector|texture|int. " +
                     "Float/Range parse a number; Color parses 'r,g,b[,a]' or '#RRGGBB[AA]'; Vector parses 'x,y,z,w'; " +
                     "Texture loads a Texture at the given asset path (empty value clears the texture). Returns the read-back value.")]
        [SceneEditingTool]
        public static object SetMaterialProperty(
            [ToolParam("Material asset path (preferred), or a scene GameObject id/name/path whose Renderer.sharedMaterial is edited.")] string path,
            [ToolParam("Shader property name (case-sensitive, e.g. '_BaseColor', '_Metallic', '_MainTex').")] string name,
            [ToolParam("Value to set, format depends on type (number / 'r,g,b[,a]' or '#hex' / 'x,y,z,w' / texture asset path).")] string value,
            [ToolParam("Property kind: 'auto' (detect from shader), or float|color|vector|texture|int.", Required = false)] string type = "auto")
        {
            var mat = ResolveMaterial(path, out var source);
            if (mat == null)
                return Response.Error("MATERIAL_NOT_FOUND",
                    new { path, hint = "Provide a valid .mat asset path, or a scene GameObject with a Renderer that has a sharedMaterial." });

            if (string.IsNullOrEmpty(name))
                return Response.Error("INVALID_PARAM", new { param = "name", provided = name, expected = "a shader property name" });

            var shader = mat.shader;
            int idx = FindPropertyIndex(shader, name);
            if (idx < 0)
                return Response.Error("PROPERTY_NOT_FOUND", new
                {
                    name,
                    available = ListPropertyNames(shader),
                    hint = "Property name is case-sensitive and must be declared by the material's shader."
                });

            var detected = ShaderUtil.GetPropertyType(shader, idx);

            string category;
            if (string.IsNullOrEmpty(type) || string.Equals(type, "auto", StringComparison.OrdinalIgnoreCase))
            {
                category = CategoryOf(detected);
            }
            else
            {
                category = NormalizeType(type);
                if (category == null)
                    return Response.Error("INVALID_PARAM",
                        new { param = "type", provided = type, expected = "auto|float|color|vector|texture|int" });
            }

            Undo.RecordObject(mat, $"Set material property {name}");

            object readback;
            switch (category)
            {
                case "float":
                {
                    if (!double.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        return Response.Error("INVALID_PARAM", new { param = "value", provided = value, expected = "a number (float)" });
                    mat.SetFloat(name, (float)d);
                    readback = mat.GetFloat(name);
                    break;
                }
                case "color":
                {
                    if (!TryParseColor(value, out var col, out var cErr))
                        return Response.Error("INVALID_PARAM", new { param = "value", provided = value, expected = "'r,g,b[,a]' or '#RRGGBB[AA]'", detail = cErr });
                    mat.SetColor(name, col);
                    var c = mat.GetColor(name);
                    readback = new { r = c.r, g = c.g, b = c.b, a = c.a };
                    break;
                }
                case "vector":
                {
                    if (!TryParseVector4(value, out var vec, out var vErr))
                        return Response.Error("INVALID_PARAM", new { param = "value", provided = value, expected = "'x,y,z,w' (1-4 comma-separated numbers)", detail = vErr });
                    mat.SetVector(name, vec);
                    var v = mat.GetVector(name);
                    readback = new { x = v.x, y = v.y, z = v.z, w = v.w };
                    break;
                }
                case "texture":
                {
                    Texture tex = null;
                    if (!string.IsNullOrEmpty(value))
                    {
                        tex = AssetDatabase.LoadAssetAtPath<Texture>(value);
                        if (tex == null)
                            return Response.Error("TEXTURE_NOT_FOUND",
                                new { value, hint = "Provide a valid texture asset path, or an empty value to clear the texture." });
                    }
                    mat.SetTexture(name, tex);
                    var t = mat.GetTexture(name);
                    readback = t != null ? AssetDatabase.GetAssetPath(t) : null;
                    break;
                }
                case "int":
                {
                    if (!int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                        return Response.Error("INVALID_PARAM", new { param = "value", provided = value, expected = "an integer" });
                    mat.SetInteger(name, iv);
                    readback = mat.GetInteger(name);
                    break;
                }
                default:
                    return Response.Error("UNSUPPORTED_TYPE", new { category });
            }

            EditorUtility.SetDirty(mat);
            bool persisted;
            if (EditorUtility.IsPersistent(mat))
            {
                AssetDatabase.SaveAssetIfDirty(mat);
                persisted = true;
            }
            else
            {
                // A scene-embedded (non-asset) material: there is no asset to save. Mark the active
                // scene dirty so the change is a visible unsaved edit that persists on scene save,
                // rather than a Success reported for a change that would silently vanish on reload.
                var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
                if (scene.IsValid())
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                persisted = false;
            }

            return Response.Success($"Set '{name}' on material '{mat.name}'.", new
            {
                material = mat.name,
                source,
                name,
                type = category,
                value = readback,
                persistedToAsset = persisted,
                note = persisted ? null : "Material is scene-embedded (not an asset); the owning scene was marked dirty. Save the scene to persist."
            });
        }

        // -------- Helpers --------

        // Prefer an asset-path resolution; only fall back to a scene GameObject's Renderer.sharedMaterial.
        private static Material ResolveMaterial(string target, out string source)
        {
            source = null;
            if (string.IsNullOrEmpty(target))
                return null;

            var asset = AssetDatabase.LoadAssetAtPath<Material>(target);
            if (asset != null)
            {
                source = "asset:" + target;
                return asset;
            }

            var go = ObjectsHelper.FindTarget(target);
            if (go != null)
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    source = "renderer:" + ObjectsHelper.GetGameObjectPath(go);
                    return renderer.sharedMaterial;
                }
            }

            return null;
        }

        private static int FindPropertyIndex(Shader shader, string name)
        {
            if (shader == null) return -1;
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyName(shader, i) == name)
                    return i;
            }
            return -1;
        }

        private static List<string> ListPropertyNames(Shader shader)
        {
            var names = new List<string>();
            if (shader == null) return names;
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
                names.Add(ShaderUtil.GetPropertyName(shader, i));
            return names;
        }

        private static string CategoryOf(ShaderUtil.ShaderPropertyType t)
        {
            switch (t)
            {
                case ShaderUtil.ShaderPropertyType.Color: return "color";
                case ShaderUtil.ShaderPropertyType.Vector: return "vector";
                case ShaderUtil.ShaderPropertyType.Float:
                case ShaderUtil.ShaderPropertyType.Range: return "float";
                case ShaderUtil.ShaderPropertyType.TexEnv: return "texture";
                default: return "int"; // ShaderPropertyType.Int and any future kinds
            }
        }

        private static string NormalizeType(string type)
        {
            switch ((type ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "float":
                case "range": return "float";
                case "color": return "color";
                case "vector":
                case "vec": return "vector";
                case "texenv":
                case "texture":
                case "tex": return "texture";
                case "int":
                case "integer": return "int";
                default: return null;
            }
        }

        private static bool TryFloat(string s, out float f)
        {
            return float.TryParse((s ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f);
        }

        // Parse 'r,g,b[,a]' or '#RRGGBB[AA]', reporting malformed input instead of silently returning white.
        private static bool TryParseColor(string value, out Color color, out string error)
        {
            color = Color.white;
            error = null;
            if (string.IsNullOrEmpty(value))
            {
                error = "empty color string";
                return false;
            }

            var v = value.Trim();
            if (v.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(v, out color))
                    return true;
                error = $"'{value}' is not a valid #hex color";
                return false;
            }

            var trimmed = v.Trim('(', ')', ' ');
            var parts = trimmed.Split(',');
            if (parts.Length < 3 || parts.Length > 4)
            {
                error = $"expected 'r,g,b' or 'r,g,b,a', got {parts.Length} component(s)";
                return false;
            }

            if (!TryFloat(parts[0], out var r) || !TryFloat(parts[1], out var g) || !TryFloat(parts[2], out var b))
            {
                error = $"'{value}' has a non-numeric color component";
                return false;
            }

            float a = 1f;
            if (parts.Length == 4 && !TryFloat(parts[3], out a))
            {
                error = $"'{value}' has a non-numeric alpha component";
                return false;
            }

            color = new Color(r, g, b, a);
            return true;
        }

        // Parse 'x,y,z,w' (1-4 components; missing components default to 0), erroring on bad input.
        private static bool TryParseVector4(string value, out Vector4 vec, out string error)
        {
            vec = Vector4.zero;
            error = null;
            if (string.IsNullOrEmpty(value))
            {
                error = "empty vector string";
                return false;
            }

            var trimmed = value.Trim('(', ')', ' ');
            var parts = trimmed.Split(',');
            if (parts.Length < 1 || parts.Length > 4)
            {
                error = $"expected 1-4 comma-separated numbers 'x,y,z,w', got {parts.Length}";
                return false;
            }

            var c = new float[4];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryFloat(parts[i], out c[i]))
                {
                    error = $"'{value}' has a non-numeric component at index {i}";
                    return false;
                }
            }

            vec = new Vector4(c[0], c[1], c[2], c[3]);
            return true;
        }
    }
}
