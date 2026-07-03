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
    internal sealed class MCPBrokerClientTransport : IMCPTransport
    {
        private const int PullTimeoutMs = 35000;
        private const int PushTimeoutMs = 30000;
        private const int AttachTimeoutMs = 1000;
        private const int DetachTimeoutMs = 750;
        private const int ReconnectBackoffMs = 500;
        private const int RequestExecutionTimeoutSeconds = 300;

        private readonly int _port;
        private readonly string _token;
        private readonly string _baseUrl;
        private readonly string _sessionId;
        private CancellationTokenSource _cts;
        private volatile bool _isRunning;

        public MCPBrokerClientTransport(int port, string token)
        {
            _port = port;
            _token = token ?? string.Empty;
            _baseUrl = "http://127.0.0.1:" + _port;
            _sessionId = Guid.NewGuid().ToString("N");
        }

        public bool IsRunning => _isRunning;
        public bool IsAttachedToExistingServer => true;
        public event Action<MCPRequest, Action<MCPResponse>> OnRequestReceived;

        public Task<bool> StartAsync(CancellationToken ct = default)
        {
            if (_isRunning)
                return Task.FromResult(true);

            if (string.IsNullOrEmpty(_token))
            {
                Debug.LogError("[Funplay MCP Server] Broker transport cannot start without a broker token.");
                return Task.FromResult(false);
            }

            if (!TryAttach())
            {
                Debug.LogError("[Funplay MCP Server] Broker transport could not attach its Unity backend session.");
                return Task.FromResult(false);
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _isRunning = true;
            _ = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
            PluginDebugLogger.Log("[Funplay MCP Server] Broker transport attached to " + _baseUrl + "/");
            return Task.FromResult(true);
        }

        public Task StopAsync()
        {
            Stop();
            return Task.CompletedTask;
        }

        public void Stop()
        {
            if (!_isRunning && _cts == null)
                return;

            var wasRunning = _isRunning;
            _isRunning = false;
            try { _cts?.Cancel(); } catch { }
            if (wasRunning)
                TryDetach();

            try { _cts?.Dispose(); } catch { }
            _cts = null;
            PluginDebugLogger.Log("[Funplay MCP Server] Broker transport stopped");
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                BrokerPullResult pull;
                try
                {
                    pull = await Task.Run(() => PullOnce(), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    await DelaySafe(ReconnectBackoffMs, ct);
                    continue;
                }

                if (pull == null)
                    continue;

                try
                {
                    await HandleAndPushAsync(pull, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a single bad request kill the poll loop -- that would
                    // leave the broker queueing requests with nobody pulling them.
                    Debug.LogError("[Funplay MCP Server] Broker request handling failed: " + ex.Message);
                }
            }
        }

        private async Task HandleAndPushAsync(BrokerPullResult pull, CancellationToken ct)
        {
            var responseJson = string.Empty;
            var canPiggybackNotification = false;
            try
            {
                var request = ParseJsonRequest(pull.Body);
                if (request != null)
                    request.IsBrokerRedelivery = pull.IsRedelivery;

                var handler = OnRequestReceived;
                if (request == null || handler == null)
                {
                    responseJson = SerializeResponse(CreateError(null, -32000, "MCP server is stopping or not ready."));
                }
                else
                {
                    var responseTcs = new TaskCompletionSource<MCPResponse>();
                    handler.Invoke(request, response => responseTcs.TrySetResult(response));

                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(RequestExecutionTimeoutSeconds)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                    {
                        var completed = await Task.WhenAny(responseTcs.Task, Task.Delay(-1, linkedCts.Token));
                        if (completed == responseTcs.Task)
                        {
                            var response = await responseTcs.Task;
                            if (response == null)
                            {
                                responseJson = SerializeResponse(CreateAccepted(request.Id));
                            }
                            else
                            {
                                responseJson = SerializeResponse(response);
                                canPiggybackNotification =
                                    !string.Equals(request.Method, "initialize", StringComparison.Ordinal);
                            }
                        }
                        else
                        {
                            responseJson = SerializeResponse(CreateError(
                                request.Id,
                                -32000,
                                timeoutCts.IsCancellationRequested ? "Request timeout" : "Request cancelled"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                responseJson = SerializeResponse(CreateError(null, -32603, "Internal error: " + ex.Message));
            }

            string contentType = null;
            if (canPiggybackNotification && pull.AcceptsSse && MCPToolListChangeNotifier.TryConsumePending())
            {
                responseJson = MCPToolListChangeNotifier.BuildSseBody(responseJson);
                contentType = "text/event-stream";
                PluginDebugLogger.Log("[Funplay MCP Server] Delivering tools/list_changed notification via broker SSE response.");
            }

            try
            {
                await Task.Run(() => PushOnce(pull.RequestId, responseJson, contentType), ct);
            }
            catch (OperationCanceledException)
            {
                // The broker will make this request available to the next Unity session.
                if (contentType != null)
                    MCPToolListChangeNotifier.RestorePending();
            }
            catch (Exception ex)
            {
                if (contentType != null)
                    MCPToolListChangeNotifier.RestorePending();
                Debug.LogError("[Funplay MCP Server] Broker push failed: " + ex.Message);
            }
        }

        private BrokerPullResult PullOnce()
        {
            var request = (HttpWebRequest)WebRequest.Create(_baseUrl + MCPBrokerProtocol.PullPath);
            request.Method = "GET";
            request.Timeout = PullTimeoutMs;
            request.ReadWriteTimeout = PullTimeoutMs;
            request.KeepAlive = false;
            request.Headers[MCPBrokerProtocol.TokenHeader] = _token;
            request.Headers[MCPBrokerProtocol.SessionHeader] = _sessionId;

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode == HttpStatusCode.NoContent)
                    return null;

                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("Broker pull returned HTTP " + (int)response.StatusCode);

                var reqIdText = response.Headers[MCPBrokerProtocol.ReqIdHeader];
                if (string.IsNullOrEmpty(reqIdText) || !long.TryParse(reqIdText, out var reqId))
                    throw new InvalidOperationException("Broker pull did not include a request id.");

                var redeliveryText = response.Headers[MCPBrokerProtocol.RedeliveryHeader];
                var acceptsSseText = response.Headers[MCPBrokerProtocol.AcceptSseHeader];
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                {
                    return new BrokerPullResult
                    {
                        RequestId = reqId,
                        IsRedelivery = string.Equals(redeliveryText, "1", StringComparison.Ordinal),
                        AcceptsSse = string.Equals(acceptsSseText, "1", StringComparison.Ordinal),
                        Body = reader.ReadToEnd()
                    };
                }
            }
        }

        private void PushOnce(long requestId, string body, string clientContentType = null)
        {
            var request = (HttpWebRequest)WebRequest.Create(_baseUrl + MCPBrokerProtocol.PushPath);
            request.Method = "POST";
            request.Timeout = PushTimeoutMs;
            request.ReadWriteTimeout = PushTimeoutMs;
            request.ContentType = "application/json; charset=utf-8";
            request.KeepAlive = false;
            request.Headers[MCPBrokerProtocol.TokenHeader] = _token;
            request.Headers[MCPBrokerProtocol.SessionHeader] = _sessionId;
            request.Headers[MCPBrokerProtocol.ReqIdHeader] = requestId.ToString();
            if (!string.IsNullOrEmpty(clientContentType))
                request.Headers[MCPBrokerProtocol.ContentTypeHeader] = clientContentType;

            var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            request.ContentLength = bytes.Length;
            using (var requestStream = request.GetRequestStream())
                requestStream.Write(bytes, 0, bytes.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                reader.ReadToEnd();
        }

        private bool TryAttach()
        {
            return TryPostSession(MCPBrokerProtocol.AttachPath, AttachTimeoutMs);
        }

        private void TryDetach()
        {
            TryPostSession(MCPBrokerProtocol.DetachPath, DetachTimeoutMs);
        }

        private bool TryPostSession(string path, int timeoutMs)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(_baseUrl + path);
                request.Method = "POST";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.ContentLength = 0;
                request.KeepAlive = false;
                request.Headers[MCPBrokerProtocol.TokenHeader] = _token;
                request.Headers[MCPBrokerProtocol.SessionHeader] = _sessionId;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                        reader.ReadToEnd();
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                // Best effort for detach. Attach callers treat false as startup failure.
                return false;
            }
        }

        private MCPRequest ParseJsonRequest(string json)
        {
            try
            {
                var dict = SimpleJsonHelper.Deserialize(json) as Dictionary<string, object>;
                if (dict == null)
                    return null;

                return new MCPRequest
                {
                    JsonRpc = dict.ContainsKey("jsonrpc") ? dict["jsonrpc"]?.ToString() : "2.0",
                    Id = dict.ContainsKey("id") ? dict["id"] : null,
                    Method = dict.ContainsKey("method") ? dict["method"]?.ToString() : null,
                    Params = dict.ContainsKey("params") ? dict["params"] as Dictionary<string, object> : new Dictionary<string, object>()
                };
            }
            catch
            {
                return null;
            }
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
                var error = new Dictionary<string, object>
                {
                    ["code"] = response.Error.Code,
                    ["message"] = response.Error.Message
                };
                if (response.Error.Data != null)
                    error["data"] = response.Error.Data;
                dict["error"] = error;
            }
            else
            {
                dict["result"] = response.Result;
            }

            return SimpleJsonHelper.Serialize(dict);
        }

        private static MCPResponse CreateError(object requestId, int code, string message)
        {
            return new MCPResponse
            {
                Id = requestId,
                Error = new MCPError { Code = code, Message = message }
            };
        }

        private static MCPResponse CreateAccepted(object requestId)
        {
            return new MCPResponse
            {
                Id = requestId,
                Result = new Dictionary<string, object>
                {
                    ["content"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = "Accepted."
                        }
                    }
                }
            };
        }

        private static async Task DelaySafe(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct); }
            catch (OperationCanceledException) { }
        }

        private sealed class BrokerPullResult
        {
            public long RequestId;
            public bool IsRedelivery;
            public bool AcceptsSse;
            public string Body;
        }
    }
}
