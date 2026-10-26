// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Linq;
using Funplay.Editor.DI;
using Funplay.Editor.Settings;
using Funplay.Editor.Tools;

namespace Funplay.Editor.MCP.Server
{
    internal static class MCPRecoveryGuidance
    {
        internal static bool Allowed(string name)
        {
            var settings = RootScopeServices.Services?.GetService(typeof(ISettingsController)) as ISettingsController;
            return ToolRegistry.IsEnabled(name) && MCPToolExportPolicy.IsToolAllowed(name,
                MCPToolExportPolicy.Parse(settings?.MCPToolExportProfile), settings?.MCPCoreToolsConfigured ?? false,
                settings?.MCPCoreTools, settings?.MCPFullToolsConfigured ?? false, settings?.MCPFullTools);
        }
        internal static string Describe(Func<string, bool> allowed = null)
        {
            allowed = allowed ?? Allowed;
            var routes = new[] { "get_task", "get_editor_state", "get_compilation_errors", "get_reload_recovery_status" }.Where(allowed).ToArray();
            return "No uncertain mutation was replayed. " + (routes.Length == 0
                ? "No recovery reader is exposed. Inspect tools/list and request deliberate access or report the blocker."
                : "Available recovery readers: " + string.Join(", ", routes) +
                  ". Use an existing task ID/request_key with get_task when exposed; otherwise read the exact affected state. Historical reload events are not readiness.");
        }
    }
}
