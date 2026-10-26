# Execution trust and verification

These additions preserve the 40-tool Core profile. Five management/diagnostic tools live in
Full by default, and existing custom exposure lists remain authoritative. Task observation
continues through `get_task`; no second status/polling system is introduced.

## Verify the project before writes

Generated configurations use a project-scoped URL:

```text
http://127.0.0.1:<port>/mcp/project/<project-path-sha256>/
```

Read `initialize.funplay.context` and independently compare `project_path` / `project_id`
with the intended project. Do not derive the intended identity from whichever server happened
to respond. The context also carries a process-session `instance_id`, a domain-reload
`editor_epoch` and `expected_context` (`project_id:instance_id`). Context prevents accidental
misrouting; it is not authentication or a security sandbox.

Old `/`, `/mcp` and `/mcp/` URLs still support reads/initialization. Mutations on those URLs
require the verified `expected_context` argument (or `params._meta.expected_context`).
Missing identity returns `PROJECT_CONTEXT_REQUIRED`; a wrong project returns
`PROJECT_CONTEXT_MISMATCH`; a stale process token returns `EDITOR_INSTANCE_CHANGED`.
None executes the tool. Reconfigure existing clients to the generated scoped URL, or retain
the token explicitly. Scoped URLs guard the project; pass the optional token too when a
specific process session must be pinned. The reload epoch changes across compilation while
the process-session token remains stable.

The MCP Server window checks the selected client's saved configuration and shows the connection
reminder inside the existing blue **Project Skills** notice card when its project entry still
uses an unbound address; no second banner is added. Connection and Skills messages share that
card with a single **Reconfigure MCP + Skills** action that updates the selected client's MCP
address and managed Project Skills using the existing combined configuration workflow. The card remains
visible until both concerns are resolved, including clients without Skills support.
For clients without generated Skills support, the same single action is explicitly labeled
**Reconfigure MCP** and updates only MCP; unreadable configs disable the action pending review.
Click the card's action, **Configure** or **Configure + Skills**, then reconnect/restart that client to
load the new endpoint. The notice clears after the saved entry is updated; a running client
may still hold its old connection until reconnected. Fresh configurations and unrelated
projects' entries do not trigger the notice. Historical shared entries are reported when
there is no current project entry; they are not deleted automatically. Unreadable configs
produce a review notice without preventing the rest of the window from loading. Inspection
never edits client files.

Both direct HTTP and Broker preserve scoped routing. Broker v5 upgrades replace older broker
processes through the existing managed upgrade mechanism. JSON result envelopes, MCP result
metadata and visual geometry include context so evidence can be attributed independently.

## Readiness is layered

`current_editor.ready` retains its existing import/compile/mode meaning. It does not certify
SDK initialization, PlayerLoop progression, input handling or business behavior.

`get_editor_state` additionally reports a passive `player` witness: frame count, wall clock,
unscaled/game clocks, pause, focus, background execution, timeScale, scene and observer status.
The observer is a small owned PlayerLoop node after script Update. It is removed on Play exit,
reload and quit without replacing unrelated loop nodes. No status request changes focus,
pause, timeScale or background execution. Fewer than two samples is inconclusive; old samples
are stalled; `timeScale=0` with advancing frames is a paused scaled clock, not a stopped loop.
An installed-but-replaced observer is reported unavailable rather than reinserted by a read.

Recording `frame_count` remains the encoded capture count. `first_player_frame` /
`last_player_frame` are separate actual Update evidence and can remain unchanged while a clip
is successfully encoded. A clip, API success or frame progress alone does not establish
application acceptance. The distinction follows Unity's documented
[frameCount](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Time-frameCount.html)
and per-frame
[input-edge behavior](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Input.GetMouseButtonDown.html).

## Select an honest input route

`simulate_mouse_click` keeps its existing `auto` default for compatibility. `allow_fallback=false`
limits auto to EventSystem dispatch. Explicit `input_route` choices never silently fall back:

| Route | What is tested |
| --- | --- |
| `ui_eventsystem` | Raycast/top handler and synthetic pointer dispatch, not hardware polling |
| `direct_button` | Direct uGUI button fallback; not EventSystem fidelity |
| `physics` | Camera raycast and synthetic physics click messages |
| `new_input_system` | Owned synthetic mouse consumed by actual Dynamic PlayerLoop updates |
| `legacy_polling` | Explicit unsupported error; no fake legacy injection |

New Input System jobs require an unpaused Play Mode and Dynamic input update mode. They do not
change project input settings. `hold_frames` is 2..30 actual frames, with a 10-second wall-clock
deadline. Down, Held and Up samples retain frame/clock evidence. A private synthetic mouse is
removed on completion, cancellation, Play exit, reload or quit; physical devices are not
modified or removed. The result is a synthetic-device test, not hardware provenance or proof
that a project `PlayerInput` paired to another device accepted it. Read status through the
returned `input:<id>` task. Cleanup failures remain explicit, not successful completion.

## Bounded verification scopes

Full exposes `verification_scope(action=start, timeout_seconds=60, allow_background=false)`
and `verification_scope(action=end, verification_id=...)`. Start requires stable Play Mode in
a saved authored scene; it never creates a fixture scene or injects business data. Scope
status is available through `get_task(task_id="verification:<id>")` only while that kind is
deliberately exposed.

Use a unique optional `request_key` on start. Repeating the same key and options returns the
existing receipt without acquiring another scope; conflicting options are rejected. A lost
start response can be recovered through `get_task(kind="verification", request_key=...)`.
Historical completed/interrupted scopes are not resumed by reusing their keys.

Pass `verification_id` on a recording, extraction or cross-frame input start to enroll it.
Only enrolled resource IDs can be stopped/cancelled by that scope. `allow_background=true`
explicitly allows a temporary `Application.runInBackground=true`; cleanup compares the actual
value with the assigned value before restoring the original. A later different user value
is preserved. A changed scene set is reported and never overwritten.

Expiration, Play exit/reload and quit trigger owned cleanup. `end` may report `cleanup_pending`
while an encoder is still finalizing or a resource failed to release. Read the result; retry
explicit `end` after the underlying job settles. A read-only status does not retry cleanup.
Active ownership is journaled in SessionState for domain-reload diagnosis. It does not resume
work or claim ownership in another Editor process. Business data, arbitrary callbacks,
network effects, scene edits and unrelated dirty state are outside its rollback guarantee.

## Selective serialized-reference transactions

`patch_serialized_reference` supports one persistent ObjectReference field on a normalized
`Assets/*.asset` or `.prefab`. Provide the selected serialized object's local file ID, exact
SerializedProperty path, current file SHA256, expected old GUID/fileID and a replacement asset
plus subasset local file ID (or omit replacement to set null).

The transaction requires idle Edit Mode, no dirty loaded objects from the target asset, no
open stage for that prefab, writable source and text serialization. It does not change the
project serialization mode. Assets are limited to 16 MiB. A conservative read-only inventory retains document IDs, PPtr
slot order/property prefixes and every GUID/fileID/type token, including unresolved references.
Ambiguous duplicate old PPtrs or unsupported formats are rejected, not guessed.

Unity creates a disposable copy. The original inventory must survive copying; a SerializedObject
edit, save, synchronous import and persisted readback must preserve every untouched slot on
the copy. The live hash/dirty precondition is rechecked, an exact byte backup is retained under
`Library/FunplayMcp/ReferenceBackups`, and only then is the live edit attempted. A failed live
comparison triggers exact-byte replacement through
[FileUtil.ReplaceFile](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/FileUtil.ReplaceFile.html)
and synchronous import, with a hash check afterwards. Inspect `rollback_verified`,
`cleanup_pending`, `restore_error` and `backup_path` on failure.

The exact owned preflight asset is removed afterwards. A failed preflight cleanup is returned
explicitly with `preflight_path`, `cleanup_pending` and `transaction_succeeded`; do not blindly
repeat the live edit merely because cleanup failed.

This is reference-slot protection, not a general serialization or business transaction.
Serialization callbacks can have side effects that byte recovery cannot undo, or rewrite the
restored file. Unsupported/reordered layouts deliberately fail closed. Existing general
setters retain their original behavior; use this transaction for unresolved-reference risk
rather than treating ordinary setter success as a preservation guarantee. Unity asset saves
use the documented targeted
[SaveAssetIfDirty](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.SaveAssetIfDirty.html)
or prefab API, never a broad SaveAssets operation.

## Actual render-resource users

`inspect_render_bindings(target=<verified GameObject ID or prefab path>, recursive=true,
limit=100)` is read-only. It walks up to 5,000 transforms / 200 render components, 64 material
or texture slots per component and 32 loaded cameras, reporting incompleteness explicitly.
It follows configured shared materials, particle trail serialized material, shader, textures,
sprite/raw texture, source prefab/overrides, sorting, layers, Canvas/camera and masks.

It avoids `Renderer.material(s)` and `Graphic.materialForRendering`, which can instantiate or
derive materials. Unity documents shared bindings separately in
[sharedMaterials](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Renderer-sharedMaterials.html).
The graph is not a GPU draw-call trace: custom Graphic overrides, MaterialPropertyBlock,
replacement shaders and SRP renderer features require separate evidence. Pair the graph with
the real authored scene's captured render. `Shader.isSupported` alone is not visual acceptance.

## Decoding and continuation evidence

`extract_recording_frames` retains its existing native-decoder path and limits (16 timestamps,
1920-pixel dimensions, 120-second deadline). `decoder_backend` accepts `auto`, `unity`, `ffmpeg`.
Automatic external fallback occurs only with `allow_external_decoder=true`. Optional
`decoder_path` selects an existing absolute `ffmpeg` executable. Discovery never executes or
installs it; the start mutation explicitly opts into executable use. Process arguments are
fixed/escaped, without a shell, and input protocols are restricted to file/pipe.

Receipts include requested/actual presentation timestamps and deltas, source/PNG SHA256,
decoder executable path/hash/version and geometry. Sources are hash-checked again at completion;
frame reads reject changed PNGs. Cancellation/cleanup controls only owned processes/PNG paths
and never removes the original video. Extracted historical frames are not live click captures.

Full `save_verification_evidence(paths=<JSON file list>, checkpoint=<JSON>, configuration=<JSON>)`
captures bounded static resource inventories and continuation notes. Paths are explicit Assets,
ProjectSettings or package-manifest files. The fingerprint includes exact file/meta hashes,
AssetDatabase dependency hashes, Unity version, project identity and canonical configuration.
Missing-script, unresolved-GUID and unexamined values remain distinct; missing subasset IDs
are not inferred merely from GUID resolution. No saved scene is opened to calculate counts.

`read_verification_evidence` is a separate read-only capability. It rehashes current inputs,
returns `cache_valid=false` for invalidation and never resumes tasks or reuses cached runtime
observations as current. Supply changed task configuration explicitly. Checkpoints are
caller-authored historical notes, not independently certified acceptance. Always reread live
frames, input state and project-specific application predicates before accepting current behavior.

## Native-dialog exception

Prefer MCP for all covered Unity operations. When a confirmed native Unity modal blocks MCP
and MCP cannot handle it, computer use may handle only that modal. Explain the blocker first.
An unrequested save/discard/overwrite/install/authorization/security choice still needs user
direction. Return to MCP, independently recheck identity/state and read back the interrupted
operation; never replay an uncertain mutation. Disconnection alone does not establish a modal
or authorize bypassing custom exposure.
