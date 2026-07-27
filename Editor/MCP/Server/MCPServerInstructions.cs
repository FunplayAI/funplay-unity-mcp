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
        public const string Text =
@"This server drives the Unity Editor. Core conventions:

- Non-image tool results are JSON envelopes. Success is `{ ""success"": true, ""message"": ""..."", ... }` with an optional payload under `data`; failure is `{ ""success"": false, ""code"": ""..."", ... }`. Branch on `code`, never on human-readable text. Inline screenshots are returned as MCP image content.
- Edit scenes, prefabs, and ScriptableObjects ONLY through these tools / Unity Editor APIs. Never hand-edit .unity/.prefab/.asset files as text while the Editor is open - it overwrites your changes from its in-memory copy.
- To change serialized fields on one prefab component, prefer `set_prefab_property` / `set_prefab_properties`. They avoid Prefab Mode, synchronously reimport the asset, and return persisted readback. Use `open_prefab_stage` only for structural edits; a stage save serializes the full in-memory prefab graph, so review its warning and verify the result afterward.
- Inspect an object before mutating a user-named target; treat user-supplied names as hints, not paths. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- After editing scripts outside Unity, call `request_recompile`, `wait_for_compilation`, then `get_compilation_errors`. `request_recompile` is rejected in Play Mode - call `exit_play_mode` first. After `enter_play_mode`, poll `get_reload_recovery_status` until ready before the next call (the HTTP server briefly drops during domain reload).
- `get_console_logs` accepts `group_duplicates=true` to collapse spammy repeated logs and `filter_text` to narrow them.
- Large screenshot results automatically fall back to a project-local file. Prefer `save_to_file` when the client can read local files, and use explicit width/height when only a small inline preview is needed.
- Save only the assets you intentionally changed, then read them back to confirm the exact values.";
    }
}
