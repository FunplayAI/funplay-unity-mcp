// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("InputSimulation")]
    internal static class InputInteractionFunctions
    {
        [Description("Simulate a mouse click at a screen position in Play Mode. Coordinates are in screen pixels with 0,0 at the bottom-left. Works without the Input System by dispatching UI and physics events. Returns a structured result reporting whether a target was actually hit, by which strategy, and the object that was clicked (or null when nothing was hit).")]
        [ReadOnlyTool]
        public static object SimulateMouseClick(
            [ToolParam("Screen X coordinate in pixels")] int x,
            [ToolParam("Screen Y coordinate in pixels")] int y,
            [ToolParam("Mouse button: left, right, or middle", Required = false)] string button = "left")
        {
            if (!EditorApplication.isPlaying)
                return Response.Error("PLAY_MODE_REQUIRED", new { message = "SimulateMouseClick only works in Play Mode." });

            try
            {
                var inputButton = ParseButton(button);
                var uiPosition = ResolveUiClickPosition(x, y, out var uiMessage);
                var viewportPosition = ResolveViewportClickPosition(x, y, out var viewportMessage);

                // The click behavior is unchanged: the UI path (EventSystem raycast, then a
                // direct-Button fallback) and the physics path both run. Each strategy now
                // reports honestly whether it actually landed on a target.
                var attempts = new List<ClickOutcome>();
                var uiOutcome = PerformUiClick(Mathf.RoundToInt(uiPosition.x), Mathf.RoundToInt(uiPosition.y), inputButton, attempts);
                var physicsOutcome = PerformPhysicsClick(viewportPosition, attempts);

                var winner = uiOutcome.Hit ? uiOutcome : (physicsOutcome.Hit ? physicsOutcome : null);
                var hit = winner != null;

                object target = null;
                string hitName = null;
                if (hit && winner.Target != null)
                {
                    hitName = winner.Target.name;
                    target = new { name = hitName, instanceId = ObjectIdHelper.GetSerializableId(winner.Target) };
                }

                var requested = $"screen({x}, {y}) {inputButton.ToString().ToLowerInvariant()}";

                var attempted = new List<object>();
                foreach (var attempt in attempts)
                    attempted.Add(new { strategy = attempt.Strategy, hit = attempt.Hit, detail = attempt.Detail });

                var data = new
                {
                    hit,
                    strategy = hit ? winner.Strategy : null,
                    target,
                    requested,
                    attempted,
                    mapping = new
                    {
                        ui = uiMessage,
                        viewport = viewportMessage
                    }
                };

                var message = hit
                    ? $"Mouse {button} click at ({x}, {y}) hit '{hitName}' via {winner.Strategy}"
                    : $"Mouse {button} click at ({x}, {y}) hit no target ({attempts.Count} strategies tried)";

                return Response.Success(message, data);
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        private sealed class ClickOutcome
        {
            public string Strategy;
            public bool Hit;
            public GameObject Target;
            public string Detail;

            public static ClickOutcome Miss(string strategy, string detail)
            {
                return new ClickOutcome { Strategy = strategy, Hit = false, Target = null, Detail = detail };
            }
        }

        private static Vector2 ResolveUiClickPosition(float x, float y, out string message)
        {
            message = null;

            if (!TryGetGameViewRenderSize(out var renderSize))
                return new Vector2(x, y);

            if (renderSize.x <= 0f || renderSize.y <= 0f)
                return new Vector2(x, y);

            var uiX = Mathf.Clamp(x, 0f, renderSize.x);
            var uiY = Mathf.Clamp(y, 0f, renderSize.y);
            message = $"  UI mapping: input ({x:F1}, {y:F1}) in render {renderSize.x:F0}x{renderSize.y:F0} -> ({uiX:F1}, {uiY:F1})";
            return new Vector2(uiX, uiY);
        }

        private static Vector2 ResolveViewportClickPosition(float x, float y, out string message)
        {
            message = null;

            if (!TryGetGameViewRenderSize(out var renderSize) || renderSize.x <= 0f || renderSize.y <= 0f)
                return new Vector2(0.5f, 0.5f);

            var viewport = new Vector2(
                Mathf.Clamp01(x / Mathf.Max(1f, renderSize.x)),
                Mathf.Clamp01(y / Mathf.Max(1f, renderSize.y)));
            message = $"  Physics mapping: input ({x:F1}, {y:F1}) in render {renderSize.x:F0}x{renderSize.y:F0} -> viewport ({viewport.x:F3}, {viewport.y:F3})";
            return viewport;
        }

        private static ClickOutcome PerformUiClick(int x, int y, PointerEventData.InputButton inputButton, List<ClickOutcome> attempts)
        {
            Canvas.ForceUpdateCanvases();

            var eventSystem = EventSystem.current ?? UnityEngine.Object.FindFirstObjectByType<EventSystem>();
            if (eventSystem == null)
            {
                attempts.Add(ClickOutcome.Miss("ui-eventsystem", "no EventSystem found"));
                return PerformDirectButtonFallback(new Vector2(x, y), attempts);
            }

            var pointerData = CreatePointerData(eventSystem, new Vector2(x, y), inputButton);
            if (!TryGetTopUiTarget(eventSystem, pointerData, out var target, out var raycast))
            {
                attempts.Add(ClickOutcome.Miss("ui-eventsystem", "no UI element at position"));
                return PerformDirectButtonFallback(new Vector2(x, y), attempts);
            }

            pointerData.pointerCurrentRaycast = raycast;
            pointerData.pointerPressRaycast = raycast;
            ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(target, pointerData, ExecuteEvents.pointerClickHandler);

            if (target.TryGetComponent<Button>(out var button))
                button.onClick?.Invoke();

            var outcome = new ClickOutcome
            {
                Strategy = "ui-eventsystem",
                Hit = true,
                Target = target,
                Detail = $"clicked on '{target.name}'"
            };
            attempts.Add(outcome);
            return outcome;
        }

        private static ClickOutcome PerformDirectButtonFallback(Vector2 position, List<ClickOutcome> attempts)
        {
            foreach (var button in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (button == null || !button.isActiveAndEnabled)
                    continue;

                var rectTransform = button.GetComponent<RectTransform>();
                if (rectTransform == null)
                    continue;

                if (!RectTransformUtility.RectangleContainsScreenPoint(rectTransform, position, null))
                    continue;

                button.onClick?.Invoke();
                var outcome = new ClickOutcome
                {
                    Strategy = "ui-direct-button",
                    Hit = true,
                    Target = button.gameObject,
                    Detail = $"invoked onClick on '{button.name}'"
                };
                attempts.Add(outcome);
                return outcome;
            }

            var miss = ClickOutcome.Miss("ui-direct-button", "no active Button contains the point");
            attempts.Add(miss);
            return miss;
        }

        private static ClickOutcome PerformPhysicsClick(Vector2 viewportPosition, List<ClickOutcome> attempts)
        {
            var mainCamera = Camera.main;
            if (mainCamera == null)
            {
                var miss = ClickOutcome.Miss("physics-raycast", "no Main Camera found");
                attempts.Add(miss);
                return miss;
            }

            Physics.SyncTransforms();
            var ray = mainCamera.ViewportPointToRay(new Vector3(viewportPosition.x, viewportPosition.y, 0f));
            if (Physics.Raycast(ray, out var hit, 1000f))
            {
                hit.collider.gameObject.SendMessage("OnMouseDown", SendMessageOptions.DontRequireReceiver);
                hit.collider.gameObject.SendMessage("OnMouseUp", SendMessageOptions.DontRequireReceiver);
                var outcome = new ClickOutcome
                {
                    Strategy = "physics-raycast",
                    Hit = true,
                    Target = hit.collider.gameObject,
                    Detail = $"OnMouseDown/OnMouseUp on '{hit.collider.gameObject.name}'"
                };
                attempts.Add(outcome);
                return outcome;
            }

            var noHit = ClickOutcome.Miss("physics-raycast", "no 3D object hit");
            attempts.Add(noHit);
            return noHit;
        }

        private static PointerEventData CreatePointerData(EventSystem eventSystem, Vector2 position, PointerEventData.InputButton inputButton)
        {
            return new PointerEventData(eventSystem)
            {
                position = position,
                pressPosition = position,
                button = inputButton
            };
        }

        private static bool TryGetTopUiTarget(
            EventSystem eventSystem,
            PointerEventData pointerData,
            out GameObject target,
            out RaycastResult raycast)
        {
            var raycastResults = new List<RaycastResult>();
            eventSystem.RaycastAll(pointerData, raycastResults);

            if (raycastResults.Count == 0)
            {
                target = null;
                raycast = default;
                return false;
            }

            raycast = raycastResults[0];
            target = raycast.gameObject;
            return target != null;
        }

        private static bool TryGetGameViewRenderSize(out Vector2 size)
        {
            size = default;

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

                if (value is Vector2 vector2)
                {
                    size = vector2;
                    return size.x > 0f && size.y > 0f;
                }
            }
            catch
            {
            }

            return false;
        }

        private static PointerEventData.InputButton ParseButton(string button)
        {
            switch ((button ?? "left").Trim().ToLowerInvariant())
            {
                case "right":
                    return PointerEventData.InputButton.Right;
                case "middle":
                    return PointerEventData.InputButton.Middle;
                default:
                    return PointerEventData.InputButton.Left;
            }
        }
    }
}
