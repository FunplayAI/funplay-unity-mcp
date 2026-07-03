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
    中文 | <a href="./README.md">English</a>
  </p>
  <p align="center">
    <img src="./Documentation~/Text%2BLogo.png" alt="The Most Advanced MCP Server for Unity" width="100%">
  </p>
</p>

> 💖 如果这个项目对你有帮助，欢迎顺手点一个 Star。它能帮助更多 Unity 开发者发现这个项目，也能支持后续持续维护。

---

Funplay MCP for Unity 是一个采用 MIT 协议的 Unity 编辑器 MCP 服务器，让 Claude Code、Cursor、LM Studio、Windsurf、Codex、VS Code Copilot 等 AI 助手直接操作正在运行的 Unity 项目。

一句话描述你的游戏 — AI 助手通过 Funplay MCP for Unity 的 121 个内置工具自动创建场景、编写脚本、验证运行态、模拟输入、分析性能并完成编辑器自动化，把所有逻辑串联起来。

> *"做一个贪吃蛇游戏，10x10 网格，食物随机生成，计分 UI，游戏结束界面"*
>
> AI 助手通过 Funplay MCP for Unity 全程处理：创建场景、生成全部脚本、搭建 UI、配置游戏逻辑 — 只需一句话。

<p align="center">
  <img src="./Documentation~/demo.gif" alt="Funplay MCP for Unity — 16 秒 demo" width="100%">
</p>
<p align="center"><em>16 秒 demo — AI 生成 3D 模型并端到端集成进场景。<a href="https://github.com/FunplayAI/funplay-unity-mcp/raw/main/Documentation~/demo.mp4">观看高清 MP4</a>。</em></p>

## 快速开始

如果你只想尽快跑起来，先做这三步：

- 用 Git URL 安装 Unity 包
- 打开 `Funplay > MCP Server`
- 使用内置的一键客户端配置

### 1. 通过 UPM 安装 (Git URL)

在 Unity 中，打开 **Window → Package Manager → + → Add package from git URL**：

```
https://github.com/FunplayAI/funplay-unity-mcp.git
```

> 💡 在 clone 或安装之前，如果你愿意顺手点一个 ⭐，会非常感谢。

### 可选方案：通过 OpenUPM 安装

如果你希望 Unity Package Manager 显示 registry 提供的完整“版本历史记录”并能选择历史版本，可以改用 OpenUPM 安装。

使用 OpenUPM CLI：

```bash
openupm add com.gamebooom.unity.mcp
```

或者手动在 `Packages/manifest.json` 中添加 scoped registry：

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

如果之前是用 Git URL 安装的，先移除 Git dependency，再从 OpenUPM 安装。Git 来源的包在 Unity 中只会显示当前解析到的 Git 版本，不会显示 registry 提供的完整 Version History。

### 2. 启动 MCP Server

**菜单：Funplay → MCP Server** 启动服务。

默认从 `http://127.0.0.1:8765/` 启动。

默认传输仍然是进程内 Direct HTTP。如果你需要在 Unity 脚本重编译或进入 Play Mode 触发域重载时尽量保持 MCP 客户端连接，可以在 MCP Server 窗口启用 **Experimental Broker Mode**。它会用 Unity 自带 Mono 启动一个很小的本地 broker，客户端仍然连接同一个 `127.0.0.1` 端口，不需要改 MCP 配置。

如果你想编辑 `core` 或 `full` 各自暴露哪些工具，可以打开 **Funplay → Tool Exposure**。

如果需要调整 `execute_code` 安全默认值或插件 debug 日志，可以打开 **Funplay → MCP Settings**。

### 3. 配置 AI 客户端

优先使用 `Funplay > MCP Server` 窗口里的 **一键 MCP 配置**。

选择目标客户端后点击 **Configure**，插件会直接帮你写入推荐的 MCP 配置项。

对于 Claude Code、Cursor 和 Codex，也可以点击 **Configure + Skills**，同时安装默认的项目级 MCP 工作流 skill。

如果你希望为当前 Unity 项目配置项目级 AI 指引，可以打开 **Funplay → Project Skills**，为支持的平台安装默认的 `unity-mcp-workflow` skill。

如果你更想手动编辑配置文件，再参考下面这些示例：

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

LM Studio 的 `mcp.json` 路径会随版本和平台变化。建议优先在 LM Studio 中通过 **Program > Install > Edit mcp.json** 打开当前生效的配置文件。Funplay 的一键 Configure 会打开 LM Studio 官方 `lmstudio://add_mcp` 链接，并且只在发现已有配置文件时顺手更新它，不会创建一个猜测出来的路径。

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

## 开始前说明

- 这是一个 **仅限 Editor** 的包，不会向最终构建产物添加运行时代码。
- MCP Server 默认从 `http://127.0.0.1:8765/` 启动。
- 本地 MCP Server 配置保存在 `UserSettings/FunplayMcpSettings.json`。
- 插件默认使用 `core` MCP 工具暴露配置，减少 AI 客户端的工具噪音；`core` 当前暴露 30 个高频工具，覆盖 `execute_code`、运行模式控制、输入模拟、截图、性能检查、日志、编译检查、结构化对象定位与组件编辑、编辑器选中与 prefab stage 状态读写，以及 `execute_menu_item` 兜底入口。如果你需要完整工具集，可在 MCP Server 窗口切换到 `full`，暴露全部 121 个工具。
- `execute_code` safety checks 和更严格的文件系统 guard 现在可在 **Funplay > MCP Settings** 设置默认值，默认开启；它会阻止明显破坏性片段、宽泛的 `System.IO` 写入、原始文件流、绝对路径、用户/系统目录路径和 `../` 穿越路径，但它不是完整沙箱。客户端仍可在单次调用中用可选 `safety_checks` 参数显式覆盖。
- 插件 debug 日志默认关闭，也可在 **Funplay > MCP Settings** 中开启；Warning 和 Error 始终会输出到 Unity Console。
- 所有已暴露的 MCP 工具都会直接执行，不再提供额外的 approval 开关。
- **菜单：`Funplay > Check for Updates`** 可按安装来源自动更新：Git 安装会直接重新拉取，`.unitypackage` 导入会自动下载并导入最新版。

## 能力概览

- **`execute_code` 主工具优先** — 核心体验围绕一个内存 C# 执行工具构建，适合复杂编辑器/运行态编排。详见下方 [`execute_code`：内存 C# 执行](#execute_code内存-c-执行)。
- **默认安全检查** — `execute_code` 现在有持久化、默认开启的 safety toggle，并包含更严格的文件系统 guard，适合 LM Studio 这类不明显暴露单次参数的客户端
- **Play Mode 自动化闭环** — 进入运行模式、模拟键鼠输入、截图、查看日志、验证行为都能在同一 MCP 会话里完成
- **内建项目上下文** — 直接提供项目状态、当前场景、选择对象、编译错误、控制台输出和 MCP 交互记录资源
- **默认聚焦，必要时全量** — 默认 `core` 工具集更利于 AI 选工具，需要时可切到 `full` 暴露全部 121 个工具
- **单 Unity 包落地** — 不需要额外 approval 开关，Unity 侧也不依赖单独 Python 守护进程
- **可扩展** — 支持 Attribute 发现自定义工具，也支持连接外部 MCP 服务

## 核心特性

- **121 个内置工具** — 覆盖场景编辑、脚本、资产、运行态控制、截图、性能分析、Prompts、Resources、结构化对象定位、SerializedObject 组件编辑、编辑器状态读写、菜单项兜底以及编辑器自动化，共 23 个模块
- **结构化返回 + `instanceId` 链式调用** — 工具返回 `{success, message, data}` JSON 并附带稳定的 `instanceId`，agent 后续直接 `by_id` 调用，不再受重名困扰
- **`execute_code` 的 `IFunplayCommand` 模板** — 新模板自动 Undo（`ctx.RegisterObjectCreation/Modification/DestroyObject`）、结构化日志（`ctx.Log/LogWarning/LogError`），并把改动列表回传给 agent
- **Resources 与 Prompts** — 暴露实时项目上下文、场景/选择/错误资源、资源模板，以及常见 Unity 工作流的可复用 MCP Prompt
- **输入模拟 + 截图验证** — 在 Play Mode 中模拟键盘/鼠标，再用 Game View / Scene View 截图验证结果
- **内置更新** — 直接在 Unity 菜单中检查更新，并根据安装方式自动重新拉取 Git 包或导入最新 `unitypackage`
- **一键客户端配置** — 直接在 Unity 窗口里为 Claude Code、Cursor、LM Studio、VS Code、Kiro、Trae、Codex 等客户端生成 MCP 配置
- **工具暴露控制** — 编辑 `core` 和 `full` 各自暴露的具体工具
- **项目 Skills 管理器** — 为支持的 AI 客户端配置项目级 skills，目前安装默认的 `unity-mcp-workflow` skill
- **插件设置** — 排查 MCP 连接或工具执行问题时，可开关详细 debug 日志
- **厂商无关** — 兼容任意支持 MCP 的 AI 客户端：Claude Code、Cursor、LM Studio、Windsurf、Codex、VS Code Copilot 等

## `execute_code`：内存 C# 执行

`execute_code` 是 Funplay MCP for Unity 的核心工具。AI 写一段 C#，通过 Roslyn 优先的内存编译流程完成编译，并在编辑器线程直接执行——agent 拿到 Unity Editor 与 Runtime 的全套 API，但完全不需要往项目里写文件。

- **零项目落盘编译** —— 优先使用 Unity 自带 Roslyn csc 编译，同时保留内存编译/内存执行流程。`Assets/` 下不会多出 `.cs` 文件，不会触发 domain reload，除非 snippet 自己显式改，否则项目状态不动。
- **运行前自动就绪** —— 每次调用都会先刷新 AssetDatabase 并等待 pending compilation 完成，外部文件编辑会被自动拾取，不需要额外 `request_recompile`。
- **自动 Undo + 结构化日志（推荐模板）** —— 实现 `IFunplayCommand`，用注入的 `ExecutionContext`：所有新建/修改/销毁的对象都自动进 editor Undo，改动列表也会回传给 agent。

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
        ctx.RegisterObjectCreation(go);          // 自动 Undo + 追踪
        ctx.Log("Created {0}", go.name);
        ctx.ReturnValue = GameObjectSerializer.Describe(go, includeComponents: false);
    }
}
```

返回里带 `{ logs, created, modified, destroyed, returnValue }`，agent 不用再回查场景就能确认改动。

旧模板（`public static string Run()`）仍然兼容，适合一次性 inspection snippet——不需要结构化追踪的场景。

**什么时候用 `execute_code` vs 专门工具** —— `execute_code` 适合多步编排、新颖查询、或者会被拆成 5-10 个细粒度调用的场景，一段 snippet 比一连串小工具更省。要是单字段组件修改、简单选中切换，或者已有专门工具能搞定的，优先用专门工具——对 LLM 调用成本更低、验证更直接。

## 与 Coplay 的对比

下表基于 Coplay 官方公开 GitHub README 所描述的能力与安装方式进行对比。

| 维度 | Funplay MCP for Unity | Coplay `unity-mcp` |
|------|-------------------------|--------------------|
| Unity 侧架构 | Unity 包内置 HTTP MCP server | Unity bridge + 本地 Python MCP server |
| 额外本地依赖 | `core` 工作流下只需要 Unity 包本身 | 官方 quick start 要求 Python 3.10+ 与 `uv` |
| 主要交互模型 | 以 `execute_code` 为主，再配合少量高频辅助工具 | 以大量 `manage_*` 工具族为主 |
| 默认工具暴露 | 默认 `core` 精简工具集，可切 `full` | 公开文档强调广泛工具面 |
| 上下文能力 | 内建项目资源、资源模板、工作流 prompts、交互历史 | 公开 README 主要强调 bridge/server 与工具族 |
| Play Mode 验证 | 包内置运行模式控制、截图、日志、输入模拟 | 公开 README 强调广泛 Unity 管理与自动化能力 |
| 定位 | 轻量、直接、MIT 协议的 Unity MCP 服务器 | Coplay 维护的全功能 Unity bridge 方案 |

Coplay 信息来源：[CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp)

## 与 Unity AI Assistant 的对比

下表对比本仓库与 Unity Technologies 官方包 `com.unity.ai.assistant`（2026-05 时点 v2.7.0-pre.2）。

| 维度 | Funplay MCP for Unity | Unity AI Assistant |
|------|-------------------------|--------------------|
| 最低 Unity 版本 | 2022.3 | 6000.3（仅 Unity 6）|
| 协议 / License | MIT 开源 | Unity Terms of Service，私有 |
| 部署 | Editor 内嵌 HTTP MCP server，纯本地 | Editor + 原生 Relay 子进程 + Unity Cloud 后端 |
| 计费 | 免费，用户自带 AI 客户端 | Credits 点数制（Unity Dashboard）|
| 工具暴露 | 121 工具 / 23 模块，`core` (30) / `full` profile | ~15 个 MCP 工具（多数为 `Manage*` 大粒度族）|
| 通用逃生口 | `execute_code` — Roslyn 优先内存编译、`IFunplayCommand` + Undo、无沙箱（客户端层审批）| `RunCommand` — 命名空间黑名单沙箱 |
| Play Mode 验证 | 完整闭环：进入 / 模拟输入 / 截图 / 读日志 / 退出 | 仅进入/退出，无输入模拟 |
| 资产生成器 | 不内建（通过 `execute_code` 组合外部 API）| 内建 Image / Mesh / PBR / Sound / Animation 五类生成器 |
| 主要客户端模型 | BYO 任意 MCP 客户端（Claude Code / Cursor / LM Studio / Codex / VS Code）| 自带对话窗口 + ACP 经 Gateway 接 Claude/Gemini |
| 离线可用 | ✅ 工具调用本身全本地（推理依赖所选客户端）| ❌ 推理必须连 Unity Cloud |

长文对比见 [Funplay Unity MCP 与 Unity AI Assistant 详细对比](https://blog.csdn.net/m0_62670368/article/details/161039766)。

## MCP 能力结构

当前开源包有四层高价值能力：

- **Tools** — `full` 下共 121 个工具，`core` 下 30 个高频工具
- **Primary execution** — `execute_code` 用于复杂编辑器/运行态编排
- **Prompts** — 包括 `fix_compile_errors`、`runtime_validation`、`create_playable_prototype` 等工作流 Prompt
- **Resources** — 项目上下文、场景摘要、选择状态、编译错误、控制台错误、MCP 交互记录，以及按对象/组件/资源路径展开的模板资源

## 内置工具

Funplay MCP for Unity 当前提供 **121 个工具函数**，覆盖 23 个模块：

| 分类 | 工具 |
|------|------|
| **游戏对象** | `create_primitive`, `create_game_object`, `delete_game_object`, `find_game_objects`, `get_game_object_info`, `set_transform`, `duplicate_game_object`, `rename_game_object`, `set_parent`, `add_component`, `set_tag_and_layer`, `set_active` |
| **层级** | `get_hierarchy` |
| **组件** | `get_component_properties`, `list_components`, `set_component_property`, `set_component_properties` |
| **脚本** | `create_script`, `edit_script`, `patch_script` |
| **资产** | `create_material`, `assign_material`, `find_assets`, `delete_asset`, `rename_asset`, `copy_asset` |
| **文件** | `read_file`, `write_file`, `search_files`, `list_directory`, `exists` |
| **场景** | `get_scene_info`, `list_scenes`, `save_scene`, `open_scene`, `create_new_scene`, `enter_play_mode`, `exit_play_mode`, `set_time_scale`, `get_time_scale` |
| **预制体** | `create_prefab`, `instantiate_prefab`, `unpack_prefab`, `open_prefab_stage`, `save_prefab_stage`, `close_prefab_stage` |
| **ScriptableObject** | `create_scriptable_object`, `get_scriptable_object`, `set_scriptable_object_properties` |
| **UI** | `create_canvas`, `create_button`, `create_text`, `create_image` |
| **动画** | `create_animation_clip`, `create_animator_controller`, `assign_animator`, `get_animator_state`, `set_animator_parameter`, `play_animator_state` |
| **相机** | `get_camera_properties`, `set_camera_projection`, `set_camera_settings`, `set_camera_culling_mask` |
| **截图** | `capture_game_view`, `capture_simulator_view`, `capture_scene_view`, `capture_multiview` |
| **脚本执行** | `execute_code`, `get_execute_code_history`, `replay_execute_code`, `clear_execute_code_history` |
| **输入模拟** | `simulate_key_press`, `simulate_key_combo`, `simulate_mouse_click`, `simulate_mouse_drag` |
| **性能分析** | `get_performance_snapshot`, `analyze_scene_complexity` |
| **Profiler** | `profiler_start`, `profiler_stop`, `profiler_status`, `get_frame_timing`, `get_counters`, `get_object_memory`, `get_top_memory_objects`, `memory_take_snapshot`, `memory_list_snapshots`, `memory_compare_snapshots`, `frame_debugger_enable`, `frame_debugger_disable`, `frame_debugger_get_events` |
| **包管理** | `install_package`, `remove_package`, `list_packages` |
| **编译** | `wait_for_compilation`, `request_recompile`, `get_compilation_errors`, `get_reload_recovery_status` |
| **测试** | `run_tests`, `get_test_job`, `cancel_test_run` |
| **编辑器状态** | `get_editor_state`, `get_selection`, `set_selection`, `get_prefab_stage`, `get_active_tool`, `set_active_tool`, `get_windows`, `get_tags`, `add_tag`, `remove_tag`, `get_layers`, `add_layer`, `get_build_settings` |
| **菜单项** | `execute_menu_item`, `validate_menu_item` |
| **可视化反馈** | `select_object`, `focus_on_object`, `ping_asset`, `log_message`, `show_dialog`, `get_console_logs` |

> 📊 完整的 Profiler 工具参考、实现细节、已知限制和测试报告见 [PROFILER_TOOLS_CN.md](PROFILER_TOOLS_CN.md)。

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
                └─ Tool Functions (121 个内置工具，23 个模块)
```

```
外部 AI 客户端 → HTTP 请求 → MCPRequestHandler → MCPExecutionBridge → FunctionInvokerController → 工具方法
```

## 环境要求

- Unity 2022.3 或更高版本
- .NET / Mono + `Newtonsoft.Json`

## 参与贡献

欢迎贡献！提交 PR 前请阅读 [贡献指南](CONTRIBUTING.md)。

## 许可证

[MIT](LICENSE) — 可自由使用、修改、分发，也可集成到商业或开源项目中。
