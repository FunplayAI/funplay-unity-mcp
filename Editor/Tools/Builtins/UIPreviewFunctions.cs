// Copyright (C) Funplay. Licensed under MIT.
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.State;
namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("UIPreview")]
    internal static class UIPreviewFunctions
    {
        [Description("Start a temporary prefab/scene-template preview. Requires idle Edit Mode, saved clean scenes and no Prefab Stage. Snapshots original scenes, selection and supported Game View settings. MCP briefly waits with request_key; continue through get_task. Business data is project-owned; Play preparation survives reload. Full-profile workflow.")]
        public static object StartUiPreviewSession(
            [ToolParam("JSON array of 0..20 prefab asset paths", Required = false)] string prefab_paths = null,
            [ToolParam("Saved scene copied into the temporary environment; original is not modified", Required = false)] string scene_template = null,
            [ToolParam("Enter Play Mode after preparing environment", Required = false)] bool enter_play_mode = false,
            [ToolParam("Temporary Game View width 128..4096; both dimensions zero preserves current size", Required = false)] int width = 0,
            [ToolParam("Temporary Game View height 128..4096", Required = false)] int height = 0,
            [ToolParam("Idempotency key to recover an ambiguous start response", Required = false)] string request_key = null,
            [ToolParam("MCP completion wait, 0..30 seconds; requires request_key for recovery", Required = false)] int wait_seconds = 2)
            => TaskStatusService.ValidateWait(wait_seconds) ?? TaskStatusService.Attach("ui_preview", UIPreviewSession.Start(prefab_paths, scene_template, enter_play_mode, width, height, request_key));
        [Description("Read current/most recent preview session, editor state and restoration flags. Domain reload does not lose its ID. A restarted Editor requires explicit recovery, never automatic scene closure.")]
        [ReadOnlyTool]
        public static object GetUiPreviewSession([ToolParam("Preview ID; omitted reads latest", Required = false)] string session_id = null)
            => UIPreviewSession.Get(session_id);
        [Description("End the identified preview: asynchronously exit Play, restore original scenes/selection/supported Game View settings, remove only owned temporary files. Refuses unrelated open scenes and unsaved preview edits. Repeated end is safe. Inspect restoration flags/warnings; never assume network/save-game effects were rolled back.")]
        public static object EndUiPreviewSession(
            [ToolParam("Preview session ID")] string session_id,
            [ToolParam("Explicitly discard changes to this session's temporary scene only, never unrelated scenes/assets", Required = false)] bool discard_preview_changes = false,
            [ToolParam("MCP completion wait, 0..30 seconds", Required = false)] int wait_seconds = 2)
            => TaskStatusService.ValidateWait(wait_seconds) ?? TaskStatusService.Attach("ui_preview", UIPreviewSession.End(session_id, discard_preview_changes));
    }
}
