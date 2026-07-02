// Copyright (C) Funplay. Licensed under MIT.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Animation")]
    internal static class AnimationFunctions
    {
        [Description("Create an Animator Controller asset")]
        [SceneEditingTool]
        public static string CreateAnimatorController(
            [ToolParam("Name of the controller")] string name,
            [ToolParam("Save path", Required = false)] string save_path = "Assets/Animations/")
        {
            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

            var fullPath = $"{save_path}{name}.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(fullPath);
            return $"Created Animator Controller at {fullPath}";
        }

        [Description("Create an Animation Clip asset")]
        [SceneEditingTool]
        public static string CreateAnimationClip(
            [ToolParam("Name of the animation clip")] string name,
            [ToolParam("Save path", Required = false)] string save_path = "Assets/Animations/")
        {
            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

            var clip = new AnimationClip();
            clip.name = name;

            var fullPath = $"{save_path}{name}.anim";
            AssetDatabase.CreateAsset(clip, fullPath);
            AssetDatabase.Refresh();
            return $"Created Animation Clip at {fullPath}";
        }

        [Description("Assign an Animator Controller to a GameObject")]
        [SceneEditingTool]
        public static string AssignAnimator(
            [ToolParam("Name of the GameObject")] string game_object_name,
            [ToolParam("Path to the Animator Controller asset")] string controller_path)
        {
            var go = GameObject.Find(game_object_name);
            if (go == null)
                return ToolResultFormatter.Error("GAME_OBJECT_NOT_FOUND", new { game_object_name });

            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controller_path);
            if (controller == null)
                return ToolResultFormatter.Error("ANIMATOR_CONTROLLER_NOT_FOUND", new { controller_path });

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                animator = Undo.AddComponent<Animator>(go);

            Undo.RecordObject(animator, $"Assign animator to {game_object_name}");
            animator.runtimeAnimatorController = controller;
            return $"Assigned Animator Controller to '{game_object_name}'";
        }

        [Description("Get an Animator's current runtime state: active state name (resolved from the controller when possible), " +
                     "normalized time, transition status, and all parameters with their current values. " +
                     "Most useful in Play Mode; in Edit Mode a non-playing Animator reports default state info.")]
        [ReadOnlyTool]
        public static object GetAnimatorState(
            [ToolParam("GameObject identifier (instance id, name, path, tag…)")] string target,
            [ToolParam("Layer index to report the current state for", Required = false)] int layer = 0,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var resolved = ResolveAnimator(target, find_method);
            if (resolved.Error != null) return resolved.Error;
            var animator = resolved.Animator;

            if (layer < 0 || layer >= animator.layerCount)
                return Response.Error("INVALID_LAYER", new { layer, layerCount = animator.layerCount });

            var stateInfo = animator.GetCurrentAnimatorStateInfo(layer);
            var parameters = new System.Collections.Generic.List<object>();
            foreach (var p in animator.parameters)
            {
                object value;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: value = animator.GetFloat(p.nameHash); break;
                    case AnimatorControllerParameterType.Int: value = animator.GetInteger(p.nameHash); break;
                    case AnimatorControllerParameterType.Bool: value = animator.GetBool(p.nameHash); break;
                    case AnimatorControllerParameterType.Trigger: value = animator.GetBool(p.nameHash) ? "pending" : "idle"; break;
                    default: value = null; break;
                }
                parameters.Add(new { name = p.name, type = p.type.ToString(), value });
            }

            return Response.Success($"Animator state on '{animator.gameObject.name}' (layer {layer}).", new
            {
                gameObject = new { instanceId = ObjectIdHelper.GetSerializableId(animator.gameObject), name = animator.gameObject.name },
                controller = animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : null,
                isActiveAndEnabled = animator.isActiveAndEnabled,
                layer,
                layerCount = animator.layerCount,
                currentState = new
                {
                    name = ResolveStateName(animator, layer, stateInfo.shortNameHash),
                    shortNameHash = stateInfo.shortNameHash,
                    normalizedTime = stateInfo.normalizedTime,
                    length = stateInfo.length,
                    speed = stateInfo.speed,
                    loop = stateInfo.loop
                },
                isInTransition = animator.IsInTransition(layer),
                parameters
            });
        }

        [Description("Set an Animator parameter by name. The parameter type (Float/Int/Bool/Trigger) is detected automatically " +
                     "from the controller. For Trigger parameters pass true (or 'set') to fire, false (or 'reset') to clear. " +
                     "Runtime-only state: not undoable, resets when Play Mode exits.")]
        public static object SetAnimatorParameter(
            [ToolParam("GameObject identifier (instance id, name, path, tag…)")] string target,
            [ToolParam("Parameter name as defined in the Animator Controller")] string parameter,
            [ToolParam("Value: number for Float/Int, true/false for Bool, true/'set'/false/'reset' for Trigger")] string value,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            if (string.IsNullOrEmpty(parameter))
                return Response.Error("PARAMETER_REQUIRED");
            if (string.IsNullOrEmpty(value))
                return Response.Error("VALUE_REQUIRED");

            var resolved = ResolveAnimator(target, find_method);
            if (resolved.Error != null) return resolved.Error;
            var animator = resolved.Animator;

            AnimatorControllerParameter match = null;
            foreach (var p in animator.parameters)
            {
                if (string.Equals(p.name, parameter, System.StringComparison.Ordinal)) { match = p; break; }
            }
            if (match == null)
            {
                var available = new System.Collections.Generic.List<string>();
                foreach (var p in animator.parameters)
                    available.Add($"{p.name} ({p.type})");
                return Response.Error("PARAMETER_NOT_FOUND", new { parameter, available });
            }

            value = value.Trim();
            switch (match.type)
            {
                case AnimatorControllerParameterType.Float:
                    if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var f))
                        return Response.Error("INVALID_FLOAT", new { value });
                    animator.SetFloat(match.nameHash, f);
                    return Response.Success($"Set float '{parameter}' = {f}.");
                case AnimatorControllerParameterType.Int:
                    if (!int.TryParse(value, out var i))
                        return Response.Error("INVALID_INT", new { value });
                    animator.SetInteger(match.nameHash, i);
                    return Response.Success($"Set int '{parameter}' = {i}.");
                case AnimatorControllerParameterType.Bool:
                    if (!bool.TryParse(value, out var b))
                        return Response.Error("INVALID_BOOL", new { value });
                    animator.SetBool(match.nameHash, b);
                    return Response.Success($"Set bool '{parameter}' = {b}.");
                case AnimatorControllerParameterType.Trigger:
                    var lowered = value.ToLowerInvariant();
                    if (lowered == "true" || lowered == "set")
                    {
                        animator.SetTrigger(match.nameHash);
                        return Response.Success($"Fired trigger '{parameter}'.");
                    }
                    if (lowered == "false" || lowered == "reset")
                    {
                        animator.ResetTrigger(match.nameHash);
                        return Response.Success($"Reset trigger '{parameter}'.");
                    }
                    return Response.Error("INVALID_TRIGGER_VALUE", new { value, accepted = new[] { "true", "set", "false", "reset" } });
                default:
                    return Response.Error("UNSUPPORTED_PARAMETER_TYPE", new { type = match.type.ToString() });
            }
        }

        [Description("Play a specific Animator state by name, e.g. to drive UI or a character to a known visual state " +
                     "before taking a screenshot. In Edit Mode the animator is force-evaluated once so the pose applies " +
                     "without entering Play Mode. Runtime-only state: not undoable.")]
        public static object PlayAnimatorState(
            [ToolParam("GameObject identifier (instance id, name, path, tag…)")] string target,
            [ToolParam("State name as defined in the Animator Controller (e.g. 'Idle' or 'Base Layer.Idle')")] string state,
            [ToolParam("Layer index; -1 lets Unity pick the first layer containing the state", Required = false)] int layer = -1,
            [ToolParam("Normalized start time 0-1; omit to keep Unity's default", Required = false)] float normalized_time = float.NegativeInfinity,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            if (string.IsNullOrEmpty(state))
                return Response.Error("STATE_REQUIRED");

            var resolved = ResolveAnimator(target, find_method);
            if (resolved.Error != null) return resolved.Error;
            var animator = resolved.Animator;

            var hash = Animator.StringToHash(state);
            bool found = false;
            if (layer >= 0)
            {
                found = layer < animator.layerCount && animator.HasState(layer, hash);
            }
            else
            {
                for (int l = 0; l < animator.layerCount; l++)
                {
                    if (animator.HasState(l, hash)) { found = true; break; }
                }
            }
            if (!found)
                return Response.Error("STATE_NOT_FOUND", new { state, layer, hint = "Check the state name in the Animator Controller (short name or 'LayerName.StateName')." });

            animator.Play(hash, layer, normalized_time);

            // In Edit Mode the animator doesn't tick on its own -- evaluate once so the pose is visible immediately.
            if (!EditorApplication.isPlaying)
                animator.Update(0f);

            return Response.Success($"Playing state '{state}' on '{animator.gameObject.name}' (layer {layer}).", new
            {
                gameObject = new { instanceId = ObjectIdHelper.GetSerializableId(animator.gameObject), name = animator.gameObject.name },
                state,
                layer
            });
        }

        // -------- Helpers --------

        private struct ResolvedAnimator
        {
            public Animator Animator;
            public object Error;
        }

        private static ResolvedAnimator ResolveAnimator(string target, string findMethod)
        {
            var go = ObjectsHelper.FindObject(target, findMethod, searchInactive: true);
            if (go == null)
                return new ResolvedAnimator { Error = Response.Error("TARGET_NOT_FOUND", new { target, find_method = findMethod }) };

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                return new ResolvedAnimator { Error = Response.Error("NO_ANIMATOR_ON_TARGET", new { target = go.name }) };

            if (animator.runtimeAnimatorController == null)
                return new ResolvedAnimator { Error = Response.Error("NO_CONTROLLER_ASSIGNED", new { target = go.name }) };

            return new ResolvedAnimator { Animator = animator };
        }

        // Best-effort: resolve the current state's display name from the editor-side controller asset.
        // Returns null when the controller isn't an AnimatorController (e.g. pure override chains we can't unwrap)
        // or the hash isn't found (state inside a nested sub-state machine, etc).
        private static string ResolveStateName(Animator animator, int layer, int shortNameHash)
        {
            var runtimeController = animator.runtimeAnimatorController;
            var overrideController = runtimeController as AnimatorOverrideController;
            if (overrideController != null)
                runtimeController = overrideController.runtimeAnimatorController;

            var controller = runtimeController as AnimatorController;
            if (controller == null || layer >= controller.layers.Length)
                return null;

            foreach (var childState in controller.layers[layer].stateMachine.states)
            {
                if (Animator.StringToHash(childState.state.name) == shortNameHash)
                    return childState.state.name;
            }
            return null;
        }
    }
}
