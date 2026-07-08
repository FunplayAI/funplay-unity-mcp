// Copyright (C) Funplay. Licensed under MIT.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Particle")]
    internal static class ParticleFunctions
    {
        [Description("Control a ParticleSystem's playback for deterministic VFX inspection/screenshots. " +
                     "action='simulate' fast-forwards the system to a fixed time (useful in Edit Mode to freeze a VFX " +
                     "at a repeatable frame for capture); 'play'/'pause'/'stop'/'restart' drive the matching playback state. " +
                     "Resolves the ParticleSystem on the target GameObject (or its first child that has one). " +
                     "Returns the resulting playback state (isPlaying/isEmitting/isStopped/particleCount) plus the main " +
                     "module's loop/duration.")]
        [SceneEditingTool]
        public static object ParticleControl(
            [ToolParam("Identifier of the GameObject holding the ParticleSystem (instance id, name, or hierarchy path). Inactive objects are found.")] string target,
            [ToolParam("Action: 'simulate' | 'play' | 'pause' | 'stop' | 'restart'", Required = false)] string action = "simulate",
            [ToolParam("Time in seconds to fast-forward to (only used by action='simulate')", Required = false)] float time = 0.5f,
            [ToolParam("Apply the action to child ParticleSystems as well", Required = false)] bool with_children = true)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target });

            // Prefer a ParticleSystem directly on the target, else the first one in its children
            // (searchInactive=true so a disabled VFX node still resolves, matching FindTarget's behavior).
            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null)
                ps = go.GetComponentInChildren<ParticleSystem>(true);
            if (ps == null)
                return Response.Error("PARTICLE_SYSTEM_NOT_FOUND",
                    new { target, gameObject = go.name, hint = "No ParticleSystem on the target or its children." });

            var normalized = string.IsNullOrEmpty(action) ? "simulate" : action.Trim().ToLowerInvariant();

            // These are transient playback-state controls, not serialized-field mutations, so there is no
            // meaningful Undo entry to record (Undo.RecordObject would only dirty the scene without capturing state).
            switch (normalized)
            {
                case "simulate":
                    // Clamp the fast-forward time and use fixedTimeStep=false (one variable step)
                    // so a large value can't step the system through tens of thousands of fixed
                    // sub-steps on the main thread and freeze the editor.
                    time = Mathf.Clamp(time, 0f, 60f);
                    // Simulate(t, withChildren, restart, fixedTimeStep): fixedTimeStep=false does one
                    // variable step so a large time can't churn tens of thousands of fixed sub-steps.
                    ps.Simulate(time, with_children, true, false);
                    break;
                case "play":
                    ps.Play(with_children);
                    break;
                case "pause":
                    ps.Pause(with_children);
                    break;
                case "stop":
                    ps.Stop(with_children);
                    break;
                case "restart":
                    // No native Restart(): clear existing particles then play from the start.
                    ps.Clear(with_children);
                    ps.Play(with_children);
                    break;
                default:
                    return Response.Error("UNKNOWN_ACTION",
                        new { action, expected = "simulate|play|pause|stop|restart" });
            }

            var main = ps.main;
            var message = normalized == "simulate"
                ? $"Simulated ParticleSystem '{ps.name}' to {time}s (particleCount={ps.particleCount})."
                : $"ParticleSystem '{ps.name}' action '{normalized}' applied.";

            return Response.Success(message, new
            {
                action = normalized,
                gameObject = go.name,
                instanceId = ObjectIdHelper.GetSerializableId(ps.gameObject),
                isPlaying = ps.isPlaying,
                isEmitting = ps.isEmitting,
                isStopped = ps.isStopped,
                particleCount = ps.particleCount,
                loop = main.loop,
                duration = main.duration
            });
        }
    }
}
