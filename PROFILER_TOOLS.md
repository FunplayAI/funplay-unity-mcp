# Profiler Tools

*[中文](PROFILER_TOOLS_CN.md) | English*

This document covers the 13 Profiler-related tools added in `Editor/Tools/Builtins/ProfilerFunctions.cs` (category `Profiler`). It is a companion reference for the [pull request that introduces them](#) — see the main [README.md](README.md#built-in-tools) for how this fits into the full tool list.

## Why

Before this addition, the only performance-related tools were `get_performance_snapshot` and `analyze_scene_complexity` (category `Performance`) — both lightweight, single-shot summaries. There was no way to:

- start/stop an actual Profiler capture session and read live counters over time
- get precise CPU/GPU frame timing independent of a one-off snapshot
- inspect the runtime memory footprint of a specific asset or GameObject
- take and diff memory snapshots to spot growth trends
- drive the Frame Debugger to see what's actually being submitted to the GPU this frame

These 13 tools close that gap. They're intentionally built as small, focused, flat tools (one job each) rather than one large `manage_profiler`-style dispatch tool, to match this repo's existing flat-tool convention.

## Tool Reference

### Session control

| Tool | Params | Returns | Notes |
|---|---|---|---|
| `profiler_start` | *(none)* | Confirmation + recorder count | Enables `UnityEngine.Profiling.Profiler` and starts 9 persistent `ProfilerRecorder`s (5 Render-category, 4 Memory-category — see [Default counters](#default-counters)). Idempotent — safe to call again while already running. |
| `profiler_stop` | *(none)* | Confirmation | Disposes all persistent recorders and disables `Profiler`. |
| `profiler_status` | *(none)* | Multi-line status | Whether a session is running, active recorder count, and per-recorder `running`/`valid` flags. |

### Frame timing & counters

| Tool | Params | Returns | Notes |
|---|---|---|---|
| `get_frame_timing` | `sample_frames` (int, optional, default 1, clamped 1–30) | Avg CPU/GPU frame time (ms) + approx FPS | Uses `UnityEngine.FrameTimingManager`. Does **not** require `profiler_start`. Falls back to `Time.unscaledDeltaTime` if no timing samples are available yet. |
| `get_counters` | `names` (string, optional, comma-separated) | One line per counter: `last`, `current`, `unit` | Reads the persistent recorders started by `profiler_start`; auto-starts them (with a warning) if not already running. See [Known limitation](#known-limitations) re: Render counters. |

#### Default counters

```
Render/Draw Calls Count, Render/Batches Count, Render/SetPass Calls Count,
Render/Triangles Count, Render/Vertices Count,
Memory/GC Allocated In Frame, Memory/GC Used Memory,
Memory/Gfx Used Memory, Memory/Total Used Memory
```

### Object memory

| Tool | Params | Returns | Notes |
|---|---|---|---|
| `get_object_memory` | `target` (string, required) | Type + `Profiler.GetRuntimeMemorySizeLong` in human-readable bytes | `target` starting with `Assets/` resolves via `AssetDatabase.LoadMainAssetAtPath`; otherwise resolves via `GameObject.Find` (hierarchy path, e.g. `Canvas/Panel/Icon`). For a GameObject, also sums memory across all child components (`Total (incl. all child components)`). |
| `get_top_memory_objects` | `type_name` (string, optional, default `Texture2D`), `top_n` (int, optional, default 20, clamped 1–100) | Top N objects of the type by memory, sorted descending, with per-object detail (texture WxH+format, mesh vertex count, audio length) and `hideFlags` | The reverse lookup to `get_object_memory`: enumerate ALL loaded objects of a type via `Resources.FindObjectsOfTypeAll` and rank by `GetRuntimeMemorySizeLong`. Pass `type_name='All'` for a per-type total summary (Texture2D/RenderTexture/Mesh/AudioClip/Material/AnimationClip/Shader/Sprite). Any other `UnityEngine.Object`-derived type name is resolved by reflection across loaded assemblies. Designed as the follow-up to a `memory_compare_snapshots` diff that shows growth: it answers *which objects* are consuming the memory. Note: in the Editor this also enumerates editor-owned objects — a non-empty flags column (e.g. `HideAndDontSave`) usually marks editor internals or runtime-created objects. |

### Memory snapshots

| Tool | Params | Returns | Notes |
|---|---|---|---|
| `memory_take_snapshot` | `name` (string, optional) | Saved file path + Total Allocated / Mono Used | Writes a small JSON file (aggregate numbers only — `Profiler.GetTotalAllocatedMemoryLong`, `GetTotalReservedMemoryLong`, `GetMonoUsedSizeLong`, `GetMonoHeapSizeLong`, plus 3 Memory-category counters) to `MemoryCaptures/mcp-snapshots/`. **This is intentionally NOT a real Unity `.snap` file** — see [Known limitations](#known-limitations) for why. |
| `memory_list_snapshots` | *(none)* | File names | Lists `*.json` files in the snapshot directory. |
| `memory_compare_snapshots` | `path_a`, `path_b` (string, required) | Per-field delta (`before -> after (Δ)`) | Accepts file names with or without `.json`. Sanitizes input via `Path.GetFileName` before combining with the snapshot directory (rejects path-traversal attempts by collapsing them to a bare filename that then fails the existence check). |

### Frame Debugger

| Tool | Params | Returns | Notes |
|---|---|---|---|
| `frame_debugger_enable` | *(none)* | Confirmation | Opens a Frame Debugger window (reflection) and drives its internal capture loop. See [Implementation notes](#implementation-notes) — a bare `FrameDebuggerUtility.SetEnabled` call does **not** populate events without a live window instance. |
| `frame_debugger_disable` | *(none)* | Confirmation | Closes **all** currently open Frame Debugger windows, including any opened manually by a developer — not just the one this tool opened. |
| `frame_debugger_get_events` | `max_events` (int, optional, default 50, clamped 1–500) | Per-event name + associated object | Best-effort: returns the event name (render pass / draw call label) and associated `UnityEngine.Object` if any — not full per-draw-call shader parameter dumps (that would require constructing the internal `FrameDebuggerEventData` struct via reflection, out of scope for v1). |

## Implementation notes

Two non-obvious things worth flagging for reviewers, both found by testing against a live Editor rather than assumed from API docs:

1. **`ProfilerRecorder` must stay alive across frames.** Constructing a `ProfilerRecorder`, reading it, and disposing it within a single synchronous call reads `0` — the recorder needs to have been running across at least one real frame boundary. `profiler_start` keeps a `Dictionary<string, ProfilerRecorder>` alive as a static field (disposed on `AssemblyReloadEvents.beforeAssemblyReload` to avoid leaking the native handle across domain reloads) rather than constructing recorders per-call.

2. **Enabling `FrameDebuggerUtility` alone does not drive capture.** `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility` is a data container; the actual capture loop is driven by `UnityEditor.FrameDebuggerWindow`'s `Update()`/`OnGUI()`. `frame_debugger_enable` reflects `FrameDebuggerWindow.OpenWindow()` (public static) to get/create a window instance, then reflects the instance's `EnableFrameDebugger()` (non-public instance method) and calls `Repaint()`. `frame_debugger_disable` finds all open instances via `Resources.FindObjectsOfTypeAll` and calls `DisableFrameDebugger()` + `Close()` on each.

## Known limitations

- **Render-category counters read `0` in the Unity Editor**, even during confirmed active rendering. This was verified by running `frame_debugger_get_events` at the same time as `get_counters` — the Frame Debugger showed 78–95 real render events in one test session while `Render/Draw Calls Count` etc. stayed at `0`. Toggling `ProfilerDriver.profileEditor = true` did not change this. Memory-category counters (`GC Used Memory`, `Gfx Used Memory`, `Total Used Memory`) are unaffected and read real values reliably. **Not verified** whether this limitation also applies to a Standalone Player build — if anyone testing this PR can check that, it'd be a useful data point.
- **`memory_take_snapshot` is not a real Unity Memory Profiler snapshot.** `com.unity.memoryprofiler`'s public scripting API is effectively empty (only 3 unrelated public types found via reflection across its assemblies), the legacy `UnityEngine.Profiling.Memory.Experimental.MemoryProfiler.TakeSnapshot` has been fully removed as of Unity 6, and `ProfilerDriver.RequestMemorySnapshot()` didn't reliably produce a file in testing — plus real snapshots in a mid-sized project can run into the multi-GB range, which isn't something an AI-agent-callable tool should trigger routinely. So this tool captures a small set of aggregate numbers as its own JSON format instead, stored separately from any real `.snap` files.
- **`frame_debugger_disable` closes every open Frame Debugger window**, not just the one this tool opened — a caveat worth surfacing to an end user if they had one open manually.

## Compatibility

Verified against two Unity versions:

- **Unity 6000.3.13f1** — verified live, via real Unity Editor MCP calls (see [Test Report](#test-report)).
- **Unity 2022.3.62f1c1** (the earliest complete Unity 2022 LTS installation available for testing) — verified offline via a standalone reflection check (no Unity Editor launch required): loading `UnityEditor.CoreModule.dll` / `UnityEngine.CoreModule.dll` from the Editor's `Managed/UnityEngine/` directory with `Assembly.LoadFrom` and confirming every reflection target and public API surface this file depends on (34 checks — types, methods, properties, the `FrameDebuggerInternal` nested namespace, the `ProfilerRecorder(string, string, int)` constructor overload, etc.) resolves identically to 6000.3.13f1. **34/34 passed** — no version-specific branching needed for Unity 2022.3.

This project's `package.json` already declares `"unity": "2022.3"` as the minimum, so this check specifically targeted that floor.

## Test Report

All 13 tools were tested twice, independently:

### 1. Development-time testing (per-tool, via reflection)

During implementation, each tool was compiled and exercised individually against a live running Unity Editor using `execute_code` reflection calls into `ProfilerFunctions`'s static methods (the tools weren't yet registered as named MCP tools mid-session — see [test note](#a-note-on-mcp-tool-discovery) below). This covered:

- `profiler_start` → `profiler_status` → `profiler_stop` full lifecycle, confirming recorder count (9) and clean teardown (0 active recorders after stop).
- `get_frame_timing` sampled over 1–15 frames in both Edit Mode (flagged a real gotcha: `FrameTimingManager` can report a stale multi-hundred-ms sample when the Editor is idle with no continuous repaint) and Play Mode (consistently sane, ~20ms CPU / ~2-3ms GPU).
- `get_counters` default set and a `names` filter including a comma+space edge case (`"Draw Calls Count, Triangles Count"`) — caught and fixed a real bug where the filter didn't trim whitespace before matching.
- `get_object_memory` against a real project texture asset, a real scene GameObject (checking the `Total (incl. all child components)` aggregation), and a nonexistent path (clean error, no exception).
- `memory_take_snapshot` → `memory_list_snapshots` → `memory_compare_snapshots`, allocating ~400MB of `Texture2D` between two snapshots and confirming the delta direction was correct — caught and fixed a real bug where the `ReadCounterOrZero` helper read `ProfilerRecorder.LastValue` (which is `0` for Memory-category recorders) instead of `CurrentValue`. Also caught and fixed a real path-traversal bug where `memory_compare_snapshots`'s `path_a`/`path_b` reached `Path.Combine` unsanitized (`../secret` escaped the snapshot directory) before adding a `Path.GetFileName` guard.
- `frame_debugger_enable` → `frame_debugger_get_events` → `frame_debugger_disable`, with `capture_game_view` screenshots taken before/after `disable` to confirm rendering resumes cleanly. This is where the `FrameDebuggerWindow`-driving requirement (see [Implementation notes](#implementation-notes)) was discovered — the first implementation attempt correctly reported itself as blocked rather than shipping a tool that silently didn't work, which is how the two-type reflection design was found and verified.
- `get_top_memory_objects` in all four modes against a live project in Play Mode: `'All'` per-type summary (8 types, plausible totals); `Texture2D` top list (correctly surfaced editor internals with their `hideFlags` — `GizmoIconAtlas HideAndDontSave` — alongside real project content like 4× 8MB TMP font atlases and per-atlas-named 2MB `sactx-*` sprite atlas pages); `RenderTexture` (URP camera attachments and the GameView RT, with sizes/formats); a bad type name (clean suggestion message listing known types, no exception); and a reflection-fallback type not in the built-in list (`Font` → resolved via assembly scan, listed 2× 16MB CJK fonts).

### 2. Real end-to-end MCP testing (post-merge, via actual named MCP tool calls)

After registering the tools with the MCP server (see the note below), all 13 were re-run **as real first-class MCP tool calls** (not reflection) against a live Unity Editor in Play Mode, back to back:

```
profiler_start → profiler_status (9 recorders, running) → get_frame_timing(5)
  (Avg CPU 25.85ms / Avg GPU 2.77ms / ~38.7 FPS)
→ get_counters() (Render: 0 as documented; Memory: real non-zero values)
→ get_counters(names="Draw Calls Count, Triangles Count") (whitespace-trim fix confirmed)
→ get_object_memory(asset path) → get_object_memory(GameObject path) → get_object_memory(bad path, clean error)
→ memory_take_snapshot("before") → [allocate 60x 1024x1024 Texture2D] → memory_take_snapshot("after")
→ memory_list_snapshots() → memory_compare_snapshots(before, after)
  (Gfx Used Memory: +287.80MB, consistent with ~240MB of raw texture data plus overhead)
→ memory_compare_snapshots(valid, "../../../../etc/hosts") → clean "Snapshot not found", traversal blocked
→ frame_debugger_enable → frame_debugger_get_events(10) (78 real events, incl. object names like "WorldSea1")
→ capture_game_view (clean, no corruption) → frame_debugger_disable → capture_game_view (identical, rendering intact)
→ profiler_stop → profiler_status (0 recorders, clean teardown)
→ get_top_memory_objects("All") (per-type summary: 2125 Texture2D / 310.76MB, 28 RenderTexture / 126.38MB, ...)
→ get_top_memory_objects("Texture2D", 10) (ranked list with WxH/format/hideFlags per object)
```

Every step matched expected behavior. No regressions found in this pass.

### 3. Real-world dogfooding

As a final check, these tools were used to analyze an actual in-development mobile game project (not a toy test scene) — sampling frame timing and memory over ~2 minutes of Play Mode, comparing snapshots across several windows, and cross-referencing `get_counters`'s Render-counter blind spot against `frame_debugger_get_events`'s real event count to confirm it wasn't a bug specific to that project. This surfaced genuinely useful signal (a steadily climbing `Gfx Used Memory` trend worth longer-term monitoring, plus two unrelated console warnings) using nothing but this tool set, which is a reasonable proof that the tools are useful for their intended purpose, not just individually correct.

#### A note on MCP tool discovery

One operational thing worth documenting for anyone testing this locally: after adding a new `[ToolProvider]` class, the running MCP server's tool list did not automatically pick up the new tools for an already-connected AI client, even after `request_recompile` confirmed 0 compile errors and even across multiple domain reloads. Two things were required: (1) in the Funplay **MCP Server → Tool Exposure** panel, the new tools had to be manually checked and **Save**d for the active profile (the panel's "`full` defaults to every registered tool" behavior didn't apply here because the exposure list had been customized away from that default at some earlier point) — this restarts the running server; and (2) the AI client had to reconnect (in this case, restarting the Claude Code session) to redo its `tools/list` handshake against the now-corrected server. This isn't a bug in this PR's code, but it's a real "why can't my AI assistant see the new tool" trap worth a mention in the docs for anyone extending this package.
