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
        [Description("Dispatch one UI scroll event at a validated screen/image point in Play Mode. Uses the top raycast hit's IScrollHandler (no click-through). Deltas are uGUI wheel units, not pixels; ScrollRect sensitivity belongs to the project. Records a timeline marker when recording is active.")]
        public static object SimulateUiScroll(
            [ToolParam("X coordinate")] float x,
            [ToolParam("Y coordinate")] float y,
            [ToolParam("Vertical wheel delta; negative normally scrolls down")] float delta_y,
            [ToolParam("Horizontal wheel delta", Required = false)] float delta_x = 0,
            [ToolParam("render_pixels, image_pixels or normalized", Required = false)] string coordinate_space = "render_pixels",
            [ToolParam("bottom_left or top_left", Required = false)] string origin = "bottom_left",
            [ToolParam("Fresh screenshot ID; required for image_pixels", Required = false)] string capture_id = null)
        {
            if (!EditorApplication.isPlaying) return Response.Error("PLAY_MODE_REQUIRED");
            if (float.IsNaN(delta_x) || float.IsNaN(delta_y) || float.IsInfinity(delta_x) || float.IsInfinity(delta_y) ||
                Mathf.Abs(delta_x) > 1000 || Mathf.Abs(delta_y) > 1000) return Response.Error("INVALID_SCROLL_DELTA");
            if (!VisualCoordinates.TryResolve(x, y, coordinate_space, origin, capture_id, out var point, out var geometry, out var error)) return Response.Error(error);
            var events = EventSystem.current; if (events == null) return Response.Error("NO_EVENT_SYSTEM");
            var data = new PointerEventData(events) { position = point, scrollDelta = new Vector2(delta_x, delta_y) };
            var hits = new List<RaycastResult>(); events.RaycastAll(data, hits);
            var target = hits.Count > 0 ? ExecuteEvents.GetEventHandler<IScrollHandler>(hits[0].gameObject) : null;
            if (target == null) return Response.Error("NO_SCROLL_HANDLER", new { geometry, top_hit = hits.Count > 0 ? ObjectsHelper.GetGameObjectPath(hits[0].gameObject) : null });
            VideoRecordingFunctions.RecordMarker("scroll", "UI wheel delta " + data.scrollDelta.ToString(), point, geometry: geometry);
            ExecuteEvents.Execute(target, data, ExecuteEvents.scrollHandler);
            return Response.Success("Scroll event dispatched; capture/record to verify the resulting layout.", new
            { target_id = ObjectIdHelper.GetSerializableId(target), target_path = ObjectsHelper.GetGameObjectPath(target),
                point = new { x = point.x, y = point.y }, delta_x, delta_y, geometry });
        }

        [Description("Simulate a mouse click at a screen position in Play Mode. Coordinates are in screen pixels with 0,0 at the bottom-left. Works without the Input System by dispatching UI and physics events. Returns a structured result reporting whether a target was actually hit, by which strategy, and the object that was clicked (or null when nothing was hit).")]
        public static object SimulateMouseClick(
            [ToolParam("X coordinate (pixels by default)")] float x,
            [ToolParam("Y coordinate (pixels by default)")] float y,
            [ToolParam("Mouse button: left, right, or middle", Required = false)] string button = "left",
            [ToolParam("render_pixels (legacy default), image_pixels, or normalized", Required = false)] string coordinate_space = "render_pixels",
            [ToolParam("bottom_left (legacy default) or top_left", Required = false)] string origin = "bottom_left",
            [ToolParam("Screenshot capture ID; required for image_pixels. Rejects changed geometry/mode or captures older than 30 seconds.", Required = false)] string capture_id = null)
        {
            if (!EditorApplication.isPlaying)
                return Response.Error("PLAY_MODE_REQUIRED", new { message = "SimulateMouseClick only works in Play Mode." });

            try
            {
                if (!TryParseButton(button, out var inputButton))
                {
                    return Response.Error("INVALID_MOUSE_BUTTON", new
                    {
                        button,
                        allowed = new[] { "left", "right", "middle" }
                    });
                }

                if (!VisualCoordinates.TryResolve(x, y, coordinate_space, origin, capture_id, out var uiPosition, out var geometry, out var coordinateError))
                    return Response.Error(coordinateError, new { hint = "Capture a fresh Game View and pass its capture_id for image coordinates." });
                var viewportPosition = new Vector2(uiPosition.x / geometry.render_width, uiPosition.y / geometry.render_height);
                var uiMessage = "Converted to bottom-left render pixels using the visual coordinate contract.";
                var viewportMessage = "Normalized full-render coordinates; camera rect is applied by physics dispatch.";

                var attempts = new List<ClickOutcome>();
                VideoRecordingFunctions.RecordMarker("click", button + " click dispatched", uiPosition, geometry: geometry);
                var uiOutcome = PerformUiClick(
                    Mathf.RoundToInt(uiPosition.x),
                    Mathf.RoundToInt(uiPosition.y),
                    inputButton,
                    attempts);
                ClickOutcome winner = uiOutcome.Hit ? uiOutcome : null;

                // Match normal input precedence: UI gets first refusal. A raycast blocker
                // prevents click-through even when it has no click handler.
                if (winner == null && !uiOutcome.BlocksFurtherDispatch)
                {
                    var physicsOutcome = PerformPhysicsClick(viewportPosition, attempts);
                    if (physicsOutcome.Hit)
                        winner = physicsOutcome;
                }

                var hit = winner != null;

                var attempted = new List<object>();
                foreach (var attempt in attempts)
                {
                    attempted.Add(new
                    {
                        strategy = attempt.Strategy,
                        hit = attempt.Hit,
                        blocked = attempt.BlocksFurtherDispatch,
                        target = BuildTargetPayload(attempt.Target),
                        blockedBy = BuildTargetPayload(attempt.BlockedBy),
                        detail = attempt.Detail
                    });
                }

                ClickTargetSnapshot blockedBy = null;
                foreach (var attempt in attempts)
                {
                    if (attempt.BlockedBy != null)
                    {
                        blockedBy = attempt.BlockedBy;
                        break;
                    }
                }

                var data = new
                {
                    hit,
                    geometry,
                    strategy = hit ? winner.Strategy : null,
                    target = BuildTargetPayload(hit ? winner.Target : null),
                    blockedBy = BuildTargetPayload(blockedBy),
                    requested = new
                    {
                        x,
                        y,
                        coordinate_space, origin, capture_id,
                        button = inputButton.ToString().ToLowerInvariant()
                    },
                    attempted,
                    mapping = new
                    {
                        ui = new { x = uiPosition.x, y = uiPosition.y, detail = uiMessage },
                        viewport = new
                        {
                            x = viewportPosition.x,
                            y = viewportPosition.y,
                            detail = viewportMessage
                        }
                    }
                };

                var message = hit
                    ? $"Mouse {button} click at ({x}, {y}) hit '{winner.Target.Name}' via {winner.Strategy}"
                    : blockedBy != null
                        ? $"Mouse {button} click at ({x}, {y}) was blocked by '{blockedBy.Name}' with no click handler"
                        : $"Mouse {button} click at ({x}, {y}) hit no target ({attempts.Count} strategies tried)";

                return Response.Success(message, data);
            }
            catch (Exception ex)
            {
                return Response.Error("MOUSE_CLICK_FAILED", new
                {
                    message = ex.Message,
                    exception_type = ex.GetType().FullName
                });
            }
        }

        internal sealed class ClickTargetSnapshot
        {
            public string Name;
            public string InstanceId;
            public string Path;
        }

        internal sealed class ClickOutcome
        {
            public string Strategy;
            public bool Hit;
            public bool BlocksFurtherDispatch;
            public ClickTargetSnapshot Target;
            public ClickTargetSnapshot BlockedBy;
            public string Detail;

            public static ClickOutcome Miss(
                string strategy,
                string detail,
                bool blocksFurtherDispatch = false,
                ClickTargetSnapshot blockedBy = null)
            {
                return new ClickOutcome
                {
                    Strategy = strategy,
                    Hit = false,
                    BlocksFurtherDispatch = blocksFurtherDispatch,
                    BlockedBy = blockedBy,
                    Detail = detail
                };
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
                return PerformDirectButtonFallback(new Vector2(x, y), inputButton, attempts);
            }

            var pointerData = CreatePointerData(eventSystem, new Vector2(x, y), inputButton);
            if (!TryGetTopUiTarget(eventSystem, pointerData, out var target, out var raycast))
            {
                attempts.Add(ClickOutcome.Miss("ui-eventsystem", "no UI element at position"));
                return PerformDirectButtonFallback(new Vector2(x, y), inputButton, attempts);
            }

            return DispatchUiClick(target, raycast, pointerData, attempts);
        }

        internal static ClickOutcome DispatchUiClick(
            GameObject target,
            RaycastResult raycast,
            PointerEventData pointerData,
            List<ClickOutcome> attempts)
        {
            if (target == null)
            {
                var missing = ClickOutcome.Miss("ui-eventsystem", "raycast target was destroyed before dispatch");
                attempts.Add(missing);
                return missing;
            }

            var raycastTarget = CreateTargetSnapshot(target);
            var enterReceiver = ExecuteEvents.GetEventHandler<IPointerEnterHandler>(target);
            var downReceiver = ExecuteEvents.GetEventHandler<IPointerDownHandler>(target);
            var upReceiver = ExecuteEvents.GetEventHandler<IPointerUpHandler>(target);
            var clickReceiver = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            var clickReceiverSnapshot = CreateTargetSnapshot(clickReceiver);

            pointerData.pointerCurrentRaycast = raycast;
            pointerData.pointerPressRaycast = raycast;
            if (enterReceiver != null)
                ExecuteEvents.Execute(enterReceiver, pointerData, ExecuteEvents.pointerEnterHandler);
            if (downReceiver != null)
                ExecuteEvents.Execute(downReceiver, pointerData, ExecuteEvents.pointerDownHandler);
            if (upReceiver != null)
                ExecuteEvents.Execute(upReceiver, pointerData, ExecuteEvents.pointerUpHandler);

            var clickDispatched = clickReceiver != null;
            if (clickDispatched)
                ExecuteEvents.Execute(clickReceiver, pointerData, ExecuteEvents.pointerClickHandler);

            if (!clickDispatched)
            {
                var detail = clickReceiverSnapshot == null
                    ? $"top UI hit '{raycastTarget.Name}' has no IPointerClickHandler in its parent chain"
                    : $"click receiver '{clickReceiverSnapshot.Name}' was destroyed before pointerClick dispatch";
                var blocked = ClickOutcome.Miss(
                    "ui-eventsystem",
                    detail,
                    blocksFurtherDispatch: true,
                    blockedBy: raycastTarget);
                attempts.Add(blocked);
                return blocked;
            }

            var outcome = new ClickOutcome
            {
                Strategy = "ui-eventsystem",
                Hit = true,
                Target = clickReceiverSnapshot,
                Detail = $"dispatched pointerClick to '{clickReceiverSnapshot.Name}'"
            };
            attempts.Add(outcome);
            return outcome;
        }

        private static ClickOutcome PerformDirectButtonFallback(
            Vector2 position,
            PointerEventData.InputButton inputButton,
            List<ClickOutcome> attempts)
        {
            if (inputButton != PointerEventData.InputButton.Left)
            {
                var unsupported = ClickOutcome.Miss(
                    "ui-direct-button",
                    "direct Button fallback only invokes left clicks");
                attempts.Add(unsupported);
                return unsupported;
            }

            foreach (var button in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (button == null || !button.isActiveAndEnabled)
                    continue;

                var rectTransform = button.GetComponent<RectTransform>();
                if (rectTransform == null)
                    continue;

                if (!RectTransformUtility.RectangleContainsScreenPoint(rectTransform, position, null))
                    continue;

                var target = CreateTargetSnapshot(button.gameObject);
                button.onClick?.Invoke();
                var outcome = new ClickOutcome
                {
                    Strategy = "ui-direct-button",
                    Hit = true,
                    Target = target,
                    Detail = $"invoked onClick on '{target.Name}'"
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
            var cameraRect = mainCamera.rect;
            if (!cameraRect.Contains(viewportPosition) || cameraRect.width <= 0 || cameraRect.height <= 0)
            {
                var outside = ClickOutcome.Miss("physics-raycast", "point is outside the Main Camera viewport");
                attempts.Add(outside); return outside;
            }
            var ray = mainCamera.ViewportPointToRay(new Vector3((viewportPosition.x - cameraRect.x) / cameraRect.width, (viewportPosition.y - cameraRect.y) / cameraRect.height, 0f));
            if (Physics.Raycast(ray, out var hit, 1000f))
            {
                var hitObject = hit.collider.gameObject;
                var target = CreateTargetSnapshot(hitObject);
                hitObject.SendMessage("OnMouseDown", SendMessageOptions.DontRequireReceiver);
                if (hitObject != null)
                    hitObject.SendMessage("OnMouseUp", SendMessageOptions.DontRequireReceiver);

                var outcome = new ClickOutcome
                {
                    Strategy = "physics-raycast",
                    Hit = true,
                    Target = target,
                    Detail = $"dispatched OnMouseDown/OnMouseUp to '{target.Name}'"
                };
                attempts.Add(outcome);
                return outcome;
            }

            var noHit = ClickOutcome.Miss("physics-raycast", "no 3D object hit");
            attempts.Add(noHit);
            return noHit;
        }

        private static ClickTargetSnapshot CreateTargetSnapshot(GameObject target)
        {
            if (target == null)
                return null;

            return new ClickTargetSnapshot
            {
                Name = target.name,
                InstanceId = ObjectIdHelper.GetSerializableId(target),
                Path = ObjectsHelper.GetGameObjectPath(target)
            };
        }

        private static object BuildTargetPayload(ClickTargetSnapshot target)
        {
            return target == null
                ? null
                : new
                {
                    name = target.Name,
                    instanceId = target.InstanceId,
                    path = target.Path
                };
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

        private static bool TryParseButton(string button, out PointerEventData.InputButton inputButton)
        {
            switch ((button ?? "left").Trim().ToLowerInvariant())
            {
                case "left":
                    inputButton = PointerEventData.InputButton.Left;
                    return true;
                case "right":
                    inputButton = PointerEventData.InputButton.Right;
                    return true;
                case "middle":
                    inputButton = PointerEventData.InputButton.Middle;
                    return true;
                default:
                    inputButton = PointerEventData.InputButton.Left;
                    return false;
            }
        }
    }
}
