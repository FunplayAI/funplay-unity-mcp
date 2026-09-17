// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;

namespace Funplay.Editor.MCP.Server
{
    internal enum MCPToolExportProfile
    {
        Core,
        Full
    }

    internal static class MCPToolExportPolicy
    {
        private static readonly HashSet<string> CoreTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "execute_code",
            "simulate_key_press",
            "simulate_key_combo",
            "simulate_mouse_click",
            "simulate_mouse_drag",
            "get_scene_info",
            "get_hierarchy",
            "get_console_logs",
            "capture_scene_view",
            "capture_game_view",
            "record_game_view",
            "extract_recording_frames",
            "get_recording_frame",
            "simulate_ui_scroll",
            "raycast_at_point",
            "get_compilation_errors",
            "prepare_editor",
            "get_task",
            "cancel_editor_operation",
            "audit_ui",
            "cancel_ui_audit",
            "find_project_types",
            "inspect_ui_sprites",
            "get_tool_capabilities",
            "get_visual_coordinates",
            "get_object_screen_bounds",
            "get_ui_defaults",
            "create_project_ui",
            // Editor state -- high-frequency reads/writes
            "get_editor_state",
            "get_selection",
            "set_selection",
            "get_prefab_stage",
            // Object location + component editing -- the new structured surface
            "find_game_objects",
            "list_components",
            "get_component_properties",
            "set_component_property",
            "set_component_properties",
            "set_prefab_property",
            "set_prefab_properties",
            // Menu items -- safer fallback than execute_code for editor actions
            "execute_menu_item"
        };

        public static IReadOnlyCollection<string> DefaultCoreTools => CoreTools;

        public static MCPToolExportProfile Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return MCPToolExportProfile.Core;

            switch (value.Trim().ToLowerInvariant())
            {
                case "full":
                    return MCPToolExportProfile.Full;
                default:
                    return MCPToolExportProfile.Core;
            }
        }

        public static string ToSettingValue(MCPToolExportProfile profile)
        {
            switch (profile)
            {
                case MCPToolExportProfile.Full:
                    return "full";
                default:
                    return "core";
            }
        }

        public static bool IsToolAllowed(
            string toolName,
            MCPToolExportProfile profile,
            bool coreToolsConfigured,
            IEnumerable<string> coreTools,
            bool fullToolsConfigured,
            IEnumerable<string> fullTools)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            if (profile == MCPToolExportProfile.Full)
                return !fullToolsConfigured || ContainsTool(fullTools, toolName);

            return coreToolsConfigured
                ? ContainsTool(coreTools, toolName)
                : CoreTools.Contains(toolName);
        }

        private static bool ContainsTool(IEnumerable<string> tools, string toolName)
        {
            if (tools == null)
                return false;

            foreach (var tool in tools)
            {
                if (string.Equals(tool, toolName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public static string BuildDescriptionPrefix(MCPToolExportProfile profile)
        {
            return profile == MCPToolExportProfile.Core ? "[core] " : string.Empty;
        }

        public static int GetSortRank(string toolName, MCPToolExportProfile profile)
        {
            if (string.Equals(toolName, "execute_code", StringComparison.OrdinalIgnoreCase))
                return 10000;

            if (profile == MCPToolExportProfile.Core && CoreTools.Contains(toolName))
                return 100;

            return 1000;
        }

        public static string BuildDescriptionPrefix(string toolName, MCPToolExportProfile profile)
        {
            if (string.Equals(toolName, "execute_code", StringComparison.OrdinalIgnoreCase))
                return "[fallback] " + BuildDescriptionPrefix(profile);

            return BuildDescriptionPrefix(profile);
        }
    }
}
