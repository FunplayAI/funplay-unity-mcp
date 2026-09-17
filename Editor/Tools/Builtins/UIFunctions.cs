// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("UI")]
    internal static class UIFunctions
    {
        [Description("Create an Edit Mode Canvas from the project template/defaults, preserving existing EventSystems. Does not save automatically.")]
        [SceneEditingTool]
        public static object CreateCanvas(
            [ToolParam("Canvas name", Required = false)] string name = "Canvas",
            [ToolParam("Optional render-mode override; omitted preserves the template", Required = false)] string render_mode = null,
            [ToolParam("Optional Canvas prefab asset", Required = false)] string template_path = null,
            [ToolParam("auto, input_system or legacy; never replaces existing modules", Required = false)] string input_module = null)
            => UICreationService.Create("canvas", name, null, null, null, null, null, null, null, render_mode, null, template_path, null, null, null, input_module);

        [Description("Create an Edit Mode UI Button using project prefab/typography defaults. Preserves template bindings; no runtime UI reconstruction.")]
        [SceneEditingTool]
        public static object CreateButton(
            [ToolParam("Button name")] string name,
            [ToolParam("Button label")] string text,
            [ToolParam("Unique parent name/path/instance ID", Required = false)] string parent_name = "Canvas",
            [ToolParam("Optional anchored x,y", Required = false)] string position = null,
            [ToolParam("Optional width,height", Required = false)] string size = null,
            [ToolParam("Optional anchor preset", Required = false)] string anchor = null,
            [ToolParam("Optional pivot x,y", Required = false)] string pivot = null,
            [ToolParam("auto, tmp or legacy", Required = false)] string text_component = null,
            [ToolParam("Optional prefab asset", Required = false)] string template_path = null,
            [ToolParam("Label path relative to template root", Required = false)] string text_path = null,
            [ToolParam("Optional font asset override", Required = false)] string font_asset = null,
            [ToolParam("Optional shared font material preset", Required = false)] string font_material = null)
            => UICreationService.Create("button", name, text, parent_name, position, size, anchor, pivot, null, null, text_component, template_path, text_path, font_asset, font_material, null);

        [Description("Create an Edit Mode text element using project prefab/typography defaults. Empty projects prefer TextMeshProUGUI; missing TMP resources are reported without fallback.")]
        [SceneEditingTool]
        public static object CreateText(
            [ToolParam("Object name")] string name,
            [ToolParam("Text content")] string text,
            [ToolParam("Unique parent name/path/instance ID", Required = false)] string parent_name = "Canvas",
            [ToolParam("Optional font size override", Required = false)] string font_size = null,
            [ToolParam("Optional anchored x,y", Required = false)] string position = null,
            [ToolParam("Optional width,height", Required = false)] string size = null,
            [ToolParam("Optional anchor preset", Required = false)] string anchor = null,
            [ToolParam("Optional pivot x,y", Required = false)] string pivot = null,
            [ToolParam("auto, tmp or legacy", Required = false)] string text_component = null,
            [ToolParam("Optional prefab asset", Required = false)] string template_path = null,
            [ToolParam("Label path relative to template root", Required = false)] string text_path = null,
            [ToolParam("Optional font asset override", Required = false)] string font_asset = null,
            [ToolParam("Optional shared font material preset", Required = false)] string font_material = null)
            => UICreationService.Create("text", name, text, parent_name, position, size, anchor, pivot, font_size, null, text_component, template_path, text_path, font_asset, font_material, null);

        [Description("Create a UI Image element")]
        [SceneEditingTool]
        public static string CreateImage(
            [ToolParam("Name for the image element")] string name,
            [ToolParam("Parent Canvas/UI element name, hierarchy path, or instance ID (finds inactive too)", Required = false)] string parent_name = "Canvas",
            [ToolParam("Color as 'r,g,b,a' or hex", Required = false)] string color = "1,1,1,1",
            [ToolParam("Size as 'width,height'", Required = false)] string size = "100,100",
            [ToolParam("Anchored position as 'x,y'", Required = false)] string position = "0,0",
            [ToolParam("Anchor preset: 'top-left','top-center','top-right','middle-left','center','middle-right','bottom-left','bottom-center','bottom-right','stretch-horizontal','stretch-vertical','stretch-full'", Required = false)] string anchor = null,
            [ToolParam("Pivot as 'x,y' (0-1)", Required = false)] string pivot = null)
        {
            var parent = FindParent(parent_name);
            if (parent == null)
                return ToolResultFormatter.Error("PARENT_NOT_FOUND", new { parent_name, hint = "Create a Canvas first." });

            var imageGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(imageGo, $"Create Image {name}");
            imageGo.transform.SetParent(parent, false);

            var rect = imageGo.AddComponent<RectTransform>();
            rect.sizeDelta = ParseVector2(size);
            rect.anchoredPosition = ParseVector2(position);
            ApplyAnchorPreset(rect, anchor, pivot);

            var image = imageGo.AddComponent<Image>();
            image.color = ParseColor(color);

            Selection.activeGameObject = imageGo;
            return $"Created UI Image '{name}'";
        }

        internal static void ApplyAnchorPreset(RectTransform rect, string anchor, string pivot)
        {
            if (!string.IsNullOrEmpty(pivot))
            {
                var p = ParseVector2(pivot);
                rect.pivot = p;
            }

            if (string.IsNullOrEmpty(anchor)) return;

            switch (anchor.ToLowerInvariant().Replace(" ", "").Replace("_", "-"))
            {
                case "top-left":
                    rect.anchorMin = new Vector2(0, 1);
                    rect.anchorMax = new Vector2(0, 1);
                    rect.pivot = new Vector2(0, 1);
                    break;
                case "top-center":
                    rect.anchorMin = new Vector2(0.5f, 1);
                    rect.anchorMax = new Vector2(0.5f, 1);
                    rect.pivot = new Vector2(0.5f, 1);
                    break;
                case "top-right":
                    rect.anchorMin = new Vector2(1, 1);
                    rect.anchorMax = new Vector2(1, 1);
                    rect.pivot = new Vector2(1, 1);
                    break;
                case "middle-left":
                    rect.anchorMin = new Vector2(0, 0.5f);
                    rect.anchorMax = new Vector2(0, 0.5f);
                    rect.pivot = new Vector2(0, 0.5f);
                    break;
                case "center":
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "middle-right":
                    rect.anchorMin = new Vector2(1, 0.5f);
                    rect.anchorMax = new Vector2(1, 0.5f);
                    rect.pivot = new Vector2(1, 0.5f);
                    break;
                case "bottom-left":
                    rect.anchorMin = new Vector2(0, 0);
                    rect.anchorMax = new Vector2(0, 0);
                    rect.pivot = new Vector2(0, 0);
                    break;
                case "bottom-center":
                    rect.anchorMin = new Vector2(0.5f, 0);
                    rect.anchorMax = new Vector2(0.5f, 0);
                    rect.pivot = new Vector2(0.5f, 0);
                    break;
                case "bottom-right":
                    rect.anchorMin = new Vector2(1, 0);
                    rect.anchorMax = new Vector2(1, 0);
                    rect.pivot = new Vector2(1, 0);
                    break;
                case "stretch-horizontal":
                    rect.anchorMin = new Vector2(0, 0.5f);
                    rect.anchorMax = new Vector2(1, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.offsetMin = new Vector2(0, rect.offsetMin.y);
                    rect.offsetMax = new Vector2(0, rect.offsetMax.y);
                    break;
                case "stretch-vertical":
                    rect.anchorMin = new Vector2(0.5f, 0);
                    rect.anchorMax = new Vector2(0.5f, 1);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.offsetMin = new Vector2(rect.offsetMin.x, 0);
                    rect.offsetMax = new Vector2(rect.offsetMax.x, 0);
                    break;
                case "stretch-full":
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                    break;
            }

            // If user also specified a custom pivot, override the preset's pivot
            if (!string.IsNullOrEmpty(pivot))
                rect.pivot = ParseVector2(pivot);
        }

        private static Transform FindParent(string name)
        {
            var go = ObjectsHelper.FindTarget(name);
            return go?.transform;
        }

        private static Vector2 ParseVector2(string value)
        {
            if (string.IsNullOrEmpty(value)) return Vector2.zero;
            value = value.Trim('(', ')', ' ');
            var p = value.Split(',');
            if (p.Length >= 2)
                return new Vector2(
                    float.Parse(p[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            return Vector2.zero;
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

        [Description("Diagnose what UI (or physics) elements a click/tap at a screen point would hit, using the live " +
                     "EventSystem's RaycastAll. Returns the full ordered hit chain -- for each hit: hierarchy path, the " +
                     "raycast-receiving Graphic and its raycastTarget flag, sorting info, and the IPointerClickHandler " +
                     "that would actually receive the click (resolved by bubbling up the hierarchy, exactly like uGUI does). " +
                     "The summary names the click receiver, or the element silently swallowing the click when the topmost " +
                     "hit has no click handler anywhere up its parent chain -- the classic 'invisible raycast blocker' bug. " +
                     "Requires a live EventSystem (Play Mode).")]
        [ReadOnlyTool]
        public static string RaycastAtPoint(
            [ToolParam("X coordinate of the point to test.")] float x,
            [ToolParam("Y coordinate of the point to test.")] float y,
            [ToolParam("Interpret x/y as normalized 0-1 viewport coordinates instead of pixels.", Required = false)] bool normalized = false,
            [ToolParam("Coordinate origin: 'bottom_left' (Unity screen space, default) or 'top_left' (screenshot/image space).", Required = false)] string origin = "bottom_left",
            [ToolParam("Maximum number of hits to include in the result. Default 20.", Required = false)] int max_results = 20,
            [ToolParam("render_pixels, image_pixels or normalized. Omitted preserves the normalized flag.", Required = false)] string coordinate_space = null,
            [ToolParam("Screenshot ID, required for image_pixels; freshness/geometry validated", Required = false)] string capture_id = null)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return ToolResultFormatter.Error("NO_EVENT_SYSTEM", new
                {
                    hint = "No live EventSystem found. Enter Play Mode (or ensure the scene has an enabled EventSystem) first."
                });
            }

            // Screen.width/height on the editor main thread report the focused editor
            // window, not the game render resolution the UI canvases are laid out in.
            // Resolve the real Game View target size (same reflection the screenshot
            // tools use) so normalized coordinates and the top_left flip match what
            // GraphicRaycaster actually raycasts against.
            if (!VisualCoordinates.TryResolve(x, y, coordinate_space ?? (normalized ? "normalized" : "render_pixels"), origin, capture_id,
                out var point, out var geometry, out var coordinateError)) return ToolResultFormatter.Error(coordinateError);
            var screenWidth = geometry.render_width; var screenHeight = geometry.render_height;
            var px = point.x; var py = point.y;

            var pointerData = new PointerEventData(eventSystem) { position = new Vector2(px, py) };
            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(pointerData, results);

            max_results = Mathf.Clamp(max_results, 1, 100);
            var hits = new List<object>(Mathf.Min(results.Count, max_results));
            foreach (var result in results.Take(max_results))
            {
                var go = result.gameObject;
                if (go == null)
                    continue;

                var graphic = go.GetComponent<Graphic>();
                var clickHandlerGo = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
                hits.Add(new
                {
                    path = ObjectsHelper.GetGameObjectPath(go),
                    raycast_component = graphic != null ? graphic.GetType().Name : DescribeNonGraphicRaycastReceiver(go),
                    raycast_target = graphic != null ? (bool?)graphic.raycastTarget : null,
                    raycaster = result.module != null ? result.module.GetType().Name : null,
                    sorting_layer = result.sortingLayer,
                    sorting_order = result.sortingOrder,
                    depth = result.depth,
                    distance = result.distance,
                    click_handler = DescribeClickHandler(clickHandlerGo)
                });
            }

            object clickReceiver = null;
            object swallowedBy = null;
            if (results.Count > 0 && results[0].gameObject != null)
            {
                var topHandlerGo = ExecuteEvents.GetEventHandler<IPointerClickHandler>(results[0].gameObject);
                if (topHandlerGo != null)
                    clickReceiver = DescribeClickHandler(topHandlerGo);
                else
                    swallowedBy = new
                    {
                        path = ObjectsHelper.GetGameObjectPath(results[0].gameObject),
                        hint = "The topmost hit has no IPointerClickHandler anywhere up its parent chain, so a click " +
                               "here is consumed without reaching anything -- check for a stray raycastTarget Graphic " +
                               "or a leftover full-screen shield."
                    };
            }

            return JsonConvert.SerializeObject(Response.Success(
                results.Count == 0
                    ? "No raycast hits at this point."
                    : $"Raycast hit {results.Count} element(s); showing {hits.Count}.",
                new
                {
                    screen_position = new { x = px, y = py },
                    geometry,
                    screen_size = new { width = screenWidth, height = screenHeight },
                    hit_count = results.Count,
                    click_receiver = clickReceiver,
                    swallowed_by = swallowedBy,
                    hits
                }));
        }

        private static string DescribeNonGraphicRaycastReceiver(GameObject go)
        {
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                return collider.GetType().Name;
            var collider2D = go.GetComponent<Collider2D>();
            if (collider2D != null)
                return collider2D.GetType().Name;
            return "<none>";
        }

        private static object DescribeClickHandler(GameObject handlerGo)
        {
            if (handlerGo == null)
                return null;

            var handlerComponents = handlerGo.GetComponents<Component>()
                .Where(c => c is IPointerClickHandler)
                .Select(c => c.GetType().Name)
                .ToArray();

            var selectable = handlerGo.GetComponent<Selectable>();
            return new
            {
                path = ObjectsHelper.GetGameObjectPath(handlerGo),
                components = handlerComponents,
                interactable = selectable != null ? (bool?)selectable.IsInteractable() : null
            };
        }
    }
}
