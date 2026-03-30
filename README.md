<p align="center">
  <h1 align="center">GameBooom MCP For Unity</h1>
  <p align="center">
    <strong>Open-Source MCP Server for Unity Editor</strong>
  </p>
  <p align="center">
    <a href="#"><img src="https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity" alt="Unity 6000.0+"></a>
    <a href="#"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPLv3"></a>
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

---

GameBooom MCP For Unity is an open-source Unity Editor plugin that acts as an MCP (Model Context Protocol) server, allowing AI assistants like Claude Code, Cursor, Windsurf, Codex, and VS Code Copilot to interact directly with your Unity Editor.

Describe your game in one sentence — your AI assistant builds it in Unity through GameBooom MCP For Unity's 77 built-in tools for scene creation, script generation, runtime validation, input simulation, and editor automation.

> *"Build a snake game with a 10x10 grid, food spawning, score UI, and game-over screen"*
>
> Your AI assistant handles it through GameBooom MCP For Unity: creates the scene, generates all scripts, sets up the UI, and configures the game logic — all from a single prompt.

## Highlights

- **77 Built-in Tools** — Scene editing, assets, scripts, play mode control, screenshots, prompts, and editor automation across 18 modules
- **`execute_code` First Workflow** — A high-flexibility C# execution tool for rich editor/runtime orchestration when many small tools would be noisy
- **Input Simulation + Screenshots** — Drive play mode with keyboard/mouse simulation and verify results with game/scene captures
- **Resources & Prompts** — Exposes live project context, scene/selection/error resources, resource templates, and reusable MCP prompts
- **MCP Server + MCP Client** — Expose Unity to external AI clients and connect Unity to external MCP servers when needed
- **Reflection-Based Tool Discovery** — Add custom tools by annotating public static classes and methods
- **Vendor Agnostic** — Works with any AI client that supports MCP: Claude Code, Cursor, Windsurf, Codex, VS Code Copilot, etc.

## Before You Start

- This package is **Editor-only**. It does not add runtime components to your built game.
- The MCP server listens on `http://127.0.0.1:8765/` by default.
- The package defaults to the `core` MCP tool profile to reduce tool-list noise for AI clients. `core` currently exposes 17 high-signal tools centered on `execute_code`, play mode control, input simulation, screenshots, logs, and compilation checks. Switch to `full` in the MCP Server window if you want all 77 tools exposed.
- All exposed MCP tools run directly. There is no extra approval toggle.

## Quick Start

### 1. Install via UPM (Git URL)

In Unity, go to **Window → Package Manager → + → Add package from git URL**:

```
https://github.com/GameBooom/unity-mcp.git
```

<details>
<summary>Alternative: Install via OpenUPM</summary>

```bash
openupm add com.gamebooom.unity.mcp
```

</details>

### 2. Start the MCP Server

**Menu: GameBooom → MCP Server** to start the server.

The server runs on `http://127.0.0.1:8765/` by default.

### 3. Configure Your AI Client

<details>
<summary>Claude Code / Claude Desktop</summary>

```json
{
  "mcpServers": {
    "gamebooom": {
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
    "gamebooom": {
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
    "gamebooom": {
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
    "gamebooom": {
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
    "gamebooom": {
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
[mcp_servers.gamebooom]
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

## MCP Capabilities

The current open-source package exposes four high-value capability layers:

- **Tools** — 77 total tools in `full`, 17 focused tools in `core`
- **Primary execution** — `execute_code` for rich editor/runtime orchestration
- **Prompts** — workflow prompts like `fix_compile_errors`, `runtime_validation`, and `create_playable_prototype`
- **Resources** — project context, scene summaries, selection state, compile errors, console errors, MCP interaction history, plus resource templates for scene objects, components, and asset paths

## Built-in Tools

GameBooom MCP For Unity currently ships with **77 tool functions** across 18 modules:

| Category | Tools |
|----------|-------|
| **GameObject** | `create_primitive`, `create_game_object`, `delete_game_object`, `find_game_objects`, `get_game_object_info`, `set_transform`, `duplicate_game_object`, `rename_game_object`, `set_parent`, `add_component`, `set_tag_and_layer`, `set_active` |
| **Hierarchy** | `get_hierarchy` |
| **Components** | `get_component_properties`, `list_components`, `set_component_property`, `set_component_properties` |
| **Scripts** | `create_script`, `edit_script`, `patch_script` |
| **Assets** | `create_material`, `assign_material`, `find_assets`, `delete_asset`, `rename_asset`, `copy_asset` |
| **Files** | `read_file`, `write_file`, `search_files`, `list_directory`, `exists` |
| **Scene** | `get_scene_info`, `list_scenes`, `save_scene`, `open_scene`, `create_new_scene`, `enter_play_mode`, `exit_play_mode`, `set_time_scale`, `get_time_scale` |
| **Prefabs** | `create_prefab`, `instantiate_prefab`, `unpack_prefab` |
| **UI** | `create_canvas`, `create_button`, `create_text`, `create_image` |
| **Animation** | `create_animation_clip`, `create_animator_controller`, `assign_animator` |
| **Camera** | `get_camera_properties`, `set_camera_projection`, `set_camera_settings`, `set_camera_culling_mask` |
| **Screenshot** | `capture_game_view`, `capture_scene_view` |
| **Script Execution** | `execute_code` |
| **Input Simulation** | `simulate_key_press`, `simulate_key_combo`, `simulate_mouse_click`, `simulate_mouse_drag` |
| **Packages** | `install_package`, `remove_package`, `list_packages` |
| **Compilation** | `wait_for_compilation`, `request_recompile`, `get_compilation_errors`, `get_reload_recovery_status` |
| **Visual Feedback** | `select_object`, `focus_on_object`, `ping_asset`, `log_message`, `show_dialog`, `get_console_logs` |

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
                └─ Tool Functions (77 built-in tools across 18 modules)
```

```
External AI Client → HTTP Request → MCPRequestHandler → MCPExecutionBridge → FunctionInvokerController → tool method
```

## Requirements

- Unity 2022.3 or later
- .NET / Mono with `Newtonsoft.Json`

## Contributing

Contributions are welcome! Please read the [Contributing Guide](CONTRIBUTING.md) before submitting a PR.

Since GameBooom MCP For Unity is licensed under GPLv3, all derivative works must also be open-sourced under the same license.

## License

[GPLv3](LICENSE) — Free to use, modify, and distribute. Any derivative work must be open-sourced under GPLv3.
