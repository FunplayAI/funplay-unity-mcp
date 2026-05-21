// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.Settings;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
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
        private const int StartRetryAttempts = 40;
        private const int StartRetryDelayMs = 250;

        public bool IsRunning => _isRunning;
        public event Action<MCPRequest, Action<MCPResponse>> OnRequestReceived;

        public HttpMCPTransport(int port)
        {
            _port = port;
        }

        public async Task<bool> StartAsync(CancellationToken ct = default)
        {
            if (_isRunning) return true;

            for (var attempt = 1; attempt <= StartRetryAttempts; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                    _listener.Prefixes.Add($"http://localhost:{_port}/");
                    _listener.Start();

                    _cts = new CancellationTokenSource();
                    _isRunning = true;

                    _ = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);

                    PluginDebugLogger.Log($"[Funplay MCP Server] HTTP transport started on http://127.0.0.1:{_port}/");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    CleanupFailedStart();
                    _isRunning = false;
                    return false;
                }
                catch (Exception ex) when (IsAddressInUse(ex) && attempt < StartRetryAttempts)
                {
                    CleanupFailedStart();
                    if (attempt == 1)
                    {
                        Debug.LogWarning(
                            $"[Funplay MCP Server] Port {_port} is temporarily in use; retrying for up to {(StartRetryAttempts * StartRetryDelayMs) / 1000f:0.#} seconds.");
                    }

                    await Task.Delay(StartRetryDelayMs, ct);
                }
                catch (Exception ex)
                {
                    CleanupFailedStart();
                    Debug.LogError($"[Funplay MCP Server] Failed to start HTTP transport: {ex.Message}");
                    _isRunning = false;
                    return false;
                }
            }

            _isRunning = false;
            return false;
        }

        public Task StopAsync()
        {
            Stop();
            return Task.CompletedTask;
        }

        public void Stop()
        {
            if (!_isRunning && _listener == null) return;
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();
                PluginDebugLogger.Log("[Funplay MCP Server] HTTP transport stopped");
            }
            catch (ObjectDisposedException)
            {
                PluginDebugLogger.Log("[Funplay MCP Server] HTTP transport was already disposed");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error stopping HTTP transport: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
                _listener = null;
            }
        }

        private void CleanupFailedStart()
        {
            try
            {
                _listener?.Close();
            }
            catch
            {
                // Best-effort cleanup after a failed bind.
            }
            finally
            {
                _listener = null;
            }
        }

        private static bool IsAddressInUse(Exception ex)
        {
            return ex is HttpListenerException listenerException &&
                   (listenerException.ErrorCode == 48 ||
                    listenerException.ErrorCode == 98 ||
                    listenerException.ErrorCode == 183 ||
                    listenerException.ErrorCode == 10048 ||
                    listenerException.Message.IndexOf("Only one usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    listenerException.Message.IndexOf("Address already in use", StringComparison.OrdinalIgnoreCase) >= 0);
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
                        Debug.LogError($"[Funplay MCP Server] Error in listen loop: {ex.Message}");
                    break;
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
        {
            MCPRequest request = null;
            try
            {
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    await SendOptionsResponseAsync(context.Response);
                    return;
                }

                if (context.Request.HttpMethod != "POST")
                {
                    await SendMethodNotAllowedAsync(context.Response, "POST, OPTIONS");
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
                            var response = await responseTask;
                            if (response == null)
                            {
                                await SendAcceptedAsync(context.Response);
                            }
                            else
                            {
                                await SendResponseAsync(context.Response, response);
                            }
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
                Debug.LogError($"[Funplay MCP Server] Error handling request: {ex.Message}");
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
                Debug.LogError($"[Funplay MCP Server] Failed to parse request: {ex.Message}");
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
                Debug.LogError($"[Funplay MCP Server] JSON parse error: {ex.Message}");
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
                Debug.LogError($"[Funplay MCP Server] Failed to send response: {ex.Message}");
            }
        }

        private Task SendOptionsResponseAsync(HttpListenerResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.ContentLength64 = 0;
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            response.OutputStream.Close();
            return Task.CompletedTask;
        }

        private Task SendMethodNotAllowedAsync(HttpListenerResponse response, string allowHeader)
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.ContentLength64 = 0;
            response.Headers.Add("Allow", allowHeader);
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            response.OutputStream.Close();
            return Task.CompletedTask;
        }

        private Task SendAcceptedAsync(HttpListenerResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Accepted;
            response.ContentLength64 = 0;
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            response.OutputStream.Close();
            return Task.CompletedTask;
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
            Stop();
            _cts?.Dispose();
            _listener = null;
        }
    }
}
