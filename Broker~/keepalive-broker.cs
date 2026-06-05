// Copyright (C) Funplay. Licensed under MIT.
//
// Out-of-process keepalive broker for the Funplay Unity MCP server (C# / Mono edition).
//
// Runs as a SEPARATE process under the Unity-bundled Mono runtime, so it is unaffected by
// Unity AppDomain reloads. It owns the client-facing port; the Unity plugin connects OUT to
// it (BrokerClientTransport) and long-polls for work. When Unity reloads, only the plugin's
// poll connection drops — the broker keeps every MCP client connection open and re-queues any
// in-flight request, so the client sees a slightly slower call instead of "connection refused".
//
// One TCP listener on 127.0.0.1:<port>, three roles by HTTP path (raw HTTP, like HttpMCPTransport):
//   • MCP client  -> POST /            held open until Unity answers (or 120s deadline)
//   • Unity plugin-> GET  /_b/pull     long-poll: 200 + header "X-Broker-ReqId: N" + body = client
//                                       request body, or 204 when there is no work
//                   POST /_b/push      header "X-Broker-ReqId: N" + body = response; forwarded
//                                       verbatim to the matching client (no JSON parsing here)
//
// Build (once, with the Unity-bundled compiler):
//   <MonoBleedingEdge>/bin/mcs -out:keepalive-broker.exe keepalive-broker.cs
// Run:
//   <MonoBleedingEdge>/bin/mono keepalive-broker.exe <port>
//
// Conservative C# (mono 4.5 profile) so the same .exe runs on Unity 2019+ bundled Mono.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FunplayBroker
{
    internal static class KeepaliveBroker
    {
        private const int PullTimeoutMs = 25000;     // long-poll hold for Unity
        private const long HoldDeadlineMs = 120000;  // max hold for a client request (~ funplay restart window)
        private const long RedeliverAfterMs = 1500;  // re-queue in-flight reqs unanswered this long
        private const string ReqIdHeader = "X-Broker-ReqId";

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, Pending> PendingMap = new Dictionary<long, Pending>();
        private static readonly List<long> QueueIds = new List<long>();
        private static Waiter WaitingPull;
        private static long _seq;
        private static bool _unityConnected;

        private sealed class Pending
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public string Body;
            public long DeliveredAt;
            public bool Delivered;
            public long EnqueuedAt;
        }

        private sealed class Waiter
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public long StashedAt;
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static void Log(string m)
        {
            Console.Error.WriteLine("[broker " + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + m);
        }

        public static int Main(string[] args)
        {
            int port = 8765;
            if (args.Length > 0) int.TryParse(args[0], out port);
            if (port <= 0) port = 8765;

            TcpListener listener = null;
            int bindAttempts = 0;
            while (true)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Server.NoDelay = true;
                    listener.Start();
                    break;
                }
                catch (SocketException ex)
                {
                    // EADDRINUSE: when funplay switches Direct->Broker it frees the port a moment
                    // before we grab it (OS TIME_WAIT). Retry briefly.
                    if ((ex.SocketErrorCode == SocketError.AddressAlreadyInUse) && bindAttempts < 40)
                    {
                        if (bindAttempts == 0) Log("port " + port + " busy, retrying bind for up to 10s...");
                        bindAttempts++;
                        Thread.Sleep(250);
                        continue;
                    }
                    Log("fatal: cannot bind port " + port + ": " + ex.Message);
                    return 1;
                }
            }

            Log("listening on http://127.0.0.1:" + port + "/  (front=/  back=/_b/pull,/_b/push)");

            var sweeper = new Thread(SweepLoop);
            sweeper.IsBackground = true;
            sweeper.Start();

            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception ex) { Log("accept error: " + ex.Message); continue; }

                var t = new Thread(HandleConnectionThread);
                t.IsBackground = true;
                t.Start(client);
            }
        }

        private static void HandleConnectionThread(object state)
        {
            var client = (TcpClient)state;
            try { HandleConnection(client); }
            catch (Exception ex) { Log("handler error: " + ex.Message); SafeClose(client); }
        }

        private static void HandleConnection(TcpClient client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            string method, path, body;
            Dictionary<string, string> headers;
            if (!ReadHttpRequest(stream, out method, out path, out headers, out body))
            {
                SafeClose(client);
                return;
            }

            if (method == "GET" && path.StartsWith("/_b/pull"))
            {
                HandlePull(client, stream);
                return; // stream kept open until matched or timed out
            }

            if (method == "POST" && path.StartsWith("/_b/push"))
            {
                long reqId = 0;
                string idStr;
                if (headers.TryGetValue(ReqIdHeader.ToLowerInvariant(), out idStr)) long.TryParse(idStr, out reqId);
                HandlePush(reqId, body);
                WriteResponse(stream, 200, "OK", "application/json", null, "{\"ok\":true}");
                SafeClose(client);
                return;
            }

            if (method == "OPTIONS")
            {
                WriteResponse(stream, 204, "No Content", "text/plain",
                    "Access-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\n", "");
                SafeClose(client);
                return;
            }

            // A front MCP client request: stash it open, queue, try to hand to a waiting pull.
            HandleClientRequest(client, stream, body);
        }

        private static void HandlePull(TcpClient client, NetworkStream stream)
        {
            lock (Gate)
            {
                if (!_unityConnected) { _unityConnected = true; Log("Unity attached (pull)"); }

                // Replace any previous waiting pull (Unity reconnected).
                if (WaitingPull != null)
                {
                    TryWrite204(WaitingPull.Stream);
                    SafeClose(WaitingPull.Client);
                    WaitingPull = null;
                }

                WaitingPull = new Waiter { Client = client, Stream = stream, StashedAt = NowMs() };
                TryDispatch();
            }
        }

        private static void HandlePush(long reqId, string body)
        {
            lock (Gate)
            {
                Pending p;
                if (reqId != 0 && PendingMap.TryGetValue(reqId, out p))
                {
                    PendingMap.Remove(reqId);
                    try
                    {
                        WriteResponse(p.Stream, 200, "OK", "application/json; charset=utf-8", null, body ?? "");
                    }
                    catch (Exception ex) { Log("#" + reqId + " client gone before push delivered: " + ex.Message); }
                    SafeClose(p.Client);
                }
            }
        }

        private static void HandleClientRequest(TcpClient client, NetworkStream stream, string body)
        {
            lock (Gate)
            {
                long reqId = ++_seq;
                PendingMap[reqId] = new Pending
                {
                    Client = client, Stream = stream, Body = body ?? "",
                    EnqueuedAt = NowMs(), Delivered = false, DeliveredAt = 0,
                };
                QueueIds.Add(reqId);
                Log("#" + reqId + " client request queued (unity=" + (_unityConnected ? "up" : "DOWN") + ", queue=" + QueueIds.Count + ")");
                TryDispatch();
            }
        }

        // Hand the oldest queued request to a waiting pull, if both exist. Caller holds Gate.
        private static void TryDispatch()
        {
            if (WaitingPull == null) return;

            long now = NowMs();
            foreach (var kv in PendingMap)
            {
                var p = kv.Value;
                if (p.Delivered && !QueueIds.Contains(kv.Key) && (now - p.DeliveredAt) > RedeliverAfterMs)
                {
                    p.Delivered = false;
                    QueueIds.Add(kv.Key);
                    Log("#" + kv.Key + " re-queued (in-flight across a Unity gap -> redeliver)");
                }
            }
            if (QueueIds.Count == 0) return;

            long reqId = QueueIds[0];
            QueueIds.RemoveAt(0);
            Pending pending;
            if (!PendingMap.TryGetValue(reqId, out pending)) return;

            var pull = WaitingPull;
            WaitingPull = null;
            try
            {
                WriteResponse(pull.Stream, 200, "OK", "application/json; charset=utf-8",
                    ReqIdHeader + ": " + reqId + "\r\n", pending.Body);
                pending.Delivered = true;
                pending.DeliveredAt = NowMs();
            }
            catch (Exception ex)
            {
                Log("dispatch write failed for #" + reqId + ": " + ex.Message);
                QueueIds.Insert(0, reqId); // try again on next pull
            }
            finally
            {
                SafeClose(pull.Client);
            }
        }

        private static void SweepLoop()
        {
            while (true)
            {
                Thread.Sleep(1000);
                lock (Gate)
                {
                    long now = NowMs();
                    if (WaitingPull != null && (now - WaitingPull.StashedAt) > PullTimeoutMs)
                    {
                        TryWrite204(WaitingPull.Stream);
                        SafeClose(WaitingPull.Client);
                        WaitingPull = null;
                    }

                    var expired = new List<long>();
                    foreach (var kv in PendingMap)
                        if ((now - kv.Value.EnqueuedAt) > HoldDeadlineMs) expired.Add(kv.Key);
                    foreach (var id in expired)
                    {
                        var p = PendingMap[id];
                        PendingMap.Remove(id);
                        QueueIds.Remove(id);
                        try { WriteResponse(p.Stream, 504, "Gateway Timeout", "application/json", null, "{\"error\":\"broker_hold_deadline_exceeded\"}"); }
                        catch { }
                        SafeClose(p.Client);
                        Log("#" + id + " hold-deadline exceeded");
                    }
                }
            }
        }

        // ---- HTTP helpers (raw, mirrors HttpMCPTransport) ----

        private static bool ReadHttpRequest(NetworkStream stream, out string method, out string path,
            out Dictionary<string, string> headers, out string body)
        {
            method = null; path = null; body = null;
            headers = new Dictionary<string, string>();

            var buffer = new byte[8192];
            var raw = new MemoryStream();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int read;
                try { read = stream.Read(buffer, 0, buffer.Length); }
                catch { return false; }
                if (read == 0) return false;
                raw.Write(buffer, 0, read);
                if (raw.Length > 256 * 1024) return false;
                headerEnd = FindHeaderEnd(raw.GetBuffer(), (int)raw.Length);
            }

            var bytes = raw.ToArray();
            var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return false;

            var reqLine = lines[0].Split(' ');
            if (reqLine.Length < 2) return false;
            method = reqLine[0];
            path = reqLine[1];

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int sep = lines[i].IndexOf(':');
                if (sep <= 0) continue;
                var name = lines[i].Substring(0, sep).Trim().ToLowerInvariant();
                var value = lines[i].Substring(sep + 1).Trim();
                headers[name] = value;
                if (name == "content-length") int.TryParse(value, out contentLength);
            }

            int bodyStart = headerEnd + 4;
            var bodyBytes = new byte[contentLength];
            int copied = Math.Min(contentLength, bytes.Length - bodyStart);
            if (copied > 0) Buffer.BlockCopy(bytes, bodyStart, bodyBytes, 0, copied);
            while (copied < contentLength)
            {
                int read;
                try { read = stream.Read(bodyBytes, copied, contentLength - copied); }
                catch { break; }
                if (read == 0) break;
                copied += read;
            }
            body = Encoding.UTF8.GetString(bodyBytes, 0, copied);
            return true;
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 3; i < length; i++)
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                    return i - 3;
            return -1;
        }

        private static void WriteResponse(NetworkStream stream, int status, string reason, string contentType,
            string extraHeaders, string body)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
            var header = "HTTP/1.1 " + status + " " + reason + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + bodyBytes.Length + "\r\n"
                + "Connection: close\r\n"
                + "Access-Control-Allow-Origin: *\r\n"
                + (extraHeaders ?? "")
                + "\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (bodyBytes.Length > 0) stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static void TryWrite204(NetworkStream stream)
        {
            try { WriteResponse(stream, 204, "No Content", "text/plain", null, ""); }
            catch { }
        }

        private static void SafeClose(TcpClient client)
        {
            try { if (client != null) client.Close(); } catch { }
        }
    }
}
