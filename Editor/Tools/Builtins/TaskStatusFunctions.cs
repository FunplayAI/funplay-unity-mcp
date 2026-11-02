// Copyright (C) Funplay. Licensed under MIT.
using System.ComponentModel;
using Funplay.Editor.State;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Tasks")]
    internal static class TaskStatusFunctions
    {
        [Description("Read an async task without starting, cancelling or cleaning it. Use the returned task_id; for a lost editor/preview start response use kind + request_key. MCP waits up to wait_seconds for completion, or a phase/status change after after_revision. Return the latest revision next time and obey poll_after_ms. A preview ready state completes preparation, not session restoration. Original task payloads remain under data.")]
        [ReadOnlyTool]
        public static object GetTask(
            [ToolParam("Namespaced task ID returned in data.task.task_id", Required = false)] string task_id = null,
            [ToolParam("For request-key lookup: editor, ui_preview, verification, input (drag) or execution. Bare IDs also support ui_audit, recording, recording_frames and tests.", Required = false)] string kind = null,
            [ToolParam("Original preparation/preview/verification/drag/execution request key", Required = false)] string request_key = null,
            [ToolParam("Last data.task.revision; waits for a meaningful state change, not each progress counter", Required = false)] string after_revision = null,
            [ToolParam("MCP bounded wait, 0..30 seconds; zero returns immediately", Required = false)] int wait_seconds = 20,
            [ToolParam("UI audit finding offset, execution item offset, or execution text next_offset", Required = false)] int offset = 0,
            [ToolParam("UI audit/execution item page size, 1..500; execution text permits 1..16384 UTF-16 characters within the byte cap", Required = false)] int limit = 100,
            [ToolParam("Execution results: summary (default, bounded inline if small), items (selected JSON array/object), or text (raw snapshot chunks). Never re-executes code.", Required = false)] string result_format = "summary",
            [ToolParam("JSON Pointer into an execution snapshot, e.g. /data/returnValue/rows; items only", Required = false)] string result_path = null,
            [ToolParam("Include retained bounded/sanitized execution or input diagnostics; observation only, no source paths/arguments/raw stacks", Required = false)] bool include_diagnostics = false)
            => TaskStatusService.ValidateWait(wait_seconds) ?? TaskStatusService.Query(task_id, kind, request_key, after_revision, offset, limit, result_format, result_path, include_diagnostics);
    }
}
