<p align="center">
  <h1 align="center">Funplay MCP for Unity</h1>
  <p align="center">
    <strong>The Most Advanced MCP Server for Unity Editor</strong>
  </p>
  <p align="center">
    <a href="#"><img src="https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity" alt="Unity 2022.3+"></a>
    <a href="#"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT"></a>
    <a href="#"><img src="https://img.shields.io/badge/MCP-Compatible-green" alt="MCP Compatible"></a>
    <a href="#"><img src="https://img.shields.io/badge/Platform-Editor%20Only-orange" alt="Editor Only"></a>
  </p>
  <p align="center">
    <a href="./README_CN.md">中文</a> | English
  </p>
  <p align="center">
    <img src="./Documentation~/Text%2BLogo.png" alt="The Most Advanced MCP Server for Unity" width="100%">
  </p>
</p>

> 💖 If you find this project useful, please consider giving it a Star. It helps more Unity developers discover it and supports ongoing development.

---

Funplay MCP for Unity is an MIT-licensed Unity Editor MCP server that lets AI assistants like Claude Code, Cursor, LM Studio, Windsurf, Codex, and VS Code Copilot operate directly inside your running Unity project.

Describe your game in one sentence — your AI assistant builds it in Unity through Funplay MCP for Unity's 121 built-in tools for scene creation, script generation, runtime validation, input simulation, performance analysis, and editor automation.

> *"Build a snake game with a 10x10 grid, food spawning, score UI, and game-over screen"*
>
> Your AI assistant handles it through Funplay MCP for Unity: creates the scene, generates all scripts, sets up the UI, and configures the game logic — all from a single prompt.

<p align="center">
  <img src="./Documentation~/demo.gif" alt="Funplay MCP for Unity — 16s demo" width="100%">
</p>
<p align="center"><em>16-second demo — AI generates a 3D model and integrates it into the scene end-to-end. <a href="https://github.com/FunplayAI/funplay-unity-mcp/raw/main/Documentation~/demo.mp4">Watch HD MP4</a>.</em></p>

## Quick Start

If you just want to get connected fast, do these three things:

- Install the Unity package from the Git URL
- Start `Funplay > MCP Server`
- Use the built-in one-click client configuration

### 1. Install via UPM (Git URL)

In Unity, go to **Window → Package Manager → + → Add package from git URL**:

```
https://github.com/FunplayAI/funplay-unity-mcp.git
```

> 💡 Before you clone or install, a quick ⭐ on GitHub would be greatly appreciated.

### Optional: Install via OpenUPM

If you want Unity Package Manager to show registry-backed package version history and allow version selection, install from OpenUPM instead of Git.

Using the OpenUPM CLI:

```bash
openupm add com.gamebooom.unity.mcp
```

Or add the scoped registry manually in `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.gamebooom"
      ]
    }
  ],
  "dependencies": {
    "com.gamebooom.unity.mcp": "0.4.8"
  }
}
```

If you installed from a Git URL before, remove the Git dependency first, then install from OpenUPM. Git-installed packages only show the resolved Git version in Unity and do not get the registry-backed Version History list.

### 2. Start the MCP Server

**Menu: Funplay → MCP Server** to start the server.

The server starts on `http://127.0.0.1:8765/` by default.

Direct in-process HTTP is the default transport. If you need stronger connection continuity across Unity script recompiles or Play Mode domain reloads, enable **Experimental Broker Mode** in the MCP Server window. It runs a tiny local broker with Unity's bundled Mono, keeps the same `127.0.0.1` port for MCP clients, and requires no client config change.

Open **Funplay → Tool Exposure** if you want to edit the exact tools exposed by `core` or `full`.

Open **Funplay → MCP Settings** if you need to adjust `execute_code` safety defaults or plugin debug logging.

### 3. Configure Your AI Client

Use the built-in **One-Click MCP Configuration** in the `Funplay > MCP Server` window first.

Select your target client, click **Configure**, and the package writes the recommended MCP config entry for you.

For Claude Code, Cursor, and Codex, click **Configure + Skills** to also install the default project MCP workflow skill.

If you want project-specific AI guidance for the current Unity project, open **Funplay → Project Skills** to choose supported platforms and install the default `unity-mcp-workflow` skill.

If you prefer to edit config files manually, use the examples below as fallback references:

<details>
<summary>Claude Code / Claude Desktop</summary>

```json
{
  "mcpServers": {
    "funplay": {
      "type": "http",
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>Cursor</summary>

```json
{
  "mcpServers": {
    "funplay": {
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>LM Studio</summary>

LM Studio's `mcp.json` location can vary by version and platform. Prefer **Program > Install > Edit mcp.json** in LM Studio. Funplay's one-click Configure button opens LM Studio's `lmstudio://add_mcp` link and only updates an existing config file if one is already present, instead of creating a guessed path.

```json
{
  "mcpServers": {
    "funplay": {
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>VS Code</summary>

```json
{
  "servers": {
    "funplay": {
      "type": "http",
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>Trae</summary>

```json
{
  "mcpServers": {
    "funplay": {
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>Kiro</summary>

```json
{
  "mcpServers": {
    "funplay": {
      "type": "http",
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```

</details>

<details>
<summary>Codex</summary>

```toml
[mcp_servers.funplay]
url = "http://127.0.0.1:8765/"
```

</details>

<details>
<summary>Windsurf</summary>

Use the same JSON structure as Cursor unless your local Windsurf version requires a different MCP config format.

</details>

### 4. Verify the Connection

Open your AI client and try a few safe requests first:

- "Call `get_scene_info` and tell me what scene is open."
- "Read `unity://project/context` and summarize the current editor state."
- "Use `execute_code` to return the active scene name."

If those work, the MCP server, resources, and primary execution tool are connected correctly.

### 5. Start Building

Open your AI client and try: *"Create a 3D platformer level with 5 floating platforms"*

## Before You Start

- This package is **Editor-only**. It does not add runtime components to your built game.
- The MCP server starts on `http://127.0.0.1:8765/` by default.
- Local MCP server settings are stored in `UserSettings/FunplayMcpSettings.json`.
- The package defaults to the `core` MCP tool profile to reduce tool-list noise for AI clients. `core` currently exposes 30 high-signal tools centered on `execute_code`, play mode control, input simulation, screenshots, performance inspection, logs, compilation checks, structured object location and component editing, editor selection / prefab-stage state, and `execute_menu_item` as a low-friction fallback. Switch to `full` in the MCP Server window if you want all 121 tools exposed.
- `execute_code` safety checks and the stricter filesystem guard are enabled by default from **Funplay > MCP Settings**. The guard blocks obvious destructive snippets, broad `System.IO` writes, raw file streams, and absolute/user/system/traversal paths, but it is not a complete sandbox. Clients may still override the default per call with the optional `safety_checks` argument.
- Plugin debug logging is off by default and can also be enabled from **Funplay > MCP Settings**. Warnings and errors are always written to the Unity Console.
- All exposed MCP tools run directly. There is no extra approval toggle.
- **Menu: `Funplay > Check for Updates`** can refresh Git installs in place or download and import the latest `unitypackage` automatically.

## Why This Project

- **`execute_code` First** — Optimized around one in-memory C# execution tool for rich editor/runtime orchestration. See [`execute_code`: In-Memory C# Execution](#execute_code-in-memory-c-execution) below for details.
- **Default Safety Checks** — `execute_code` now has persistent default-on safety toggles, including a stricter filesystem guard for clients that do not expose per-call arguments clearly
- **Play Mode Automation** — Enter play mode, simulate keyboard/mouse input, capture screenshots, inspect logs, and validate behavior from the same MCP session
- **Project Context Built In** — Exposes live resources for project state, active scene, selection, compilation, console output, and MCP interaction history
- **Focused by Default, Full When Needed** — `core` exposes a compact high-signal toolset; `full` exposes all 121 tools
- **Single Unity Package** — No extra approval UI, no external daemon to click through, and no Python requirement for the Unity-side plugin itself
- **Extensible** — Add custom tools with attribute-based discovery, or connect Unity to external MCP services when needed

## Highlights

- **121 Built-in Tools** — Scene editing, assets, scripts, play mode control, screenshots, performance analysis, prompts, resources, structured object location, SerializedObject-based component editing, editor-state inspection, menu-item fallback, and editor automation across 23 modules
- **Structured Returns + `instanceId` Chaining** — Tools return `{success, message, data}` JSON with stable `instanceId` fields so agents can chain `by_id` calls reliably instead of re-resolving by name
- **`IFunplayCommand` for `execute_code`** — New snippet template with auto-Undo (`ctx.RegisterObjectCreation/Modification/DestroyObject`), structured logs (`ctx.Log/LogWarning/LogError`), and a tracked changelog returned to the agent
- **Resources & Prompts** — Live project context, scene/selection/error resources, resource templates, and reusable workflow prompts
- **Input Simulation + Screenshots** — Drive play mode with keyboard/mouse simulation and verify results with game/scene captures
- **Built-in Updating** — Check for updates from the Unity menu and either re-pull the Git package or auto-import the latest `unitypackage`
- **One-Click Client Configuration** — Generate MCP config entries for Claude Code, Cursor, LM Studio, VS Code, Kiro, Trae, Codex, and similar clients directly from the Unity window
- **Tool Exposure Control** — Edit the exact tools exposed by `core` and `full`
- **Project Skills Manager** — Configure project-level skills for supported AI clients, currently installing the default `unity-mcp-workflow` skill
- **MCP Settings** — Adjust `execute_code` safety defaults and enable verbose plugin debug logging when troubleshooting MCP connections or tool execution
- **Vendor Agnostic** — Works with any AI client that supports MCP: Claude Code, Cursor, LM Studio, Windsurf, Codex, VS Code Copilot, etc.

## `execute_code`: In-Memory C# Execution

`execute_code` is the heart of Funplay MCP for Unity. It lets an AI write a C# snippet, compile it through a Roslyn-first in-memory flow, and run it on the editor thread — the agent gets the full Unity Editor and runtime API surface without writing any project files to disk.

- **Zero project footprint compilation** — Snippets are compiled with Unity's bundled Roslyn csc first while preserving the in-memory compilation/execution flow. No `.cs` files are written under `Assets/`, no domain reload is triggered, no project state is touched beyond what the snippet itself does.
- **Editor-ready before it runs** — Each call refreshes the AssetDatabase and waits for any pending compilation to settle before compiling the snippet, so external file edits are picked up automatically without a separate `request_recompile`.
- **Auto-Undo + structured logs (recommended template)** — Implement `IFunplayCommand` and use the injected `ExecutionContext` so every created / modified / destroyed object participates in editor Undo, and the changelog is returned to the agent.

```csharp
using UnityEngine;
using UnityEditor;
using Funplay.Editor.Tools.Helpers;
using Funplay.Editor.Tools.Scripting;

public class CommandScript : IFunplayCommand
{
    public void Execute(ExecutionContext ctx)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ctx.RegisterObjectCreation(go);          // auto-Undo + tracked
        ctx.Log("Created {0}", go.name);
        ctx.ReturnValue = GameObjectSerializer.Describe(go, includeComponents: false);
    }
}
```

The response carries `{ logs, created, modified, destroyed, returnValue }`, so the agent can verify exactly what changed without re-querying the scene.

The legacy template (`public static string Run()`) is still supported — useful for one-off inspection snippets where structured tracking is overkill.

**When to reach for `execute_code` vs a specialized tool** — `execute_code` shines for multi-step orchestration, novel reads, and situations where chaining 5–10 narrow tool calls would be noisier than one snippet. For single-field component edits, simple selection changes, or anything covered by an existing tool, prefer the dedicated tool — it is cheaper for the LLM to call and easier to verify.

## Comparison With Coplay

The table below compares this repository with the publicly documented behavior of Coplay's open-source `unity-mcp` repository on GitHub.

| Area | Funplay MCP for Unity | Coplay `unity-mcp` |
|------|--------------------------|--------------------|
| Unity-side architecture | Embedded Unity Editor package with built-in HTTP MCP server | Unity bridge plus local Python MCP server |
| Extra local prerequisites | Unity package only for core workflows | Unity + Python 3.10+ + `uv` according to the public quick start |
| Primary workflow style | `execute_code` first, then focused helper tools | Broad `manage_*` tool families exposed through the bridge |
| Default tool exposure | Compact `core` profile with optional `full` expansion | Public docs emphasize a broad always-available tool surface |
| Built-in context model | Project resources, resource templates, workflow prompts, interaction history | Public README emphasizes tool families and bridge/server workflow |
| Play mode validation | Built-in play mode control, screenshots, logs, and input simulation in the package | Public README emphasizes broad Unity management and automation tools |
| Positioning | Lightweight, direct, MIT-licensed Unity MCP server for AI-driven editor control | Full-featured Unity bridge maintained by Coplay with Python-backed server setup |

Source for Coplay column: [CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp)

## Comparison With Unity AI Assistant

The table below compares this repository with Unity Technologies' official `com.unity.ai.assistant` package (v2.7.0-pre.2 as of 2026-05).

| Area | Funplay MCP for Unity | Unity AI Assistant |
|------|--------------------------|--------------------|
| Minimum Unity version | 2022.3 | 6000.3 (Unity 6 only) |
| License | MIT, open source | Unity Terms of Service, proprietary |
| Deployment | Local HTTP MCP server in Editor, no cloud | Editor + native Relay subprocess + Unity Cloud backend |
| Billing | Free, user brings their own AI client | Credits-based (Unity Dashboard) |
| Tool exposure | 121 tools across 23 modules, `core` (30) / `full` profiles | ~15 MCP tools (mostly `Manage*` families) |
| Generic escape hatch | `execute_code` — Roslyn-first in-memory compile, `IFunplayCommand` + Undo, no sandbox (client-side approval) | `RunCommand` — namespace blacklist sandbox |
| Play mode validation | Full loop: enter / simulate input / capture / read logs / exit | Enter/Exit only; no input simulation |
| Asset generators | Not built-in (compose external APIs via `execute_code`) | Native Image / Mesh / PBR / Sound / Animation generators |
| Primary client model | BYO any MCP client (Claude Code / Cursor / LM Studio / Codex / VS Code) | Built-in chat window + ACP for Claude/Gemini via Gateway |
| Offline-capable | Yes for tool calls (inference depends on chosen client) | No (inference requires Unity Cloud) |

For a long-form comparison of the two approaches see [Funplay Unity MCP vs Unity AI Assistant detailed comparison](https://blog.csdn.net/m0_62670368/article/details/161039766) (Chinese).

## MCP Capabilities

The current open-source package exposes four high-value capability layers:

- **Tools** — 121 total tools in `full`, 30 focused tools in `core`
- **Primary execution** — `execute_code` for rich editor/runtime orchestration
- **Prompts** — workflow prompts like `fix_compile_errors`, `runtime_validation`, and `create_playable_prototype`
- **Resources** — project context, scene summaries, selection state, compile errors, console errors, MCP interaction history, plus resource templates for scene objects, components, and asset paths

## Built-in Tools

Funplay MCP for Unity currently ships with **121 tool functions** across 23 modules:

| Category | Tools |
|----------|-------|
| **GameObject** | `create_primitive`, `create_game_object`, `delete_game_object`, `find_game_objects`, `get_game_object_info`, `set_transform`, `duplicate_game_object`, `rename_game_object`, `set_parent`, `add_component`, `set_tag_and_layer`, `set_active` |
| **Hierarchy** | `get_hierarchy` |
| **Components** | `get_component_properties`, `list_components`, `set_component_property`, `set_component_properties` |
| **Scripts** | `create_script`, `edit_script`, `patch_script` |
| **Assets** | `create_material`, `assign_material`, `find_assets`, `delete_asset`, `rename_asset`, `copy_asset` |
| **Files** | `read_file`, `write_file`, `search_files`, `list_directory`, `exists` |
| **Scene** | `get_scene_info`, `list_scenes`, `save_scene`, `open_scene`, `create_new_scene`, `enter_play_mode`, `exit_play_mode`, `set_time_scale`, `get_time_scale` |
| **Prefabs** | `create_prefab`, `instantiate_prefab`, `unpack_prefab`, `open_prefab_stage`, `save_prefab_stage`, `close_prefab_stage` |
| **ScriptableObject** | `create_scriptable_object`, `get_scriptable_object`, `set_scriptable_object_properties` |
| **UI** | `create_canvas`, `create_button`, `create_text`, `create_image` |
| **Animation** | `create_animation_clip`, `create_animator_controller`, `assign_animator`, `get_animator_state`, `set_animator_parameter`, `play_animator_state` |
| **Camera** | `get_camera_properties`, `set_camera_projection`, `set_camera_settings`, `set_camera_culling_mask` |
| **Screenshot** | `capture_game_view`, `capture_simulator_view`, `capture_scene_view`, `capture_multiview` |
| **Script Execution** | `execute_code`, `get_execute_code_history`, `replay_execute_code`, `clear_execute_code_history` |
| **Input Simulation** | `simulate_key_press`, `simulate_key_combo`, `simulate_mouse_click`, `simulate_mouse_drag` |
| **Performance** | `get_performance_snapshot`, `analyze_scene_complexity` |
| **Profiler** | `profiler_start`, `profiler_stop`, `profiler_status`, `get_frame_timing`, `get_counters`, `get_object_memory`, `get_top_memory_objects`, `memory_take_snapshot`, `memory_list_snapshots`, `memory_compare_snapshots`, `frame_debugger_enable`, `frame_debugger_disable`, `frame_debugger_get_events` |
| **Packages** | `install_package`, `remove_package`, `list_packages` |
| **Compilation** | `wait_for_compilation`, `request_recompile`, `get_compilation_errors`, `get_reload_recovery_status` |
| **Testing** | `run_tests`, `get_test_job`, `cancel_test_run` |
| **Editor State** | `get_editor_state`, `get_selection`, `set_selection`, `get_prefab_stage`, `get_active_tool`, `set_active_tool`, `get_windows`, `get_tags`, `add_tag`, `remove_tag`, `get_layers`, `add_layer`, `get_build_settings` |
| **Menu Items** | `execute_menu_item`, `validate_menu_item` |
| **Visual Feedback** | `select_object`, `focus_on_object`, `ping_asset`, `log_message`, `show_dialog`, `get_console_logs` |

> 📊 See [PROFILER_TOOLS.md](PROFILER_TOOLS.md) for the full Profiler tool reference, implementation notes, known limitations, and test report.

## Adding Custom Tools

Create your own tools with simple attribute annotations:

```csharp
using System.ComponentModel;

[ToolProvider("MyTools")]
public static class MyCustomTools
{
    [Description("Spawns enemies at random positions in the scene")]
    public static string SpawnEnemies(
        [ToolParam("Number of enemies to spawn", Required = true)] int count,
        [ToolParam("Prefab path in Assets")] string prefabPath)
    {
        // Your implementation here
        return $"Spawned {count} enemies";
    }
}
```

Methods are automatically discovered, converted to snake_case (`spawn_enemies`), and exposed via MCP with JSON Schema definitions.

## Architecture

```
MCP Server (HTTP JSON-RPC 2.0)
    └─ MCPRequestHandler (protocol handling)
        └─ MCPExecutionBridge
            └─ FunctionInvokerController (reflection-based invocation)
                └─ Tool Functions (121 built-in tools across 23 modules)
```

```
External AI Client → HTTP Request → MCPRequestHandler → MCPExecutionBridge → FunctionInvokerController → tool method
```

## Requirements

- Unity 2022.3 or later
- .NET / Mono with `Newtonsoft.Json`

## Contributing

Contributions are welcome! Please read the [Contributing Guide](CONTRIBUTING.md) before submitting a PR.

## License

[MIT](LICENSE) — Free to use, modify, distribute, and integrate into commercial or open-source projects.
