// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Funplay.Editor.MCP.Server;
using UnityEditor;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.State
{
    [Serializable]
    internal sealed class PlayerClockSample
    {
        public int frame;
        public double wall_seconds, unscaled_seconds, game_seconds;
        public float time_scale;
        public bool focused, background;
    }

    [InitializeOnLoad]
    internal static class PlayerLoopWitness
    {
        private sealed class FramePulse { }
        private static readonly Queue<PlayerClockSample> Samples = new Queue<PlayerClockSample>();
        internal static event Action<PlayerClockSample> OnPlayerFrame;
        internal static double Wall => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        private static bool installed;
        private static double installedAt;
        static PlayerLoopWitness()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode) Install();
                if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode) Remove();
            };
            AssemblyReloadEvents.beforeAssemblyReload += Remove;
            EditorApplication.quitting += Remove;
            EditorApplication.delayCall += () => { if (EditorApplication.isPlaying) Install(); };
        }
        private static void Install()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (Contains(loop)) { installed = true; return; }
            Samples.Clear();
            if (Insert(ref loop)) { PlayerLoop.SetPlayerLoop(loop); installed = true; installedAt = Wall; }
        }
        private static bool Insert(ref PlayerLoopSystem loop)
        {
            if (loop.type == typeof(UnityEngine.PlayerLoop.Update))
            {
                loop.subSystemList = (loop.subSystemList ?? Array.Empty<PlayerLoopSystem>()).Concat(new[] {
                    new PlayerLoopSystem { type = typeof(FramePulse), updateDelegate = Tick } }).ToArray();
                return true;
            }
            var children = loop.subSystemList;
            if (children != null) for (int i = 0; i < children.Length; i++) if (Insert(ref children[i])) return true;
            return false;
        }
        private static bool Contains(PlayerLoopSystem loop) => loop.type == typeof(FramePulse) ||
            loop.subSystemList != null && loop.subSystemList.Any(Contains);
        internal static void Remove()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (RemoveOwn(ref loop)) PlayerLoop.SetPlayerLoop(loop);
            // Other owners retain the final historical witness during exit/reload cleanup.
            // The next Install clears it; not-playing snapshots never claim a live frame.
            installed = false;
        }
        private static bool RemoveOwn(ref PlayerLoopSystem loop)
        {
            if (loop.subSystemList == null) return false;
            bool changed = loop.subSystemList.Any(x => x.type == typeof(FramePulse));
            loop.subSystemList = loop.subSystemList.Where(x => x.type != typeof(FramePulse)).ToArray();
            for (int i = 0; i < loop.subSystemList.Length; i++) changed |= RemoveOwn(ref loop.subSystemList[i]);
            return changed;
        }
        private static void Tick()
        {
            if (!EditorApplication.isPlaying) return;
            var sample = new PlayerClockSample { frame = Time.frameCount, wall_seconds = Wall,
                unscaled_seconds = Time.unscaledTimeAsDouble, game_seconds = Time.timeAsDouble,
                time_scale = Time.timeScale, focused = Application.isFocused, background = Application.runInBackground };
            Samples.Enqueue(sample); while (Samples.Count > 32) Samples.Dequeue();
            var handlers = OnPlayerFrame;
            if (handlers == null) return;
            foreach (Action<PlayerClockSample> callback in handlers.GetInvocationList())
            {
                try { callback(sample); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("Funplay frame observer failed: " + ex.Message); }
            }
        }
        // Pure classification distinguishes a stopped scaled clock from a stalled PlayerLoop.
        internal static string Classify(bool playing, bool paused, bool observer, PlayerClockSample first,
            PlayerClockSample last, double now)
        {
            if (!playing) return "not_playing";
            if (paused) return "editor_paused";
            if (!observer) return "observer_unavailable";
            if (last != null && now - last.wall_seconds > 1) return "stalled";
            if (last == null || first == null || last.frame <= first.frame) return "insufficient_samples";
            return last.time_scale == 0 ? "running_scaled_time_paused" : "running";
        }
        internal static object Snapshot()
        {
            var frames = Samples.ToArray();
            var first = frames.FirstOrDefault(); var last = frames.LastOrDefault();
            bool observer = installed && Contains(PlayerLoop.GetCurrentPlayerLoop());
            var state = Classify(EditorApplication.isPlaying, EditorApplication.isPaused, observer, first, last, Wall);
            if (observer && EditorApplication.isPlaying && !EditorApplication.isPaused && last == null && Wall - installedAt > 1) state = "stalled_no_samples";
            return new
            {
                status = state, context = MCPProjectContext.Current, observer_installed = observer,
                is_playing = EditorApplication.isPlaying, is_paused = EditorApplication.isPaused,
                is_focused = Application.isFocused, run_in_background = Application.runInBackground,
                time_scale = Time.timeScale, observed_frames = first == null || last == null ? 0 : last.frame - first.frame,
                sample_window_seconds = first == null || last == null ? 0 : last.wall_seconds - first.wall_seconds,
                last_frame_age_seconds = last == null ? (double?)null : Math.Max(0, Wall - last.wall_seconds),
                first, last, scene = SceneManager.GetActiveScene().path,
                application_ready = (bool?)null,
                note = "Passive PlayerLoop evidence, not business readiness, input fidelity or rendered correctness. Reads do not change focus, pause or background settings."
            };
        }
        internal static PlayerClockSample Latest => Samples.LastOrDefault();
    }
}
