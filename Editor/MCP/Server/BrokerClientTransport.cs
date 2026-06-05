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
    /// Out-of-process MCP transport. Instead of binding a <see cref="System.Net.Sockets.TcpListener"/>
    /// inside Unity's managed AppDomain (which is destroyed on every domain reload, dropping the
    /// client connection), this transport connects OUT to a persistent broker process and long-polls
    /// it for MCP requests, pushing responses back over HTTP.
    ///
    /// Because the broker owns the client-facing socket and lives outside Unity, MCP client
    /// connections survive Unity domain reloads (script recompile / entering Play Mode): only this
    /// poll loop drops, and funplay's existing post-reload restart re-creates the transport, which
    /// re-attaches to the broker. The broker holds in-flight client requests across the gap, so the
    /// client sees a slightly slower call instead of "connection refused".
    ///
    /// Back-channel protocol (HTTP long-poll, header framing — see Broker~/keepalive-broker.cs):
    ///   GET  {broker}/_b/pull  -> 200 + header "X-Broker-ReqId: N" + body = client json-rpc | 204 no work
    ///   POST {broker}/_b/push  -> header "X-Broker-ReqId: N" + body = json-rpc response
    /// </summary>
    internal class BrokerClientTransport : IMCPTransport
    {
        private readonly string _brokerBaseUrl;
        private CancellationTokenSource _cts;
        private volatile bool _isRunning;
        private const int PullTimeoutMs = 35000;     // > broker's long-poll hold (25s)
        private const int PushTimeoutMs = 10000;
        private const int ReconnectBackoffMs = 500;
        private const string ReqIdHeader = "X-Broker-ReqId";

        public bool IsRunning => _isRunning;

        /// <summary>Mirrors the seam consumed by <see cref="MCPServerService"/>: this transport
        /// is attached to an external (broker) server rather than owning an in-process listener.</summary>
        public bool IsAttachedToExistingServer => true;

        public event Action<MCPRequest, Action<MCPResponse>> OnRequestReceived;

        public BrokerClientTransport(int port)
        {
            _brokerBaseUrl = $"http://127.0.0.1:{port}";
        }

        public Task<bool> StartAsync(CancellationToken ct = default)
        {
            if (_isRunning) return Task.FromResult(true);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _isRunning = true;
            _ = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);

            PluginDebugLogger.Log($"[Funplay MCP Server] Broker-client transport attached to {_brokerBaseUrl}/");
            return Task.FromResult(true);
        }

        public Task StopAsync()
        {
            Stop();
            return Task.CompletedTask;
        }

        public void Stop()
        {
            if (!_isRunning && _cts == null) return;
            _isRunning = false;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _cts?.Dispose(); } catch { /* ignore */ }
            _cts = null;
            PluginDebugLogger.Log("[Funplay MCP Server] Broker-client transport stopped");
        }

        public void Dispose() => Stop();

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                PullResult pull;
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
                    // Broker unreachable or long-poll timed out — back off briefly and retry.
                    await DelaySafe(ReconnectBackoffMs, ct);
                    continue;
                }

                if (pull == null)
                    continue; // 204: no work, re-poll

                // Handle + push without blocking the poll loop, so multiple requests can overlap.
                _ = HandleAndPushAsync(pull.ReqId, pull.Body, ct);
            }
        }

        private async Task HandleAndPushAsync(long reqId, string clientBody, CancellationToken ct)
        {
            string responseJson;
            try
            {
                var request = ParseJsonRequest(clientBody);
                var handler = OnRequestReceived;

                if (request == null || handler == null)
                {
                    responseJson = SerializeResponse(CreateError(null, -32000, "MCP server is stopping or not ready."));
                }
                else
                {
                    var responseTcs = new TaskCompletionSource<MCPResponse>();
                    handler.Invoke(request, r => responseTcs.TrySetResult(r));

                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                    {
                        var completed = await Task.WhenAny(responseTcs.Task, Task.Delay(-1, linkedCts.Token));
                        if (completed == responseTcs.Task)
                        {
                            var response = await responseTcs.Task;
                            responseJson = response == null
                                ? SerializeResponse(CreateError(request.Id, -32000, "Empty response"))
                                : SerializeResponse(response);
                        }
                        else
                        {
                            responseJson = SerializeResponse(CreateError(request.Id, -32000,
                                timeoutCts.IsCancellationRequested ? "Request timeout" : "Request cancelled"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                responseJson = SerializeResponse(CreateError(null, -32603, $"Internal error: {ex.Message}"));
            }

            try
            {
                await Task.Run(() => PushOnce(reqId, responseJson), ct);
            }
            catch (OperationCanceledException)
            {
                // Shutting down or reloading — the broker re-delivers this reqId to the next poll.
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Broker push failed (reqId={reqId}): {ex.Message}");
            }
        }

        private static async Task DelaySafe(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct); }
            catch (OperationCanceledException) { /* ignore */ }
        }

        // ---- Back-channel HTTP client (HttpWebRequest — editor BCL, no asmdef ref) ----
        // Header framing: the broker tags pulled work with the X-Broker-ReqId response header and
        // the client's raw JSON-RPC as the body; we echo that header back on push with the response
        // body. No JSON envelope is parsed here, so the broker stays a dumb byte forwarder.

        private sealed class PullResult
        {
            public long ReqId;
            public string Body;
        }

        private PullResult PullOnce()
        {
            var req = (HttpWebRequest)WebRequest.Create(_brokerBaseUrl + "/_b/pull");
            req.Method = "GET";
            req.Timeout = PullTimeoutMs;
            req.ReadWriteTimeout = PullTimeoutMs;
            req.KeepAlive = false;
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                if (resp.StatusCode == HttpStatusCode.NoContent)
                    return null;
                var idStr = resp.Headers[ReqIdHeader];
                long reqId;
                if (string.IsNullOrEmpty(idStr) || !long.TryParse(idStr, out reqId))
                    return null;
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                    return new PullResult { ReqId = reqId, Body = reader.ReadToEnd() };
            }
        }

        private void PushOnce(long reqId, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(_brokerBaseUrl + "/_b/push");
            req.Method = "POST";
            req.Timeout = PushTimeoutMs;
            req.ReadWriteTimeout = PushTimeoutMs;
            req.ContentType = "application/json; charset=utf-8";
            req.KeepAlive = false;
            req.Headers[ReqIdHeader] = reqId.ToString();
            var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            req.ContentLength = bytes.Length;
            using (var rs = req.GetRequestStream())
                rs.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                reader.ReadToEnd();
        }

        // ---- Request parse / response serialize (mirrors HttpMCPTransport) ----

        private MCPRequest ParseJsonRequest(string json)
        {
            try
            {
                if (!(SimpleJsonHelper.Deserialize(json) is Dictionary<string, object> dict))
                    return null;

                return new MCPRequest
                {
                    JsonRpc = dict.ContainsKey("jsonrpc") ? dict["jsonrpc"]?.ToString() : "2.0",
                    Id = dict.ContainsKey("id") ? dict["id"] : null,
                    Method = dict.ContainsKey("method") ? dict["method"]?.ToString() : null,
                    Params = dict.ContainsKey("params") ? dict["params"] as Dictionary<string, object> : new Dictionary<string, object>(),
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
                ["id"] = response.Id,
            };

            if (response.Error != null)
            {
                var errorDict = new Dictionary<string, object>
                {
                    ["code"] = response.Error.Code,
                    ["message"] = response.Error.Message,
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

        private static MCPResponse CreateError(object requestId, int code, string message)
        {
            return new MCPResponse
            {
                JsonRpc = "2.0",
                Id = requestId,
                Error = new MCPError { Code = code, Message = message },
            };
        }
    }
}
