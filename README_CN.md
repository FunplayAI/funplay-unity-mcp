<p align="center">
  <h1 align="center">GameBooom MCP For Unity</h1>
  <p align="center">
    <strong>开源 Unity 编辑器 MCP 服务器</strong>
  </p>
  <p align="center">
    <a href="#"><img src="https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity" alt="Unity 6000.0+"></a>
    <a href="#"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPLv3"></a>
    <a href="#"><img src="https://img.shields.io/badge/MCP-Compatible-green" alt="MCP Compatible"></a>
    <a href="#"><img src="https://img.shields.io/badge/Platform-Editor%20Only-orange" alt="Editor Only"></a>
  </p>
  <p align="center">
    中文 | <a href="./README.md">English</a>
  </p>
  <p align="center">
    <img src="./Documentation~/Text%2BLogo.png" alt="The Most Advanced MCP Server for Unity" width="100%">
  </p>
</p>

---

GameBooom MCP For Unity 是一个开源的 Unity 编辑器插件，作为 MCP (Model Context Protocol) 服务器运行，让 Claude Code、Cursor、Windsurf、Codex、VS Code Copilot 等 AI 助手直接与你的 Unity 编辑器交互。

一句话描述你的游戏 — AI 助手通过 GameBooom MCP For Unity 的 77 个内置工具自动创建场景、编写脚本、验证运行态、模拟输入并完成编辑器自动化，把所有逻辑串联起来。

> *"做一个贪吃蛇游戏，10x10 网格，食物随机生成，计分 UI，游戏结束界面"*
>
> AI 助手通过 GameBooom MCP For Unity 全程处理：创建场景、生成全部脚本、搭建 UI、配置游戏逻辑 — 只需一句话。

## 核心特性

- **77 个内置工具** — 覆盖场景编辑、脚本、资产、运行态控制、截图、Prompts 与编辑器自动化，共 18 个模块
- **`execute_code` 主工具** — 适合复杂编辑器/运行态编排的高灵活度 C# 执行工具
- **输入模拟 + 截图验证** — 在 Play Mode 中模拟键盘/鼠标，再用 Game View / Scene View 截图验证结果
- **Resources 与 Prompts** — 暴露实时项目上下文、场景/选择/错误资源、资源模板，以及常见 Unity 工作流的可复用 MCP Prompt
- **MCP Server + MCP Client** — 既能把 Unity 暴露给外部 AI 客户端，也能连接外部 MCP 服务扩展能力
- **反射式工具发现** — 添加自定义工具只需标注 Attribute，无需注册代码
- **厂商无关** — 兼容任意支持 MCP 的 AI 客户端：Claude Code、Cursor、Windsurf、Codex、VS Code Copilot 等

## 开始前说明

- 这是一个 **仅限 Editor** 的包，不会向最终构建产物添加运行时代码。
- MCP Server 默认监听 `http://127.0.0.1:8765/`。
- 插件默认使用 `core` MCP 工具暴露配置，减少 AI 客户端的工具噪音；`core` 当前暴露 17 个高频工具，以 `execute_code`、运行模式控制、输入模拟、截图、日志和编译检查为主。如果你需要完整工具集，可在 MCP Server 窗口切换到 `full`，暴露全部 77 个工具。
- 所有已暴露的 MCP 工具都会直接执行，不再提供额外的 approval 开关。

## 快速开始

### 1. 通过 UPM 安装 (Git URL)

在 Unity 中，打开 **Window → Package Manager → + → Add package from git URL**：

```
https://github.com/GameBooom/unity-mcp.git
```

<details>
<summary>其他方式：通过 OpenUPM 安装</summary>

```bash
openupm add com.gamebooom.unity.mcp
```

</details>

### 2. 启动 MCP Server

**菜单：GameBooom → MCP Server** 启动服务。

默认运行在 `http://127.0.0.1:8765/`。

### 3. 配置 AI 客户端

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

除非你本地 Windsurf 版本要求不同的 MCP 配置格式，否则可直接使用与 Cursor 相同的 JSON 结构。

</details>

### 4. 验证连接

先在 AI 客户端里试几个安全请求：

> “调用 `get_scene_info`，告诉我当前打开的是哪个场景。”

> “读取 `unity://project/context`，总结当前编辑器状态。”

> “调用 `execute_code`，返回当前激活场景名。”

如果这些都正常返回，说明 MCP server、resources 和主执行工具都已经连通。

### 5. 开始构建

打开你的 AI 客户端，试试：*"创建一个 3D 平台跳跃关卡，包含 5 个浮空平台"*

## MCP 能力结构

当前开源包有四层高价值能力：

- **Tools** — `full` 下共 77 个工具，`core` 下 17 个高频工具
- **Primary execution** — `execute_code` 用于复杂编辑器/运行态编排
- **Prompts** — 包括 `fix_compile_errors`、`runtime_validation`、`create_playable_prototype` 等工作流 Prompt
- **Resources** — 项目上下文、场景摘要、选择状态、编译错误、控制台错误、MCP 交互记录，以及按对象/组件/资源路径展开的模板资源

## 内置工具

GameBooom MCP For Unity 当前提供 **77 个工具函数**，覆盖 18 个模块：

| 分类 | 工具 |
|------|------|
| **游戏对象** | `create_primitive`, `create_game_object`, `delete_game_object`, `find_game_objects`, `get_game_object_info`, `set_transform`, `duplicate_game_object`, `rename_game_object`, `set_parent`, `add_component`, `set_tag_and_layer`, `set_active` |
| **层级** | `get_hierarchy` |
| **组件** | `get_component_properties`, `list_components`, `set_component_property`, `set_component_properties` |
| **脚本** | `create_script`, `edit_script`, `patch_script` |
| **资产** | `create_material`, `assign_material`, `find_assets`, `delete_asset`, `rename_asset`, `copy_asset` |
| **文件** | `read_file`, `write_file`, `search_files`, `list_directory`, `exists` |
| **场景** | `get_scene_info`, `list_scenes`, `save_scene`, `open_scene`, `create_new_scene`, `enter_play_mode`, `exit_play_mode`, `set_time_scale`, `get_time_scale` |
| **预制体** | `create_prefab`, `instantiate_prefab`, `unpack_prefab` |
| **UI** | `create_canvas`, `create_button`, `create_text`, `create_image` |
| **动画** | `create_animation_clip`, `create_animator_controller`, `assign_animator` |
| **相机** | `get_camera_properties`, `set_camera_projection`, `set_camera_settings`, `set_camera_culling_mask` |
| **截图** | `capture_game_view`, `capture_scene_view` |
| **脚本执行** | `execute_code` |
| **输入模拟** | `simulate_key_press`, `simulate_key_combo`, `simulate_mouse_click`, `simulate_mouse_drag` |
| **包管理** | `install_package`, `remove_package`, `list_packages` |
| **编译** | `wait_for_compilation`, `request_recompile`, `get_compilation_errors`, `get_reload_recovery_status` |
| **可视化反馈** | `select_object`, `focus_on_object`, `ping_asset`, `log_message`, `show_dialog`, `get_console_logs` |

## 添加自定义工具

通过简单的 Attribute 标注即可创建自定义工具：

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

方法会被自动发现，名称转换为 snake_case（`spawn_enemies`），并通过 MCP 自动生成 JSON Schema 定义暴露给 AI。

## 架构

```
MCP Server (HTTP JSON-RPC 2.0)
    └─ MCPRequestHandler (协议处理)
        └─ MCPExecutionBridge
            └─ FunctionInvokerController (反射式调用)
                └─ Tool Functions (77 个内置工具，18 个模块)
```

```
外部 AI 客户端 → HTTP 请求 → MCPRequestHandler → MCPExecutionBridge → FunctionInvokerController → 工具方法
```

## 环境要求

- Unity 2022.3 或更高版本
- .NET / Mono + `Newtonsoft.Json`

## 参与贡献

欢迎贡献！提交 PR 前请阅读 [贡献指南](CONTRIBUTING.md)。

GameBooom MCP For Unity 采用 GPLv3 许可证，所有衍生作品必须同样以 GPLv3 开源。

## 许可证

[GPLv3](LICENSE) — 可自由使用、修改和分发，衍生作品必须以 GPLv3 开源。
