// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools.Helpers;
using Funplay.Editor.Tools.Builtins;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Controls;
#endif

namespace Funplay.Editor.State
{
    [Serializable] internal sealed class InputFramePoint
    {
        public float x, y;
        internal InputFramePoint() { }
        internal InputFramePoint(Vector2 value) { x = value.x; y = value.y; }
        internal Vector2 Vector => new Vector2(x, y);
    }
    [Serializable] internal sealed class InputFrameEdge
    {
        public int frame;
        public double wall_seconds, unscaled_seconds, game_seconds;
        public bool down, held, up, milestone;
        public string phase;
        public InputFramePoint position;
    }
    [Serializable] internal sealed class InputFrameReceipt
    {
        public string job_id, status = "running", phase = "press_queued", error, route = "new_input_system";
        public string gesture = "click", source_tool = "simulate_mouse_click", pointer_device = "mouse", button = "left";
        public string request_key, request_signature, device_name, cleanup_method;
        public string input_background_behavior, editor_input_behavior;
        public bool focused_at_start, run_in_background_at_start;
        public int device_id, observed_frames, hold_frames;
        public ProjectExecutionContext context;
        public VisualGeometry geometry;
        public InputFramePoint start, end;
        public double duration_seconds, press_hold_seconds, end_hold_seconds, started_wall_seconds, deadline_wall_seconds;
        public double? down_wall_seconds, move_started_wall_seconds, target_reached_wall_seconds, finished_wall_seconds;
        public bool down_observed, held_observed, up_observed, target_observed, complete, cleanup_verified, edge_samples_truncated;
        public bool? application_accepted;
        public List<InputFrameEdge> edges = new List<InputFrameEdge>();
    }
    [InitializeOnLoad] internal static class InputFrameJob
    {
        private const string Key = "Funplay.InputFrame.Jobs";
        private static List<InputFrameReceipt> jobs;
        private static InputFrameReceipt active;
#if ENABLE_INPUT_SYSTEM
        private static InputDevice device;
        private static ButtonControl control;
        private static int downFrame, targetFrame, releaseFrame;
        private static bool releaseQueued, targetQueued;
#endif
        static InputFrameJob()
        {
            try { jobs = JsonConvert.DeserializeObject<List<InputFrameReceipt>>(SessionState.GetString(Key, "[]")) ?? new List<InputFrameReceipt>(); }
            catch { jobs = new List<InputFrameReceipt>(); }
            jobs.RemoveAll(x => x == null || !Guid.TryParseExact(x.job_id, "N", out _));
            foreach (var job in jobs)
            {
                job.edges = job.edges ?? new List<InputFrameEdge>();
                if (job.status != "running" && job.status != "cleanup_pending") continue;
                job.status = "interrupted"; job.phase = "interrupted"; job.error = "domain_reload"; job.complete = false;
                Cleanup(job); // Only a journaled device with the exact owned ID and name can be removed.
            }
            PlayerLoopWitness.OnPlayerFrame += Observe;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += () => Finish("interrupted", "domain_reload");
            EditorApplication.quitting += () => Finish("interrupted", "editor_quit");
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingPlayMode) Finish("interrupted", "play_mode_exit"); };
            Save();
        }
        internal static object Availability()
        {
#if ENABLE_INPUT_SYSTEM
            return new { available = InputSystem.settings.updateMode == InputSettings.UpdateMode.ProcessEventsInDynamicUpdate,
                backend = "new_input_system", required_update = "dynamic", current_update = InputSystem.settings.updateMode.ToString(),
                pointer_devices = new[] { "mouse", "touch" }, cross_frame_drag = true,
                legacy_polling_injection = false, note = "Owned synthetic devices, observed across actual PlayerLoop frames. No manual input updates or legacy UnityEngine.Input injection; application acceptance is separate." };
#else
            return new { available = false, backend = "new_input_system", reason = "ENABLE_INPUT_SYSTEM is not enabled", legacy_polling_injection = false };
#endif
        }
        internal static object Start(Vector2 point, string button, VisualGeometry geometry, int frames, string verificationId)
        {
            if (frames < 2 || frames > 30) return Response.Error("INVALID_HOLD_FRAMES");
            if (!ValidButton(button)) return Response.Error("INVALID_MOUSE_BUTTON");
            if (!Finite(point.x) || !Finite(point.y)) return Response.Error("INVALID_COORDINATES");
            return Begin(new InputFrameReceipt { start = new InputFramePoint(point), end = new InputFramePoint(point),
                button = button, geometry = geometry, hold_frames = frames }, verificationId);
        }
        internal static string ValidateDrag(float duration, string button, string pointerDevice, float pressHold, float endHold)
        {
            if (!Finite(duration) || duration < 0.05f || duration > 10f) return "INVALID_DRAG_DURATION";
            if (!Finite(pressHold) || !Finite(endHold) || pressHold < 0 || pressHold > 5 || endHold < 0 || endHold > 5) return "INVALID_DRAG_HOLD_DURATION";
            if (!ValidButton(button)) return "INVALID_MOUSE_BUTTON";
            if (pointerDevice != "mouse" && pointerDevice != "touch") return "INVALID_POINTER_DEVICE";
            if (pointerDevice == "touch" && button != "left") return "TOUCH_REQUIRES_LEFT_BUTTON";
            return null;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool ValidButton(string button) => button == "left" || button == "right" || button == "middle";
        internal static object StartDrag(Vector2 start, Vector2 end, float duration, string button, VisualGeometry geometry,
            string pointerDevice, float pressHold, float endHold, string verificationId, string requestKey)
        {
            var error = ValidateDrag(duration, button, pointerDevice, pressHold, endHold);
            if (error != null) return Response.Error(error);
            if (!Finite(start.x) || !Finite(start.y) || !Finite(end.x) || !Finite(end.y)) return Response.Error("INVALID_COORDINATES");
            if (requestKey != null && (string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 128)) return Response.Error("INVALID_INPUT_REQUEST_KEY");
            var job = new InputFrameReceipt { gesture = "drag", source_tool = "simulate_mouse_drag", pointer_device = pointerDevice,
                button = button, start = new InputFramePoint(start), end = new InputFramePoint(end), geometry = geometry,
                duration_seconds = duration, press_hold_seconds = pressHold, end_hold_seconds = endHold, request_key = requestKey };
            using (var sha = SHA256.Create()) job.request_signature = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new {
                job.start, job.end, job.duration_seconds, job.press_hold_seconds, job.end_hold_seconds, job.button, job.pointer_device,
                verificationId, render_width = geometry?.render_width, render_height = geometry?.render_height,
                view_id = geometry?.view_id, scene_signature = geometry?.scene_signature, camera_signature = geometry?.camera_signature }))));
            var previous = Find(null, requestKey);
            if (previous != null)
            {
                if (previous.request_signature != job.request_signature) return Response.Error("INPUT_REQUEST_KEY_CONFLICT");
                // Lost responses may be recovered, never replayed after a reload or a completed gesture.
                return Read(previous.job_id);
            }
            return Begin(job, verificationId);
        }
        private static object Begin(InputFrameReceipt job, string verificationId)
        {
            if (active != null) return Response.Error("INPUT_FRAME_JOB_ACTIVE");
            if (jobs.Any(x => x.status == "cleanup_pending")) return Response.Error("INPUT_CLEANUP_PENDING");
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) return Response.Error("UNPAUSED_PLAY_MODE_REQUIRED");
            var problem = VerificationScope.ValidateOwner(verificationId); if (problem != null) return Response.Error(problem);
#if ENABLE_INPUT_SYSTEM
            if (InputSystem.settings.updateMode != InputSettings.UpdateMode.ProcessEventsInDynamicUpdate)
                return Response.Error("INPUT_UPDATE_MODE_UNSUPPORTED", Availability());
            active = job; active.job_id = Guid.NewGuid().ToString("N"); active.context = MCPProjectContext.Current;
            active.focused_at_start = Application.isFocused; active.run_in_background_at_start = Application.runInBackground;
            active.input_background_behavior = InputSystem.settings.backgroundBehavior.ToString();
            active.editor_input_behavior = InputSystem.settings.editorInputBehaviorInPlayMode.ToString();
            active.started_wall_seconds = PlayerLoopWitness.Wall;
            active.deadline_wall_seconds = active.started_wall_seconds + (job.gesture == "click" ? 10 : job.duration_seconds + job.press_hold_seconds + job.end_hold_seconds + 5);
            jobs.Add(active); while (jobs.Count > 16) jobs.RemoveAt(0);
            Save(); // Journal ownership before creating or pressing an input device.
            try
            {
                device = job.pointer_device == "touch" ? (InputDevice)InputSystem.AddDevice<Touchscreen>("FunplayMcp-" + active.job_id) : InputSystem.AddDevice<Mouse>("FunplayMcp-" + active.job_id);
                active.device_id = device.deviceId; active.device_name = device.name; Save();
                control = device is Touchscreen touch ? touch.primaryTouch.press : job.button == "right" ? ((Mouse)device).rightButton : job.button == "middle" ? ((Mouse)device).middleButton : ((Mouse)device).leftButton;
                downFrame = targetFrame = releaseFrame = -1; releaseQueued = targetQueued = false;
                VerificationScope.Enroll(verificationId, "input", active.job_id);
                Queue(true, job.start.Vector, true);
                if (job.gesture == "drag") VideoRecordingFunctions.RecordMarker("drag", "Cross-frame " + job.pointer_device + " drag started; application acceptance is unverified", job.start.Vector, job.end.Vector, job.geometry);
                Save(); return Read(active.job_id);
            }
            catch (Exception ex) { var id = active.job_id; Finish("failed", ex.Message); return Read(id); }
#else
            return Response.Error("INPUT_BACKEND_UNAVAILABLE", Availability());
#endif
        }
        private static InputFrameReceipt Find(string id, string requestKey = null) => id != null ? jobs.FirstOrDefault(x => x.job_id == id) :
            requestKey == null ? null : jobs.FirstOrDefault(x => x.request_key == requestKey && x.source_tool == "simulate_mouse_drag");
        internal static bool CanRead(string id, string requestKey, Func<string, bool> allowed)
        {
            var job = Find(id, requestKey);
            return job == null || allowed(job.source_tool);
        }
        internal static object Read(string id, string requestKey = null)
        {
            var job = Find(id, requestKey);
            return job == null ? Response.Error("INPUT_JOB_NOT_FOUND") : Response.Success("Synthetic input job: " + job.status + ". No business acceptance is inferred.", job);
        }
        internal static object Cancel(string id, string requiredTool = null)
        {
            var old = Find(id);
            if (old == null) return Response.Error("INPUT_JOB_NOT_FOUND");
            if (requiredTool != null && old.source_tool != requiredTool) return Response.Error("INPUT_JOB_OWNER_TOOL_MISMATCH");
            if (active?.job_id == id) Finish("cancelled", null);
            else if (old.status == "cleanup_pending") { old.status = "cancelled"; old.phase = "cancelled"; Cleanup(old); Save(); }
            if (old.status == "cleanup_pending") return Response.Error("INPUT_CLEANUP_UNVERIFIED", old);
            return Read(id);
        }
        private static void Observe(PlayerClockSample frame)
        {
            if (active == null || active.status != "running") return;
#if ENABLE_INPUT_SYSTEM
            try
            {
                if (device == null || !device.added) { Finish("failed", "OWNED_INPUT_DEVICE_UNAVAILABLE"); return; }
                if (!device.enabled) { Finish("failed", "INPUT_DEVICE_DISABLED_CHECK_GAME_VIEW_FOCUS_AND_BACKGROUND_POLICY"); return; }
                if (InputSystem.settings.updateMode != InputSettings.UpdateMode.ProcessEventsInDynamicUpdate) { Finish("failed", "INPUT_UPDATE_MODE_CHANGED"); return; }
                if (active.geometry != null)
                {
                    var geometryError = VisualCoordinates.Validate(active.geometry, VisualCoordinates.Current(), 0);
                    if (geometryError != null) { Finish("failed", geometryError); return; }
                }
                bool down = control.wasPressedThisFrame, held = control.isPressed, up = control.wasReleasedThisFrame;
                var position = device is Touchscreen touch ? touch.primaryTouch.position.ReadValue() : ((Mouse)device).position.ReadValue();
                string previousPhase = active.phase;
                if (down && downFrame < 0)
                {
                    active.down_observed = true; active.down_wall_seconds = frame.wall_seconds; downFrame = frame.frame;
                    active.phase = active.gesture == "drag" ? "press_holding" : "holding";
                }
                if (held && downFrame >= 0 && frame.frame > downFrame) active.held_observed = true;
                if (held && downFrame >= 0 && !releaseQueued)
                {
                    if (active.gesture == "click")
                    {
                        if (frame.frame - downFrame >= active.hold_frames - 1) Release();
                    }
                    else AdvanceDrag(frame, position);
                }
                if (up)
                {
                    active.up_observed = true;
                    bool complete = releaseQueued && !held && active.down_observed && active.held_observed && (active.gesture != "drag" || active.target_observed);
                    if (!complete) { Finish("failed", "INPUT_EDGES_INCOMPLETE"); return; }
                    if (releaseFrame < 0) { releaseFrame = frame.frame; active.phase = "release_settling"; }
                }
                AddEdge(active, new InputFrameEdge { frame = frame.frame, down = down, held = held, up = up,
                    position = new InputFramePoint(position), phase = active.phase, wall_seconds = frame.wall_seconds,
                    unscaled_seconds = frame.unscaled_seconds, game_seconds = frame.game_seconds,
                    milestone = down || up || previousPhase != active.phase });
                // Keep the released device for another real frame. Touch/UI modules and
                // LateUpdate consumers must finish their release path before device removal.
                if (releaseFrame >= 0 && frame.frame > releaseFrame && !held) Finish("completed", null);
                else Save();
            }
            catch (Exception ex) { Finish("failed", "INPUT_FRAME_FAILED: " + ex.Message); }
#endif
        }
        internal static void AddEdge(InputFrameReceipt job, InputFrameEdge edge)
        {
            job.observed_frames++;
            if (job.edges.Count >= 256)
            {
                int index = job.edges.FindIndex(1, x => !x.milestone);
                job.edges.RemoveAt(index < 0 ? 1 : index); job.edge_samples_truncated = true;
            }
            job.edges.Add(edge);
        }
#if ENABLE_INPUT_SYSTEM
        private static void AdvanceDrag(PlayerClockSample frame, Vector2 position)
        {
            if (!active.move_started_wall_seconds.HasValue)
            {
                // Even zero hold time gives scripts a distinct Down and Held frame.
                if (frame.frame <= downFrame || frame.wall_seconds - active.down_wall_seconds.Value < active.press_hold_seconds) return;
                active.move_started_wall_seconds = frame.wall_seconds; active.phase = "dragging";
            }
            if (!active.target_reached_wall_seconds.HasValue)
            {
                if (targetQueued && (position - active.end.Vector).sqrMagnitude < 0.001f)
                {
                    active.target_observed = true; active.target_reached_wall_seconds = frame.wall_seconds;
                    active.phase = "target_holding"; targetFrame = frame.frame;
                }
                else
                {
                    float progress = Mathf.Clamp01((float)((frame.wall_seconds - active.move_started_wall_seconds.Value) / active.duration_seconds));
                    Queue(true, Vector2.Lerp(active.start.Vector, active.end.Vector, progress));
                    if (progress >= 1) targetQueued = true;
                }
            }
            if (active.target_reached_wall_seconds.HasValue && frame.frame > targetFrame &&
                frame.wall_seconds - active.target_reached_wall_seconds.Value >= active.end_hold_seconds) Release();
        }
        private static void Release()
        {
            Queue(false, active.end.Vector); releaseQueued = true; active.phase = "release_queued";
        }
        private static void Queue(bool pressed, Vector2 position, bool begin = false)
        {
            if (device is Touchscreen touch)
            {
                var delta = begin ? Vector2.zero : position - touch.primaryTouch.position.ReadValue();
                InputSystem.QueueStateEvent(touch, new TouchState { touchId = 1, position = position, delta = delta, pressure = pressed ? 1 : 0,
                    phase = !pressed ? UnityEngine.InputSystem.TouchPhase.Ended : begin ? UnityEngine.InputSystem.TouchPhase.Began :
                        delta.sqrMagnitude > 0 ? UnityEngine.InputSystem.TouchPhase.Moved : UnityEngine.InputSystem.TouchPhase.Stationary });
                return;
            }
            var mouse = (Mouse)device;
            using (StateEvent.From(device, out var ptr))
            {
                mouse.position.WriteValueIntoEvent(position, ptr);
                mouse.delta.WriteValueIntoEvent(begin ? Vector2.zero : position - mouse.position.ReadValue(), ptr);
                control.WriteValueIntoEvent(pressed ? 1f : 0f, ptr);
                InputSystem.QueueEvent(ptr);
            }
            // Deliberately no InputSystem.Update(): the real Dynamic PlayerLoop consumes it.
        }
#endif
        private static void Tick() => TickAt(PlayerLoopWitness.Wall);
        internal static void TickAt(double now)
        {
            if (active == null || active.status != "running") return;
            if (!EditorApplication.isPlaying) Finish("interrupted", "play_mode_exit");
            else if (EditorApplication.isPaused) Finish("failed", "EDITOR_PAUSED_DURING_INPUT");
            else if (now >= active.deadline_wall_seconds) Finish("failed", "INPUT_PLAYER_LOOP_OR_EDGE_TIMEOUT");
        }
        private static void Finish(string status, string error)
        {
            if (active == null) return;
            active.status = status; active.phase = status; active.error = error; active.complete = status == "completed";
            active.finished_wall_seconds = PlayerLoopWitness.Wall;
            Cleanup(active);
            if (active.status != "cleanup_pending") active = null;
            Save();
        }
        private static void Cleanup(InputFrameReceipt job)
        {
            try
            {
#if ENABLE_INPUT_SYSTEM
                var owned = job.device_id == 0 ? null : InputSystem.GetDeviceById(job.device_id);
                if (owned != null)
                {
                    if (owned.name != job.device_name || !owned.name.StartsWith("FunplayMcp-" + job.job_id, StringComparison.Ordinal))
                        throw new InvalidOperationException("OWNED_INPUT_DEVICE_IDENTITY_CHANGED");
                    if (!job.up_observed) InputSystem.ResetDevice(owned); // Cancels only this device's actions; never pumps the global input loop.
                    InputSystem.RemoveDevice(owned);
                    if (owned.added) throw new InvalidOperationException("OWNED_INPUT_DEVICE_STILL_ADDED");
                }
                if (active == job) { device = null; control = null; }
#endif
                job.cleanup_verified = true;
                job.cleanup_method = job.up_observed ? "released_then_owned_device_removed" : "owned_device_reset_and_removed_no_release_frame_claimed";
            }
            catch (Exception ex) { job.status = "cleanup_pending"; job.phase = "cleanup_pending"; job.error = ex.Message; job.complete = false; job.cleanup_verified = false; }
        }
        private static void Save() => SessionState.SetString(Key, JsonConvert.SerializeObject(jobs));
    }
}
