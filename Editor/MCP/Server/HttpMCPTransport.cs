// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GameBooom.Editor.MCP.Server
{
    /// <summary>
    /// HTTP transport implementation for MCP using System.Net.HttpListener.
    /// Listens for JSON-RPC requests over HTTP.
    /// </summary>
    internal class HttpMCPTransport : IMCPTransport
    {
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private readonly int _port;
        private bool _isRunning;

        public bool IsRunning => _isRunning;
        public event Action<MCPRequest, Action<MCPResponse>> OnRequestReceived;

        public HttpMCPTransport(int port)
        {
            _port = port;
        }

        public Task<bool> StartAsync(CancellationToken ct = default)
        {
            if (_isRunning) return Task.FromResult(true);

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();

                _cts = new CancellationTokenSource();
                _isRunning = true;

                _ = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);

                Debug.Log($"[GameBooom MCP Server] HTTP transport started on http://localhost:{_port}/");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom MCP Server] Failed to start HTTP transport: {ex.Message}");
                _isRunning = false;
                return Task.FromResult(false);
            }
        }

        public Task StopAsync()
        {
            if (!_isRunning) return Task.CompletedTask;
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();
                _isRunning = false;
                Debug.Log("[GameBooom MCP Server] HTTP transport stopped");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom MCP Server] Error stopping HTTP transport: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        Debug.LogError($"[GameBooom MCP Server] Error in listen loop: {ex.Message}");
                    break;
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
        {
            MCPRequest request = null;
            try
            {
                if (context.Request.HttpMethod != "POST")
                {
                    await SendErrorResponseAsync(context.Response, null, -32600, "Invalid Request: Only POST method is supported");
                    return;
                }

                request = await ParseRequestAsync(context.Request);
                if (request == null)
                {
                    await SendErrorResponseAsync(context.Response, null, -32700, "Parse error");
                    return;
                }

                var responseTcs = new TaskCompletionSource<MCPResponse>();
                OnRequestReceived?.Invoke(request, r => responseTcs.TrySetResult(r));

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                {
                    try
                    {
                        var responseTask = responseTcs.Task;
                        var completedTask = await Task.WhenAny(responseTask, Task.Delay(-1, linkedCts.Token));
                        if (completedTask == responseTask)
                        {
                            await SendResponseAsync(context.Response, await responseTask);
                        }
                        else
                        {
                            throw new OperationCanceledException();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        var errResponse = timeoutCts.IsCancellationRequested
                            ? CreateErrorResponse(request.Id, -32000, "Request timeout")
                            : CreateErrorResponse(request.Id, -32000, "Request cancelled");
                        await SendResponseAsync(context.Response, errResponse);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom MCP Server] Error handling request: {ex.Message}");
                await SendErrorResponseAsync(context.Response, request?.Id, -32603, $"Internal error: {ex.Message}");
            }
        }

        private async Task<MCPRequest> ParseRequestAsync(HttpListenerRequest request)
        {
            try
            {
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    var json = await reader.ReadToEndAsync();
                    return ParseJsonRequest(json);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom MCP Server] Failed to parse request: {ex.Message}");
                return null;
            }
        }

        private MCPRequest ParseJsonRequest(string json)
        {
            try
            {
                var dict = SimpleJsonHelper.Deserialize(json) as Dictionary<string, object>;
                if (dict == null) return null;

                return new MCPRequest
                {
                    JsonRpc = dict.ContainsKey("jsonrpc") ? dict["jsonrpc"]?.ToString() : "2.0",
                    Id = dict.ContainsKey("id") ? dict["id"] : null,
                    Method = dict.ContainsKey("method") ? dict["method"]?.ToString() : null,
                    Params = dict.ContainsKey("params") ? dict["params"] as Dictionary<string, object> : new Dictionary<string, object>()
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom MCP Server] JSON parse error: {ex.Message}");
                return null;
            }
        }

        private async Task SendResponseAsync(HttpListenerResponse response, MCPResponse mcpResponse)
        {
            try
            {
                var json = SerializeResponse(mcpResponse);
                var bytes = Encoding.UTF8.GetBytes(json);

                response.ContentType = "application/json";
                response.ContentEncoding = Encoding.UTF8;
                response.ContentLength64 = bytes.Length;
                response.StatusCode = 200;

                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom MCP Server] Failed to send response: {ex.Message}");
            }
        }

        private async Task SendErrorResponseAsync(HttpListenerResponse response, object requestId, int code, string message)
        {
            await SendResponseAsync(response, CreateErrorResponse(requestId, code, message));
        }

        private MCPResponse CreateErrorResponse(object requestId, int code, string message)
        {
            return new MCPResponse
            {
                JsonRpc = "2.0",
                Id = requestId,
                Error = new MCPError { Code = code, Message = message }
            };
        }

        private string SerializeResponse(MCPResponse response)
        {
            var dict = new Dictionary<string, object>
            {
                ["jsonrpc"] = response.JsonRpc,
                ["id"] = response.Id
            };

            if (response.Error != null)
            {
                var errorDict = new Dictionary<string, object>
                {
                    ["code"] = response.Error.Code,
                    ["message"] = response.Error.Message
                };
                if (response.Error.Data != null) errorDict["data"] = response.Error.Data;
                dict["error"] = errorDict;
            }
            else
            {
                dict["result"] = response.Result;
            }

            return SimpleJsonHelper.Serialize(dict);
        }

        public void Dispose()
        {
            _ = StopAsync();
            _cts?.Dispose();
            _listener = null;
        }
    }
}
