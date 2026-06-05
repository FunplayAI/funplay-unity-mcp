// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Screenshot")]
    internal static class ScreenshotFunctions
    {
        private const string ImagePrefix = "data:image/png;base64,";
        private const int MultiviewMaxAngles = 36;

        [Description("Capture a screenshot of the Game View (what the main camera sees). Returns a base64-encoded PNG image.")]
        [ReadOnlyTool]
        public static string CaptureGameView(
            [ToolParam("Width of the screenshot in pixels", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels", Required = false)] int height = 0)
        {
            // First try: read the Game View's last-rendered frame directly from the
            // PlayModeView's internal RenderTexture. This is pipeline-agnostic and
            // works correctly with URP, HDRP, and Built-in render pipelines.
            // camera.Render() (used in the fallback) bypasses Scriptable Render Pipelines
            // and produces a black image for URP/HDRP cameras.
            var directCapture = TryCapturePlayModeViewRT();
            if (directCapture != null)
                return directCapture;

            // Fallback: render the camera manually into a temporary RT.
            // Works for Built-in pipeline in Edit Mode.
            if (!TryResolveGameViewSize(ref width, ref height))
            {
                width = Mathf.Clamp(width > 0 ? width : 512, 64, 4096);
                height = Mathf.Clamp(height > 0 ? height : 512, 64, 4096);
            }

            var camera = Camera.main;
            if (camera == null)
                camera = UnityEngine.Object.FindFirstObjectByType<Camera>();

            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { hint = "Add a Camera component to capture the Game View." });

            return CaptureWithUI(camera, width, height);
        }

        [Description("Capture a screenshot of the Scene View (the editor's scene camera perspective). Returns a base64-encoded PNG image.")]
        [ReadOnlyTool]
        public static string CaptureSceneView(
            [ToolParam("Width of the screenshot in pixels", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels", Required = false)] int height = 0)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return ToolResultFormatter.Error("SCENE_VIEW_NOT_OPEN", new { hint = "Open a Scene View window first." });

            var camera = sceneView.camera;
            if (camera == null)
                return ToolResultFormatter.Error("SCENE_VIEW_CAMERA_UNAVAILABLE");

            if (width <= 0 || height <= 0)
            {
                width = Mathf.RoundToInt(camera.pixelWidth);
                height = Mathf.RoundToInt(camera.pixelHeight);
            }

            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);

            return CaptureFromCamera(camera, width, height);
        }

        [Description("Capture the scene from multiple synthesized camera angles around a target. " +
                     "Useful for visual reviews (\"show me this prefab/object from every side\") without manually " +
                     "moving the editor camera. Two modes:\n" +
                     "  - surround: 6 axis-aligned views (front/back/left/right/top/bottom).\n" +
                     "  - orbit: cartesian product of evenly-spaced azimuth angles × explicit elevations.\n" +
                     "Target defaults to the active scene's bounds center; pass target_name to focus on a specific GameObject. " +
                     "Each capture is rendered with a temporary camera (current scene cameras are not modified). " +
                     "Returns base64 PNG images in data.captures[] together with the angle metadata of each.")]
        [ReadOnlyTool]
        public static object CaptureMultiview(
            [ToolParam("Name (or hierarchy path) of the GameObject to focus on. Empty/null uses the active scene's bounds center.", Required = false)] string target_name = null,
            [ToolParam("Capture mode: surround (6 axis views, default) or orbit (azimuth × elevations grid).", Required = false)] string mode = "surround",
            [ToolParam("Per-frame width in pixels. Default 512.", Required = false)] int width = 512,
            [ToolParam("Per-frame height in pixels. Default 512.", Required = false)] int height = 512,
            [ToolParam("Orbit only: number of evenly-spaced azimuth samples (1-36). Default 8.", Required = false)] int orbit_angles = 8,
            [ToolParam("Orbit only: comma-separated elevations in degrees (e.g. \"0,30,-15\"). Default \"0,30,-15\".", Required = false)] string orbit_elevations = "0,30,-15",
            [ToolParam("Camera distance from target. 0 = auto-fit based on target bounds. Default 0.", Required = false)] float orbit_distance = 0f,
            [ToolParam("Camera vertical field of view in degrees. Default 60.", Required = false)] float orbit_fov = 60f)
        {
            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);
            orbit_fov = Mathf.Clamp(orbit_fov, 10f, 170f);

            var modeNormalized = (mode ?? "surround").Trim().ToLowerInvariant();
            if (modeNormalized != "surround" && modeNormalized != "orbit")
                return Response.Error("INVALID_MODE",
                    new { provided = mode, accepted = new[] { "surround", "orbit" } });

            if (!TryResolveMultiviewTarget(target_name, out var targetCenter, out var targetExtent, out var targetName, out var errorCode, out var errorDetail))
                return Response.Error(errorCode, errorDetail);

            var distance = orbit_distance > 0f
                ? orbit_distance
                : Mathf.Max(targetExtent * 2.5f, 1f);

            List<(float azimuth, float elevation)> angles;
            if (modeNormalized == "surround")
            {
                // Front, back, right, left, top, bottom
                angles = new List<(float, float)>
                {
                    (0f, 0f), (180f, 0f), (90f, 0f), (-90f, 0f), (0f, 89f), (0f, -89f),
                };
            }
            else
            {
                var clampedAngles = Mathf.Clamp(orbit_angles, 1, MultiviewMaxAngles);
                var elevations = ParseElevations(orbit_elevations);
                if (elevations.Count == 0)
                    return Response.Error("INVALID_ELEVATIONS",
                        new { provided = orbit_elevations, hint = "Provide a comma-separated list of degrees (e.g. \"0,30,-15\")." });

                angles = new List<(float, float)>(clampedAngles * elevations.Count);
                for (int e = 0; e < elevations.Count; e++)
                {
                    var elevation = Mathf.Clamp(elevations[e], -89f, 89f);
                    for (int a = 0; a < clampedAngles; a++)
                    {
                        var azimuth = 360f * a / clampedAngles;
                        angles.Add((azimuth, elevation));
                    }
                }
            }

            var cameraHostObject = new GameObject("_FunplayMultiviewCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = null;
            try
            {
                camera = cameraHostObject.AddComponent<Camera>();
                camera.fieldOfView = orbit_fov;
                camera.nearClipPlane = Mathf.Max(0.01f, distance * 0.01f);
                camera.farClipPlane = Mathf.Max(distance * 10f, 1000f);
                camera.clearFlags = CameraClearFlags.Skybox;

                var captures = new List<object>(angles.Count);
                foreach (var (azimuth, elevation) in angles)
                {
                    PositionCameraOnSphere(camera, targetCenter, distance, azimuth, elevation);
                    var image = CaptureFromCamera(camera, width, height);
                    captures.Add(new { angle = new { azimuth, elevation }, image });
                }

                return Response.Success(
                    $"Captured {captures.Count} multiview frame(s) of '{targetName}' in {modeNormalized} mode.",
                    new
                    {
                        target = new
                        {
                            name = targetName,
                            center = new { x = targetCenter.x, y = targetCenter.y, z = targetCenter.z },
                            bounds_extent = targetExtent
                        },
                        mode = modeNormalized,
                        distance,
                        fov = orbit_fov,
                        per_frame = new { width, height },
                        count = captures.Count,
                        captures
                    });
            }
            catch (Exception ex)
            {
                return Response.Error("MULTIVIEW_FAILED", new { message = ex.Message });
            }
            finally
            {
                if (camera != null) UnityEngine.Object.DestroyImmediate(camera);
                UnityEngine.Object.DestroyImmediate(cameraHostObject);
            }
        }

        private static void PositionCameraOnSphere(Camera camera, Vector3 center, float distance, float azimuthDegrees, float elevationDegrees)
        {
            var elevation = elevationDegrees * Mathf.Deg2Rad;
            var azimuth = azimuthDegrees * Mathf.Deg2Rad;
            var offset = new Vector3(
                distance * Mathf.Cos(elevation) * Mathf.Sin(azimuth),
                distance * Mathf.Sin(elevation),
                distance * Mathf.Cos(elevation) * Mathf.Cos(azimuth));
            camera.transform.position = center + offset;
            camera.transform.LookAt(center, Mathf.Abs(elevationDegrees) > 80f ? Vector3.forward : Vector3.up);
        }

        private static List<float> ParseElevations(string raw)
        {
            var result = new List<float>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;
            foreach (var part in raw.Split(','))
            {
                var trimmed = part.Trim();
                if (float.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                    result.Add(value);
            }
            return result;
        }

        private static bool TryResolveMultiviewTarget(
            string requestedName,
            out Vector3 center,
            out float extent,
            out string resolvedName,
            out string errorCode,
            out object errorDetail)
        {
            center = Vector3.zero;
            extent = 1f;
            resolvedName = null;
            errorCode = null;
            errorDetail = null;

            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                var go = GameObject.Find(requestedName);
                if (go == null)
                {
                    errorCode = "TARGET_NOT_FOUND";
                    errorDetail = new { name = requestedName };
                    return false;
                }
                var bounds = ComputeRenderableBounds(go);
                center = bounds.center;
                extent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z, 0.25f);
                resolvedName = go.name;
                return true;
            }

            // Fall back to scene-wide bounds.
            Bounds? merged = null;
            foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (renderer == null || !renderer.gameObject.activeInHierarchy) continue;
                merged = merged.HasValue ? Encapsulate(merged.Value, renderer.bounds) : renderer.bounds;
            }
            if (!merged.HasValue)
            {
                errorCode = "EMPTY_SCENE";
                errorDetail = new { hint = "No active Renderers found. Pass target_name to focus on a specific GameObject." };
                return false;
            }
            center = merged.Value.center;
            extent = Mathf.Max(merged.Value.extents.x, merged.Value.extents.y, merged.Value.extents.z, 0.25f);
            resolvedName = "<scene>";
            return true;
        }

        private static Bounds ComputeRenderableBounds(GameObject root)
        {
            Bounds? merged = null;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                merged = merged.HasValue ? Encapsulate(merged.Value, renderer.bounds) : renderer.bounds;
            }
            if (merged.HasValue) return merged.Value;
            // Fall back to transform position with a small default extent.
            return new Bounds(root.transform.position, Vector3.one);
        }

        private static Bounds Encapsulate(Bounds a, Bounds b)
        {
            a.Encapsulate(b);
            return a;
        }

        /// <summary>
        /// Reads the last rendered frame directly from the PlayModeView's internal
        /// RenderTexture via reflection. This approach is render-pipeline-agnostic:
        /// it captures whatever Unity has already rendered to the Game View, including
        /// all URP/HDRP post-processing and ScreenSpaceOverlay UI.
        ///
        /// The source texture is often in BGRA32 format (Metal/Vulkan) and cannot be
        /// read directly with Texture2D.ReadPixels — it must first be blitted into an
        /// ARGB32 RenderTexture to produce a correctly-formatted, CPU-readable buffer.
        ///
        /// Returns null when the Game View is unavailable or has not rendered a frame.
        /// </summary>
        private static string TryCapturePlayModeViewRT()
        {
            RenderTexture blitRT = null;
            Texture2D screenshot = null;
            var previousActive = RenderTexture.active;

            try
            {
                var playModeViewType = Type.GetType("UnityEditor.PlayModeView,UnityEditor");
                var getMainMethod = playModeViewType?.GetMethod(
                    "GetMainPlayModeView",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var playModeView = getMainMethod?.Invoke(null, null);
                if (playModeView == null)
                    return null;

                var rtField = playModeView.GetType().GetField(
                    "m_RenderTexture",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var sourceRT = rtField?.GetValue(playModeView) as RenderTexture;

                if (sourceRT == null || !sourceRT.IsCreated() || sourceRT.width <= 0 || sourceRT.height <= 0)
                    return null;

                // Blit into a readable ARGB32 RT. Direct ReadPixels from the source
                // (BGRA32 on Metal/URP) produces black pixels on some platforms.
                blitRT = new RenderTexture(sourceRT.width, sourceRT.height, 0, RenderTextureFormat.ARGB32);
                blitRT.Create();
                Graphics.Blit(sourceRT, blitRT);

                RenderTexture.active = blitRT;
                screenshot = new Texture2D(blitRT.width, blitRT.height, TextureFormat.RGB24, false);
                screenshot.ReadPixels(new Rect(0, 0, blitRT.width, blitRT.height), 0, 0);
                screenshot.Apply();

                var pngBytes = screenshot.EncodeToPNG();
                return ImagePrefix + Convert.ToBase64String(pngBytes);
            }
            catch
            {
                return null;
            }
            finally
            {
                RenderTexture.active = previousActive;

                if (blitRT != null)
                {
                    blitRT.Release();
                    UnityEngine.Object.DestroyImmediate(blitRT);
                }
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        private static bool TryResolveGameViewSize(ref int width, ref int height)
        {
            if (width > 0 && height > 0)
            {
                width = Mathf.Clamp(width, 64, 4096);
                height = Mathf.Clamp(height, 64, 4096);
                return true;
            }

            try
            {
                var playModeViewType = Type.GetType("UnityEditor.PlayModeView,UnityEditor");
                var getMainPlayModeView = playModeViewType?.GetMethod(
                    "GetMainPlayModeView",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var playModeView = getMainPlayModeView?.Invoke(null, null);
                if (playModeView == null)
                    return false;

                var targetRenderSizeProperty = playModeView.GetType().GetProperty(
                    "targetRenderSize",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var targetSizeProperty = playModeView.GetType().GetProperty(
                    "targetSize",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                var value = targetRenderSizeProperty?.GetValue(playModeView, null)
                           ?? targetSizeProperty?.GetValue(playModeView, null);

                if (value is Vector2 vector2 && vector2.x > 0f && vector2.y > 0f)
                {
                    width = Mathf.Clamp(Mathf.RoundToInt(vector2.x), 64, 4096);
                    height = Mathf.Clamp(Mathf.RoundToInt(vector2.y), 64, 4096);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        /// <summary>
        /// Captures the game view including ScreenSpaceOverlay UI by temporarily
        /// switching overlay canvases to ScreenSpaceCamera during render.
        /// </summary>
        private static string CaptureWithUI(Camera camera, int width, int height)
        {
            RenderTexture renderTexture = null;
            RenderTexture previousTarget = null;
            RenderTexture previousActive = null;
            Texture2D screenshot = null;
            var overlayCanvases = new List<Canvas>();

            try
            {
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.Create();

                // Find all ScreenSpaceOverlay canvases and temporarily switch to ScreenSpaceCamera
                var allCanvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
                foreach (var canvas in allCanvases)
                {
                    if (canvas.renderMode == RenderMode.ScreenSpaceOverlay && canvas.gameObject.activeInHierarchy)
                    {
                        overlayCanvases.Add(canvas);
                        canvas.renderMode = RenderMode.ScreenSpaceCamera;
                        canvas.worldCamera = camera;
                        canvas.planeDistance = camera.nearClipPlane + 0.1f;
                    }
                }

                previousTarget = camera.targetTexture;
                previousActive = RenderTexture.active;

                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
                screenshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                screenshot.Apply();

                var pngBytes = screenshot.EncodeToPNG();
                var base64 = Convert.ToBase64String(pngBytes);

                return ImagePrefix + base64;
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { message = ex.Message });
            }
            finally
            {
                // Restore overlay canvases
                foreach (var canvas in overlayCanvases)
                {
                    if (canvas != null)
                    {
                        canvas.worldCamera = null;
                        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    }
                }

                if (camera != null)
                    camera.targetTexture = previousTarget;

                RenderTexture.active = previousActive;

                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        private static string CaptureFromCamera(Camera camera, int width, int height)
        {
            RenderTexture renderTexture = null;
            RenderTexture previousTarget = null;
            RenderTexture previousActive = null;
            Texture2D screenshot = null;

            try
            {
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.Create();

                previousTarget = camera.targetTexture;
                previousActive = RenderTexture.active;

                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
                screenshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                screenshot.Apply();

                var pngBytes = screenshot.EncodeToPNG();
                var base64 = Convert.ToBase64String(pngBytes);

                return ImagePrefix + base64;
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { message = ex.Message });
            }
            finally
            {
                if (camera != null)
                    camera.targetTexture = previousTarget;

                RenderTexture.active = previousActive;

                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }
    }
}
