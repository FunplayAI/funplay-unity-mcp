// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    /// <summary>
    /// Read and mutate editor-level state: selection, prefab stage, tags, layers, windows,
    /// active tool, and build settings. These tools fill the gap that previously forced agents
    /// to write <c>execute_code</c> snippets just to inspect the editor.
    /// </summary>
    [ToolProvider("EditorState")]
    internal static class EditorStateFunctions
    {
        [Description("Get high-level editor runtime state: play mode, paused, compiling, updating, time-since-startup.")]
        [ReadOnlyTool]
        public static object GetEditorState()
        {
            return Response.Success("Editor state", new
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                isPlayingOrWillChange = EditorApplication.isPlayingOrWillChangePlaymode,
                applicationPath = EditorApplication.applicationPath,
                timeSinceStartup = EditorApplication.timeSinceStartup,
                unityVersion = Application.unityVersion,
                readiness = Funplay.Editor.State.EditorOperationService.CurrentEditor()
            });
        }

        // ----- Selection -----

        [Description("Get the current Hierarchy selection: instance ids, names, and paths.")]
        [ReadOnlyTool]
        public static object GetSelection()
        {
            var gos = Selection.gameObjects ?? Array.Empty<GameObject>();
            var items = gos.Select(go => new
            {
                instanceId = ObjectIdHelper.GetSerializableId(go),
                name = go.name,
                path = ObjectsHelper.GetGameObjectPath(go)
            }).ToList();

            return Response.Success($"Selection contains {items.Count} object(s).", new
            {
                count = items.Count,
                activeInstanceId = ObjectIdHelper.GetSerializableId(Selection.activeGameObject),
                items
            });
        }

        [Description("Replace the current selection. Pass either a comma-separated list of instance ids, names, or paths. " +
                     "Use find_method to control how each token is resolved (by_id, by_name, by_path).")]
        [SceneEditingTool]
        public static object SetSelection(
            [ToolParam("Comma-separated instance ids, names, or paths")] string targets,
            [ToolParam("How to resolve each token (by_id, by_name, by_path, by_id_or_name_or_path)", Required = false)] string find_method = null)
        {
            if (string.IsNullOrWhiteSpace(targets))
            {
                Selection.objects = Array.Empty<UnityEngine.Object>();
                return Response.Success("Cleared selection.");
            }

            var tokens = targets.Split(',');
            var picked = new List<GameObject>();
            var missing = new List<string>();

            foreach (var raw in tokens)
            {
                var token = raw.Trim();
                if (string.IsNullOrEmpty(token)) continue;
                var go = ObjectsHelper.FindObject(token, find_method);
                if (go != null) picked.Add(go);
                else missing.Add(token);
            }

            Selection.objects = picked.Cast<UnityEngine.Object>().ToArray();
            if (picked.Count > 0)
                Selection.activeGameObject = picked[0];

            return Response.Success(
                $"Selected {picked.Count} object(s){(missing.Count > 0 ? $", {missing.Count} not found" : string.Empty)}.",
                new
                {
                    selected = picked.Select(g => new { instanceId = ObjectIdHelper.GetSerializableId(g), name = g.name }).ToList(),
                    notFound = missing
                });
        }

        // ----- Prefab Stage -----

        [Description("Get info about the currently open prefab stage (if any). Returns prefab asset path, root instance id, and dirty state.")]
        [ReadOnlyTool]
        public static object GetPrefabStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return Response.Success("No prefab stage open.", new { open = false });

            var root = stage.prefabContentsRoot;
            return Response.Success("Prefab stage open.", new
            {
                open = true,
                assetPath = stage.assetPath,
                rootInstanceId = ObjectIdHelper.GetSerializableId(root),
                rootName = root != null ? root.name : null,
                mode = stage.mode.ToString(),
                scene = stage.scene.IsValid() ? stage.scene.name : null
            });
        }

        // ----- Active Tool -----

        [Description("Get the currently active editor manipulation tool (Move/Rotate/Scale/Rect/Transform/View/None/Custom).")]
        [ReadOnlyTool]
        public static object GetActiveTool()
        {
            return Response.Success("Active tool.", new
            {
                tool = UnityEditor.Tools.current.ToString(),
                pivotMode = UnityEditor.Tools.pivotMode.ToString(),
                pivotRotation = UnityEditor.Tools.pivotRotation.ToString()
            });
        }

        [Description("Set the active editor manipulation tool. Accepts: View, Move, Rotate, Scale, Rect, Transform, None.")]
        [SceneEditingTool]
        public static object SetActiveTool(
            [ToolParam("Tool name: View, Move, Rotate, Scale, Rect, Transform, None")] string tool)
        {
            if (!Enum.TryParse<UnityEditor.Tool>(tool, ignoreCase: true, out var parsed))
                return Response.Error("INVALID_TOOL",
                    new { tool, accepted = Enum.GetNames(typeof(UnityEditor.Tool)) });

            UnityEditor.Tools.current = parsed;
            return Response.Success($"Active tool set to {parsed}.");
        }

        // ----- Windows -----

        [Description("List all currently open EditorWindows: title, type, and focused state.")]
        [ReadOnlyTool]
        public static object GetWindows()
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var focused = EditorWindow.focusedWindow;
            var items = windows
                .Where(w => w != null)
                .Select(w => new
                {
                    title = w.titleContent != null ? w.titleContent.text : w.GetType().Name,
                    type = w.GetType().FullName,
                    focused = w == focused,
                    docked = w.docked
                })
                .ToList();
            return Response.Success($"{items.Count} window(s) open.", items);
        }

        // ----- Tags -----

        [Description("List every defined tag in TagManager.")]
        [ReadOnlyTool]
        public static object GetTags()
        {
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            return Response.Success($"{tags.Length} tag(s).", tags);
        }

        [Description("Add a new tag to TagManager (no-op if it already exists).")]
        [SceneEditingTool]
        public static object AddTag(
            [ToolParam("Tag name to add")] string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return Response.Error("INVALID_TAG", new { message = "Tag name cannot be empty." });

            if (UnityEditorInternal.InternalEditorUtility.tags.Contains(tag))
                return Response.Success($"Tag '{tag}' already exists.");

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var tagsProp = tagManager.FindProperty("tags");
            tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
            tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tag;
            tagManager.ApplyModifiedProperties();
            return Response.Success($"Added tag '{tag}'.");
        }

        [Description("Remove a tag from TagManager. Errors if the tag is still in use.")]
        [SceneEditingTool]
        public static object RemoveTag(
            [ToolParam("Tag name to remove")] string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return Response.Error("INVALID_TAG", new { message = "Tag name cannot be empty." });

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var tagsProp = tagManager.FindProperty("tags");
            for (int i = 0; i < tagsProp.arraySize; i++)
            {
                if (tagsProp.GetArrayElementAtIndex(i).stringValue == tag)
                {
                    tagsProp.DeleteArrayElementAtIndex(i);
                    tagManager.ApplyModifiedProperties();
                    return Response.Success($"Removed tag '{tag}'.");
                }
            }
            return Response.Error("TAG_NOT_FOUND", new { tag });
        }

        // ----- Layers -----

        [Description("List all 32 layer slots with their names (empty for unused slots).")]
        [ReadOnlyTool]
        public static object GetLayers()
        {
            var layers = new List<object>(32);
            for (int i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                layers.Add(new { index = i, name = string.IsNullOrEmpty(name) ? null : name });
            }
            return Response.Success("Layer table.", layers);
        }

        [Description("Add a new layer to the first empty user slot (8-31). Errors if no slot is free.")]
        [SceneEditingTool]
        public static object AddLayer(
            [ToolParam("Layer name to add")] string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
                return Response.Error("INVALID_LAYER", new { message = "Layer name cannot be empty." });

            if (LayerMask.NameToLayer(layer) != -1)
                return Response.Success($"Layer '{layer}' already exists at index {LayerMask.NameToLayer(layer)}.");

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layersProp = tagManager.FindProperty("layers");
            for (int i = 8; i < layersProp.arraySize; i++)
            {
                var slot = layersProp.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = layer;
                    tagManager.ApplyModifiedProperties();
                    return Response.Success($"Added layer '{layer}' at index {i}.");
                }
            }
            return Response.Error("NO_FREE_LAYER", new { message = "All user layer slots (8-31) are taken." });
        }

        // ----- Build Settings -----

        [Description("Get build settings: enabled scenes (in build order), active build target, scripting backend.")]
        [ReadOnlyTool]
        public static object GetBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes
                .Select((s, i) => new { index = i, path = s.path, enabled = s.enabled, guid = s.guid.ToString() })
                .ToList();

            return Response.Success("Build settings.", new
            {
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                selectedBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString(),
                scenes,
                sceneCount = scenes.Count,
                enabledSceneCount = scenes.Count(s => s.enabled)
            });
        }
    }
}
