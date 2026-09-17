// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Linq;
using System.ComponentModel;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("VisualInspection")]
    internal static class VisualInspectionFunctions
    {
        [Description("Read the current Game View coordinate contract, or a retained screenshot receipt by capture_id. Captures are retained for 32 images within this domain; interaction requires the same render geometry/mode and age <=30 seconds. No screenshot is taken by this query.")]
        [ReadOnlyTool]
        public static object GetVisualCoordinates([ToolParam("Optional screenshot ID", Required = false)] string capture_id = null)
        {
            try
            {
                var current = VisualCoordinates.Current();
                if (string.IsNullOrEmpty(capture_id)) return Response.Success("Current Game View geometry (not a capture).", current);
                var capture = VisualCoordinates.Find(capture_id);
                if (capture == null) return Response.Error("CAPTURE_NOT_FOUND_OR_EXPIRED");
                var age = (DateTime.UtcNow - DateTime.Parse(capture.captured_at, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()).TotalSeconds;
                var error = VisualCoordinates.Validate(capture, current, age);
                return Response.Success("Capture geometry and current interaction validity.", new { capture, current, interaction_valid = error == null, invalid_reason = error, age_seconds = age });
            }
            catch (Exception ex) { return Response.Error("GAME_VIEW_GEOMETRY_UNAVAILABLE", new { detail = ex.Message }); }
        }

        [Description("Read RectTransform/Renderer screen bounds in bottom-left render pixels and top-left image pixels using the same contract as capture/click/raycast. Optional fresh capture_id maps bounds into that image's resolution. Returns corners and depth/visibility hints; bounds alone do not prove visibility, masking or input reachability.")]
        [ReadOnlyTool]
        public static object GetObjectScreenBounds([ToolParam("Exact object ID/path/name")] string target,
            [ToolParam("Object selector, prefer by_id", Required = false)] string find_method = "by_id_or_name_or_path",
            [ToolParam("Optional Camera ID (required when the object has no applicable camera)", Required = false)] string camera_id = null,
            [ToolParam("Optional fresh screenshot ID", Required = false)] string capture_id = null)
        {
            try
            {
                var matches = ObjectsHelper.FindObjects(target, find_method, true, searchInactive: true);
                if (matches.Count != 1) return Response.Error(matches.Count == 0 ? "TARGET_NOT_FOUND" : "AMBIGUOUS_TARGET", new { count = matches.Count });
                var go = matches[0];
                if (!VisualCoordinates.TryResolve(0, 0, "render_pixels", "bottom_left", capture_id, out _, out var geometry, out var coordinateError)) return Response.Error(coordinateError);
                Camera camera = null;
                if (!string.IsNullOrEmpty(camera_id))
                {
                    var obj = ObjectIdHelper.ToObject(camera_id);
                    camera = obj as Camera ?? (obj as GameObject)?.GetComponent<Camera>();
                    if (camera == null) return Response.Error("CAMERA_NOT_FOUND");
                }
                Vector3[] world;
                var rect = go.transform as RectTransform;
                bool overlay = false;
                if (rect != null)
                {
                    var canvas = go.GetComponentInParent<Canvas>();
                    if (canvas == null) return Response.Error("CANVAS_NOT_FOUND");
                    overlay = canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;
                    if (overlay) camera = null;
                    else camera = camera ?? canvas.worldCamera ?? Camera.main;
                    world = new Vector3[4]; rect.GetWorldCorners(world);
                }
                else
                {
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer == null) return Response.Error("RECT_TRANSFORM_OR_RENDERER_REQUIRED");
                    camera = camera ?? Camera.main;
                    var bounds = renderer.bounds;
                    world = Enumerable.Range(0, 8).Select(i => bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))).ToArray();
                }
                if (!overlay && camera == null) return Response.Error("CAMERA_NOT_FOUND");
                if (camera != null && (camera.targetTexture != null || camera.targetDisplay != 0)) return Response.Error("CAMERA_NOT_ON_PRIMARY_GAME_VIEW");
                var projected = world.Select(p => VisualCoordinates.WorldToRender(p, camera, geometry)).ToArray();
                var image = projected.Select(p => VisualCoordinates.ToImage(p, geometry, "top_left")).ToArray();
                return Response.Success("Object bounds from current transforms/layout.", new
                {
                    object_id = ObjectIdHelper.GetSerializableId(go), path = ObjectsHelper.GetGameObjectPath(go), geometry,
                    camera_id = ObjectIdHelper.GetSerializableId(camera), active = go.activeInHierarchy,
                    behind_camera = !overlay && projected.All(p => p.z <= 0), crosses_camera_plane = !overlay && projected.Any(p => p.z <= 0),
                    render_bounds = Bounds(projected.Select(p => (Vector2)p).ToArray()), image_bounds = Bounds(image),
                    render_corners = projected.Select(p => new { x = p.x, y = p.y, depth = p.z }).ToArray(),
                    image_corners = image.Select(p => new { x = p.x, y = p.y }).ToArray(),
                    note = "No layout rebuild was forced. Verify laid-out runtime state; this does not test occlusion or clipping. Bounds crossing the camera plane are not reliable click targets."
                });
            }
            catch (Exception ex) { return Response.Error("SCREEN_BOUNDS_FAILED", new { detail = ex.Message }); }
        }
        private static object Bounds(Vector2[] points) => new { x_min = points.Min(p => p.x), y_min = points.Min(p => p.y), x_max = points.Max(p => p.x), y_max = points.Max(p => p.y) };
    }
}
