# Reliable UI workflows

## MCP-first Unity operation

Do not use computer use to operate Unity unless necessary. For UI authoring and review,
prefer MCP for supported Unity operations, including inspection,
editing, interaction, compilation/Play control and visual capture. Check tool capabilities,
exposure and Editor readiness before treating a step as unsupported; an unexposed tool or
temporary reload/disconnection does not justify switching to desktop automation.

Prefer specialized tools, then permitted guarded `execute_code` for project-specific Unity API
work. Consider computer use only for a confirmed capability gap or an explicit user request;
explain the gap, limit the fallback to that step and resume MCP readback when possible. Do not
bypass custom allowlists. Ordinary source edits and viewing reference/captured media still use
appropriate file and media tools. The server initialization instructions and both built-in
skills share this routing policy across all six supported client targets.

## Preparing the Editor

### Shared task status and bounded waiting

MCP start calls for preparation, audits, preview setup/restoration and frame extraction default
to a short `wait_seconds=2` completion window (0–30; zero returns immediately). Small jobs can
finish in that one response. This is transport-level waiting: direct C# calls still return
receipts immediately. Unity updates continue while the MCP response is pending.

Every start preserves its original ID/payload and adds `data.task`, including the namespaced
`task_id`, `status`, `phase`, `revision`, `wait_complete`, `snapshot_at` and `poll_after_ms`.
Use `get_task` for `editor`, `ui_audit`, `ui_preview`, `recording`, `recording_frames` or `tests`:

```json
{"tool":"get_task","arguments":{"task_id":"ui_audit:<returned-id>","after_revision":"<last revision>","wait_seconds":20,"offset":0,"limit":100}}
```

With no revision, wait for the current work to settle. With `after_revision`, return when a
meaningful status/phase/error/readiness change occurs, the task settles, or the wait expires.
Frame counts, scanned-object counts and elapsed time do not wake the model on every update.
Unchanged phases back off from 1 to 2, 5 and at most 10 seconds via `data.task.poll_after_ms`; after an
unchanged response, honor that delay rather than having the model poll once per second.
`wait_reason=read_timeout` returns the last observed snapshot if the Editor queue cannot be read
within the budget; `snapshot_at` is not a claim of current readiness. Disconnecting/cancelling
the HTTP wait does not cancel, restart or clean the underlying task.
If even the initial Editor read cannot run, `EDITOR_STATUS_UNAVAILABLE` explicitly reports
an unknown outcome and a retry delay; query the same ID/key, do not repeat the mutation.
`get_task` includes initial queue time in its wait budget. `wait_seconds=0` skips task waiting
but permits up to one second to obtain the initial Editor snapshot.

`wait_complete` only means this waiting phase has settled, not that it succeeded: inspect the
native status, errors, audit `complete`, recording `ready`, and restoration flags. A preview
with status `ready` is still an open session, not a restored/closed one. Task journals and
retention remain owned by their original services; expired or unknown IDs fail explicitly.

Recording starts always return immediately so callers can perform interactions while capturing.
Test starts retain immediate delivery across Test Runner reloads. Preparation/preview starts
without `request_key` also return immediately before a possible reload. Supply a key for short
waiting and recover a lost response using `get_task(kind=editor|ui_preview, request_key=...)`;
never replay an uncertain mutation. A domain reload can still interrupt an in-flight HTTP wait.

`get_task` is read-only and has no cancel/cleanup action. It requires an enabled, exposed task
tool or its compatible legacy status tool for that kind; exposing only `get_task` cannot bypass
custom allowlists. Cancellation and restoration retain their explicit original tools.

After external script or asset changes, call:

```json
{"tool":"prepare_editor","arguments":{"target":"play","refresh_assets":true,"timeout_seconds":120,"request_key":"profile-page-validation-1"}}
```

The receipt retains `data.operation.operation_id` and adds `data.task.task_id`. Use `get_task`
with that handle (or `kind=editor` and the original `request_key`). The JSON envelope acknowledges the
query; **only `operation.status == "ready"` and `current_editor.ready == true` mean preparation
is complete and the Editor is currently idle**. Then inspect `current_editor.is_playing`.
Paused Play Mode counts as Play Mode; preparation does not unpause the user's Editor.

The operation exits Play when necessary, imports assets, waits for compilation, requests the
target mode, and verifies stable readback. It does not save dirty scenes, run business setup,
replay arbitrary `execute_code`, or promise that runtime game logic is ready. Runtime initialization
and console exceptions still need project-specific verification.

The ID, compiler errors (up to 100), phase history and deadline survive domain reload in
`Library/FunplayMcp/Operations/editor-operations.json`. The HTTP endpoint can briefly disappear
while Unity reloads: retry **the status read** after reconnecting, using the same ID/key. Local
clients can read the journal while disconnected; that is a last-known snapshot, not proof of
current readiness. A restarted Editor marks unfinished operations `interrupted` rather than
replaying effects. `list_editor_operations` returns the latest 32 receipts. Older keys can expire;
use a unique key per intended preparation, not a permanent project-wide key.

Repeating the same retained key and parameters returns the original receipt. Reusing a key with
different parameters fails `IDEMPOTENCY_CONFLICT`; an overlapping preparation fails
`EDITOR_OPERATION_IN_PROGRESS`. If the initial response is lost, query by key first.

Terminal states are `ready`, `failed`, `cancelled`, and `interrupted`. Errors include
`COMPILATION_FAILED`, `REFRESH_FAILED`, `OPERATION_TIMEOUT`, and `EDITOR_RESTARTED`. Compiler
errors include source locations where supplied by Unity. A timeout/cancellation stops later
steps, **not native work already started** (nor an in-flight refresh's fallback sequence).
Check current state before starting another operation. A journal write failure stops dispatch
and reports `OPERATION_JOURNAL_UNAVAILABLE`; don't discard a journal while work may still run.

Legacy compile/Play tools remain supported. `get_reload_recovery_status` remains a historical
event API and now includes current readiness; its event `status` must not be used as a ready flag.

Implementation uses Unity's [assembly reload events](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssemblyReloadEvents.html)
and [compilation status](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/EditorUtility-scriptCompilationFailed.html).

## Read-only UI audit

`audit_ui` starts a job and briefly waits for completion. `get_task` reads status and pages its
findings; `cancel_ui_audit` explicitly stops it. `get_ui_audit` remains a compatible full-profile reader.
Scopes are `selection`, `scene` (all loaded roots or an explicit JSON `roots` array),
`prefabs`, `scenes`, and `assets` (JSON `paths` arrays of Assets files/folders).
For example, audit `paths=["Assets/UI","Assets/Scenes/Menu.unity"]` with `scope=assets`.
Prefab assets are read directly; saved scenes are loaded in owned preview scenes, never over
an existing dirty live scene. Saved-scene audits require Edit Mode and a supported preview-loading
API; unsupported versions produce an explicit skipped asset and incomplete result, not a clean bill.

Findings include rule, severity, deterministic/contextual confidence, asset/object identity,
component/property, measured evidence, suggestion and runtime-verification requirements.
Rules cover zero-border Sliced Images (using the effective override Sprite), missing scripts and
broken serialized references, required input text bindings, transparent active raycast targets,
live legacy/TMP text overflow, RectMask2D clipping and competing layout ownership. A null
optional reference is not automatically an error. A valid one-axis border is not an error.
Scroll clipping and layout-owned coordinates are often intentional and are reported accordingly.
Prefab/saved text geometry is explicitly deferred to live layout verification. No arbitrary
project property getters, Canvas rebuild, prefab save or automatic fixes are performed.

Transparency includes Graphic color, parent CanvasGroups and the
[CanvasRenderer alpha](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/CanvasRenderer.GetAlpha.html)
used by fades. An invisible hit area can be intentional, so this remains a contextual warning.

Defaults: 10,000 objects, 1,000 findings, 60 seconds, up to 500 candidate assets. Scanning yields
between objects (8 ms per editor update); a native Unity asset load or serialization call cannot
be preempted. Inspect `complete`, scanned/skipped counts and `stop_reason`. `complete=false`
never means the rest of the project is clean. Only the latest job is retained; completed findings
survive domain reload, unfinished work is marked `interrupted`. Live data can change during a scan.

For project-specific required fields and intentional exceptions, supply `configuration` JSON or
check in `ProjectSettings/FunplayMcp.UIAudit.json` (an inline configuration replaces that file):

```json
{
  "required_references": [
    {"component_type":"MyGame.ProfilePanel","property":"avatar","asset_prefix":"Assets/UI/"}
  ],
  "suppressions": [
    {"rule":"transparent_raycast","object_path":"Canvas/ModalBlocker","reason":"Intentional modal hit shield"}
  ]
}
```

Use exact full component names and serialized property paths. Suppressions match an exact rule,
optional asset-path prefix and optional exact hierarchy path. Suppressed findings remain visible
with the reason; missing reasons, unknown rules or malformed configuration fail explicitly.

The audit is diagnostic, not a proof of visual fidelity. Compare actual target-resolution captures
with each design/state; runtime business data, arbitrary stencil/sprite-mask shapes, interactions,
localization and animation still require dedicated verification. Unity's
[TMP overflow observation](https://docs.unity3d.com/Packages/com.unity.textmeshpro@3.0/api/TMPro.TMP_Text.isTextOverflowing.html)
reports current layout, not all possible strings or screen sizes.

## Structured inspection instead of snippets

`find_game_objects` preserves its legacy simple-query result when no structured options are
provided. With `filter`, `scope`, or `snapshot_id` it returns a bounded, paged observation:

```json
{
  "scope":"scene",
  "include_inactive":"true",
  "filter":"{\"component\":\"UnityEngine.UI.Image\",\"where\":[{\"property\":\"m_Type\",\"op\":\"eq\",\"value\":\"Sliced\"}],\"select\":[\"m_Sprite\",\"m_Color.a\"]}",
  "max":"50"
}
```

Scopes: loaded `scene` roots (optionally `in_parent`), `selection`, and `prefabs` with a JSON
`asset_paths` array of prefab files/folders. Up to 200 prefabs are loaded; larger scopes require
narrowing. Predicates are ANDed on the **same component instance**, and every matching component
is identified. Use exact serialized paths (including array element/nested paths), not arbitrary
public getters. Supported operators: `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `contains`, `starts_with`,
`is_null`, `not_null`, `missing`. Enum values use display names; reference equality accepts
`{"asset_path":"Assets/..."}` or `{"fileID":"<editor object ID>"}`. Missing/invalid properties
are reported explicitly rather than silently producing a clean empty result.

The response includes `snapshot_id`, `total_matches`, `scanned_objects`, `complete`, errors,
`next_offset` and stop reason. Page the same snapshot using its ID, offset and max. Snapshots are
immutable observations (last 8, 120 seconds, invalidated by domain reload), not permission to reuse
stale object IDs for writes. Scan limits are 10,000 objects by default, at most 2,000 matches and
roughly 2 seconds between Unity calls; an asset load itself cannot be preempted. A limited or
partially failed scan is incomplete. Loaded assets may be dirty; inspection never claims disk
persistence or silently saves/reimports them.

`find_project_types` finds full/assembly-qualified names for components **and ordinary helper
classes**. Use `exact=true` to detect short-name ambiguity; filter by exact assembly or pass an
assembly-qualified name instead of choosing the first match.

`inspect_ui_sprites` accepts a JSON selector array (1–100 selectors, at most 500 Image/Sprite
observations per batch):

```json
[
  {"image_id":"12345"},
  {"asset_path":"Assets/UI/buttons.png"},
  {"asset_path":"Assets/UI/buttons.png","local_file_id":21300002},
  {"prefab_path":"Assets/UI/Panel.prefab"}
]
```

It reports source/effective override Sprite, border, rect, GUID/local ID, packed texture state,
Single/Multiple importer settings and dirty status. Duplicate sprite names are ambiguous: use
local IDs. `include_dependents=true` additionally performs a bounded recursive-reference scan of
Assets prefabs, scenes and SpriteAtlases; inspect its separate completeness flag. A reference
does not prove runtime usage. Generated/runtime Sprites may have no importer. Never edit an atlas
texture to fix a source Sprite's border.

`packed` reports Unity's current Sprite binding, not whether a source belongs to an atlas:
Editor clones can remain unbound even after an atlas has been packed. Use source identity and
dependency results for atlas membership; do not treat `packed=false` as proof of no atlas.

Component setters now include readback provenance and affected asset paths. Scene/component
setters report **in-memory** values; prefab setters report **saved and synchronously reimported**
values. Nested serialized paths are read back directly. Existing per-field failures remain visible.

`get_tool_capabilities` distinguishes implemented, enabled and exposed tools under the active
Core/Full configuration. It changes no settings. Newly added inspection tools are Core defaults,
but explicit customized allowlists remain respected. `execute_code` is now labelled/sorted as a
fallback for project-specific gaps; it remains available and backward-compatible.

## One coordinate contract for visual evidence and input

Screenshot receipts now contain `geometry`; inline MCP captures still return a native image,
followed by structured text metadata (never a base64 string rendered as text). File receipts retain
their original path/byte fields. Metadata identifies the capture and timestamp, surface, render
and output dimensions, output viewport, pixel units, image top-left/render bottom-left origins,
scale factors and whether the capture is suitable for live input mapping. Camera-fallback, Scene
View and editor-window captures are **not** interchangeable with the live Game View.

For a click measured on a resized screenshot, pass `coordinate_space=image_pixels`,
`origin=top_left` and the returned `capture_id` to `simulate_mouse_click`. Use the same fields on
`simulate_mouse_drag` and `raycast_at_point`. Pixel/render bottom-left remains the default, and
raycast's legacy `normalized` flag is still supported. Inputs now reject out-of-bounds/non-finite
coordinates rather than silently clamping onto another target. `normalized` uses 0–1 across the
full render framebuffer; camera letterbox bars remain part of that framebuffer.

Capture-based input requires a retained Game View capture no older than 30 seconds, the same
editor domain, Play state, view, scene setup, camera viewport layout and render size. Otherwise it
fails with a specific stale/changed-geometry error: take a new capture. This check protects
coordinate mapping; it cannot prove that animated/dynamic UI contents have not changed. The
last 32 captures are retained until domain reload. `get_visual_coordinates` can inspect current
geometry or check a capture's validity without taking another screenshot.

`get_object_screen_bounds` returns current RectTransform or Renderer corners/bounds in both
render and image coordinates. Pass a fresh capture ID to project into its resized output. Overlay
Canvas coordinates and camera viewport offsets are handled separately. Camera depth/behind-plane
flags are reported; these bounds do not prove visibility, masking, occlusion or click reachability.
Use the raycast diagnostic and a visual capture to verify the intended target.

Recording receipts carry the same source/output geometry. Legacy mouse-drag dispatch remains
synchronous: `duration` controls interpolation steps, not elapsed playback time. Its receipt
states this explicitly; use recording evidence to validate resulting scrolling/animation.

## Project UI defaults and prefab templates

`get_ui_defaults` reports the project configuration and bounded counts of legacy/TMP labels,
fonts and input modules. It does not infer the project's preferred font from package presence.
An empty project defaults to TMP; ties and incomplete scans require an explicit decision.
`configure_ui_defaults` atomically validates and replaces
`ProjectSettings/FunplayMcp.UI.json`, suitable for version control:

```json
{
  "schema_version": 1,
  "text_component": "tmp",
  "font_asset": "Assets/UI/Fonts/MainFont.asset",
  "font_material": "Assets/UI/Fonts/MainFont Outline.mat",
  "input_module": "input_system",
  "button": {"asset_path":"Assets/UI/PrimaryButton.prefab","text_path":"Visual/Label"},
  "text": {"asset_path":"Assets/UI/BodyLabel.prefab"}
}
```

Paths in this example must be replaced with actual project assets. Text policies are
`auto/tmp/legacy`; input policies are `auto/input_system/legacy`. Templates can be
`canvas/button/text`, with a RectTransform root and the corresponding root Canvas/Button.
Specify `text_path` when a template has multiple labels; other labels are not rewritten.
An omitted root-label path is resolved only when exactly one label exists.

`create_project_ui(kind,name,...)` and the existing `create_canvas/button/text` use the
shared authoring policy. Explicit arguments override configuration, then verified conventions.
Templates preserve their own text component type, prefab connection, size/position and typography
unless explicitly overridden. A requested incompatible text type fails instead of replacing
components and breaking references. No automatic Outline/Shadow or shared-material mutation occurs.
Only explicitly supplied text changes a template's label; omitted text preserves it.

Creation requires Edit Mode, a uniquely identified live RectTransform parent for text/buttons,
valid fonts and TMP Essential Resources when TMP is chosen. Missing resources fail before
creating objects: import them deliberately through Unity, rather than silently using legacy Text.
Canvas creation preserves existing EventSystems and their input modules. A new module follows
Active Input Handling and verified/configured project policy; dependencies are not installed
automatically. Prefab Stage authoring does not add a global EventSystem.
Input System setup uses Unity's
[default action assignment](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.7/api/UnityEngine.InputSystem.UI.InputSystemUIInputModule.html).

Returned IDs, chosen policy and actual geometry describe in-memory authoring with Undo:
`persisted=false`. Save the intended scene/prefab explicitly. These creation APIs do not
rebuild an existing screen, migrate its text system, or replace existing controls.

## Recording markers and extracted frames

When exposed (`full` by default), `mark_recording(recording_id,label)` records explicit markers using the same elapsed clock as
the encoder. Click, drag and `simulate_ui_scroll` automatically mark dispatch and include
render-coordinate metadata. These are dispatch events, not assertions of successful business
behavior. Up to 256 markers are kept; `dropped_markers` makes overflow explicit.
`simulate_ui_scroll` dispatches to the top raycast hit's scroll handler, with no click-through;
deltas are uGUI wheel units, not pixels (the project's ScrollRect sensitivity applies).

After `record_game_view` reports `ready=true`:

```json
{"tool":"extract_recording_frames","arguments":{"recording_id":"<id>","timestamps":"[0,1.5,3]"}}
```

Start briefly waits for completion; if still pending, use its task handle with `get_task` and require
`complete=true`. Requests contain 1–16 finite timestamps within `0..last_frame_seconds`;
they are sorted/deduplicated. Each PNG receipt includes requested/actual presentation time,
delta, dimensions and historical geometry. The selection is **first decoded frame at or after
the requested time**, not the nearest or an exact-time guarantee. Slow captures leave real gaps;
a marker after the last captured frame cannot provide visual evidence of that action.
`get_recording_frame(job_id,index)` returns a native MCP image and metadata (or path-only with
`inline=false`). Historical frame IDs are never valid for live input.

Decoding yields between frames, has a 120-second deadline and 7,500-frame cap, and accepts only
owned finalized recording receipts, not arbitrary file paths. It works in Edit or Play Mode
without changing scenes or requiring external executables. Unity's native decoder is an internal
Editor interface and is feature-detected; unsupported versions report `VIDEO_DECODER_UNAVAILABLE`.
The narrow interface is documented in the
[Unity reference source](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/Media/Bindings/MediaDecoder.bindings.cs).
Native codec calls themselves cannot be preempted.

`action=cancel` releases decoder/texture resources. Domain reload marks unfinished extraction
interrupted; completed partial PNGs remain available. The latest eight extraction receipts survive
domain reload for this Editor session. `action=cleanup` removes only PNG files listed by that
job, not its source MP4 or arbitrary directories. Retain required evidence first. Recording
receipts persist beside MP4s in Library, so an older recording can be inspected without stopping
a newer active recording. No footage is uploaded by these tools.

## Recoverable UI preview sessions

Preview environment management is available in `full`, or an explicit custom exposure list;
it is not part of the focused default `core`. Confirm exposure before starting this workflow.

`start_ui_preview_session` accepts a JSON `prefab_paths` array (0–20 real prefab assets),
an optional saved `scene_template`, optional `enter_play_mode=true`, and optional width/height
in 128–4096 (both zero preserves the current resolution). It requires idle Edit Mode, saved clean
original scenes and no open Prefab Stage. It never silently saves/discards the user's work to meet
those requirements. Its request key is idempotent for the most recently retained session.

The session journals original scene setup, selection and supported Game View state before
changing the environment. It copies a supplied scene template, or creates a temporary default
scene, then instantiates the prefabs with their connections intact. RectTransform prefabs without
their own Canvas use the unique root Canvas (or a project-aware new Canvas); multiple candidate
canvases require an explicitly assembled scene template. Business data stays project-specific:
provide a project harness prefab/scene or deliberately bind test data through project APIs.
Prefab lifecycle code may execute during setup, so this is environment management, not a sandbox.

Use `get_task` until session.status is `ready`; inspect the
current Editor mode and runtime/console state too. Play preparation uses the durable operation
service. Journals live at `Library/FunplayMcp/Preview/session.json`, while the disposable scene
is owned under `Assets/FunplayMcpPreviews/<session-id>/Preview.unity`.

`end_ui_preview_session(session_id)` exits Play asynchronously and briefly waits for
restoration. Continue with `get_task` until `closed/closed_with_warnings`, then inspect all four restoration flags:
`scenes_restored, view_restored, selection_restored, assets_cleaned`. Ending again is idempotent.
Scene path/load/active state uses Unity's
[scene setup snapshot](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SceneManagement.EditorSceneManager.GetSceneManagerSetup.html);
original scene files are never overwritten. Changed original files are loaded as they now exist,
with a warning. Supported Game View size, zoom, display and low-resolution settings are restored
only when compatible with the recorded state; changed user choices/windows are preserved.
Play Mode status uses runtime-safe scene enumeration. Game View zoom constraints are updated
for the restored resolution and read back again after repaint before reporting restoration.

An unrelated open scene blocks automatic restoration even with
`discard_preview_changes=true`. Unsaved or saved modifications to the temporary scene require
inspection and an explicit discard decision; useful work should first be saved outside the
temporary folder. Only the exact owned scene and its empty folder are removed. Additional files
are retained and reported. `needs_attention` returns the failed phase/error and journal for
recovery; after resolving the conflict, end can be retried without replaying setup.
Domain reload retains the session; a full Editor restart reports interruption and requires an
explicit end/recovery request rather than automatically closing scenes.

No claim is made to roll back source assets, scripts, networking, PlayerPrefs, save-game files,
external services or arbitrary project side effects. Preview sessions cannot establish device
performance or pixel fidelity without reviewing captures at the intended resolution/content.

## Focused default exposure and compatibility

The v0.6.9 catalog has 180 tools across 42 modules; default `core` exposes 40. The
existing custom Core/Full lists remain authoritative and are never overwritten by this change.
High-frequency object/component/prefab editing, UI creation/inspection/auditing, capture,
recording/evidence, interaction, readiness, console and cancellation remain in Core.

`full` retains all legacy status and compile/Play entry points, Editor-operation history,
project-default configuration, preview-session management, explicit recording markers,
performance diagnostics, Simulator/window capture and time-scale inspection. These are not
deleted or renamed. Prefer `prepare_editor` and `get_task` in Core; old clients can deliberately
select Full or preserve an explicit list. A missing default exposure is not a missing capability.
