// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Collections.Generic;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using UnityEditor;
using UnityEngine;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("Screenshot")]
    internal static class ScreenshotFunctions
    {
        private const string ImagePrefix = "data:image/png;base64,";

        [Description("Capture a screenshot of the Game View (what the main camera sees). Returns a base64-encoded PNG image.")]
        [ReadOnlyTool]
        public static string CaptureGameView(
            [ToolParam("Width of the screenshot in pixels", Required = false)] int width = 512,
            [ToolParam("Height of the screenshot in pixels", Required = false)] int height = 512)
        {
            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);

            var camera = Camera.main;
            if (camera == null)
                camera = UnityEngine.Object.FindFirstObjectByType<Camera>();

            if (camera == null)
                return "Error: No camera found in the scene. Add a Camera component to capture the Game View.";

            return CaptureWithUI(camera, width, height);
        }

        [Description("Capture a screenshot of the Scene View (the editor's scene camera perspective). Returns a base64-encoded PNG image.")]
        [ReadOnlyTool]
        public static string CaptureSceneView(
            [ToolParam("Width of the screenshot in pixels", Required = false)] int width = 512,
            [ToolParam("Height of the screenshot in pixels", Required = false)] int height = 512)
        {
            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return "Error: No Scene View is currently open. Open a Scene View window first.";

            var camera = sceneView.camera;
            if (camera == null)
                return "Error: Scene View camera is not available.";

            return CaptureFromCamera(camera, width, height);
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
                return $"Error: Failed to capture screenshot: {ex.Message}";
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
                return $"Error: Failed to capture screenshot: {ex.Message}";
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
