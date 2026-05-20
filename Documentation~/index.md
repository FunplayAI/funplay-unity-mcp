# Funplay MCP for Unity

Funplay MCP for Unity is an open-source MCP server for the Unity Editor.

## Getting Started

1. Install via UPM using the Git URL for this repository
2. Open **Funplay > MCP Server**
3. Start the server and use the built-in one-click client configuration
4. Connect your AI client to the endpoint shown in the window (`http://127.0.0.1:8765/` by default)
5. Open **Funplay > Tool Exposure** to edit the exact tools exposed by `core` or `full`
6. For Claude Code, Cursor, and Codex, use **Configure + Skills** or open **Funplay > Project Skills** to install the default `unity-mcp-workflow` skill
7. Open **Funplay > Plugin Settings** to adjust debug logging when troubleshooting

## Highlights

- 91 built-in tool functions across scene, asset, script, prefab, UI, animation, camera, screenshot, package, editor-state, menu-item, and feedback workflows
- Structured `{success, message, data}` JSON returns with stable `instanceId` fields so agents can chain `by_id` lookups
- `IFunplayCommand` template for `execute_code` with auto-Undo, structured logs, and a returned changelog of created/modified/destroyed objects
- HTTP JSON-RPC 2.0 MCP server compatible with Claude Code, Cursor, LM Studio, Windsurf, Codex, VS Code Copilot, and other MCP clients
- Reflection-based tool discovery via `[ToolProvider]`
- One-click local MCP config generation for supported clients, including LM Studio
- Separate tool exposure window for editing which tools `core` and `full` expose
- One-click MCP config plus project workflow skill setup for Claude Code, Cursor, and Codex
- Project skills management for supported AI clients, currently installing the default `unity-mcp-workflow` skill
- Dedicated plugin settings window with a debug logging toggle that is enabled by default
- Persisted MCP server settings in `UserSettings/FunplayMcpSettings.json`
- Domain reload recovery for the MCP server during Unity recompilation

## Custom Tools

Add a public static class marked with `[ToolProvider("CategoryName")]`, then expose `public static` methods with `[ToolParam]` metadata. Method return types may be `string` (legacy text response) or any object — non-string returns are serialized to JSON via Newtonsoft. Use `Funplay.Editor.Tools.Helpers.Response.Success/Error` for the structured envelope. Tool names are exported in snake_case automatically.

## Requirements

- Unity 2022.3 or later
- `com.unity.nuget.newtonsoft-json`
