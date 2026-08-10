// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.Settings;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// HTTP transport implementation for MCP using a loopback TCP listener.
    /// Listens for JSON-RPC requests over HTTP.
    /// </summary>
    internal class HttpMCPTransport : IMCPTransport
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly int _requestedPort;
        private readonly PortFallbackHints _fallbackHints;
        private int _activePort;
        private bool _isRunning;
        private const int StartRetryAttempts = 40;

        // When the requested port is already known to be foreign-owned (this session fell back
        // before), the owner is not going to release it the way a tearing-down previous AppDomain
        // does -- waiting the full window would just add ~10s of MCP outage to every domain reload.
        private const int KnownConflictRetryAttempts = 3;
        private const int StartRetryDelayMs = 250;
        private const int FallbackScanAttempts = 3;
        private const int MaxHeaderBytes = 64 * 1024;

        public bool IsRunning => _isRunning;
        public bool IsAttachedToExistingServer => false;

        /// <summary>
        /// Port actually bound, which differs from the requested one when that port was taken and the
        /// transport fell back. Callers must publish this rather than the requested port.
        /// </summary>
        public int Port => _activePort;

        public event Action<MCPRequest, Action<MCPResponse>> OnRequestReceived;

        private enum StartAttemptOutcome
        {
            Started,
            PortUnavailable,
            Cancelled,
            Failed
        }

        public HttpMCPTransport(
            int port,
            string expectedServerName = null,
            string expectedProjectIdentity = null,
            PortFallbackHints fallbackHints = default)
        {
            _requestedPort = port;
            _activePort = port;
            _fallbackHints = fallbackHints;
        }

        public async Task<bool> StartAsync(CancellationToken ct = default)
        {
            if (_isRunning) return true;

            var retryAttempts = _fallbackHints.RequestedPortKnownForeign
                ? KnownConflictRetryAttempts
                : StartRetryAttempts;
            var outcome = await TryStartOnPortAsync(_requestedPort, retryAttempts, ct).ConfigureAwait(false);
            if (outcome == StartAttemptOutcome.Started)
                return true;

            if (outcome != StartAttemptOutcome.PortUnavailable)
            {
                _isRunning = false;
                return false;
            }

            // The port stayed occupied for the whole retry window, so this is not the previous
            // AppDomain's listener finishing its teardown -- it belongs to someone else: another
            // project whose derived port collided, or an unrelated process. Serving on a fallback port
            // beats not serving at all; the fallback is remembered only in SessionState (see
            // FunplayPortFallbackMemory), never persisted, so it cannot outlive the conflict.

            // A fallback this session already served is tried first so a client connected to it
            // survives the domain reload instead of chasing a re-rolled port.
            var preferredPort = _fallbackHints.PreferredFallbackPort;
            if (preferredPort > 0 && preferredPort != _requestedPort)
            {
                var preferredOutcome = await TryStartOnPortAsync(preferredPort, 1, ct).ConfigureAwait(false);
                if (preferredOutcome == StartAttemptOutcome.Started)
                {
                    LogFallbackBind(preferredPort);
                    _isRunning = true;
                    return true;
                }

                if (preferredOutcome != StartAttemptOutcome.PortUnavailable)
                {
                    _isRunning = false;
                    return false;
                }
            }

            for (var scan = 1; scan <= FallbackScanAttempts; scan++)
            {
                int fallbackPort;
                if (!FunplayFreePortScanner.TryFindFreePort(_requestedPort, out fallbackPort))
                    break;

                // The scan probes by binding and releasing, so another process can take the port in
                // between. Re-scan instead of giving up: losing that race once must not leave this
                // editor without an MCP server while plenty of ports are free.
                var fallbackOutcome = await TryStartOnPortAsync(fallbackPort, 1, ct).ConfigureAwait(false);
                if (fallbackOutcome == StartAttemptOutcome.Started)
                {
                    LogFallbackBind(fallbackPort);
                    _isRunning = true;
                    return true;
                }

                if (fallbackOutcome != StartAttemptOutcome.PortUnavailable)
                {
                    _isRunning = false;
                    return false;
                }
            }

            Debug.LogError(
                $"[Funplay MCP Server] Port {_requestedPort} is in use and no free port nearby could be bound; the MCP server is not serving.");
            _isRunning = false;
            return false;
        }

        private void LogFallbackBind(int fallbackPort)
        {
            Debug.LogWarning(
                $"[Funplay MCP Server] Port {_requestedPort} is held by another process; serving on {fallbackPort} instead. " +
                FunplayFreePortScanner.FallbackGuidance);
        }

        private async Task<StartAttemptOutcome> TryStartOnPortAsync(int port, int retryAttempts, CancellationToken ct)
        {
            for (var attempt = 1; attempt <= retryAttempts; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    _listener = new TcpListener(IPAddress.Loopback, port);
                    _listener.Server.NoDelay = true;
                    _listener.Start();

                    _cts = new CancellationTokenSource();
                    _activePort = port;
                    _isRunning = true;

                    _ = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);

                    PluginDebugLogger.Log($"[Funplay MCP Server] HTTP transport started on http://127.0.0.1:{port}/");
                    return StartAttemptOutcome.Started;
                }
                catch (OperationCanceledException)
                {
                    CleanupFailedStart();
                    _isRunning = false;
                    return StartAttemptOutcome.Cancelled;
                }
                catch (Exception ex) when (IsAddressInUse(ex))
                {
                    CleanupFailedStart();
                    if (attempt >= retryAttempts)
                        return StartAttemptOutcome.PortUnavailable;

                    if (attempt == 1)
                    {
                        Debug.LogWarning(
                            $"[Funplay MCP Server] Port {port} is temporarily in use; retrying for up to {(retryAttempts * StartRetryDelayMs) / 1000f:0.#} seconds.");
                    }

                    if (!await DelayBeforeRetryAsync(ct).ConfigureAwait(false))
                        return StartAttemptOutcome.Cancelled;
                }
                catch (Exception ex)
                {
                    CleanupFailedStart();
                    Debug.LogError($"[Funplay MCP Server] Failed to start HTTP transport: {ex.Message}");
                    _isRunning = false;
                    return StartAttemptOutcome.Failed;
                }
            }

            return StartAttemptOutcome.PortUnavailable;
        }

        public Task StopAsync()
        {
            Stop();
            return Task.CompletedTask;
        }

        public void Stop()
        {
            if (!_isRunning && _listener == null && _cts == null)
                return;

            try
            {
                _isRunning = false;
                _cts?.Cancel();
                CloseListener();
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
                _listener = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void CleanupFailedStart()
        {
            try
            {
                _isRunning = false;
                CloseListener();
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

        private static async Task<bool> DelayBeforeRetryAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(StartRetryDelayMs, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static bool IsAddressInUse(Exception ex)
        {
            var message = ex?.Message ?? string.Empty;
            if (message.IndexOf("Only one usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Address already in use", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return ex is SocketException socketException &&
                   (socketException.ErrorCode == 48 ||
                    socketException.ErrorCode == 98 ||
                    socketException.ErrorCode == 183 ||
                    socketException.ErrorCode == 10048);
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            var stoppedUnexpectedly = false;
            try
            {
                while (!ct.IsCancellationRequested && _isRunning)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted ||
                                                     ex.SocketErrorCode == SocketError.OperationAborted)
                    {
                        stoppedUnexpectedly = !ct.IsCancellationRequested && _isRunning;
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        stoppedUnexpectedly = !ct.IsCancellationRequested && _isRunning;
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested && _isRunning)
                        {
                            Debug.LogError($"[Funplay MCP Server] Error in listen loop: {ex.Message}");
                            stoppedUnexpectedly = true;
                        }
                        break;
                    }
                }
            }
            finally
            {
                if (stoppedUnexpectedly)
                {
                    _isRunning = false;
                    CloseListener();
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            MCPRequest request = null;
            NetworkStream stream = null;
            try
            {
                using (client)
                {
                    stream = client.GetStream();
                    var httpRequest = await ReadHttpRequestAsync(stream, ct);
                    if (httpRequest == null)
                        return;

                    if (httpRequest.Method == "OPTIONS")
                    {
                        await SendOptionsResponseAsync(stream, ct);
                        return;
                    }

                    if (httpRequest.Method != "POST")
                    {
                        await SendMethodNotAllowedAsync(stream, "POST, OPTIONS", ct);
                        return;
                    }

                    request = ParseJsonRequest(httpRequest.Body);
                    if (request == null)
                    {
                        await SendErrorResponseAsync(stream, null, -32700, "Parse error", ct);
                        return;
                    }

                    var requestReceived = OnRequestReceived;
                    if (requestReceived == null)
                    {
                        await SendErrorResponseAsync(stream, request.Id, -32000, "MCP server is stopping or not ready.", ct);
                        return;
                    }

                    var responseTcs = new TaskCompletionSource<MCPResponse>();
                    requestReceived.Invoke(request, r => responseTcs.TrySetResult(r));

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
                                    await SendAcceptedAsync(stream, ct);
                                }
                                else if (httpRequest.AcceptsEventStream &&
                                         !string.Equals(request.Method, "initialize", StringComparison.Ordinal) &&
                                         MCPToolListChangeNotifier.TryConsumePending())
                                {
                                    await SendSseResponseAsync(stream, response, ct);
                                }
                                else
                                {
                                    await SendResponseAsync(stream, response, ct);
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
                            await SendResponseAsync(stream, errResponse, CancellationToken.None);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error handling request: {ex.Message}");
                if (stream != null)
                    await SendErrorResponseAsync(stream, request?.Id, -32603, $"Internal error: {ex.Message}", CancellationToken.None);
            }
        }

        private async Task<HttpRequestData> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var rawRequest = new MemoryStream();
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0)
                    return null;

                rawRequest.Write(buffer, 0, read);
                if (rawRequest.Length > MaxHeaderBytes)
                    throw new InvalidOperationException("HTTP header is too large.");

                headerEnd = FindHeaderEnd(rawRequest.GetBuffer(), (int)rawRequest.Length);
            }

            var requestBytes = rawRequest.ToArray();
            var headerText = Encoding.ASCII.GetString(requestBytes, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                return null;

            var requestLineParts = lines[0].Split(' ');
            if (requestLineParts.Length < 1)
                return null;

            var contentLength = 0;
            var acceptsEventStream = false;
            for (var i = 1; i < lines.Length; i++)
            {
                var separator = lines[i].IndexOf(':');
                if (separator <= 0)
                    continue;

                var name = lines[i].Substring(0, separator).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(lines[i].Substring(separator + 1).Trim(), out contentLength);
                }
                else if (string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase))
                {
                    acceptsEventStream = lines[i].Substring(separator + 1)
                        .IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }

            var bodyStart = headerEnd + 4;
            var bodyBytes = new byte[contentLength];
            var copied = Math.Min(contentLength, requestBytes.Length - bodyStart);
            if (copied > 0)
                Buffer.BlockCopy(requestBytes, bodyStart, bodyBytes, 0, copied);

            while (copied < contentLength)
            {
                var read = await stream.ReadAsync(bodyBytes, copied, contentLength - copied, ct);
                if (read == 0)
                    break;
                copied += read;
            }

            return new HttpRequestData
            {
                Method = requestLineParts[0],
                AcceptsEventStream = acceptsEventStream,
                Body = Encoding.UTF8.GetString(bodyBytes, 0, copied)
            };
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (var i = 3; i < length; i++)
            {
                if (buffer[i - 3] == '\r' &&
                    buffer[i - 2] == '\n' &&
                    buffer[i - 1] == '\r' &&
                    buffer[i] == '\n')
                {
                    return i - 3;
                }
            }

            return -1;
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

        private async Task SendResponseAsync(NetworkStream stream, MCPResponse mcpResponse, CancellationToken ct)
        {
            try
            {
                var json = SerializeResponse(mcpResponse);
                await SendRawResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, ct);
            }
            catch (Exception ex) when (IsExpectedClientDisconnect(ex, ct))
            {
                PluginDebugLogger.Log($"[Funplay MCP Server] Response not sent because the client disconnected: {ex.Message}");
            }
        }

        /// <summary>
        /// Streamable-HTTP style response: an SSE body that carries the pending
        /// tools/list_changed notification followed by the JSON-RPC response.
        /// Only used when the client declared Accept: text/event-stream.
        /// </summary>
        private async Task SendSseResponseAsync(NetworkStream stream, MCPResponse mcpResponse, CancellationToken ct)
        {
            try
            {
                var body = MCPToolListChangeNotifier.BuildSseBody(SerializeResponse(mcpResponse));
                await SendRawResponseAsync(stream, 200, "OK", "text/event-stream", body, ct);
                PluginDebugLogger.Log("[Funplay MCP Server] Delivered tools/list_changed notification via SSE response.");
            }
            catch (Exception ex) when (IsExpectedClientDisconnect(ex, ct))
            {
                MCPToolListChangeNotifier.RestorePending();
                PluginDebugLogger.Log($"[Funplay MCP Server] SSE response not sent because the client disconnected: {ex.Message}");
            }
            catch (Exception ex)
            {
                MCPToolListChangeNotifier.RestorePending();
                Debug.LogError($"[Funplay MCP Server] Failed to send response: {ex.Message}");
            }
        }

        private bool IsExpectedClientDisconnect(Exception ex, CancellationToken ct)
        {
            return ct.IsCancellationRequested || !_isRunning || IsClientDisconnectException(ex);
        }

        internal static bool IsClientDisconnectException(Exception ex)
        {
            while (ex != null)
            {
                if (ex is OperationCanceledException ||
                    ex is ObjectDisposedException)
                {
                    return true;
                }

                if (ex is SocketException socketException &&
                    IsClientDisconnectSocketError(socketException.SocketErrorCode))
                {
                    return true;
                }

                var message = ex.Message ?? string.Empty;
                if (message.IndexOf("socket has been shut down", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("broken pipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("connection reset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("connection was aborted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("cannot access a disposed object", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                ex = ex.InnerException;
            }

            return false;
        }

        private static bool IsClientDisconnectSocketError(SocketError error)
        {
            return error == SocketError.ConnectionReset ||
                   error == SocketError.ConnectionAborted ||
                   error == SocketError.NetworkReset ||
                   error == SocketError.NotConnected ||
                   error == SocketError.Shutdown ||
                   error == SocketError.OperationAborted ||
                   error == SocketError.Interrupted;
        }

        private Task SendOptionsResponseAsync(NetworkStream stream, CancellationToken ct)
        {
            return SendRawResponseAsync(stream, (int)HttpStatusCode.NoContent, "No Content", "text/plain", string.Empty, ct);
        }

        private Task SendMethodNotAllowedAsync(NetworkStream stream, string allowHeader, CancellationToken ct)
        {
            return SendRawResponseAsync(stream, (int)HttpStatusCode.MethodNotAllowed, "Method Not Allowed", "text/plain", string.Empty, ct, "Allow: " + allowHeader + "\r\n");
        }

        private Task SendAcceptedAsync(NetworkStream stream, CancellationToken ct)
        {
            return SendRawResponseAsync(stream, (int)HttpStatusCode.Accepted, "Accepted", "text/plain", string.Empty, ct);
        }

        private async Task SendErrorResponseAsync(NetworkStream stream, object requestId, int code, string message, CancellationToken ct)
        {
            await SendResponseAsync(stream, CreateErrorResponse(requestId, code, message), ct);
        }

        private static async Task SendRawResponseAsync(
            NetworkStream stream,
            int statusCode,
            string reasonPhrase,
            string contentType,
            string body,
            CancellationToken ct,
            string extraHeaders = "")
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            var header =
                $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type\r\n" +
                extraHeaders +
                "\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
            if (bodyBytes.Length > 0)
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);
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
        }

        private void CloseListener()
        {
            if (_listener == null)
                return;

            try { _listener.Stop(); } catch { }
        }

        private sealed class HttpRequestData
        {
            public string Method { get; set; }
            public bool AcceptsEventStream { get; set; }
            public string Body { get; set; }
        }
    }
}
