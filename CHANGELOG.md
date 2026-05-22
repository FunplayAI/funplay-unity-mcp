# Changelog

## Unreleased

### Fixed
- Reuse an already-running Funplay HTTP transport for the same Unity project when a duplicate start sees the configured port as in use, while still failing normally for unrelated listeners.

## [0.3.6] - 2026-05-21

### Fixed
- Made MCP server start idempotent across concurrent window, settings, and domain-reload startup paths so repeated Start calls reuse the same in-flight startup instead of creating competing HTTP transports.
- Hardened HTTP transport cleanup during Unity reloads and Stop/Dispose races, including already-disposed `HttpListener` instances.
- Recognize Windows and Mono `HttpListener` address-in-use variants (`10048`, `183`, `Only one usage...`, and `another listener...`) during restart retry detection.
- Clean up partially initialized server transport, request handler, and resource provider state after failed or cancelled starts.

## [0.3.5] - 2026-05-21

### Fixed
- Updated LM Studio one-click configuration to use the official `lmstudio://add_mcp` flow and avoid creating guessed Windows `mcp.json` paths. Existing LM Studio config files are still updated when found.

## [0.3.4] - 2026-05-20

### Added
- Added LM Studio to the MCP Server window's one-click configuration targets. The generated config writes `funplay` to LM Studio's `mcp.json` using Cursor-compatible `mcpServers` JSON.
- Documented manual LM Studio setup paths for macOS/Linux and Windows.

## [0.3.3] - 2026-05-20

### Fixed
- Unitypackage-based updates now filter downloaded release packages before import and only allow paths under the installed `Assets/unity-mcp` root. This prevents accidental release artifacts from overwriting host-project `ProjectSettings`, `Packages`, or `Library` files during one-click updates.

## [0.3.2] - 2026-05-18

### Added
- `Funplay > MCP Server` window now shows the installed package version, polls GitHub for new releases every 6 hours, and surfaces a one-click update prompt when a newer version is available. Auto-check is skipped in Unity batch mode.

### Fixed
- Post-domain-reload server restart is now resilient to (a) the `[InitializeOnLoad]` vs `afterAssemblyReload` ordering race — the handler also kicks off a restart from its own static ctor if reload bookkeeping is pending, (b) `EditorApplication.isCompiling` still being true when the `delayCall` fires, (c) the service provider not yet being available, (d) duplicate scheduling. The restart now retries via `EditorApplication.update` until the editor is settled.
- `HttpMCPTransport.StartAsync` now retries up to 10 seconds (40 × 250 ms) when the port is briefly held by an unwinding listener after an AppDomain transition. Eliminates residual `Address already in use` failures that 0.3.1 did not fully cover for fast-reload scenarios.
- `DomainReloadHandler.CompletePendingFunction` defers the pending-function clear when the editor is mid-compile / mid-update / about to change Play Mode, instead of clearing immediately and racing the reload. 15-second fallback timeout prevents indefinite deferral.
- Root services and MCP server startup are now no-ops in Unity batch mode (`-batchmode`), so running batch jobs in parallel with a foreground Editor that already binds port 8765 no longer conflicts.
- `request_recompile` now returns a clear error when called while Unity is in Play Mode (Unity does not process script compilation or domain reloads while playing). Call `exit_play_mode` first, then retry.

### Changed
- `unity-mcp-workflow` skill (and the generated `AGENTS.md` / `CLAUDE.md` templates) now document two Play Mode lifecycle pitfalls: (1) after `enter_play_mode`, the HTTP server is briefly unreachable while Unity reloads the domain — poll `tools/list` / `get_reload_recovery_status` until it responds before issuing the next call; (2) `request_recompile` is rejected during Play Mode and must be preceded by `exit_play_mode`. Existing installs should regenerate Project Skills via `Funplay > Project Skills` to pick up the new content.

## [0.3.1] - 2026-05-17

### Fixed
- Compile errors on Unity 6000.3+ where `Object.GetInstanceID()` and `EditorUtility.InstanceIDToObject(int)` are obsolete-as-error (CS0619). Object IDs handed to MCP clients now go through a new internal `ObjectIdHelper` that uses `GetEntityId` / `EditorUtility.EntityIdToObject` on Unity 6000.3+ and the legacy `InstanceID` API on older Unity. (#3)
- HTTP transport could fail to restart after a Unity domain reload with `通常每个套接字地址(协议/网络地址/端口)只允许使用一次。` / `Address already in use`. Root cause was a fire-and-forget `StopAsync` in `beforeAssemblyReload` — Unity unloaded the AppDomain before the listener actually released the port. `MCPServerService` now exposes a synchronous `StopSync` used by both `Dispose` and the domain-reload handler, and `RootScopeServices.Initialize` skips its auto-start during a post-reload restart so only one start path runs. (#1)

### Changed (potentially breaking for downstream clients)
- `instanceId`, `componentInstanceId`, and `fileID` fields in tool responses are now always JSON strings instead of numbers. On Unity 6000.3+ they are `EntityId` text; on older Unity they are decimal `InstanceID` strings. Clients that parsed these fields as integers must accept strings.

## [0.3.0] - 2026-05-06

### Added
- New foundation helpers under `Editor/Tools/Helpers/`: `ObjectsHelper` (unified by_id/by_name/by_path/by_tag/by_layer/by_component locator with searchInactive / searchInChildren / findAll, prefab-stage aware), `ComponentSerializer` (SerializedObject-based read/write that picks up `[SerializeField] private`, Object references via `{"fileID": instanceId}`, Vector/Quaternion/Color/Enum/Array), `TypeResolver` (TypeCache-backed O(1) component type lookup), `Response` (structured `{success, message, data}` / `{success, code, error, data}` envelope), `EditorReadyHelper` (refresh + wait for compilation), `GameObjectSerializer` (structured payloads with `instanceId` so agents can chain `by_id` calls).
- New `EditorState` tool provider: `get_editor_state`, `get_selection`, `set_selection`, `get_prefab_stage`, `get_active_tool`, `set_active_tool`, `get_windows`, `get_tags`, `add_tag`, `remove_tag`, `get_layers`, `add_layer`, `get_build_settings`.
- New `MenuItem` tool provider: `execute_menu_item`, `validate_menu_item` — drive any editor menu including third-party packages without writing dedicated wrappers.
- New `IFunplayCommand` + `ExecutionContext` API for `execute_code`. Snippets that implement `IFunplayCommand` get `ctx.RegisterObjectCreation` / `RegisterObjectModification` / `DestroyObject` (auto-Undo + tracked) and `ctx.Log` / `LogWarning` / `LogError` (returned in the response).
- `ComponentPropertyFunctions`: new `component_instance_id` parameter lets tools target a specific component when a GameObject has multiple of the same type.

### Changed
- All `GameObject` tools now resolve targets through `ObjectsHelper` and accept a new `find_method` parameter (defaults to auto-detect: id → path → name).
- `GameObject` and `ComponentProperty` tools now return structured JSON (`Response.Success(...)`) instead of free-form strings, with `instanceId` included so agents can chain `by_id` lookups reliably.
- `ComponentPropertyFunctions.SetComponentProperty(ies)` now writes through `SerializedObject`, so `[SerializeField] private` fields and Object references work; partial writes return per-field success.
- `execute_code` now calls `EditorReadyHelper.RefreshAndWaitForReady` before compiling, so external file edits are picked up automatically — no separate `request_recompile` needed in most flows.
- `FunctionInvokerController` now serializes non-string tool returns to JSON via Newtonsoft, so tools can return `Response.Success(...)` or any object.
- `unity-mcp-workflow` project skill rules updated to cover structured JSON returns, `instanceId` chaining, `find_method`, the new SerializedProperty-backed component setter, the IFunplayCommand template, editor-state tools, and `execute_menu_item` as the preferred fallback before `execute_code`. Generated `AGENTS.md` / `CLAUDE.md` templates updated to match. Existing installed skills must be regenerated via `Funplay > Project Skills` to pick up the new content.
- `core` profile expanded from 19 to 29 tools: added `get_editor_state`, `get_selection`, `set_selection`, `get_prefab_stage`, `find_game_objects`, `list_components`, `get_component_properties`, `set_component_property`, `set_component_properties`, `execute_menu_item`. Lower-frequency editor-state tools (tag/layer mutation, window listing, build settings, active-tool control, `validate_menu_item`) remain `full`-only.

### Breaking
- `GameObjectFunctions` parameter renames for clarity now that resolution is method-driven: `name` → `target` (delete/duplicate/rename/set_transform/set_active/add_component/set_tag_and_layer/get_game_object_info), `parent_name` → `parent`, `child_name` → `child`. The new `find_method` parameter is optional everywhere.

## [0.2.0] - 2026-04-30

### Changed
- Limited Project Skills to the verified default `unity-mcp-workflow` skill and removed unverified optional skills from the catalog.
- Moved Codex project skill installation from `.agents/skills/` to project-root `.codex/skills/`.
- Moved Claude project skill installation from `.claude/commands/` to project-root `.claude/skills/`.
- Renamed Project Skills to use the final feature name across UI and docs.
- Added a one-click `Configure + Skills` action for supported MCP clients.
- Added `Funplay > Tool Exposure` for editing which tools `core` and `full` expose.
- Grouped the Tool Exposure editor by tool category with per-category selection controls.
- Updated the default Unity MCP workflow skill to cover default `core`, default `full`, and customized tool exposure.
- Rendered screenshot tool results as image previews in Recent Activity.
- Added `Funplay > Plugin Settings` with a toggle for verbose plugin debug logging.
- Enabled plugin debug logging by default and expanded the default Unity MCP workflow skill with safer scene, prefab, and readback validation guidance.

## [0.1.10] - 2026-04-17

### Added
- Added `Funplay > Project Skills` as a dedicated window for project-level skills setup
- Added built-in and optional project skills management for supported AI clients, with per-platform generated file visibility
- Added persistence for the currently selected one-click configuration target so related tools stay aligned across sessions

### Changed
- Moved project skills management out of the MCP Server window into its own dedicated menu entry
- Improved the Project Skills window layout with clearer sections and installed-file visibility
- Removed automatic port fallback so the MCP server now starts only on the configured port
- Replaced Unity editor star-prompt emoji with plain text for better font compatibility across Unity versions

## [0.1.9] - 2026-04-16

### Fixed
- Fixed one-click MCP configuration paths on Windows by resolving the real user profile directory
- Fixed VS Code one-click configuration to use the platform-specific user config directory with a macOS fallback
- Ensured one-click MCP configuration writes the currently running server port after automatic port fallback

## [0.1.8] - 2026-04-15

### Changed
- Rebranded the open-source package and documentation from GameBooom to Funplay
- Moved the public Git repository to `FunplayAI/funplay-unity-mcp`
- Updated Unity menu paths to `Funplay/MCP Server` and `Funplay/Check for Updates`
- Reorganized the README quick start and one-click client configuration guidance

## [0.1.7] - 2026-04-10

### Changed
- Repurposed `request_recompile` into the default AI-facing sync flow for external file edits, compilation, and domain reload recovery
- Removed `sync_external_changes` from the exposed MCP tool list to avoid duplicate AI pathways
- Prevented MCP transport restarts from running on a background thread after settings changes
- Avoided redundant settings change notifications and UI initialization callbacks in the MCP Server window

## [0.1.6] - 2026-04-08

### Added
- Updated `request_recompile` to import external file edits and wait through compilation/domain reload recovery

### Changed
- Strengthened `request_recompile` tool guidance so AI clients treat it as the default follow-up after external file edits
- Improved `request_recompile` behavior to return an explicit compilation/reload message instead of failing ambiguously during domain reload
- Persist and report recovery results for external sync operations through `get_reload_recovery_status`

## [0.1.5] - 2026-04-01

### Added
- Performance analysis tools: `get_performance_snapshot` and `analyze_scene_complexity`

### Changed
- Core MCP tool profile now includes lightweight performance inspection by default

## [0.1.4] - 2026-04-01

### Added
- Built-in update checking from `Funplay/Check for Updates` with install-source aware behavior
- Automatic Git package refresh for Git-based installs
- Automatic latest `.unitypackage` download and import for asset-import installs

### Changed
- Game View screenshots now default to the current Game View render size instead of a fixed 512x512 capture
- Mouse click simulation now maps coordinates against the real Game View render size for more reliable UI and physics hits
- Package version resolution now prefers the actual installed package location so Git installs report the correct version
- Package metadata now points to the `FunplayAI/funplay-unity-mcp` repository and `0.1.4`

## [0.1.2] - 2026-03-30

### Added
- MCP prompts support with `prompts/list` and `prompts/get`
- Rich MCP resources with project context, scene/selection/error summaries, interaction history, and resource templates
- `execute_code` as the primary high-flexibility orchestration tool
- Input simulation tools for key press, key combo, mouse click, and mouse drag workflows
- Lightweight editor context builder and package version resolver for richer MCP context output

### Changed
- Default MCP tool exposure now uses a `core` profile to reduce tool-list noise, with optional `full` exposure in the MCP Server window
- Tools exposed by the open-source build now execute directly without an extra approval toggle
- Play Mode MCP requests no longer stall on the editor thread dispatch path
- MCP server info now reports the package version dynamically instead of a hard-coded version

## [0.1.1] - 2026-03-19

### Added
- Minimal MCP resources support with `resources/list`, `resources/read`, and project/scene resource endpoints
- Reload recovery reporting via `get_reload_recovery_status`
- Cached Unity console log access via `get_console_logs`

### Changed
- Bind and document the default local MCP endpoint as `http://127.0.0.1:8765/` for better Codex compatibility
- Auto-start the MCP server on editor load when it is enabled in settings
- Improve compilation tracking and persist interrupted tool execution across domain reloads

## [0.1.0] - 2026-03-12

### Added
- Initial release of Funplay MCP for Unity (Community Edition)
- MCP Server with HTTP JSON-RPC 2.0 transport
- 60+ built-in tool functions across 15 modules (scene, asset, script, UI, camera, animation, etc.)
- Reflection-based tool discovery with attribute annotations
- Custom tool support via `[ToolProvider]` attribute
- MCP Client for connecting to external MCP servers
- One-click MCP config generation for Claude Code, Cursor, VS Code, Trae, Kiro, and Codex
- Domain reload survival across Unity recompilations
- UPM package distribution via Git URL
