// Copyright (C) Funplay. Licensed under MIT.
using System.Text;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Camera")]
    internal static class CameraFunctions
    {
        [Description("Get all camera properties including type, FOV, clipping planes, background, and culling mask")]
        [ReadOnlyTool]
        public static object GetCameraProperties(
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID (default: Main Camera). Finds inactive objects too.", Required = false)] string game_object_name = null)
        {
            var camera = FindCamera(game_object_name);
            if (camera == null)
                return Response.Error("CAMERA_NOT_FOUND", new { game_object_name });

            var pos = camera.transform.position;
            var rot = camera.transform.eulerAngles;
            var bg = camera.backgroundColor;
            var rect = camera.rect;
            var rt = camera.targetTexture;

            return Response.Success($"Camera '{camera.gameObject.name}' properties", new
            {
                instanceId = ObjectIdHelper.GetSerializableId(camera.gameObject),
                name = camera.gameObject.name,
                orthographic = camera.orthographic,
                orthographicSize = camera.orthographicSize,
                fieldOfView = camera.fieldOfView,
                nearClipPlane = camera.nearClipPlane,
                farClipPlane = camera.farClipPlane,
                backgroundColor = new { r = bg.r, g = bg.g, b = bg.b, a = bg.a, hex = "#" + ColorUtility.ToHtmlStringRGBA(bg) },
                clearFlags = camera.clearFlags.ToString(),
                depth = camera.depth,
                cullingMask = camera.cullingMask,
                cullingMaskLayers = CullingMaskToLayerNames(camera.cullingMask),
                rect = new { x = rect.x, y = rect.y, width = rect.width, height = rect.height },
                renderingPath = camera.renderingPath.ToString(),
                targetDisplay = camera.targetDisplay,
                targetTexture = rt != null ? rt.name : null,
                position = new { x = pos.x, y = pos.y, z = pos.z },
                rotation = new { x = rot.x, y = rot.y, z = rot.z }
            });
        }

        [Description("Set camera projection to orthographic or perspective with size/FOV")]
        [SceneEditingTool]
        public static string SetCameraProjection(
            [ToolParam("Projection type: 'orthographic' or 'perspective'")] string projection,
            [ToolParam("Orthographic size or FOV value", Required = false)] float size = -1f,
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID. Finds inactive objects too.", Required = false)] string game_object_name = null)
        {
            var camera = FindCamera(game_object_name);
            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { game_object_name });

            Undo.RecordObject(camera, "Set Camera Projection");

            bool ortho = projection.ToLowerInvariant().StartsWith("ortho");
            camera.orthographic = ortho;

            if (size > 0f)
            {
                if (ortho)
                    camera.orthographicSize = size;
                else
                    camera.fieldOfView = Mathf.Clamp(size, 1f, 179f);
            }

            string sizeInfo = ortho ? $"orthographicSize={camera.orthographicSize}" : $"fov={camera.fieldOfView}";
            return $"Camera '{camera.gameObject.name}' set to {(ortho ? "orthographic" : "perspective")}, {sizeInfo}";
        }

        [Description("Set camera clipping planes, background color, and clear flags")]
        [SceneEditingTool]
        public static string SetCameraSettings(
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID. Finds inactive objects too.", Required = false)] string game_object_name = null,
            [ToolParam("Near clip plane distance", Required = false)] float near = -1f,
            [ToolParam("Far clip plane distance", Required = false)] float far = -1f,
            [ToolParam("Background color as 'r,g,b,a' or hex", Required = false)] string background_color = null,
            [ToolParam("Clear flags: 'Skybox','SolidColor','Depth','Nothing'", Required = false)] string clear_flags = null,
            [ToolParam("Camera depth (render order)", Required = false)] float depth = float.MinValue)
        {
            var camera = FindCamera(game_object_name);
            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { game_object_name });

            Undo.RecordObject(camera, "Set Camera Settings");
            var changes = new StringBuilder();

            if (near > 0f)
            {
                camera.nearClipPlane = near;
                changes.Append($"near={near} ");
            }
            if (far > 0f)
            {
                camera.farClipPlane = far;
                changes.Append($"far={far} ");
            }
            if (!string.IsNullOrEmpty(background_color))
            {
                camera.backgroundColor = ParseColor(background_color);
                changes.Append($"bg={background_color} ");
            }
            if (!string.IsNullOrEmpty(clear_flags))
            {
                switch (clear_flags.ToLowerInvariant())
                {
                    case "skybox": camera.clearFlags = CameraClearFlags.Skybox; break;
                    case "solidcolor": case "solid": camera.clearFlags = CameraClearFlags.SolidColor; break;
                    case "depth": camera.clearFlags = CameraClearFlags.Depth; break;
                    case "nothing": case "none": camera.clearFlags = CameraClearFlags.Nothing; break;
                }
                changes.Append($"clearFlags={camera.clearFlags} ");
            }
            if (depth > float.MinValue)
            {
                camera.depth = depth;
                changes.Append($"depth={depth} ");
            }

            return changes.Length > 0
                ? $"Camera '{camera.gameObject.name}' updated: {changes}"
                : "No changes applied (no valid parameters provided)";
        }

        [Description("Set the camera culling mask to show/hide specific layers")]
        [SceneEditingTool]
        public static string SetCameraCullingMask(
            [ToolParam("Comma-separated layer names to show (e.g. 'Default,UI,Water')")] string layers,
            [ToolParam("Camera GameObject name, hierarchy path, or instance ID. Finds inactive objects too.", Required = false)] string game_object_name = null,
            [ToolParam("Mode: 'set' replaces mask, 'add' adds layers, 'remove' removes layers", Required = false)] string mode = "set")
        {
            var camera = FindCamera(game_object_name);
            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { game_object_name });

            Undo.RecordObject(camera, "Set Camera Culling Mask");

            var layerNames = layers.Split(',');
            int mask = 0;
            foreach (var layerName in layerNames)
            {
                int layer = LayerMask.NameToLayer(layerName.Trim());
                if (layer >= 0)
                    mask |= 1 << layer;
            }

            switch (mode.ToLowerInvariant())
            {
                case "add":
                    camera.cullingMask |= mask;
                    break;
                case "remove":
                    camera.cullingMask &= ~mask;
                    break;
                default:
                    camera.cullingMask = mask;
                    break;
            }

            return $"Camera '{camera.gameObject.name}' cullingMask updated: {CullingMaskToString(camera.cullingMask)}";
        }

        // --- Helpers ---

        private static Camera FindCamera(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Camera.main ?? Object.FindFirstObjectByType<Camera>();

            var go = ObjectsHelper.FindTarget(name);
            if (go == null) return null;
            return go.GetComponent<Camera>();
        }

        private static string CullingMaskToString(int mask)
        {
            if (mask == -1) return "Everything";
            if (mask == 0) return "Nothing";
            return string.Join(", ", CullingMaskToLayerNames(mask));
        }

        private static System.Collections.Generic.List<string> CullingMaskToLayerNames(int mask)
        {
            var names = new System.Collections.Generic.List<string>();
            if (mask == 0) return names;
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    var layerName = LayerMask.LayerToName(i);
                    if (!string.IsNullOrEmpty(layerName))
                        names.Add(layerName);
                }
            }
            return names;
        }

        private static Color ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return Color.black;
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
            return Color.black;
        }
    }
}
