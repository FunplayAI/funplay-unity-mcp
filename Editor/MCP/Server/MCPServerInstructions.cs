// Copyright (C) Funplay. Licensed under MIT.

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Server-level usage guidance returned in the MCP `initialize` result's `instructions`
    /// field. Unlike CLAUDE.md/AGENTS.md (client-specific), this reaches EVERY MCP client and
    /// pins the cross-cutting conventions a model needs to drive this Unity Editor server
    /// correctly. Keep it short, project-agnostic, and about disciplines a fresh client would
    /// otherwise get wrong - not a tool catalogue (that's what tools/list is for).
    /// </summary>
    internal static class MCPServerInstructions
    {
        // Shared with both built-in skills so native clients and generated guides agree.
        internal const string UiAutomationGuidance =
@"- Do not use computer use (desktop mouse/keyboard automation) to operate Unity unless necessary. When assembling, modifying, inspecting or validating UI, prefer Unity MCP whenever it can complete the step, including hierarchy/component/prefab reads and edits, compilation/Play state, clicks/scrolling, screenshots and recordings.
- Check the connected project's tools/list and, when available, `get_tool_capabilities`. A tool missing from exposure, compilation/domain reload or a temporary disconnection is not evidence of a missing capability: check exposure/readiness and recover status first. Respect custom allowlists; do not widen exposure or use another interaction method to bypass restrictions.
- Prefer specialized MCP tools; for project-specific gaps they do not cover, use a permitted, guarded `execute_code` call through Unity Editor APIs when it can perform the step reliably. Computer use is a fallback only for a confirmed MCP capability gap, or an explicit user request: explain the uncovered step before using it, limit it to that step, and return to MCP readback/validation when available. If recovery fails, report the connection blocker rather than silently switching methods or repeating uncertain mutations.
- A confirmed blocking native Unity modal that MCP cannot dismiss is a narrow computer-use exception. Explain the modal and handle only that blocker, then return to MCP and recheck project identity, Editor state and the interrupted operation. Do not infer permission to save, discard, overwrite, install, authorize or accept security changes merely to unblock Unity; ask when the consequential choice is not already authorized. Never replay an uncertain mutation.
- This routing applies to operating Unity, not ordinary source-file editing or viewing supplied design references and already-captured images/videos with appropriate file or media tools.";

        public const string Text =
@"This server drives the Unity Editor. Core conventions:

" + UiAutomationGuidance + @"

- Non-image tool results are JSON envelopes. Success is `{ ""success"": true, ""message"": ""..."", ... }` with an optional payload under `data`; failure is `{ ""success"": false, ""code"": ""..."", ... }`. Branch on `code`, never on human-readable text. Inline screenshots are returned as MCP image content.
- Before writes, independently verify initialize.funplay.context.project_path/project_id against the requested project. Prefer the generated project-scoped endpoint. Legacy unscoped endpoints require expected_context from that verified identity for writes; a mismatch performs no tool execution. Context guards prevent misrouting, not authentication. Evidence carries project, process-session and reload identity.
- Edit scenes, prefabs, and ScriptableObjects ONLY through these tools / Unity Editor APIs. Never hand-edit .unity/.prefab/.asset files as text while the Editor is open - it overwrites your changes from its in-memory copy.
- To change serialized fields on one prefab component, prefer `set_prefab_property` / `set_prefab_properties`. They avoid Prefab Mode, synchronously reimport the asset, and return persisted readback. Use `open_prefab_stage` only for structural edits; a stage save serializes the full in-memory prefab graph, so review its warning and verify the result afterward.
- Inspect an object before mutating a user-named target; treat user-supplied names as hints, not paths. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- Prefer structured tools (`audit_ui`, filtered `find_game_objects`, `inspect_ui_sprites`, `find_project_types`) for common UI inspection. Use `get_tool_capabilities` to distinguish implemented from exposed tools. `execute_code` is a fallback for project-specific gaps, not the default query mechanism.
- After external changes use `prepare_editor(target=edit|play, request_key=...)`. Short tasks may finish in that call; otherwise query `get_task` with data.task.task_id, wait_seconds and the last after_revision. On a lost response use kind=editor + request_key. Verify operation.status and current_editor.ready; never replay uncertain mutations. Honor poll_after_ms instead of model-driven one-second polling. Historical reload events are not readiness. Legacy compile/Play readers and low-frequency management remain in Full; custom exposure lists are preserved.
- `get_task` is read-only across editor, ui_audit, ui_preview, recording, recording_frames, tests, input and verification. It cannot cancel, restore or clean anything, and cannot bypass per-kind exposure. wait_complete means waiting has settled, not success or restoration; inspect native errors, complete/ready/restoration/cleanup_pending fields. Recording starts return immediately for interactions. HTTP wait timeout/disconnection does not cancel the underlying job.
- For continuous drag/drop or merge interactions use `simulate_mouse_drag`: real-frame New Input System mouse or single touch, duration plus optional press_hold/end_hold. Short drags briefly wait; poll longer input tasks with get_task and cancel with the original tool. Match pointer_device to actual bindings. Do not infer a successful merge from input completion, inject legacy polling through synthetic new-system devices, call business merge methods as validation, or silently switch to hardware/computer use. Check focus/background-policy errors and verify board state separately.
- Editor readiness is not PlayerLoop progress, input fidelity, rendered correctness or business acceptance. Read actual frame/clock evidence; encoded frames and UI/physics dispatch do not prove legacy Input polling edges. Explicit input routes never silently fall back. Full verification scopes own only enrolled jobs/temporary settings; end them explicitly and inspect cleanup_pending. For unresolved-reference risk use an exposed selective transaction with exact preconditions and untouched PPtr comparison, not ordinary setter success alone.
- `get_console_logs` accepts `group_duplicates=true` to collapse spammy repeated logs and `filter_text` to narrow them.
- Large screenshot results automatically fall back to a project-local file. Prefer `save_to_file` when the client can read local files, and use explicit width/height when only a small inline preview is needed.
- Save only the assets you intentionally changed, then read them back to confirm the exact values.";
    }
}
