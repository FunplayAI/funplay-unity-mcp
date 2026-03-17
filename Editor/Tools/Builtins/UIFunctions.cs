// Copyright (C) GameBooom. Licensed under GPLv3.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("UI")]
    internal static class UIFunctions
    {
        [Description("Create a Canvas in the scene (required for UI elements)")]
        [SceneEditingTool]
        public static string CreateCanvas(
            [ToolParam("Name for the Canvas", Required = false)] string name = "Canvas",
            [ToolParam("Render mode: ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace", Required = false)] string render_mode = "ScreenSpaceOverlay")
        {
            var canvasGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(canvasGo, $"Create Canvas {name}");

            var canvas = canvasGo.AddComponent<Canvas>();
            switch (render_mode.ToLowerInvariant())
            {
                case "screenspacecamera": canvas.renderMode = RenderMode.ScreenSpaceCamera; break;
                case "worldspace": canvas.renderMode = RenderMode.WorldSpace; break;
                default: canvas.renderMode = RenderMode.ScreenSpaceOverlay; break;
            }

            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            // Ensure EventSystem exists
            if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                var esGo = new GameObject("EventSystem");
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
            }

            Selection.activeGameObject = canvasGo;
            return $"Created Canvas '{name}' with {render_mode} render mode";
        }

        [Description("Create a UI Button")]
        [SceneEditingTool]
        public static string CreateButton(
            [ToolParam("Name for the button")] string name,
            [ToolParam("Button label text")] string text,
            [ToolParam("Parent Canvas or UI element name", Required = false)] string parent_name = "Canvas",
            [ToolParam("Anchored position as 'x,y'", Required = false)] string position = "0,0",
            [ToolParam("Size as 'width,height'", Required = false)] string size = "160,40",
            [ToolParam("Anchor preset: 'top-left','top-center','top-right','middle-left','center','middle-right','bottom-left','bottom-center','bottom-right','stretch-horizontal','stretch-vertical','stretch-full'", Required = false)] string anchor = null,
            [ToolParam("Pivot as 'x,y' (0-1)", Required = false)] string pivot = null)
        {
            var parent = FindParent(parent_name);
            if (parent == null)
                return $"Error: Parent '{parent_name}' not found. Create a Canvas first.";

            var buttonGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(buttonGo, $"Create Button {name}");
            buttonGo.transform.SetParent(parent, false);

            var rect = buttonGo.AddComponent<RectTransform>();
            rect.sizeDelta = ParseVector2(size);
            rect.anchoredPosition = ParseVector2(position);
            ApplyAnchorPreset(rect, anchor, pivot);

            var image = buttonGo.AddComponent<Image>();
            image.color = new Color(0.2f, 0.5f, 0.9f, 1f);
            buttonGo.AddComponent<Button>();

            // Text child
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(buttonGo.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            var textComp = textGo.AddComponent<Text>();
            textComp.text = text;
            textComp.alignment = TextAnchor.MiddleCenter;
            textComp.color = Color.white;
            textComp.fontSize = 16;
            textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            Selection.activeGameObject = buttonGo;
            return $"Created UI Button '{name}' with text '{text}'";
        }

        [Description("Create a UI Text element")]
        [SceneEditingTool]
        public static string CreateText(
            [ToolParam("Name for the text element")] string name,
            [ToolParam("Text content")] string text,
            [ToolParam("Parent Canvas or UI element name", Required = false)] string parent_name = "Canvas",
            [ToolParam("Font size", Required = false)] string font_size = "20",
            [ToolParam("Anchored position as 'x,y'", Required = false)] string position = "0,0",
            [ToolParam("Size as 'width,height'", Required = false)] string size = "300,60",
            [ToolParam("Anchor preset: 'top-left','top-center','top-right','middle-left','center','middle-right','bottom-left','bottom-center','bottom-right','stretch-horizontal','stretch-vertical','stretch-full'", Required = false)] string anchor = null,
            [ToolParam("Pivot as 'x,y' (0-1)", Required = false)] string pivot = null)
        {
            var parent = FindParent(parent_name);
            if (parent == null)
                return $"Error: Parent '{parent_name}' not found. Create a Canvas first.";

            var textGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(textGo, $"Create Text {name}");
            textGo.transform.SetParent(parent, false);

            var rect = textGo.AddComponent<RectTransform>();
            rect.sizeDelta = ParseVector2(size);
            rect.anchoredPosition = ParseVector2(position);
            ApplyAnchorPreset(rect, anchor, pivot);

            var textComp = textGo.AddComponent<Text>();
            textComp.text = text;
            textComp.alignment = TextAnchor.MiddleCenter;
            textComp.color = Color.white;
            textComp.fontSize = int.Parse(font_size);
            textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            Selection.activeGameObject = textGo;
            return $"Created UI Text '{name}'";
        }

        [Description("Create a UI Image element")]
        [SceneEditingTool]
        public static string CreateImage(
            [ToolParam("Name for the image element")] string name,
            [ToolParam("Parent Canvas or UI element name", Required = false)] string parent_name = "Canvas",
            [ToolParam("Color as 'r,g,b,a' or hex", Required = false)] string color = "1,1,1,1",
            [ToolParam("Size as 'width,height'", Required = false)] string size = "100,100",
            [ToolParam("Anchored position as 'x,y'", Required = false)] string position = "0,0",
            [ToolParam("Anchor preset: 'top-left','top-center','top-right','middle-left','center','middle-right','bottom-left','bottom-center','bottom-right','stretch-horizontal','stretch-vertical','stretch-full'", Required = false)] string anchor = null,
            [ToolParam("Pivot as 'x,y' (0-1)", Required = false)] string pivot = null)
        {
            var parent = FindParent(parent_name);
            if (parent == null)
                return $"Error: Parent '{parent_name}' not found. Create a Canvas first.";

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

        private static void ApplyAnchorPreset(RectTransform rect, string anchor, string pivot)
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
            var go = GameObject.Find(name);
            if (go != null)
                return go.transform;

            foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!candidate.scene.IsValid())
                    continue;

                if (candidate.hideFlags == HideFlags.NotEditable || candidate.hideFlags == HideFlags.HideAndDontSave)
                    continue;

                if (candidate.name == name)
                    return candidate.transform;
            }

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
    }
}
