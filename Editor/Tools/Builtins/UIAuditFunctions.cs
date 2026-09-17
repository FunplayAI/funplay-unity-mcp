// Copyright (C) Funplay. Licensed under MIT.
using System.ComponentModel;
using Funplay.Editor.Tools.Helpers;
using Funplay.Editor.State;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("UIAudit")]
    internal static class UIAuditFunctions
    {
        [Description("Read-only UI audit over selection, live roots, prefabs or saved scenes. MCP briefly waits for small scans; otherwise use get_task for bounded waiting and finding pages. Reports Sprite borders, references, invisible raycast targets, text/clipping/layout concerns with evidence. Never fixes or saves assets.")]
        [ReadOnlyTool]
        public static object AuditUi(
            [ToolParam("selection, scene, prefabs, scenes or assets", Required = false)] string scope = "scene",
            [ToolParam("JSON array of Assets paths/folders for asset scopes", Required = false)] string paths = null,
            [ToolParam("JSON array of live root IDs/paths; omitted means all loaded scene roots", Required = false)] string roots = null,
            [ToolParam("Optional JSON required_references and suppressions; otherwise reads ProjectSettings/FunplayMcp.UIAudit.json", Required = false)] string configuration = null,
            [ToolParam("Include inactive objects", Required = false)] bool include_inactive = true,
            [ToolParam("Maximum scanned objects, 1..100000", Required = false)] int max_objects = 10000,
            [ToolParam("Maximum recorded findings, 1..5000", Required = false)] int max_findings = 1000,
            [ToolParam("Total time budget, 1..300 seconds; individual Unity asset loads cannot be preempted", Required = false)] int timeout_seconds = 60,
            [ToolParam("MCP completion wait, 0..30 seconds", Required = false)] int wait_seconds = 2)
            => TaskStatusService.ValidateWait(wait_seconds) ?? TaskStatusService.Attach("ui_audit", UIAuditService.Start(scope, paths, roots, configuration, include_inactive, max_objects, max_findings, timeout_seconds));

        [Description("Read UI audit status and a findings page. complete=false means limits, skipped assets, cancellation or interruption; never treat an incomplete empty report as a clean project. Latest job retained across domain reload; unfinished scans are marked interrupted, not silently resumed.")]
        [ReadOnlyTool]
        public static object GetUiAudit([ToolParam("Audit job ID")] string job_id,
            [ToolParam("Zero-based finding offset", Required = false)] int offset = 0,
            [ToolParam("Page size, 1..500", Required = false)] int limit = 100) => UIAuditService.Read(job_id, offset, limit);

        [Description("Cancel the read-only UI audit and close only its owned preview scene. Results already gathered remain available.")]
        public static object CancelUiAudit([ToolParam("Audit job ID")] string job_id) => UIAuditService.Cancel(job_id);
    }
}
