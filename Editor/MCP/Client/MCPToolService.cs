// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using GameBooom.Editor.Api;
using GameBooom.Editor.Api.Models;
using GameBooom.Editor.Settings;

namespace GameBooom.Editor.MCP.Client
{
    /// <summary>
    /// Manages the lifecycle of an external MCP server connection:
    /// start/stop process, discover tools, route tool calls, extract results.
    /// </summary>
    internal class MCPToolService : IDisposable
    {
        private readonly ISettingsController _settings;
        private MCPHost _host;
        private readonly HashSet<string> _mcpToolNames = new HashSet<string>();
        private List<ToolDefinition> _toolDefinitions = new List<ToolDefinition>();
        private bool _isRunning;

        public bool IsRunning => _isRunning;
        public List<ToolDefinition> ToolDefinitions => _toolDefinitions;

        public MCPToolService(ISettingsController settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Start an MCP server process, initialize protocol, and discover tools.
        /// </summary>
        public async Task<bool> StartAsync(
            string command, string arguments = "",
            Dictionary<string, string> envVars = null,
            CancellationToken ct = default)
        {
            if (_isRunning) await StopAsync();

            try
            {
                _host = new MCPHost(command, arguments, envVars);
                _host.OnLog += msg => Debug.Log($"[GameBooom MCP] {msg}");

                var connected = await _host.ConnectAsync(ct);
                if (!connected)
                {
                    Debug.LogWarning("[GameBooom] Failed to connect to MCP server.");
                    return false;
                }

                var toolsJson = await _host.ListToolsAsync(ct);
                if (string.IsNullOrEmpty(toolsJson))
                {
                    Debug.LogWarning("[GameBooom] MCP server returned no tools.");
                    return false;
                }

                _toolDefinitions = MCPJsonHelper.ParseToolsList(toolsJson);
                _mcpToolNames.Clear();
                foreach (var td in _toolDefinitions)
                    _mcpToolNames.Add(td.function.name);

                _isRunning = true;
                Debug.Log($"[GameBooom] MCP started. Discovered {_toolDefinitions.Count} tools.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom] MCP start failed: {ex.Message}");
                _toolDefinitions.Clear();
                _mcpToolNames.Clear();
                return false;
            }
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            _toolDefinitions.Clear();
            _mcpToolNames.Clear();

            if (_host != null)
            {
                _host.Dispose();
                _host = null;
            }

            await Task.CompletedTask;
        }

        public bool IsMCPTool(string toolName)
        {
            return _mcpToolNames.Contains(toolName);
        }

        public async Task<string> CallToolAsync(string toolName, string rawArguments,
            CancellationToken ct = default)
        {
            if (!_isRunning || _host == null || !_host.IsConnected)
                return "Error: MCP server is not running.";

            try
            {
                var arguments = ParseArgumentsToDict(rawArguments);
                var responseJson = await _host.CallToolAsync(toolName, arguments, ct);
                if (string.IsNullOrEmpty(responseJson))
                    return "Error: MCP server returned no response.";

                return MCPJsonHelper.ExtractCallResult(responseJson) ?? "No result from MCP tool.";
            }
            catch (Exception ex)
            {
                return $"Error: MCP call failed - {ex.Message}";
            }
        }

        private Dictionary<string, object> ParseArgumentsToDict(string rawJson)
        {
            var dict = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(rawJson)) return dict;

            try
            {
                var parsed = JsonParse.ParseJsonObject(rawJson, 0, out _);
                if (parsed != null)
                {
                    foreach (var kvp in parsed)
                        dict[kvp.Key] = kvp.Value;
                }
            }
            catch
            {
                dict["_raw"] = rawJson;
            }

            return dict;
        }

        public void Dispose()
        {
            _host?.Dispose();
            _host = null;
            _isRunning = false;
            _toolDefinitions.Clear();
            _mcpToolNames.Clear();
        }
    }
}
