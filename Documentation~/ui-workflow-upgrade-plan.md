# UI workflow upgrade: delivery and verification

This work follows the v0.6.8 UI skill release and is included in v0.6.9 after explicit user
release approval on 2026-09-17. The records below describe staged development and verification;
their earlier uncommitted/unpublished notes are historical checkpoints. Existing APIs remain compatible.
Use the dedicated FunplayMcp project, never a user's game project, for mutating tests.

## Release baseline

- [x] Commit and push v0.6.8 (`efd43b8`) with UI skill 1.0.5 and explicit release notes.
- [x] Cover all 438 existing EditMode cases: 421 batch and 17 interactive UI cases passed.
- [x] Publish verified Unity/NuGet artifacts and checksums on GitHub; repository CI passed.
- [x] Verify NuGet, MCP Registry and OpenUPM indexing (all observed at 0.6.8).

## 1. Reliable editor operations

- [x] Start a bounded import/compile/Edit or Play preparation operation and immediately return its ID.
- [x] Persist phase, outcome, error details, timestamps and history across domain reloads; distinguish
      current readiness from historical recovery events and detect interrupted Editor sessions.
- [x] Provide status, cancellation, idempotency and overlapping-operation protection; never replay
      an arbitrary mutating tool after an ambiguous interruption. Expose completion only after readback.
- [x] Keep status inspection usable through reload/reconnection, and document timeout/retry semantics.
- [x] Test success, compiler errors, cancellation, timeout, reload enabled/disabled and interrupted recovery,
      including real refresh and Play Mode transitions over MCP.

Evidence: 19 deterministic coordinator tests passed; real MCP refresh/compile/reload, compiler error,
Play enter/exit with reload enabled and disabled, request-key deduplication, overlap rejection and
repeated cancellation passed. FunplayMcp `workflow-validation/editor-*.json` contains receipts.
Status during a dropped direct HTTP connection is last-known local journal data, not a live HTTP
ready response; the same ID is queryable after reconnecting. Original Play options restored.

## 2. Read-only UI audit

- [x] Audit selections, live scene roots, prefab assets/folders and saved scenes with bounded work and
      explicit scanned/skipped/incomplete counts. Do not save or alter inspected source assets.
- [x] Cover Sliced images without borders, missing scripts/references, transparent raycast blockers,
      text overflow, clipping and competing layout ownership.
- [x] Separate deterministic errors from contextual warnings and checks requiring live layout/data;
      report stable asset/object identity, rule, measured evidence and suggested next action.
- [x] Allow project-specific required-reference rules and suppressions; never invent border values.
- [x] Test positive/negative fixtures, inactive objects, variants, atlases/sub-sprites, intentional scroll
      clipping and blockers, dirty scenes, cancellation/limits and preservation of inspected assets.

## 3. Structured queries and asset inspection

- [x] Extend searches with component/property predicates, explicit scopes, bounded/paged results and
      projections, preserving existing simple queries.
- [x] Batch-read effective Image/Sprite/importer associations, borders, sprite IDs and dependent assets.
- [x] Query full project type names and assemblies with explicit ambiguity, not first-match guessing.
- [x] Preserve existing batch writes/readback; distinguish live values, persisted values and affected assets.
- [x] Make available versus exposed capabilities discoverable and position execute_code as the fallback.
- [x] Verify schema, ambiguity, invalid selectors, partial failures, sub-assets and no-mutation behavior.

Evidence: regression fixtures cover Multiple Sprite borders/local IDs, packed atlas clones,
prefab variants, genuine missing script/reference fixtures, renderer/CanvasGroup alpha, stable
query pagination and nested field readback. Source prefab/scene/importer bytes remain unchanged.
Atlas membership is not inferred from the native `Sprite.packed` runtime-binding flag.

## 4. Visual coordinate contract

- [x] Share render/output size, viewport rectangle, units, origin, scaling and capture identity across
      screenshots, recordings, clicks/drags, raycast diagnostics and object screen-bound readback.
- [x] Preserve existing defaults; support explicit screenshot/render/normalized conversions and reject
      stale or incompatible capture geometry rather than silently mis-targeting an interaction.
- [x] Test resized output, both origins, aspect/letterbox changes, camera/overlay UI and actual hit targets.

Live evidence: a 320×180 screenshot mapped into a 640×360 Game View, raycast and persistent
button callback agreed, a resize to 720×1559 invalidated the old receipt, and a Screen Space
Camera Canvas with viewport `(0.1,0.1,0.8,0.8)` still hit the intended button. See
`workflow-validation/live-{button-bounds,raycast,click,resize-rejection,camera-click-state}.json`.

## 5. Project UI defaults and templates

- [x] Support project-scoped preferred prefab templates, text/font/material defaults and input module policy.
- [x] Precedence: explicit request, project configuration, verified existing convention; new projects default
      to TMP when available, with actionable missing-dependency errors rather than silent substitutions.
- [x] Reuse configured prefabs and retain their bindings; do not convert unrelated legacy controls or replace
      an existing EventSystem. Report the selected convention and created objects precisely.
- [x] Test TMP/legacy projects, Input System/legacy/Both policy combinations, prefab templates, overrides and missing resources.

Live creation selected TMP and InputSystemUIInputModule in the dedicated project. The saved
validation prefab retained its persistent button callback through preview instantiation and
Play Mode. Tests verify shared TMP outline material identity and reject a mismatched font atlas
before overwriting configuration. Active Input Handling combinations are tested without
changing that project setting; missing-TMP and installed-TMP runs were both exercised.

## 6. Recording evidence

- [x] Extract requested keyframes from owned recordings without external one-off scripts, with bounded
      asynchronous processing, requested/actual timestamps and image receipts.
- [x] Record explicit and input-action markers on the recording's real elapsed-time timeline.
- [x] Preserve coordinate metadata, handle unfinished/corrupt recordings, interruption and cleanup, and
      validate returned frames and timestamps against a real Game View recording.

Live video `90878a008eaa43319eca4b98551e29d1`: 640×360, 61 frames, 5.92 seconds, explicit/click/scroll
markers. Extraction job `de434f7e12334950b3b30360b2964251` returned initial, confirmation-visible
and scrolled frames; actual time deltas were 0, 24.7 ms and 16.0 ms. All three images were visually
inspected; a native MCP image response was verified. Tests additionally encode/decode known
pixel colors with sparse timestamps. MP4 and PNG evidence is retained under the project's Library.

## 7. Preview sessions

- [x] Provide start/status/end for a temporary, identified UI validation environment using real prefabs.
- [x] Capture and restore scene setup, active scene, selection and supported Game View settings; protect
      dirty scenes, existing prefab stages and user changes made after session creation.
- [x] Let the project supply business data and behavior. Do not promise rollback of network/save-game effects.
- [x] Track only session-owned temporary assets/objects, survive reloads or report interruption explicitly,
      and return actionable partial-restoration results rather than silently claiming cleanup succeeded.
- [x] Test normal teardown, repeated end, setup failure, reload, user interference and recovery; demonstrate
      the full inspect/modify/compile/preview/capture/restore flow in FunplayMcp.

Live session `facddd0cf1af47dcbbceb4a31a39fa23` ended with all four restoration flags true,
no warnings, baseline scene hash unchanged, original 720×1559 preset and 0.954458 zoom restored
after repaint, temporary scene/folder removed and repeated end idempotent. Real Play Mode
validation found and fixed an editor-only scene API call; zoom validation found and fixed
restoration against stale preview-resolution constraints. Receipts: `live-final-preview-ended.json`
and `live-repeated-end.json`.

## Final acceptance

- [x] All seven stages have implementation, user documentation, regression tests and appropriate live evidence.
- [x] Run the complete plugin suite and relevant interactive/Play Mode tests; record passes, skips and limits.
- [x] No game-project edits, unrelated settings changes, orphaned preview assets or running test sessions remain.
- [x] Report delivered APIs, validation evidence, residual limitations and uncommitted/unpublished state honestly.

Final complete suite: **578 passed, 0 failed, 0 skipped**, 149.25 seconds, job
`291aa0ec-b4b6-4591-ab4f-549b83dcba07` (2026-09-16). The v0.6.8 baseline had 438 tests;
140 cases were added. Evidence: `workflow-validation/acceptance-suite.json`.

Original clean Untitled scene (Main Camera and Directional Light), Game View, domain-reload
options and disabled sprite-packer mode were restored. No UI defaults file remains. Test
prefab/scene assets, temporarily imported TMP Essential Resources and the empty preview root
were moved out of Assets into `workflow-validation/restored-assets` with their meta files,
not destroyed. Re-import TMP Essential Resources and use a saved clean validation scene to
repeat the positive font/preview integration fixtures; headless/unsupported environments may
skip graphics-dependent cases explicitly. The recorder evidence and raw receipts remain local.

Repository validation and `git diff --check` passed; all source meta files exist and GUIDs are
unique. HEAD/origin main remain the v0.6.8 release commit. All seven-stage changes are local,
uncommitted and unpublished; no further version bump, tag or release was made.

Both skills remain built-in: workflow v1.0.5 and UI composition v1.0.6. All twelve generated
outputs (six platforms × two skills) were inspected; all ten SKILL.md files passed the skill
validator and both Cursor rules preserve alwaysApply/version metadata. Legacy managed-file
migration retains the released wording while current generated guidance uses structured tools.

Validation environment: macOS, graphics-enabled Unity 2022.3.62f3c1, the already-open dedicated
FunplayMcp project. No game-project edits were made. Windows/native decoder behavior on other
Unity versions and real-device performance are not claimed as tested; unsupported reflection
interfaces return explicit errors and partial restoration is reported. Audits are diagnostics,
not proof of visual fidelity; business initialization remains project-specific.

## Follow-up: fewer calls and focused default exposure (2026-09-17)

- [x] Short MCP tasks default to a two-second completion window; `get_task` offers a
      0–30-second bounded read/wait, meaningful-change revisions and capped polling backoff.
- [x] Share read-only status for six existing task kinds, without replaying mutations or
      combining query with cancel/cleanup. Preserve native results, recovery keys and legacy APIs.
- [x] Keep the initial read and wait delivery bounded even when the Editor queue is busy;
      return an explicit unknown outcome or timestamped last snapshot, not invented readiness.
- [x] Reduce default Core from 57 to 40 tools. Full retains 180 built-ins across 42 modules;
      original custom allowlists remain authoritative, including per-kind status access.
- [x] Update server guidance and both built-in skills; generate twelve client documents and
      validate ten SKILL.md files plus the two Cursor rules' built-in metadata.

Complete suite: **653 passed, 0 failed, 0 skipped**, 138.53 seconds, job
`14f55e86-5d03-4e9b-827b-9c07e8bbdc1a`. Native MCP evidence is retained in
`FunplayMcp/workflow-validation/task-wait-20260917/`:

- `live-short-audit.json`: small scene audit completed in one 667 ms call.
- `live-video-wait.json`: recording starts returned first, frame counters did not wake
  revision waits, completion returned a usable video, and two requested keyframes decoded.
- `live-preview-wait.json`: shared status and the legacy reader agreed on the preview session,
  reads did not close it, and explicit end restored all four scene/view/selection/asset flags.
  The live test caught and fixed a nested Editor-operation ID being mistaken for the session ID.
- `live-busy-query.json`: during an owned 3.5-second Editor stall, a zero-wait query returned
  `EDITOR_STATUS_UNAVAILABLE` in 1,007 ms; the same recovery key read successfully afterwards.
- `live-core-permissions.json`: 40 tools exposed; legacy-only calls and hidden preview/test
  queries rejected, while exposed Editor-task status remained readable.
- `full-suite-final.json`: complete regression result, including the task-query and broker guards.

Compact Core tool JSON decreased from 44,938 to 36,105 characters (UTF-8 bytes:
44,954 to 36,121), approximately 20%. This is reproducible definition-size evidence, not
a measurement of a particular model's token billing or total usage.

The original clean Untitled scene and Core profile were restored. Temporary baseline/TMP
resources and the empty preview directory were moved with their meta files into this evidence
directory's `restored-assets`, leaving recoverable backups outside Assets. No further commit,
push, version bump or publication was made. Validation remains macOS/Unity 2022.3-specific.
