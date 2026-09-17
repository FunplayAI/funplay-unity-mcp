// Copyright (C) Funplay. Licensed under MIT.
using System.ComponentModel;
using Funplay.Editor.State;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("EditorOperations")]
    internal static class EditorOperationFunctions
    {
        [Description("Prepare Edit/Play Mode with a durable task: exit Play if needed, import/compile and verify readiness. MCP briefly waits when request_key is supplied; otherwise returns the receipt before possible reload. Continue with get_task, including kind=editor + request_key after a lost response. Does not save scenes or replay arbitrary code.")]
        public static object PrepareEditor(
            [ToolParam("edit or play", Required = false)] string target = "edit",
            [ToolParam("Import external changes and verify scripts before readiness", Required = false)] bool refresh_assets = true,
            [ToolParam("Total deadline, 5..900 seconds", Required = false)] int timeout_seconds = 120,
            [ToolParam("Caller-chosen idempotency key, up to 128 characters; recommended", Required = false)] string request_key = null,
            [ToolParam("MCP completion wait, 0..30 seconds; requires request_key for reload recovery", Required = false)] int wait_seconds = 2)
            => TaskStatusService.ValidateWait(wait_seconds) ?? TaskStatusService.Attach("editor", EditorOperationService.Start(target, refresh_assets, timeout_seconds, request_key));

        [Description("Read an editor preparation by operation_id or request_key. Same ID survives domain reload. A historical ready result is separate from current_editor.ready. During HTTP reconnection retry this read, not the original mutation. Local clients can also inspect the returned journal_path while the Editor is offline.")]
        [ReadOnlyTool]
        public static object GetEditorOperation(
            [ToolParam("Operation ID", Required = false)] string operation_id = null,
            [ToolParam("Original idempotency key", Required = false)] string request_key = null)
            => EditorOperationService.Get(operation_id, request_key);

        [Description("List the most recent 32 durable editor preparation receipts and current editor readiness.")]
        [ReadOnlyTool]
        public static object ListEditorOperations() => EditorOperationService.List();

        [Description("Cancel future preparation steps. Already-started native imports, compilation or Play transitions are not cancelled or rolled back. Repeated cancellation is safe.")]
        public static object CancelEditorOperation([ToolParam("Operation ID")] string operation_id)
            => EditorOperationService.Cancel(operation_id);
    }
}
