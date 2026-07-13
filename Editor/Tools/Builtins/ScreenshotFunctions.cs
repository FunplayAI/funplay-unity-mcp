// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Screenshot")]
    internal static class ScreenshotFunctions
    {
        private const string ImagePrefix = "data:image/png;base64,";
        private const int MultiviewMaxAngles = 36;
        private const string ScreenshotDirRelative = "Library/FunplayMcp/Screenshots";

        private const string SaveToFileParamDescription =
            "Save the PNG to disk and return its file path instead of base64 image data. " +
            "Use for high-resolution captures whose base64 payload would be too large for the transport.";

        private const string OutputPathParamDescription =
            "Optional output .png path under the Unity project root (absolute, or relative to the project root). " +
            "Default: " + ScreenshotDirRelative + "/<name>-<timestamp>.png. Only used when save_to_file=true.";

        // Threshold on RAW PNG bytes for spilling a capture to disk instead of inlining it.
        // The transmitted payload is base64 (~1.33x the raw bytes) and a base64 payload around
        // ~1MB reliably drops the client-side MCP socket, so this raw limit is set well below
        // that: 512KB raw -> ~683KB base64, leaving headroom under the drop point.
        internal const int MaxInlineScreenshotBytes = 512 * 1024;

        internal static bool ShouldSpillScreenshotToFile(long pngBytes, bool saveToFile)
        {
            return saveToFile || pngBytes > MaxInlineScreenshotBytes;
        }

        /// <summary>
        /// Single exit point for all capture tools: base64 data URI by default, or
        /// write-to-disk + JSON path result when the caller asked for a file (or when the
        /// payload is too large to send inline, in which case it auto-falls back to a file).
        /// </summary>
        private static string FinishCapture(byte[] pngBytes, bool saveToFile, string outputPath, string defaultBaseName)
        {
            var spillToFile = ShouldSpillScreenshotToFile(pngBytes.Length, saveToFile);
            var autoFallback = spillToFile && !saveToFile;

            if (!spillToFile)
                return ImagePrefix + Convert.ToBase64String(pngBytes);

            if (!TrySaveScreenshotBytes(pngBytes, outputPath, defaultBaseName, out var savedPath, out var error))
                return ToolResultFormatter.Error("SCREENSHOT_SAVE_FAILED", error);

            return JsonConvert.SerializeObject(Response.Success(
                autoFallback
                    ? $"Screenshot ({pngBytes.Length} bytes) exceeded the inline transport limit and was saved to a file instead. Read the file to view it."
                    : "Screenshot saved to file.",
                new { path = savedPath, bytes = pngBytes.Length, fell_back_to_file = autoFallback }));
        }

        private static bool TrySaveScreenshotBytes(byte[] pngBytes, string outputPath, string baseName, out string savedPath, out object error)
        {
            savedPath = null;
            error = null;

            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
                if (!TryResolveScreenshotOutputPath(outputPath, baseName, projectRoot, out var path, out error))
                    return false;

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(path, pngBytes);
                savedPath = path;
                return true;
            }
            catch (Exception ex)
            {
                error = new { message = ex.Message };
                return false;
            }
        }

        internal static bool TryResolveScreenshotOutputPath(
            string outputPath,
            string baseName,
            string projectRoot,
            out string path,
            out object error)
        {
            path = null;
            error = null;

            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                error = new { hint = "Unity project root could not be resolved." };
                return false;
            }

            var normalizedRoot = Path.GetFullPath(projectRoot);
            string candidatePath;
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                var fileName = baseName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
                candidatePath = Path.Combine(normalizedRoot, ScreenshotDirRelative, fileName);
            }
            else
            {
                var trimmed = outputPath.Trim();
                if (!trimmed.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    error = new { provided = outputPath, hint = "output_path must end with .png" };
                    return false;
                }

                candidatePath = Path.IsPathRooted(trimmed)
                    ? trimmed
                    : Path.Combine(normalizedRoot, trimmed);
            }

            var normalizedPath = Path.GetFullPath(candidatePath);
            if (!IsPathInsideDirectory(normalizedPath, normalizedRoot))
            {
                error = new
                {
                    provided = outputPath,
                    project_root = normalizedRoot,
                    hint = "output_path must resolve inside the Unity project root."
                };
                return false;
            }

            path = normalizedPath;
            return true;
        }

        private static bool IsPathInsideDirectory(string path, string directory)
        {
            var comparison = StringComparison.OrdinalIgnoreCase;
            var normalizedPath = Path.GetFullPath(path);
            var normalizedDirectory = EnsureTrailingSeparator(Path.GetFullPath(directory));
            return normalizedPath.StartsWith(normalizedDirectory, comparison);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            var last = path[path.Length - 1];
            if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
                return path;

            return path + Path.DirectorySeparatorChar;
        }

        [Description("Capture a screenshot of the Game View (what the main camera sees). Returns a base64-encoded PNG image, " +
                     "or a saved file path when save_to_file=true.")]
        [ReadOnlyTool]
        public static string CaptureGameView(
            [ToolParam("Width of the screenshot in pixels", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels", Required = false)] int height = 0,
            [ToolParam(SaveToFileParamDescription, Required = false)] bool save_to_file = false,
            [ToolParam(OutputPathParamDescription, Required = false)] string output_path = null)
        {
            if (!TryResolveGameViewSize(ref width, ref height))
            {
                width = Mathf.Clamp(width > 0 ? width : 512, 64, 4096);
                height = Mathf.Clamp(height > 0 ? height : 512, 64, 4096);
            }

            var playModePng = TryCapturePlayModeViewPngBytes(width, height);
            if (playModePng != null)
                return FinishCapture(playModePng, save_to_file, output_path, "game-view");

            var camera = Camera.main;
            if (camera == null)
                camera = UnityEngine.Object.FindFirstObjectByType<Camera>();

            if (camera == null)
                return ToolResultFormatter.Error("CAMERA_NOT_FOUND", new { hint = "Add a Camera component to capture the Game View." });

            try
            {
                return FinishCapture(CaptureWithUIPngBytes(camera, width, height), save_to_file, output_path, "game-view");
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { message = ex.Message });
            }
        }

        [Description("Capture the Unity Device Simulator screen. Optionally selects a Simulator device and draws a Safe Area outline over the captured image. " +
                     "If the Simulator view has to be opened or the device changes, retry once after Unity renders the next editor frame.")]
        [ReadOnlyTool]
        public static async Task<string> CaptureSimulatorView(
            [ToolParam("Width of the screenshot in pixels. Default uses the simulator screen texture size. If only width is provided, height preserves source aspect.", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels. Default uses the simulator screen texture size. If only height is provided, width preserves source aspect.", Required = false)] int height = 0,
            [ToolParam("Simulator device name to select before capture, e.g. 'iPhone 12' or 'Apple iPad Pro 12.9 (2018)'.", Required = false)] string device_name = null,
            [ToolParam("Draw a high-contrast Safe Area outline on top of the captured simulator image.", Required = false)] bool safe_area_overlay = false,
            [ToolParam("Open the Simulator view if no Simulator window is currently open.", Required = false)] bool open_if_needed = true,
            [ToolParam(SaveToFileParamDescription, Required = false)] bool save_to_file = false,
            [ToolParam(OutputPathParamDescription, Required = false)] string output_path = null)
        {
            if (!TryGetSimulatorWindow(open_if_needed, out var simulatorWindow, out var simulatorError))
                return ToolResultFormatter.Error("SIMULATOR_VIEW_NOT_AVAILABLE", simulatorError);

            if (simulatorWindow is EditorWindow editorWindow)
            {
                editorWindow.Show();
                editorWindow.Focus();
                editorWindow.Repaint();
            }

            if (!TrySelectSimulatorDevice(simulatorWindow, device_name, out var deviceChanged, out var deviceSelectionError))
                return ToolResultFormatter.Error("SIMULATOR_DEVICE_NOT_FOUND", deviceSelectionError);

            if (deviceChanged)
                await WaitForEditorFramesAsync(2);

            var deviceView = GetSimulatorDeviceView(simulatorWindow);
            if (deviceView == null)
            {
                return ToolResultFormatter.Error("SIMULATOR_DEVICE_VIEW_UNAVAILABLE", new
                {
                    hint = "The Simulator window is available, but its internal DeviceView could not be resolved. Try updating Unity or use capture_game_view instead."
                });
            }

            var sourceTexture = GetPropertyOrField<Texture>(deviceView, "PreviewTexture");
            if ((sourceTexture == null || sourceTexture.width <= 0 || sourceTexture.height <= 0) && !string.IsNullOrWhiteSpace(device_name))
            {
                await WaitForEditorFramesAsync(2);
                deviceView = GetSimulatorDeviceView(simulatorWindow);
                sourceTexture = GetPropertyOrField<Texture>(deviceView, "PreviewTexture");
            }

            if (sourceTexture == null || sourceTexture.width <= 0 || sourceTexture.height <= 0)
            {
                return ToolResultFormatter.Error("SIMULATOR_VIEW_NOT_RENDERED", new
                {
                    hint = open_if_needed
                        ? "The Simulator view is open but has not rendered a preview texture yet. Retry after the next editor frame."
                        : "Open the Simulator view and wait for it to render, or retry with open_if_needed=true."
                });
            }

            Rect? safeArea = null;
            if (safe_area_overlay)
            {
                safeArea = ResolveSimulatorSafeArea(deviceView, sourceTexture.width, sourceTexture.height);
            }

            return CaptureTexture(sourceTexture, width, height, safeArea, flipVertically: true,
                saveToFile: save_to_file, outputPath: output_path, defaultBaseName: "simulator-view");
        }

        [Description("Capture a screenshot of the Scene View (the editor's scene camera perspective). Returns a base64-encoded PNG image, " +
                     "or a saved file path when save_to_file=true.")]
        [ReadOnlyTool]
        public static string CaptureSceneView(
            [ToolParam("Width of the screenshot in pixels", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels", Required = false)] int height = 0,
            [ToolParam(SaveToFileParamDescription, Required = false)] bool save_to_file = false,
            [ToolParam(OutputPathParamDescription, Required = false)] string output_path = null)
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

            try
            {
                return FinishCapture(CaptureFromCameraPngBytes(camera, width, height), save_to_file, output_path, "scene-view");
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { message = ex.Message });
            }
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
            [ToolParam("GameObject name, hierarchy path, or instance ID to focus on (finds inactive objects too). Empty/null uses the active scene's bounds center.", Required = false)] string target_name = null,
            [ToolParam("Capture mode: surround (6 axis views, default) or orbit (azimuth × elevations grid).", Required = false)] string mode = "surround",
            [ToolParam("Per-frame width in pixels. Default 512.", Required = false)] int width = 512,
            [ToolParam("Per-frame height in pixels. Default 512.", Required = false)] int height = 512,
            [ToolParam("Orbit only: number of evenly-spaced azimuth samples (1-36). Default 8.", Required = false)] int orbit_angles = 8,
            [ToolParam("Orbit only: comma-separated elevations in degrees (e.g. \"0,30,-15\"). Default \"0,30,-15\".", Required = false)] string orbit_elevations = "0,30,-15",
            [ToolParam("Camera distance from target. 0 = auto-fit based on target bounds. Default 0.", Required = false)] float orbit_distance = 0f,
            [ToolParam("Camera vertical field of view in degrees. Default 60.", Required = false)] float orbit_fov = 60f,
            [ToolParam("Save each frame as a PNG under " + ScreenshotDirRelative + " and return file paths instead of base64 image data. " +
                       "Use for large captures whose combined base64 payload would be too large for the transport.", Required = false)] bool save_to_file = false)
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

                // Capture all frames first, then decide inline-vs-file from the COMBINED size.
                // The default (save_to_file=false) path previously concatenated every frame's
                // base64 with no size check -- a 6-36 frame multiview easily exceeds the inline
                // transport limit and drops the socket. Spill all frames to files when the total
                // would be too large (mirrors FinishCapture's single-capture auto-fallback).
                var frames = new List<(float az, float el, byte[] png)>(angles.Count);
                foreach (var (azimuth, elevation) in angles)
                {
                    PositionCameraOnSphere(camera, targetCenter, distance, azimuth, elevation);
                    frames.Add((azimuth, elevation, CaptureFromCameraPngBytes(camera, width, height)));
                }

                long totalBytes = 0;
                foreach (var f in frames) totalBytes += f.png.Length;
                var spillToFiles = ShouldSpillScreenshotToFile(totalBytes, save_to_file);
                var autoFallback = spillToFiles && !save_to_file;

                var captures = new List<object>(frames.Count);
                var frameIndex = 0;
                foreach (var (azimuth, elevation, pngBytes) in frames)
                {
                    if (spillToFiles)
                    {
                        var baseName = $"multiview-{frameIndex:D2}-az{azimuth:0}-el{elevation:0}";
                        if (!TrySaveScreenshotBytes(pngBytes, null, baseName, out var savedPath, out var saveError))
                            return Response.Error("SCREENSHOT_SAVE_FAILED", saveError);
                        captures.Add(new { angle = new { azimuth, elevation }, path = savedPath });
                    }
                    else
                    {
                        captures.Add(new { angle = new { azimuth, elevation }, image = ImagePrefix + Convert.ToBase64String(pngBytes) });
                    }
                    frameIndex++;
                }

                var msg = autoFallback
                    ? $"Captured {captures.Count} multiview frame(s) of '{targetName}' ({modeNormalized}); combined {totalBytes} bytes exceeded the inline limit so frames were saved to files. Read them to view."
                    : $"Captured {captures.Count} multiview frame(s) of '{targetName}' in {modeNormalized} mode.";

                return Response.Success(msg,
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
                        total_bytes = totalBytes,
                        saved_to_files = spillToFiles,
                        fell_back_to_file = autoFallback,
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

        [Description("Capture a screenshot of any open EditorWindow (Inspector, Console, Project, custom tool windows...) " +
                     "identified by its title or type name. Captures directly from the window's render surface via the editor's " +
                     "internal GUIView, so the window does not need to be unoccluded on screen (it does need to be open). " +
                     "Returns a base64-encoded PNG image, or a saved file path when save_to_file=true.")]
        [ReadOnlyTool]
        public static string CaptureEditorWindow(
            [ToolParam("Window title (e.g. 'Inspector', 'MCP Server') or window type name (e.g. 'ConsoleWindow'). " +
                       "Case-insensitive. Exact title match wins, then title contains, then type name.")] string window,
            [ToolParam("Width of the screenshot in pixels. 0 keeps the window's native size.", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels. 0 keeps the window's native size.", Required = false)] int height = 0,
            [ToolParam(SaveToFileParamDescription, Required = false)] bool save_to_file = false,
            [ToolParam(OutputPathParamDescription, Required = false)] string output_path = null,
            [ToolParam("Focus the window before capturing (brings its tab to the front of its dock area). Default true.", Required = false)] bool focus = true)
        {
            if (string.IsNullOrWhiteSpace(window))
                return ToolResultFormatter.Error("INVALID_WINDOW", new { hint = "Provide a window title or type name." });

            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(w => w != null)
                .ToArray();

            var target = ResolveEditorWindow(allWindows, window);
            if (target == null)
            {
                return ToolResultFormatter.Error("WINDOW_NOT_FOUND", new
                {
                    requested = window,
                    available = allWindows
                        .Select(w => new { title = w.titleContent.text, type = w.GetType().Name })
                        .ToArray()
                });
            }

            var guiViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");
            var grabPixels = guiViewType?.GetMethod("GrabPixels", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.NonPublic | BindingFlags.Instance);
            if (grabPixels == null || parentField == null)
            {
                return ToolResultFormatter.Error("EDITOR_WINDOW_CAPTURE_UNSUPPORTED", new
                {
                    hint = "UnityEditor.GUIView.GrabPixels is not available in this Unity version."
                });
            }

            if (focus)
            {
                target.Focus();
                target.Repaint();
            }

            var parent = parentField.GetValue(target);
            if (parent == null || !guiViewType.IsInstanceOfType(parent))
            {
                return ToolResultFormatter.Error("EDITOR_WINDOW_NOT_RENDERED", new
                {
                    window = target.titleContent.text,
                    hint = "The window has no host GUIView yet. Make sure it is open and visible, then retry."
                });
            }

            var pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            var nativeWidth = Mathf.Clamp(Mathf.RoundToInt(target.position.width * pixelsPerPoint), 16, 8192);
            var nativeHeight = Mathf.Clamp(Mathf.RoundToInt(target.position.height * pixelsPerPoint), 16, 8192);

            RenderTexture grabTexture = null;
            Texture2D screenshot = null;
            try
            {
                grabTexture = new RenderTexture(nativeWidth, nativeHeight, 0, RenderTextureFormat.ARGB32);
                grabTexture.Create();
                grabPixels.Invoke(parent, new object[] { grabTexture, new Rect(0, 0, nativeWidth, nativeHeight) });

                // GrabPixels output reads back vertically inverted through ReadPixels, same
                // as PlayModeView's render texture; ReadTextureToTexture2D also handles the
                // optional resize (aspect preserved when only one dimension is provided).
                ResolveCaptureSize(ref width, ref height, nativeWidth, nativeHeight);
                screenshot = ReadTextureToTexture2D(grabTexture, width, height, flipVertically: true);

                var baseName = "window-" + SanitizeFileNameFragment(target.titleContent.text);
                return FinishCapture(screenshot.EncodeToPNG(), save_to_file, output_path, baseName);
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { window = target.titleContent.text, message = ex.Message });
            }
            finally
            {
                if (grabTexture != null)
                {
                    grabTexture.Release();
                    UnityEngine.Object.DestroyImmediate(grabTexture);
                }
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        private static EditorWindow ResolveEditorWindow(EditorWindow[] windows, string requested)
        {
            var trimmed = requested.Trim();

            EditorWindow PickPreferFocused(IEnumerable<EditorWindow> candidates)
            {
                EditorWindow first = null;
                foreach (var candidate in candidates)
                {
                    if (candidate.hasFocus)
                        return candidate;
                    if (first == null)
                        first = candidate;
                }
                return first;
            }

            var exactTitle = PickPreferFocused(windows.Where(w =>
                string.Equals(w.titleContent.text, trimmed, StringComparison.OrdinalIgnoreCase)));
            if (exactTitle != null)
                return exactTitle;

            var containsTitle = PickPreferFocused(windows.Where(w =>
                w.titleContent.text != null &&
                w.titleContent.text.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0));
            if (containsTitle != null)
                return containsTitle;

            return PickPreferFocused(windows.Where(w =>
                string.Equals(w.GetType().Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                w.GetType().Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static string SanitizeFileNameFragment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "window";

            var chars = value.Trim().Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
            var sanitized = new string(chars).Trim('-');
            return string.IsNullOrEmpty(sanitized) ? "window" : sanitized;
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
                var go = ObjectsHelper.FindTarget(requestedName);
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

        internal static bool TryResolveGameViewSize(ref int width, ref int height)
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

        private static bool TryGetSimulatorWindow(bool openIfNeeded, out object simulatorWindow, out object errorDetail)
        {
            simulatorWindow = null;
            errorDetail = null;

            var simulatorWindowType = ResolveType(
                "UnityEditor.DeviceSimulation.SimulatorWindow",
                "UnityEditor.DeviceSimulatorModule");
            if (simulatorWindowType == null)
            {
                errorDetail = new
                {
                    hint = "Unity Device Simulator is not available in this editor. Use capture_game_view instead."
                };
                return false;
            }

            simulatorWindow = GetExistingSimulatorWindow(simulatorWindowType);
            if (simulatorWindow != null)
                return true;

            if (!openIfNeeded)
            {
                errorDetail = new
                {
                    hint = "No Simulator view is open. Retry with open_if_needed=true or open Window > General > Device Simulator."
                };
                return false;
            }

            try
            {
                var showWindow = simulatorWindowType.GetMethod(
                    "ShowWindow",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                showWindow?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                errorDetail = new
                {
                    message = ex.Message,
                    hint = "Failed to open the Simulator view via Unity's internal SimulatorWindow.ShowWindow()."
                };
                return false;
            }

            simulatorWindow = GetExistingSimulatorWindow(simulatorWindowType)
                              ?? Resources.FindObjectsOfTypeAll(simulatorWindowType).FirstOrDefault();
            if (simulatorWindow != null)
                return true;

            errorDetail = new
            {
                hint = "Unity accepted the request to open the Simulator view, but the window is not available yet. Retry after the next editor frame."
            };
            return false;
        }

        private static object GetExistingSimulatorWindow(Type simulatorWindowType)
        {
            try
            {
                var instancesField = simulatorWindowType.GetField(
                    "s_SimulatorInstances",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (instancesField?.GetValue(null) is System.Collections.IEnumerable instances)
                {
                    object last = null;
                    foreach (var instance in instances)
                    {
                        if (instance != null)
                            last = instance;
                    }
                    if (last != null)
                        return last;
                }
            }
            catch
            {
            }

            return Resources.FindObjectsOfTypeAll(simulatorWindowType).FirstOrDefault();
        }

        private static bool TrySelectSimulatorDevice(object simulatorWindow, string deviceName, out bool deviceChanged, out object errorDetail)
        {
            deviceChanged = false;
            errorDetail = null;
            if (string.IsNullOrWhiteSpace(deviceName))
                return true;

            var main = GetSimulatorMain(simulatorWindow);
            if (main == null)
            {
                errorDetail = new
                {
                    requested = deviceName,
                    hint = "The Simulator window is available, but its internal main controller could not be resolved."
                };
                return false;
            }

            var devices = GetPropertyOrField<Array>(main, "devices")
                          ?? GetPropertyOrField<Array>(main, "m_Devices");
            if (devices == null || devices.Length == 0)
            {
                errorDetail = new
                {
                    requested = deviceName,
                    hint = "No Simulator devices are currently loaded."
                };
                return false;
            }

            if (!TryFindSimulatorDevice(devices, deviceName, out var deviceIndex, out var resolvedDeviceName))
            {
                errorDetail = new
                {
                    requested = deviceName,
                    available = GetSimulatorDeviceNames(devices)
                };
                return false;
            }

            var currentIndex = GetPropertyOrField<int?>(main, "deviceIndex")
                               ?? GetPropertyOrField<int?>(main, "m_DeviceIndex");
            if (currentIndex.HasValue && currentIndex.Value == deviceIndex)
                return true;

            if (!TrySetPropertyOrField(main, "deviceIndex", deviceIndex) &&
                !TrySetPropertyOrField(main, "m_DeviceIndex", deviceIndex))
            {
                errorDetail = new
                {
                    requested = deviceName,
                    matched = resolvedDeviceName,
                    hint = "The Simulator device was found, but deviceIndex could not be set."
                };
                return false;
            }

            TryInvokeInstanceMethod(main, "HandleScreenChange");
            TryInvokeInstanceMethod(main, "InitScreenUI");
            deviceChanged = true;

            if (simulatorWindow is EditorWindow editorWindow)
            {
                editorWindow.Show();
                editorWindow.Focus();
                editorWindow.Repaint();
            }

            EditorApplication.QueuePlayerLoopUpdate();
            return true;
        }

        private static Task WaitForEditorFramesAsync(int frameCount)
        {
            frameCount = Mathf.Max(frameCount, 1);
            var tcs = new TaskCompletionSource<bool>();

            void Tick()
            {
                frameCount--;
                if (frameCount <= 0)
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetResult(true);
                    return;
                }

                EditorApplication.QueuePlayerLoopUpdate();
            }

            EditorApplication.update += Tick;
            EditorApplication.QueuePlayerLoopUpdate();
            return tcs.Task;
        }

        private static bool TryFindSimulatorDevice(Array devices, string requestedName, out int deviceIndex, out string resolvedDeviceName)
        {
            deviceIndex = -1;
            resolvedDeviceName = null;

            var requestedNormalized = NormalizeDeviceName(requestedName);
            for (var i = 0; i < devices.Length; i++)
            {
                var name = GetSimulatorDeviceName(devices.GetValue(i));
                if (string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeDeviceName(name), requestedNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    deviceIndex = i;
                    resolvedDeviceName = name;
                    return true;
                }
            }

            for (var i = 0; i < devices.Length; i++)
            {
                var name = GetSimulatorDeviceName(devices.GetValue(i));
                var normalized = NormalizeDeviceName(name);
                if (!string.IsNullOrEmpty(normalized) &&
                    (normalized.Contains(requestedNormalized) || requestedNormalized.Contains(normalized)))
                {
                    deviceIndex = i;
                    resolvedDeviceName = name;
                    return true;
                }
            }

            return false;
        }

        private static string[] GetSimulatorDeviceNames(Array devices)
        {
            var names = new List<string>();
            for (var i = 0; i < devices.Length; i++)
            {
                var name = GetSimulatorDeviceName(devices.GetValue(i));
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
            return names.ToArray();
        }

        private static string GetSimulatorDeviceName(object deviceAsset)
        {
            if (deviceAsset == null)
                return null;

            var deviceInfo = GetPropertyOrField<object>(deviceAsset, "deviceInfo");
            return GetPropertyOrField<string>(deviceAsset, "name")
                   ?? GetPropertyOrField<string>(deviceInfo, "friendlyName")
                   ?? GetPropertyOrField<string>(deviceInfo, "deviceModel")
                   ?? GetPropertyOrField<string>(deviceInfo, "model")
                   ?? GetPropertyOrField<string>(deviceInfo, "name");
        }

        internal static string NormalizeDeviceName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
            return new string(chars);
        }

        private static object GetSimulatorDeviceView(object simulatorWindow)
        {
            if (simulatorWindow == null)
                return null;

            var deviceView = GetPropertyOrField<object>(simulatorWindow, "DeviceView")
                             ?? GetPropertyOrField<object>(simulatorWindow, "m_DeviceView");
            if (deviceView != null)
                return deviceView;

            var main = GetSimulatorMain(simulatorWindow);
            var userInterfaceController = GetPropertyOrField<object>(main, "userInterface")
                                          ?? GetPropertyOrField<object>(main, "ui")
                                          ?? GetPropertyOrField<object>(main, "m_UserInterfaceController")
                                          ?? GetPropertyOrField<object>(main, "userInterfaceController");
            return GetPropertyOrField<object>(userInterfaceController, "DeviceView")
                   ?? GetPropertyOrField<object>(userInterfaceController, "m_DeviceView");
        }

        private static object GetSimulatorMain(object simulatorWindow)
        {
            return GetPropertyOrField<object>(simulatorWindow, "main")
                   ?? GetPropertyOrField<object>(simulatorWindow, "m_Main");
        }

        private static Rect? ResolveSimulatorSafeArea(object deviceView, int sourceWidth, int sourceHeight)
        {
            var reflectedSafeArea = GetPropertyOrField<Rect?>(deviceView, "SafeArea")
                                    ?? GetPropertyOrField<Rect?>(deviceView, "m_SafeArea");
            if (IsValidSafeArea(reflectedSafeArea, sourceWidth, sourceHeight))
                return reflectedSafeArea;

            var screenSafeArea = Screen.safeArea;
            if (IsValidSafeArea(screenSafeArea, sourceWidth, sourceHeight))
                return screenSafeArea;

            return new Rect(0, 0, sourceWidth, sourceHeight);
        }

        private static bool IsValidSafeArea(Rect? safeArea, int sourceWidth, int sourceHeight)
        {
            return safeArea.HasValue && IsValidSafeArea(safeArea.Value, sourceWidth, sourceHeight);
        }

        private static bool IsValidSafeArea(Rect safeArea, int sourceWidth, int sourceHeight)
        {
            if (safeArea.width <= 0f || safeArea.height <= 0f || sourceWidth <= 0 || sourceHeight <= 0)
                return false;

            var maxWidth = sourceWidth * 1.05f;
            var maxHeight = sourceHeight * 1.05f;
            return safeArea.xMin >= -1f &&
                   safeArea.yMin >= -1f &&
                   safeArea.xMax <= maxWidth &&
                   safeArea.yMax <= maxHeight;
        }

        private static Type ResolveType(string fullName, string assemblyName)
        {
            var type = Type.GetType($"{fullName},{assemblyName}") ?? Type.GetType(fullName);
            if (type != null)
                return type;

            try
            {
                var assembly = Assembly.Load(assemblyName);
                type = assembly?.GetType(fullName);
                if (type != null)
                    return type;
            }
            catch
            {
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static T GetPropertyOrField<T>(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return default(T);

            try
            {
                var type = instance.GetType();
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (property != null && TryCastReflectedValue(property.GetValue(instance, null), out T propertyValue))
                    return propertyValue;

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (field != null && TryCastReflectedValue(field.GetValue(instance), out T fieldValue))
                    return fieldValue;
            }
            catch
            {
            }

            return default(T);
        }

        private static bool TrySetPropertyOrField(object instance, string name, object value)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return false;

            try
            {
                var type = instance.GetType();
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(instance, value, null);
                    return true;
                }

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (field != null)
                {
                    field.SetValue(instance, value);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void TryInvokeInstanceMethod(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return;

            try
            {
                var method = instance.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                method?.Invoke(instance, null);
            }
            catch
            {
            }
        }

        private static bool TryCastReflectedValue<T>(object value, out T result)
        {
            if (value is T typed)
            {
                result = typed;
                return true;
            }

            var targetType = typeof(T);
            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null && value != null && nullableType.IsInstanceOfType(value))
            {
                result = (T)value;
                return true;
            }

            result = default(T);
            return false;
        }

        private static string CaptureTexture(Texture sourceTexture, int width, int height, Rect? safeAreaOverlay,
            bool flipVertically = false, bool saveToFile = false, string outputPath = null, string defaultBaseName = "capture")
        {
            Texture2D screenshot = null;

            try
            {
                var sourceWidth = Mathf.Max(sourceTexture.width, 1);
                var sourceHeight = Mathf.Max(sourceTexture.height, 1);
                ResolveCaptureSize(ref width, ref height, sourceWidth, sourceHeight);

                screenshot = ReadTextureToTexture2D(sourceTexture, width, height, flipVertically);

                if (safeAreaOverlay.HasValue)
                    DrawSafeAreaOverlay(screenshot, safeAreaOverlay.Value, sourceWidth, sourceHeight);

                return FinishCapture(screenshot.EncodeToPNG(), saveToFile, outputPath, defaultBaseName);
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SCREENSHOT_CAPTURE_FAILED", new { message = ex.Message });
            }
            finally
            {
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        internal static void ResolveCaptureSize(ref int width, ref int height, int sourceWidth, int sourceHeight)
        {
            sourceWidth = Mathf.Max(sourceWidth, 1);
            sourceHeight = Mathf.Max(sourceHeight, 1);

            if (width <= 0 && height <= 0)
            {
                width = sourceWidth;
                height = sourceHeight;
            }
            else if (width > 0 && height <= 0)
            {
                height = Mathf.RoundToInt(width * (sourceHeight / (float)sourceWidth));
            }
            else if (height > 0 && width <= 0)
            {
                width = Mathf.RoundToInt(height * (sourceWidth / (float)sourceHeight));
            }

            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);
        }

        internal static void FlipTextureVertically(Texture2D texture)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 1)
                return;

            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();
            for (var y = 0; y < height / 2; y++)
            {
                var oppositeY = height - y - 1;
                var row = y * width;
                var oppositeRow = oppositeY * width;
                for (var x = 0; x < width; x++)
                {
                    var index = row + x;
                    var oppositeIndex = oppositeRow + x;
                    var temp = pixels[index];
                    pixels[index] = pixels[oppositeIndex];
                    pixels[oppositeIndex] = temp;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
        }

        internal static Texture2D ReadTextureToTexture2D(Texture sourceTexture, int width, int height, bool flipVertically)
        {
            RenderTexture readableRenderTexture = null;
            RenderTexture previousActive = null;
            Texture2D screenshot = null;

            try
            {
                readableRenderTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                readableRenderTexture.Create();
                Graphics.Blit(sourceTexture, readableRenderTexture);

                previousActive = RenderTexture.active;
                RenderTexture.active = readableRenderTexture;

                screenshot = ReadActiveRenderTextureToTexture2D(width, height, flipVertically);

                var result = screenshot;
                screenshot = null;
                return result;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (readableRenderTexture != null)
                {
                    readableRenderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(readableRenderTexture);
                }
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        internal static Texture2D ReadActiveRenderTextureToTexture2D(int width, int height, bool flipVertically)
        {
            var screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
            screenshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            screenshot.Apply();

            if (flipVertically)
                FlipTextureVertically(screenshot);

            return screenshot;
        }

        internal static void DrawSafeAreaOverlay(Texture2D texture, Rect safeArea, int sourceWidth, int sourceHeight)
        {
            if (texture == null || sourceWidth <= 0 || sourceHeight <= 0)
                return;

            var xScale = texture.width / (float)sourceWidth;
            var yScale = texture.height / (float)sourceHeight;
            var xMin = Mathf.Clamp(Mathf.RoundToInt(safeArea.xMin * xScale), 0, texture.width - 1);
            var yMin = Mathf.Clamp(Mathf.RoundToInt(safeArea.yMin * yScale), 0, texture.height - 1);
            var xMax = Mathf.Clamp(Mathf.RoundToInt(safeArea.xMax * xScale), 0, texture.width - 1);
            var yMax = Mathf.Clamp(Mathf.RoundToInt(safeArea.yMax * yScale), 0, texture.height - 1);

            if (xMax < xMin || yMax < yMin)
                return;

            var color = new Color32(80, 255, 90, 255);
            var thickness = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(texture.width, texture.height) / 180f), 2, 8);
            for (var i = 0; i < thickness; i++)
            {
                DrawHorizontalLine(texture, xMin, xMax, Mathf.Clamp(yMin + i, 0, texture.height - 1), color);
                DrawHorizontalLine(texture, xMin, xMax, Mathf.Clamp(yMax - i, 0, texture.height - 1), color);
                DrawVerticalLine(texture, yMin, yMax, Mathf.Clamp(xMin + i, 0, texture.width - 1), color);
                DrawVerticalLine(texture, yMin, yMax, Mathf.Clamp(xMax - i, 0, texture.width - 1), color);
            }

            texture.Apply();
        }

        private static void DrawHorizontalLine(Texture2D texture, int xMin, int xMax, int y, Color32 color)
        {
            for (var x = xMin; x <= xMax; x++)
                texture.SetPixel(x, y, color);
        }

        private static void DrawVerticalLine(Texture2D texture, int yMin, int yMax, int x, Color32 color)
        {
            for (var y = yMin; y <= yMax; y++)
                texture.SetPixel(x, y, color);
        }

        private static byte[] TryCapturePlayModeViewPngBytes(int width, int height)
        {
            Texture2D screenshot = null;

            try
            {
                var playModeViewType = Type.GetType("UnityEditor.PlayModeView,UnityEditor");
                var getMainPlayModeView = playModeViewType?.GetMethod(
                    "GetMainPlayModeView",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var playModeView = getMainPlayModeView?.Invoke(null, null);
                if (playModeView == null)
                    return null;

                var renderTextureField = playModeView.GetType().GetField(
                    "m_RenderTexture",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var sourceRenderTexture = renderTextureField?.GetValue(playModeView) as RenderTexture;
                if (sourceRenderTexture == null || !sourceRenderTexture.IsCreated() ||
                    sourceRenderTexture.width <= 0 || sourceRenderTexture.height <= 0)
                {
                    return null;
                }

                // Read the already-rendered Game View frame. This avoids camera.Render(),
                // which can bypass SRP cameras and produce black frames in URP/HDRP.
                // PlayModeView's internal RenderTexture is vertically inverted when read
                // back through ReadPixels for PNG output.
                screenshot = ReadTextureToTexture2D(
                    sourceRenderTexture,
                    width,
                    height,
                    flipVertically: ShouldFlipPlayModeViewRenderTexture());

                return screenshot.EncodeToPNG();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (screenshot != null)
                    UnityEngine.Object.DestroyImmediate(screenshot);
            }
        }

        internal static bool ShouldFlipPlayModeViewRenderTexture()
        {
            return true;
        }

        internal static bool ShouldFlipCameraRenderTexture()
        {
            return false;
        }

        /// <summary>
        /// Captures the game view including ScreenSpaceOverlay UI by temporarily
        /// switching overlay canvases to ScreenSpaceCamera during render.
        /// </summary>
        private static byte[] CaptureWithUIPngBytes(Camera camera, int width, int height)
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
                screenshot = ReadActiveRenderTextureToTexture2D(width, height, ShouldFlipCameraRenderTexture());

                return screenshot.EncodeToPNG();
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

        private static byte[] CaptureFromCameraPngBytes(Camera camera, int width, int height)
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
                screenshot = ReadActiveRenderTextureToTexture2D(width, height, ShouldFlipCameraRenderTexture());

                return screenshot.EncodeToPNG();
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
