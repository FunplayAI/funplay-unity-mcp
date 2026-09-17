// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using Funplay.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.Tools.Helpers
{
    [Serializable] internal sealed class VisualRect { public float x, y, width, height; }
    [Serializable] internal sealed class VisualGeometry
    {
        public int schema_version = 1;
        public string capture_id, captured_at, surface, view_id, editor_epoch, scene_signature, camera_signature;
        public string units = "pixels", image_origin = "top_left", render_origin = "bottom_left";
        public int render_width, render_height, output_width, output_height;
        public VisualRect viewport;
        public float output_to_render_x, output_to_render_y;
        public bool interactive, is_playing;
        public string note = "Render framebuffer coordinates, not desktop/editor-window coordinates. Image viewport excludes editor chrome. Camera letterboxing remains part of the framebuffer. Geometry freshness does not prove that animated UI contents are unchanged.";
    }

    internal static class VisualCoordinates
    {
        private static readonly string Epoch = Guid.NewGuid().ToString("N");
        private static readonly Dictionary<string, VisualGeometry> Captures = new Dictionary<string, VisualGeometry>();
        internal static VisualGeometry Create(string surface, int renderWidth, int renderHeight, int outputWidth, int outputHeight, bool interactive)
        {
            if (renderWidth < 1 || renderHeight < 1 || outputWidth < 1 || outputHeight < 1) throw new ArgumentException("Invalid render/output dimensions.");
            var view = ScreenshotFunctions.GetMainPlayModeView() as EditorWindow;
            return new VisualGeometry
            {
                capture_id = Guid.NewGuid().ToString("N"), captured_at = DateTime.UtcNow.ToString("O"), surface = surface,
                render_width = renderWidth, render_height = renderHeight, output_width = outputWidth, output_height = outputHeight,
                viewport = new VisualRect { width = outputWidth, height = outputHeight },
                output_to_render_x = (float)renderWidth / outputWidth, output_to_render_y = (float)renderHeight / outputHeight,
                interactive = interactive, view_id = ObjectIdHelper.GetSerializableId(view), editor_epoch = Epoch,
                is_playing = EditorApplication.isPlaying, scene_signature = SceneSignature(), camera_signature = CameraSignature()
            };
        }
        internal static VisualGeometry Current()
        {
            var view = ScreenshotFunctions.GetMainPlayModeView();
            int w = 0, h = 0;
            if (ScreenshotFunctions.TryGetPlayModeViewRenderTexture(view, out var texture)) { w = texture.width; h = texture.height; }
            else if (!ScreenshotFunctions.TryResolveGameViewSize(ref w, ref h)) throw new InvalidOperationException("GAME_VIEW_GEOMETRY_UNAVAILABLE");
            var geometry = Create("game_view", w, h, w, h, true);
            geometry.capture_id = null; // Current geometry is not a captured visual observation.
            return geometry;
        }
        internal static void Register(VisualGeometry geometry)
        {
            Captures[geometry.capture_id] = geometry;
            while (Captures.Count > 32) Captures.Remove(Captures.Values.OrderBy(x => x.captured_at, StringComparer.Ordinal).First().capture_id);
        }
        internal static VisualGeometry Find(string id) => !string.IsNullOrEmpty(id) && Captures.TryGetValue(id, out var capture) ? capture : null;
        internal static string Validate(VisualGeometry capture, VisualGeometry current, double ageSeconds)
        {
            if (capture == null) return "CAPTURE_NOT_FOUND_OR_EXPIRED";
            if (!capture.interactive || capture.surface != "game_view") return "CAPTURE_NOT_INTERACTIVE";
            if (ageSeconds < 0 || ageSeconds > 30) return "STALE_CAPTURE";
            if (capture.editor_epoch != current.editor_epoch || capture.view_id != current.view_id || capture.is_playing != current.is_playing ||
                capture.scene_signature != current.scene_signature || capture.camera_signature != current.camera_signature ||
                capture.render_width != current.render_width || capture.render_height != current.render_height) return "CAPTURE_GEOMETRY_CHANGED";
            return null;
        }
        internal static bool TryResolve(float x, float y, string space, string origin, string captureId, out Vector2 point, out VisualGeometry geometry, out string error)
        {
            point = default; geometry = null; error = null;
            try
            {
                var current = Current(); geometry = current;
                if (!string.IsNullOrEmpty(captureId))
                {
                    geometry = Find(captureId);
                    double age = geometry == null ? 0 : (DateTime.UtcNow - DateTime.Parse(geometry.captured_at, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()).TotalSeconds;
                    error = Validate(geometry, current, age);
                    if (error != null) return false;
                }
                if (space == "image_pixels" && string.IsNullOrEmpty(captureId)) { error = "CAPTURE_ID_REQUIRED"; return false; }
                return TryConvert(x, y, space, origin, geometry, out point, out error);
            }
            catch (Exception ex) { error = ex is ArgumentException ? "INVALID_COORDINATES" : "GAME_VIEW_GEOMETRY_UNAVAILABLE"; return false; }
        }
        internal static bool TryConvert(float x, float y, string space, string origin, VisualGeometry g, out Vector2 point, out string error)
        {
            point = default; error = null;
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) { error = "INVALID_COORDINATES"; return false; }
            if (origin != "top_left" && origin != "bottom_left") { error = "INVALID_COORDINATE_ORIGIN"; return false; }
            if (g == null || g.render_width <= 0 || g.render_height <= 0 || g.output_width <= 0 || g.output_height <= 0) { error = "INVALID_GEOMETRY"; return false; }
            float width, height;
            switch (space)
            {
                case "render_pixels": width = g.render_width; height = g.render_height; break;
                case "image_pixels": width = g.output_width; height = g.output_height; break;
                case "normalized": width = g.render_width; height = g.render_height; x *= width; y *= height; break;
                default: error = "INVALID_COORDINATE_SPACE"; return false;
            }
            if (x < 0 || y < 0 || x > width || y > height) { error = "COORDINATE_OUT_OF_BOUNDS"; return false; }
            if (origin == "top_left") y = height - y;
            if (space == "image_pixels")
            {
                if (g.viewport == null || g.viewport.width <= 0 || g.viewport.height <= 0) { error = "INVALID_GEOMETRY"; return false; }
                // viewport is expressed bottom-left within the output image, regardless of input origin.
                if (x < g.viewport.x || y < g.viewport.y || x > g.viewport.x + g.viewport.width || y > g.viewport.y + g.viewport.height)
                { error = "POINT_OUTSIDE_RENDER_VIEWPORT"; return false; }
                x = (x - g.viewport.x) * g.render_width / g.viewport.width;
                y = (y - g.viewport.y) * g.render_height / g.viewport.height;
            }
            point = new Vector2(x, y); return true;
        }
        internal static Vector2 ToImage(Vector2 renderPoint, VisualGeometry g, string origin)
        {
            float x = g.viewport.x + renderPoint.x * g.viewport.width / g.render_width;
            float y = g.viewport.y + renderPoint.y * g.viewport.height / g.render_height;
            return new Vector2(x, origin == "top_left" ? g.output_height - y : y);
        }
        internal static Vector3 WorldToRender(Vector3 world, Camera camera, VisualGeometry geometry)
        {
            if (camera == null) return world; // Overlay Canvas world xy already uses framebuffer pixels.
            var viewport = camera.WorldToViewportPoint(world);
            var rect = camera.rect;
            return new Vector3((rect.x + viewport.x * rect.width) * geometry.render_width,
                (rect.y + viewport.y * rect.height) * geometry.render_height, viewport.z);
        }
        internal static Vector2Int PngSize(byte[] png)
        {
            if (png == null || png.Length < 24 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71) throw new ArgumentException("Invalid PNG header.");
            Func<int, int> read = offset => (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            return new Vector2Int(read(16), read(20));
        }
        internal static string WithoutInlineImage(string result)
        {
            if (string.IsNullOrEmpty(result) || !result.Contains("funplay_capture")) return result;
            try
            {
                var json = JObject.Parse(result);
                if ((int?)json["_meta"]?["funplay_capture"] != 1) return result;
                (json["data"] as JObject)?.Remove("inline_image");
                return json.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { return result; }
        }
        private static string SceneSignature() => string.Join("|", Enumerable.Range(0, SceneManager.sceneCount).Select(i => {
            var s = SceneManager.GetSceneAt(i); return s.path + ":" + s.handle + ":" + s.isLoaded;
        }));
        private static string CameraSignature() => string.Join("|", Camera.allCameras.Where(x => x.targetTexture == null).OrderBy(ObjectIdHelper.GetSerializableId).Select(x =>
            ObjectIdHelper.GetSerializableId(x) + ":" + x.rect.ToString("F4") + ":" + x.targetDisplay));
    }
}
