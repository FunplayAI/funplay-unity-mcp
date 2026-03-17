// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Threading;
using System.Threading.Tasks;
using GameBooom.Editor.Settings;
using GameBooom.Editor.Threading;
using GameBooom.Editor.Tools;
using UnityEngine;

namespace GameBooom.Editor.MCP.Server
{
    /// <summary>
    /// Main MCP server service singleton.
    /// Manages server lifecycle, coordinates transport, handler, exporter, and bridge.
    /// </summary>
    internal class MCPServerService : IDisposable
    {
        private readonly ISettingsController _settings;
        private readonly IEditorThreadHelper _threadHelper;
        private readonly FunctionInvokerController _invoker;

        private IMCPTransport _transport;
        private MCPRequestHandler _requestHandler;
        private bool _isRunning;
        private bool _disposed;

        public bool IsRunning => _isRunning;
        public int Port { get; private set; }
        public MCPInteractionLog InteractionLog { get; }

        public MCPServerService(
            ISettingsController settings,
            IEditorThreadHelper threadHelper,
            FunctionInvokerController invoker)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _threadHelper = threadHelper ?? throw new ArgumentNullException(nameof(threadHelper));
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));

            Port = _settings.MCPServerPort;
            InteractionLog = new MCPInteractionLog();
            _settings.OnSettingsChanged += HandleSettingsChanged;
        }

        public async Task<bool> StartAsync(CancellationToken ct = default)
        {
            if (_disposed)
            {
                Debug.LogWarning("[GameBooom MCP Server] Cannot start: service is disposed");
                return false;
            }

            if (_isRunning)
            {
                Debug.Log("[GameBooom MCP Server] Server is already running");
                return true;
            }

            try
            {
                Port = _settings.MCPServerPort;
                Debug.Log("[GameBooom MCP Server] Starting server...");

                _transport = new HttpMCPTransport(Port);
                var toolExporter = new MCPToolExporter();
                var executionBridge = new MCPExecutionBridge(_threadHelper, _settings, _invoker, InteractionLog);
                _requestHandler = new MCPRequestHandler(toolExporter, executionBridge);

                _transport.OnRequestReceived += HandleRequestReceived;

                var started = await _transport.StartAsync(ct);
                if (started)
                {
                    _isRunning = true;
                    Debug.Log($"[GameBooom] MCP Server started on http://localhost:{Port}/");
                    return true;
                }

                Debug.LogError("[GameBooom MCP Server] Failed to start transport");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom MCP Server] Failed to start: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        public async Task StopAsync()
        {
            if (!_isRunning) return;

            try
            {
                Debug.Log("[GameBooom MCP Server] Stopping server...");

                if (_transport != null)
                {
                    _transport.OnRequestReceived -= HandleRequestReceived;
                    await _transport.StopAsync();
                    _transport.Dispose();
                    _transport = null;
                }

                _requestHandler = null;
                _isRunning = false;
                Debug.Log("[GameBooom] MCP Server stopped");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom MCP Server] Error stopping server: {ex.Message}");
            }
        }

        private async void HandleRequestReceived(MCPRequest request, Action<MCPResponse> sendResponse)
        {
            try
            {
                var response = await _threadHelper.ExecuteAsyncOnEditorThreadAsync(
                    async () => await _requestHandler.HandleRequestAsync(request, default));
                sendResponse(response);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom MCP Server] Error handling request: {ex.Message}");
                sendResponse(new MCPResponse
                {
                    Id = request?.Id,
                    Error = new MCPError { Code = -32603, Message = $"Internal error: {ex.Message}" }
                });
            }
        }

        private void HandleSettingsChanged()
        {
            if (_disposed) return;

            if (_settings.MCPServerPort != Port && _isRunning)
            {
                Debug.Log($"[GameBooom MCP Server] Port changed from {Port} to {_settings.MCPServerPort}, restarting...");
                Port = _settings.MCPServerPort;

                _ = Task.Run(async () =>
                {
                    await StopAsync();
                    await Task.Delay(500);
                    await StartAsync();
                });
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settings.OnSettingsChanged -= HandleSettingsChanged;
            _ = StopAsync();
        }
    }
}
