// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Controls;
#endif

namespace Funplay.Editor.State
{
    [Serializable] internal sealed class InputFrameReceipt
    {
        public string job_id, status = "running", phase = "press_queued", error, route = "new_input_system";
        public ProjectExecutionContext context;
        public VisualGeometry geometry;
        public bool down_observed, held_observed, up_observed, complete;
        public bool? application_accepted;
        public List<object> edges = new List<object>();
    }
    [InitializeOnLoad] internal static class InputFrameJob
    {
        private const string Key = "Funplay.InputFrame.Jobs";
        private static List<InputFrameReceipt> jobs;
        private static InputFrameReceipt active;
        private static double deadline;
#if ENABLE_INPUT_SYSTEM
        private static Mouse device;
        private static ButtonControl control;
        private static Vector2 position;
        private static int downFrame, holdFrames;
        private static bool releaseQueued;
#endif
        static InputFrameJob()
        {
            try { jobs = JsonConvert.DeserializeObject<List<InputFrameReceipt>>(SessionState.GetString(Key, "[]")) ?? new List<InputFrameReceipt>(); }
            catch { jobs = new List<InputFrameReceipt>(); }
            foreach (var job in jobs.Where(x => x.status == "running")) { job.status = "interrupted"; job.error = "domain_reload"; }
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
                legacy_polling_injection = false, note = "Synthetic owned mouse, observed across actual PlayerLoop frames; no legacy UnityEngine.Input injection." };
#else
            return new { available = false, backend = "new_input_system", reason = "ENABLE_INPUT_SYSTEM is not enabled", legacy_polling_injection = false };
#endif
        }
        internal static object Start(Vector2 point, string button, VisualGeometry geometry, int frames, string verificationId)
        {
            if (active != null) return Response.Error("INPUT_FRAME_JOB_ACTIVE");
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) return Response.Error("UNPAUSED_PLAY_MODE_REQUIRED");
            if (frames < 2 || frames > 30) return Response.Error("INVALID_HOLD_FRAMES");
            var problem = VerificationScope.ValidateOwner(verificationId); if (problem != null) return Response.Error(problem);
#if ENABLE_INPUT_SYSTEM
            if (InputSystem.settings.updateMode != InputSettings.UpdateMode.ProcessEventsInDynamicUpdate)
                return Response.Error("INPUT_UPDATE_MODE_UNSUPPORTED", Availability());
            active = new InputFrameReceipt { job_id = Guid.NewGuid().ToString("N"), context = MCPProjectContext.Current, geometry = geometry };
            jobs.Add(active); while (jobs.Count > 16) jobs.RemoveAt(0);
            try
            {
                device = InputSystem.AddDevice<Mouse>("FunplayMcp-" + active.job_id);
                control = button == "right" ? device.rightButton : button == "middle" ? device.middleButton : device.leftButton;
                position = point; holdFrames = frames; downFrame = -1; releaseQueued = false;
                deadline = PlayerLoopWitness.Wall + 10;
                VerificationScope.Enroll(verificationId, "input", active.job_id);
                Queue(true); Save(); return Read(active.job_id);
            }
            catch (Exception ex) { var id = active.job_id; Finish("failed", ex.Message); return Read(id); }
#else
            return Response.Error("INPUT_BACKEND_UNAVAILABLE", Availability());
#endif
        }
        internal static object Read(string id)
        {
            var job = jobs.FirstOrDefault(x => x.job_id == id);
            return job == null ? Response.Error("INPUT_JOB_NOT_FOUND") : Response.Success("Synthetic input job: " + job.status + ". No business acceptance is inferred.", job);
        }
        internal static object Cancel(string id)
        {
            if (active?.job_id == id) Finish("cancelled", null);
            if (active == null)
            {
                var old = jobs.FirstOrDefault(x => x.job_id == id);
                if (old?.status == "cleanup_pending") return Response.Error("INPUT_CLEANUP_UNVERIFIED", old);
            }
            return Read(id);
        }
        private static void Observe(PlayerClockSample frame)
        {
            if (active == null) return;
#if ENABLE_INPUT_SYSTEM
            if (device == null || !device.added) { Finish("failed", "OWNED_INPUT_DEVICE_REMOVED"); return; }
            bool down = control.wasPressedThisFrame, held = control.isPressed, up = control.wasReleasedThisFrame;
            if (active.edges.Count < 34) active.edges.Add(new { frame = frame.frame, down, held, up, frame.wall_seconds, frame.unscaled_seconds, frame.game_seconds });
            if (down && downFrame < 0) { active.down_observed = true; downFrame = frame.frame; active.phase = "holding"; }
            if (held && downFrame >= 0 && frame.frame > downFrame) active.held_observed = true;
            if (!releaseQueued && downFrame >= 0 && frame.frame - downFrame >= holdFrames - 1)
            { Queue(false); releaseQueued = true; active.phase = "release_queued"; }
            if (up)
            {
                active.up_observed = true;
                Finish(active.down_observed && active.held_observed ? "completed" : "failed", active.down_observed && active.held_observed ? null : "INPUT_EDGES_INCOMPLETE");
            }
            else Save();
#endif
        }
#if ENABLE_INPUT_SYSTEM
        private static void Queue(bool pressed)
        {
            using (StateEvent.From(device, out var ptr))
            {
                device.position.WriteValueIntoEvent(position, ptr);
                control.WriteValueIntoEvent(pressed ? 1f : 0f, ptr);
                InputSystem.QueueEvent(ptr);
            }
            // Deliberately no InputSystem.Update(): the real Dynamic PlayerLoop consumes it.
        }
#endif
        private static void Tick() { if (active != null && PlayerLoopWitness.Wall >= deadline) Finish("failed", "INPUT_PLAYER_LOOP_OR_EDGE_TIMEOUT"); }
        private static void Finish(string status, string error)
        {
            if (active == null) return;
            active.status = status; active.phase = status; active.error = error; active.complete = status == "completed";
#if ENABLE_INPUT_SYSTEM
            try { if (device != null && device.added) InputSystem.RemoveDevice(device); }
            catch (Exception ex) { active.status = "cleanup_pending"; active.error = ex.Message; active.complete = false; Save(); return; }
            device = null; control = null;
#endif
            active = null; Save();
        }
        private static void Save() => SessionState.SetString(Key, JsonConvert.SerializeObject(jobs));
    }
}
