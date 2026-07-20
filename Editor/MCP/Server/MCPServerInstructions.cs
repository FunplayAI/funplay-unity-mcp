// Copyright (C) Funplay. Licensed under MIT.

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Server-level usage guidance returned in the MCP `initialize` result's `instructions`
    /// field. Unlike CLAUDE.md/AGENTS.md (client-specific), this reaches EVERY MCP client and
    /// pins the cross-cutting conventions a model needs to drive this Unity Editor server
    /// correctly. Keep it short, project-agnostic, and about disciplines a fresh client would
    /// otherwise get wrong — not a tool catalogue (that's what tools/list is for).
    /// </summary>
    internal static class MCPServerInstructions
    {
        public const string Text =
@"This server drives the Unity Editor. Core conventions:

- Tool results are JSON envelopes. Success is `{ ""success"": true, ... }` with the payload under `data`; failure is `{ ""success"": false, ""code"": ""..."", ... }`. Branch on `code`, never on human-readable text.
- Edit scenes, prefabs, and ScriptableObjects ONLY through these tools / Unity Editor APIs. Never hand-edit .unity/.prefab/.asset files as text while the Editor is open — it overwrites your changes from its in-memory copy.
- To change a single field on a prefab, prefer `set_prefab_property` (edits the asset in place, no prefab stage). `open_prefab_stage` + `save_prefab_stage` re-serializes the WHOLE prefab and can freeze layout/TMP values or zero Spine references; use the stage only for structural edits and git-diff afterward.
- Inspect an object before mutating a user-named target; treat user-supplied names as hints, not paths. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- After editing scripts outside Unity, call `request_recompile` then check `get_compilation_errors`. `request_recompile` is rejected in Play Mode — call `exit_play_mode` first. After `enter_play_mode`, poll `get_reload_recovery_status` until ready before the next call (the HTTP server briefly drops during domain reload).
- `get_console_logs` accepts `group_duplicates=true` to collapse spammy repeated logs and `filter_text` to narrow them.
- Screenshot tools can return payloads large enough to drop the connection — pass a small width/height, or use `save_to_file` and read the file.
- Save only the assets you intentionally changed, then read them back to confirm the exact values.";
    }
}
