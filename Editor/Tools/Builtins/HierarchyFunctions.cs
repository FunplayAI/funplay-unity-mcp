// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("Hierarchy")]
    internal static class HierarchyFunctions
    {
        [Description("Browse the scene hierarchy tree. Returns a tree-like view of GameObjects " +
                     "with their components, active state, and tags. " +
                     "Use root_name to start from a specific object, or leave empty for full scene.")]
        [ReadOnlyTool]
        public static string GetHierarchy(
            [ToolParam("Root object name to start from (empty = entire scene)", Required = false)] string root_name = "",
            [ToolParam("Maximum depth to traverse (1-10)", Required = false)] int depth = 3,
            [ToolParam("Include component names on each object", Required = false)] bool include_components = true,
            [ToolParam("Include inactive objects", Required = false)] bool include_inactive = true)
        {
            try
            {
                depth = Mathf.Clamp(depth, 1, 10);
                var sb = new StringBuilder();

                if (!string.IsNullOrEmpty(root_name))
                {
                    // Find specific root
                    GameObject root = GameObject.Find(root_name);
                    if (root == null)
                    {
                        // Search inactive objects too
                        foreach (var sceneRoot in SceneManager.GetActiveScene().GetRootGameObjects())
                        {
                            root = FindInChildren(sceneRoot.transform, root_name)?.gameObject;
                            if (root != null) break;
                        }
                    }
                    if (root == null)
                        return $"Error: GameObject '{root_name}' not found in scene";

                    PrintNode(sb, root.transform, 0, depth, include_components, include_inactive);
                }
                else
                {
                    // Full scene
                    var scene = SceneManager.GetActiveScene();
                    sb.AppendLine($"Scene: {scene.name}");
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (!include_inactive && !root.activeSelf) continue;
                        PrintNode(sb, root.transform, 0, depth, include_components, include_inactive);
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // --- Helpers ---

        private static void PrintNode(StringBuilder sb, Transform t, int indent, int maxDepth,
            bool includeComponents, bool includeInactive)
        {
            if (!includeInactive && !t.gameObject.activeSelf) return;

            string prefix = indent > 0 ? new string(' ', indent * 2) + "|- " : "";
            string active = t.gameObject.activeSelf ? "" : " [INACTIVE]";
            string tag = t.tag != "Untagged" ? $" tag={t.tag}" : "";

            if (includeComponents)
            {
                string comps = GetComponentSummary(t.gameObject);
                sb.AppendLine($"{prefix}{t.name}{active}{tag} [{comps}]");
            }
            else
            {
                sb.AppendLine($"{prefix}{t.name}{active}{tag}");
            }

            if (indent < maxDepth)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    PrintNode(sb, t.GetChild(i), indent + 1, maxDepth, includeComponents, includeInactive);
                }
            }
            else if (t.childCount > 0)
            {
                string childPrefix = new string(' ', (indent + 1) * 2) + "|- ";
                sb.AppendLine($"{childPrefix}... ({t.childCount} children)");
            }
        }

        private static string GetComponentSummary(GameObject go)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                string name = comp.GetType().Name;
                if (name == "Transform" || name == "RectTransform") continue; // Always present, skip
                names.Add(name);
            }
            return names.Count > 0 ? string.Join(", ", names) : "-";
        }

        private static Transform FindInChildren(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var result = FindInChildren(parent.GetChild(i), name);
                if (result != null) return result;
            }
            return null;
        }
    }
}
