// Copyright (C) GameBooom. Licensed under GPLv3.
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("Asset")]
    internal static class AssetFunctions
    {
        [Description("Create a new material with a specified color")]
        [SceneEditingTool]
        public static string CreateMaterial(
            [ToolParam("Name of the material")] string name,
            [ToolParam("Color as 'r,g,b,a' or hex '#RRGGBB'", Required = false)] string color = "1,1,1,1",
            [ToolParam("Shader name (default: Standard)", Required = false)] string shader = "Standard",
            [ToolParam("Save path (e.g. 'Assets/Materials/')", Required = false)] string save_path = "Assets/Materials/")
        {
            string actualShader = shader;
            string colorProperty = "_Color";

            if (shader == "Standard")
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (pipeline != null)
                {
                    string pipelineName = pipeline.GetType().Name;
                    if (pipelineName.Contains("Universal") || pipelineName.Contains("URP"))
                    {
                        actualShader = "Universal Render Pipeline/Lit";
                        colorProperty = "_BaseColor";
                    }
                    else if (pipelineName.Contains("HD") || pipelineName.Contains("HDRP"))
                    {
                        actualShader = "HDRP/Lit";
                        colorProperty = "_BaseColor";
                    }
                }
            }
            else
            {
                if (shader.StartsWith("Universal Render Pipeline") || shader.StartsWith("HDRP"))
                    colorProperty = "_BaseColor";
            }

            var shaderObj = Shader.Find(actualShader);
            if (shaderObj == null)
                return $"Error: Shader '{actualShader}' not found";

            var material = new Material(shaderObj);
            material.name = name;

            var c = ParseColor(color);
            if (material.HasProperty(colorProperty))
                material.SetColor(colorProperty, c);
            else
                material.color = c;

            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

            var fullPath = $"{save_path}{name}.mat";
            AssetDatabase.CreateAsset(material, fullPath);
            AssetDatabase.Refresh();

            string pipelineInfo = actualShader != shader ? $" (auto-detected: {actualShader})" : "";
            return $"Created material '{name}' at {fullPath}{pipelineInfo}";
        }

        [Description("Assign a material to a GameObject's renderer")]
        [SceneEditingTool]
        public static string AssignMaterial(
            [ToolParam("Name of the GameObject")] string game_object_name,
            [ToolParam("Path to the material asset")] string material_path)
        {
            var go = GameObject.Find(game_object_name);
            if (go == null)
                return $"Error: GameObject '{game_object_name}' not found";

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return $"Error: No Renderer on '{game_object_name}'";

            var mat = AssetDatabase.LoadAssetAtPath<Material>(material_path);
            if (mat == null)
                return $"Error: Material not found at '{material_path}'";

            Undo.RecordObject(renderer, $"Assign material to {game_object_name}");
            renderer.sharedMaterial = mat;
            return $"Assigned material '{mat.name}' to '{game_object_name}'";
        }

        [Description("Search for assets by type and name")]
        [ReadOnlyTool]
        public static string FindAssets(
            [ToolParam("Search filter (e.g. 't:Material red', 't:Prefab Player', 't:Texture')")] string filter)
        {
            var guids = AssetDatabase.FindAssets(filter);
            if (guids.Length == 0)
                return $"No assets found for filter: {filter}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {guids.Length} assets:");
            int count = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                sb.AppendLine($"  - {path}");
                count++;
                if (count >= 50) { sb.AppendLine("  ... (truncated)"); break; }
            }
            return sb.ToString();
        }

        [Description("Delete an asset")]
        [SceneEditingTool]
        public static string DeleteAsset(
            [ToolParam("Path to the asset")] string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return $"Error: Asset not found at '{path}'";

            bool deleted = AssetDatabase.DeleteAsset(path);
            return deleted ? $"Deleted asset: {path}" : $"Error: Failed to delete {path}";
        }

        [Description("Rename an asset")]
        [SceneEditingTool]
        public static string RenameAsset(
            [ToolParam("Current path of the asset")] string path,
            [ToolParam("New name (without extension)")] string new_name)
        {
            var result = AssetDatabase.RenameAsset(path, new_name);
            return string.IsNullOrEmpty(result) ? $"Renamed to '{new_name}'" : $"Error: {result}";
        }

        [Description("Copy an asset to a new location")]
        [SceneEditingTool]
        public static string CopyAsset(
            [ToolParam("Source asset path")] string source_path,
            [ToolParam("Destination asset path")] string destination_path)
        {
            var dir = Path.GetDirectoryName(destination_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool copied = AssetDatabase.CopyAsset(source_path, destination_path);
            return copied ? $"Copied '{source_path}' to '{destination_path}'" : $"Error: Failed to copy";
        }

        private static Color ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return Color.white;
            value = value.Trim();
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out var c)) return c;
            }
            value = value.Trim('(', ')', ' ');
            var p = value.Split(',');
            if (p.Length >= 3)
            {
                float r = float.Parse(p[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                float g = float.Parse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                float b = float.Parse(p[2].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                float a = p.Length >= 4 ? float.Parse(p[3].Trim(), System.Globalization.CultureInfo.InvariantCulture) : 1f;
                return new Color(r, g, b, a);
            }
            return Color.white;
        }
    }
}
