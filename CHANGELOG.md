# Changelog

## Unreleased

### Added
- Added 13 Profiler tools (category `Profiler`): `profiler_start`/`profiler_stop`/`profiler_status` for session control; `get_frame_timing`/`get_counters` for CPU/GPU frame timing and persistent `ProfilerRecorder` counters; `get_object_memory` for per-asset/GameObject runtime memory footprint; `get_top_memory_objects` for ranking ALL loaded objects of a type by memory (the "which objects are consuming it" follow-up to a snapshot diff); `memory_take_snapshot`/`memory_list_snapshots`/`memory_compare_snapshots` for lightweight aggregate memory snapshots (not real `.snap` files); `frame_debugger_enable`/`frame_debugger_disable`/`frame_debugger_get_events` for driving the Frame Debugger via reflection. See [PROFILER_TOOLS.md](PROFILER_TOOLS.md) for the full reference, implementation notes, known limitations, and test report.
- Added prefab stage editing tools: `open_prefab_stage` opens a prefab asset in Prefab Mode for isolated editing (hierarchy/component tools and `execute_code` then operate on the prefab contents), `save_prefab_stage` persists edits back to the `.prefab` asset without closing, and `close_prefab_stage` returns to the main stage with an explicit save/discard choice. Closing clears the stage's dirty flag first so a blocking "save changes?" modal can never stall the MCP request.
- `get_console_logs` gained two optional parameters: `group_duplicates` collapses repeated identical messages into one "message (xN)" line (in a real project this compacted 100 cached entries down to 20 unique lines, keeping spammy Animator warnings from drowning out unique entries), and `filter_text` filters entries by a case-insensitive substring. Both apply to the cache and console read paths; default behavior is unchanged.
- Added ScriptableObject asset tools: `create_scriptable_object` creates a new asset of any ScriptableObject-derived type, `get_scriptable_object` reads all serialized properties (including `[SerializeField]` private fields), and `set_scriptable_object_properties` writes fields with a per-field success report and persists via `SaveAssetIfDirty`. Reuses the component property machinery (`ComponentSerializer` signatures widened from `Component` to `UnityEngine.Object`, source-compatible for existing callers).
- Added Animator runtime control tools: `get_animator_state` reads the current state (state name resolved from the controller when possible, including through AnimatorOverrideController) plus all parameters with current values; `set_animator_parameter` sets a parameter by name with automatic Float/Int/Bool/Trigger type detection; `play_animator_state` plays a named state and force-evaluates the animator once in Edit Mode so poses apply without entering Play Mode (useful for driving UI to a known state before a screenshot).
- Added Unity Test Runner integration with an async job pattern: `run_tests` starts an EditMode/PlayMode run (with optional test/category/assembly filters) and returns a job id immediately; `get_test_job` polls progress (completed/total) and final results (pass/fail/skip counts plus failure messages and truncated stack traces); `cancel_test_run` cancels a stuck run (requires com.unity.test-framework 1.3+, resolved by reflection and reported as unsupported on the 1.1.x that Unity 2022.3 bundles). Job state lives in SessionState and the results callback re-registers on every domain load, so PlayMode runs that reload the domain mid-run still report completion.
- Declared `capabilities.tools.listChanged` in the `initialize` response and implemented lazy `notifications/tools/list_changed` delivery: when the exposed tool set changes (Tool Exposure save, newly registered tools after a recompile), the next client request that accepts `text/event-stream` receives an SSE response carrying the notification before the JSON-RPC result, so MCP clients such as Claude Code refresh their tool list without a session restart. Supported on both the direct HTTP transport and broker mode (broker protocol v2 with Accept/Content-Type passthrough).

### Changed
- MCP Server panel UX improvements to the transport/broker controls:
  - The transport selector is now a "Transport Mode" dropdown (`Direct HTTP (default)` / `Broker Mode (Experimental)`) instead of a checkbox, so the two transports read as an explicit mutually-exclusive choice instead of an on/off flag.
  - The "Broker Mono Path" field now shows the real effect of the "leave empty to auto-detect" default instead of always rendering blank: when no override is set, the field displays the actually auto-detected Mono executable path (display-only — it does not persist as an override), and if auto-detection fails, the field stays empty and a red inline hint explains that broker mode needs the path set manually.

  Behavior, defaults, and the underlying settings are unchanged — both are presentation-only improvements.

### Fixed
- Broker manager now gracefully shuts down a stale broker process that no longer passes the health probe (typically a protocol-version mismatch after a package upgrade) instead of leaving it holding the port and failing the server with "Address already in use".
- A failure while handling a single broker-delivered request no longer terminates the broker poll loop (which previously left all subsequent requests queued forever).
- Fixed a broker process leak when the Server Port setting changes while broker mode is active: `MCPBrokerProcessManager.EnsureRunning` only shut down the previously recorded broker process when its recorded port matched the newly requested port, so changing the port left the old broker orphaned (and deleted its pid file, making it unrecoverable by any later cleanup) while a fresh broker started on the new port. The stale-broker shutdown now runs regardless of whether the port changed.
- The "Server Port" field now commits on Enter/blur rather than per keystroke, so the settings-change restart path runs once per committed value instead of once per typed digit. The restart scheduler also uses editor-update fallbacks alongside `delayCall` for both stop and start phases, so port changes made from tools or non-IMGUI callbacks cannot get stuck with a scheduled-but-never-run restart.
- Fixed `get_performance_snapshot` and `analyze_scene_complexity` under-reporting scene stats in multi-scene projects. Both tools sourced root GameObjects from `SceneManager.GetActiveScene()` only, silently excluding any additively loaded scenes (e.g. a bootstrap scene loading a content scene on top); they now walk every loaded scene via `SceneManager.sceneCount`/`GetSceneAt`, and the "Scene:" summary line is renamed "Scene(s):" to list every scene that was counted.
- Fixed `get_hierarchy` and `get_scene_info` silently omitting additively loaded scenes: both sourced content from `SceneManager.GetActiveScene()` only, so in multi-scene projects (e.g. a bootstrap scene additively loading a content scene) everything outside the active scene was invisible. Both tools now walk every loaded scene, label each as `(active)`/`(additive)`, and `get_hierarchy`'s `root_name` inactive-object search fallback also spans all loaded scenes.
- `get_console_logs` now truncates each emitted line to 300 characters (annotated with the remaining length). A single log entry containing a huge one-line payload (observed in the wild: an entire 280KB save-file JSON logged to the console) previously blew up the whole tool response.

## [0.4.8] - 2026-06-24

### Fixed
- Fixed vertically flipped Game View screenshots when reading Unity's already-rendered PlayModeView frame.
- Kept camera-rendered screenshots such as Scene View and fallback Game View captures in their native orientation.

## [0.4.7] - 2026-06-23

### Added
- Added a recommended `IFunplayCommand` template to generated project skills, including traceable `ctx.Log` usage and Undo-aware object modification helpers.
- Added generated skill guidance for Unity fake-null references: avoid `??=` when lazily resolving `UnityEngine.Object` references and use explicit `if (field == null)` checks instead.
- Added a GitHub Actions workflow for publishing the MCP Registry entry with GitHub OIDC after the NuGet package is indexed.

### Changed
- `execute_code` now automatically adds `using Funplay.Editor.Tools.Scripting;` when a full-class snippet implements an unqualified `IFunplayCommand`, while avoiding duplicate usings when the namespace is already present.

### Fixed
- Fixed the release helper's Unity EditMode test invocation so batchmode waits for Test Runner completion and writes the XML result instead of exiting immediately after import.
- Release unitypackage validation now also rejects `.github` paths so publishing automation files cannot leak into package exports.

## [0.4.6] - 2026-06-17

### Fixed
- Made external script refresh and compilation requests resilient when Unity Auto Refresh is disabled or a hot-reload plugin intercepts the normal refresh path. `request_recompile`, `wait_for_compilation(force_refresh)`, and `execute_code` now share a fallback refresh flow and return `REFRESH_DID_NOT_START_COMPILATION` instead of reporting stale compilation results as success when scripts still look uncompiled. (#15)

## [0.4.5] - 2026-06-17

### Added
- Added `capture_simulator_view` to capture Unity's Device Simulator screen, optionally select the Simulator device by name, and draw a Safe Area outline overlay while preserving the source aspect ratio when only one output dimension is provided.

### Fixed
- Fixed Device Simulator captures being vertically flipped.
- Removed the Game View fallback from Device Simulator captures so device switches no longer return a stale 16:9 Game View image when the Simulator preview texture is not ready.

## [0.4.4] - 2026-06-15

### Added
- Added an optional experimental Broker Mode for the MCP Server. When enabled, a tiny local broker process owns the HTTP port and keeps client requests alive while Unity reloads the scripting domain; direct in-process HTTP remains the default.
- Broker Mode now returns a retryable JSON-RPC error for new requests while the Unity backend is reloading or reconnecting, instead of letting short client timeouts expire silently.

### Fixed
- Improved `execute_code` unexpected failure diagnostics by unwrapping `TargetInvocationException` and returning the underlying exception type, message, and stack trace. (#14)

## [0.4.3] - 2026-06-06

### Changed
- Documented OpenUPM as an optional UPM registry install source for users who want Unity Package Manager to show registry-backed version history.
- Added optional release-script verification for OpenUPM indexing after new tags are published.

### Fixed
- Fixed `capture_game_view` returning black frames in URP/HDRP projects by reading the rendered Game View frame before falling back to `camera.Render()`. (#11, #12)

### Contributors
- Thanks @dehuaichendragonplus for the detailed URP/HDRP Game View capture report and patch.

## [0.4.2] - 2026-06-06

### Changed
- `execute_code` now compiles snippets through Unity's bundled Roslyn csc first while preserving the in-memory compilation/execution flow. This improves support for modern C# syntax such as target-typed `new()` and switch expressions without writing snippet files into the Unity project.
- Release packaging now explicitly rejects local IDE metadata and macOS `.DS_Store` files in addition to tests, local notes, token files, and host-project folders.

## [0.4.1] - 2026-06-03

### Changed
- Narrowed optional `execute_code` project namespace auto-injection to loaded assemblies under `Library/ScriptAssemblies`, reducing wrapper size and type-name ambiguity when the opt-in setting is enabled.

### Fixed
- Downgraded expected response-write failures after client disconnects or domain reloads so `socket has been shut down` no longer appears as a Unity Console error.
- Marked non-resumed tools interrupted by script recompilation as `Interrupted` in Recent Activity instead of showing a misleading green `OK`.

## [0.4.0] - 2026-06-02

### Changed
- `execute_code` no longer auto-injects project namespaces by default. The optional MCP Settings toggle now derives namespaces from loaded project assemblies instead of regex-scanning source files, avoiding source-only, conditional, or asmdef-isolated namespaces that can make every snippet fail with `COMPILATION_FAILED`. (#9)
- Moved `execute_code` safety controls out of the MCP Server window and into **Funplay > MCP Settings** alongside debug logging.

## [0.3.9] - 2026-06-01

### Added
- Added stricter default-on filesystem safety checks for `execute_code`, covering broad `System.IO` writes, raw file streams, absolute/user/system paths, and path traversal patterns while clearly documenting that this is not a full sandbox.
- Added a local release helper script for version bumping, Unity test/export flows, unitypackage pathname validation, release notes, checksums, and optional publishing.

### Changed
- Split the MCP Server window into smaller focused panels and moved related settings, tool exposure, project skills, and skills management classes out of the monolithic window file.
- Standardized tool error results on structured JSON envelopes with `success:false`, `code`, `error`, and optional `data`; legacy `Error:` text is no longer treated as an error signal.
- Disabled verbose plugin debug logging by default and kept high-volume request logs in the Recent Activity UI instead of the Unity Console.

### Fixed
- Filtered release unitypackages through an explicit asset list so local-only files, tests, ProjectSettings, Packages, Library, and token files cannot be included accidentally.
- Hardened release-script cleanup, non-publishing flows, and Unity export handling so a lingering batchmode process does not block package validation after a package has already been written.

## [0.3.8] - 2026-05-23

### Added
- Added a default-on `execute_code` safety checks toggle to the MCP Server window. Clients that omit the `safety_checks` argument now use this project-level default, while explicit tool arguments still override it.

### Fixed
- Reworked the MCP HTTP transport to use a directly owned loopback TCP listener and retry post-domain-reload binds, avoiding Windows/Unity 6 `Address already in use` recovery failures caused by stale listener state.
- Avoided Unity synchronization-context capture during transport bind retries so occupied-port recovery cannot stall the editor when callers synchronously wait on startup.
- Hardened editor-thread queued task cleanup during server disposal so pending work is cancelled cleanly across domain reloads.

## [0.3.7] - 2026-05-22

### Fixed
- Added a project-path-hash identity to MCP `initialize` responses so an existing Funplay listener can be verified as belonging to the same Unity project without exposing the raw local path.
- When HTTP binding finds the configured port already occupied, the transport now probes `initialize` and attaches only if both the Funplay server name and project identity match.
- Attached transports detach without closing the owning listener, while owned transports still stop and close their `HttpListener` normally.
- Probe timeouts and unrelated listeners are treated as probe failures, not as external cancellation.
- The MCP Server window now distinguishes an attached existing server from a listener owned by the current service.

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
