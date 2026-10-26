# Execution trust upgrade

This work strengthens the existing workflow rather than enlarging default Core exposure. The supplied restoration retrospective is evidence for general engineering requirements, not an instruction to change that game's state or implement its business-specific owners.

## Delivery checklist

- [x] Bind project-scoped endpoints and guarded mutations to an independently verified project/Editor identity; stamp evidence and reject stale/wrong identities.
- [x] Separate Editor readiness, actual PlayerLoop progress and application acceptance; include frame/clock witnesses in recording and verification receipts.
- [x] Add strict input-route selection with truthful unsupported legacy polling and cross-frame evidence; correct input side-effect classification.
- [x] Make recovery routes exposure-aware and discover current backend availability without enabling tools or installing dependencies.
- [x] Provide owned, bounded verification scopes with task status, explicit lifecycle and compare-before-restore cleanup, without replacing the authored scene.
- [x] Protect serialized asset reference edits with preconditions, disposable-copy preflight, preserved-reference comparisons and verified recovery.
- [x] Inspect actual runtime render-resource users and camera context without material instantiation or project mutation.
- [x] Provide controlled existing-decoder fallback and versioned evidence/checkpoint reuse without caching current runtime observations as fresh state.
- [x] Update both built-in skills and initialization guidance, including narrowly scoped computer use for confirmed blocking native Unity dialogs.
- [x] Run focused regressions, all EditMode tests and real MCP checks in FunplayMcp; validate all six skill exports and unchanged Core defaults.

## Boundaries

Existing custom exposure and disabled tools remain authoritative. Read-only task/status requests never acquire a scope, change background/pause/focus, cancel, restore or clean up. Input dispatch, recorded bytes and imported assets are not application-level acceptance. Unsupported backends fail explicitly; no game input, focus/SDK-ready/outcome flags or unrelated dirty state are fabricated. Verification cleanup owns only the hooks/settings/jobs it created. Persistent project settings, business data, network effects and arbitrary user callbacks are not automatically reversible.

The native-dialog computer-use exception permits handling only a confirmed Unity modal that MCP cannot handle, followed by MCP identity/state readback. It does not authorize an otherwise unrequested save/discard/overwrite/install/security decision, widen tool exposure, or replay an uncertain mutation.

## Validation record

Final v0.7.1 macOS / Unity 2022.3.62f3c1 graphics-enabled run: **791/791 passed**, no failures or skips (61 more cases than the v0.7.0 baseline). This includes strict real-PlayerLoop input, reference-preserving transactions with 168 slots/unresolved subassets, material non-instantiation, static checkpoint invalidation, native/external decoder pixels/timestamps, Broker routing, native metadata serialization and 52 connection/Skills notice cases. An earlier 754-case development run passed before the setup-window work; a preview recovery timeout in a previous run passed unchanged in an isolated recheck and subsequent full runs. No timeout was relaxed or test excluded.

Real HTTP MCP checks independently verified the dedicated test project's identity, wrong/missing/stale write guards and absence of the rejected mutation. Core returned 40 tools and Full 185. Actual Play Mode samples observed Down/Held/Up in frames 778/779/780; a two-second recording encoded 10 frames while PlayerLoop evidence advanced from 1030 to 1810. Render inspection traced a real runtime cube, and the actual camera capture was visually inspected. Explicit ffmpeg extraction returned three hash-stamped 146x320 frames; scope cleanup completed, and Edit Mode plus the original Core profile were restored. The owned test Editor was closed; unrelated Editors were untouched.

All six skill targets were exported to an isolated temporary directory: ten SKILL.md files passed skill-creator validation, two Cursor rules retained metadata, and all 30 reference links resolved. Built-in workflow/UI versions are 1.0.7 / 1.0.9. No optional skill was added. Test-only PyYAML and ffmpeg were prepared in a temporary venv; no system/project dependency was installed by the plugin or added to package.json.

The final setup window reuses one blue Skills notice card, not a second banner, with one combined **Reconfigure MCP + Skills** action. Connection and Skills issues clear independently, unreadable configurations disable the action, and clients without generated Skills support explicitly configure MCP only. The actual window was captured and visually inspected without changing real client configuration files.

The v0.7.1 .NET 8 bridge completed Content-Length framing/UTF-8/HTTP 202 fixture checks and real initialization, 40-tool discovery and Editor-state reads against FunplayMcp. Missing, stale and wrong-project guards remained intact through the stdio bridge. Chinese/English installation examples and bridge help now direct users to the project-bound URL.

See [usage and migration](execution-trust.md). Windows and other Unity versions are not claimed as live-tested. The computer-use exception was validated as exported policy; no native modal required computer use during these tests.
