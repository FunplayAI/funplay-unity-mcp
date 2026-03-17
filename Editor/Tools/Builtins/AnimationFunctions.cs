// Copyright (C) GameBooom. Licensed under GPLv3.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameBooom.Editor.Tools.Builtins
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
                return $"Error: GameObject '{game_object_name}' not found";

            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controller_path);
            if (controller == null)
                return $"Error: Animator Controller not found at '{controller_path}'";

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                animator = Undo.AddComponent<Animator>(go);

            Undo.RecordObject(animator, $"Assign animator to {game_object_name}");
            animator.runtimeAnimatorController = controller;
            return $"Assigned Animator Controller to '{game_object_name}'";
        }
    }
}
