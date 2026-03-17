// Copyright (C) GameBooom. Licensed under GPLv3.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameBooom.Editor.Tools
{
    /// <summary>
    /// Builds a UIElements panel that lists all discovered tools grouped by category,
    /// with toggles to enable/disable each tool at runtime.
    /// </summary>
    internal static class ToolManagementPanel
    {
        public static VisualElement Build()
        {
            var root = new VisualElement();
            root.style.marginTop = 8;

            var header = new Label("Tool Functions");
            header.style.fontSize = 13;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.color = new Color(0.8f, 0.8f, 0.8f);
            header.style.marginBottom = 6;
            root.Add(header);

            var desc = new Label(
                "Enable or disable individual tools. Disabled tools are not sent to the LLM.");
            desc.style.fontSize = 10;
            desc.style.color = new Color(0.6f, 0.6f, 0.6f);
            desc.style.whiteSpace = WhiteSpace.Normal;
            desc.style.marginBottom = 8;
            root.Add(desc);

            // Group tools by provider category
            var groups = BuildToolGroups();

            foreach (var group in groups.OrderBy(g => g.Key))
            {
                var foldout = new Foldout();
                foldout.text = $"{group.Key} ({group.Value.Count})";
                foldout.value = false; // collapsed by default
                foldout.style.marginBottom = 4;

                foreach (var tool in group.Value.OrderBy(t => t.Name))
                {
                    var toolName = tool.Name; // capture for closure
                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.alignItems = Align.Center;
                    row.style.marginLeft = 4;
                    row.style.marginBottom = 2;

                    var toggle = new Toggle();
                    toggle.value = ToolRegistry.IsEnabled(toolName);
                    toggle.RegisterValueChangedCallback(evt =>
                        ToolRegistry.SetEnabled(toolName, evt.newValue));
                    toggle.style.marginRight = 4;
                    row.Add(toggle);

                    var nameLabel = new Label(toolName);
                    nameLabel.style.fontSize = 11;
                    nameLabel.style.color = Color.white;
                    nameLabel.style.minWidth = 180;
                    row.Add(nameLabel);

                    if (!string.IsNullOrEmpty(tool.Description))
                    {
                        var descLabel = new Label(tool.Description);
                        descLabel.style.fontSize = 10;
                        descLabel.style.color = new Color(0.55f, 0.55f, 0.55f);
                        descLabel.style.flexGrow = 1;
                        descLabel.style.overflow = Overflow.Hidden;
                        row.Add(descLabel);
                    }

                    // Read-only badge
                    if (tool.IsReadOnly)
                    {
                        var badge = new Label("RO");
                        badge.tooltip = "Read-only tool (does not modify the scene)";
                        badge.style.fontSize = 9;
                        badge.style.color = new Color(0.4f, 0.8f, 0.4f);
                        badge.style.marginLeft = 4;
                        row.Add(badge);
                    }

                    foldout.Add(row);
                }

                root.Add(foldout);
            }

            // Manual tools section
            var manualTools = ToolRegistry.ManualTools;
            if (manualTools.Count > 0)
            {
                var manualFoldout = new Foldout();
                manualFoldout.text = $"External ({manualTools.Count})";
                manualFoldout.value = false;
                manualFoldout.style.marginBottom = 4;

                foreach (var kvp in manualTools)
                {
                    var toolName = kvp.Key;
                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.alignItems = Align.Center;
                    row.style.marginLeft = 4;
                    row.style.marginBottom = 2;

                    var toggle = new Toggle();
                    toggle.value = ToolRegistry.IsEnabled(toolName);
                    toggle.RegisterValueChangedCallback(evt =>
                        ToolRegistry.SetEnabled(toolName, evt.newValue));
                    toggle.style.marginRight = 4;
                    row.Add(toggle);

                    var nameLabel = new Label(toolName);
                    nameLabel.style.fontSize = 11;
                    nameLabel.style.color = Color.white;
                    row.Add(nameLabel);

                    manualFoldout.Add(row);
                }

                root.Add(manualFoldout);
            }

            return root;
        }

        private struct ToolInfo
        {
            public string Name;
            public string Description;
            public bool IsReadOnly;
        }

        private static Dictionary<string, List<ToolInfo>> BuildToolGroups()
        {
            var groups = new Dictionary<string, List<ToolInfo>>();

            // Ensure registry is scanned
            var _ = ToolRegistry.ProviderTypes;

            foreach (var type in ToolRegistry.ProviderTypes)
            {
                var attr = type.GetCustomAttribute<ToolProviderAttribute>();
                var category = attr?.Category ?? type.Name;

                if (!groups.ContainsKey(category))
                    groups[category] = new List<ToolInfo>();

                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                foreach (var method in methods)
                {
                    var snakeName = ToolRegistry.ToSnakeCase(method.Name);

                    // Skip blocked tools (they won't be in the registry)
                    if (!ToolRegistry.MethodCache.ContainsKey(snakeName))
                        continue;

                    var descAttr = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();

                    groups[category].Add(new ToolInfo
                    {
                        Name = snakeName,
                        Description = descAttr?.Description ?? "",
                        IsReadOnly = ToolRegistry.IsReadOnly(method)
                    });
                }
            }

            return groups;
        }
    }
}
