# GameBooom MCP For Unity

GameBooom MCP For Unity is an open-source MCP server for the Unity Editor.

## Getting Started

1. Install via UPM using the Git URL for this repository
2. Open **GameBooom > MCP Server**
3. Enable the server and confirm the port
4. Connect your AI client to `http://localhost:8765/`

## Highlights

- 60+ built-in tool functions across scene, asset, script, prefab, UI, animation, camera, screenshot, package, and feedback workflows
- HTTP JSON-RPC 2.0 MCP server compatible with Claude Code, Cursor, Windsurf, Codex, VS Code Copilot, and other MCP clients
- Reflection-based tool discovery via `[ToolProvider]`
- One-click local MCP config generation for supported clients
- Domain reload recovery for the MCP server during Unity recompilation

## Custom Tools

Add a public static class marked with `[ToolProvider("CategoryName")]`, then expose `public static string` methods with `[ToolParam]` metadata. Tool names are exported in snake_case automatically.

## Requirements

- Unity 2022.3 or later
- `com.unity.nuget.newtonsoft-json`
