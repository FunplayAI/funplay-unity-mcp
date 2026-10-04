// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.State;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Funplay.Editor
{
    public sealed class MCPBrokerTransportTests
    {
        private string _priorStaleEnv;

        // The staleness override is a process-wide env var inherited by launched broker
        // processes. Capture/restore it reliably here: a [TearDown] runs even when a
        // [UnityTest] throws, whereas an in-test try/finally can be skipped when an
        // exception propagates out of a nested yielded IEnumerator (which would otherwise
        // leak the override into every later test's broker).
        [SetUp]
        public void CaptureStaleEnvOverride()
        {
            _priorStaleEnv = Environment.GetEnvironmentVariable(SessionStaleEnvVar);
        }

        [TearDown]
        public void RestoreStaleEnvOverride()
        {
            Environment.SetEnvironmentVariable(SessionStaleEnvVar, _priorStaleEnv);
        }

        [UnityTest]
        public IEnumerator BrokerProcess_StartsWithHealthTokenAndStops()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                Assert.IsFalse(MCPBrokerProcessManager.TryProbeBroker(port, "wrong-token", out _));
                Assert.IsTrue(MCPBrokerProcessManager.TryProbeBroker(port, connection.Token, out var health));
                Assert.AreEqual(connection.Pid, health.Pid);

                MCPBrokerProcessManager.Stop(paths);
                yield return null;

                Assert.IsFalse(MCPBrokerProcessManager.TryProbeBroker(port, connection.Token, out _));
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerProcess_DoesNotAdoptArbitraryOpenPort()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            var listener = new TcpListener(IPAddress.Loopback, port);

            try
            {
                listener.Start();

                Assert.IsFalse(MCPBrokerProcessManager.TryProbeBroker(port, "token", out _));
                Assert.IsFalse(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths));
                StringAssert.Contains("Port is already in use", MCPBrokerProcessManager.LastError);
            }
            finally
            {
                listener.Stop();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator BrokerProcess_PortChangeStopsRecordedBroker()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var oldPort = GetFreeTcpPort();
            var newPort = GetFreeTcpPort();

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(oldPort, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, oldPort, out var oldConnection));

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(newPort, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, newPort, out var newConnection));

                Assert.IsFalse(MCPBrokerProcessManager.TryProbeBroker(oldPort, oldConnection.Token, out _));
                Assert.IsTrue(MCPBrokerProcessManager.TryProbeBroker(newPort, newConnection.Token, out var health));
                Assert.AreEqual(newConnection.Pid, health.Pid);
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator BrokerProcess_ReplacesRecordedBrokerOnSamePortInOneCall()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var first));

                // Force the recorded broker to fail the identity probe (bogus pid) while keeping
                // its real token, so EnsureRunning must shut it down and relaunch on the SAME
                // port -- the shape of an in-place replacement (e.g. a protocol bump on upgrade).
                // Without the post-shutdown port-free wait this bails with "port in use" because
                // the just-closed socket lingers; with it, replacement completes in one call.
                WriteBrokerState(paths, pid: 999999, port: port, token: first.Token);

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var second));
                Assert.IsTrue(MCPBrokerProcessManager.TryProbeBroker(port, second.Token, out var health));
                Assert.AreEqual(second.Pid, health.Pid);
                Assert.AreNotEqual(first.Pid, second.Pid, "The recorded broker must be replaced by a new process on the same port.");
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator BrokerProcess_DoesNotKillUnverifiedPidFromStaleState()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var recordedPort = GetFreeTcpPort();
            var requestedPort = GetFreeTcpPort();
            var recordedPortOwner = new TcpListener(IPAddress.Loopback, recordedPort);
            var requestedPortOwner = new TcpListener(IPAddress.Loopback, requestedPort);
            Process unrelatedProcess = null;

            try
            {
                recordedPortOwner.Start();
                requestedPortOwner.Start();
                unrelatedProcess = StartLongRunningProcess();
                WriteBrokerState(paths, unrelatedProcess.Id, recordedPort, "stale-token");

                Assert.IsFalse(MCPBrokerProcessManager.EnsureRunning(requestedPort, string.Empty, paths));
                StringAssert.Contains("Port is already in use", MCPBrokerProcessManager.LastError);
                Assert.IsFalse(unrelatedProcess.HasExited, "A stale pid file must not let broker cleanup kill an unverified process.");
            }
            finally
            {
                recordedPortOwner.Stop();
                requestedPortOwner.Stop();
                StopProcess(unrelatedProcess);
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }

            yield return null;
        }

        [Test]
        public void BrokerProcessStateReader_AllowsStaleProtocolForUpgradeCleanup()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);

            try
            {
                WriteBrokerState(paths, pid: 12345, port: 8765, token: "stale-token", protocol: MCPBrokerProtocol.Version - 1);

                var method = typeof(MCPBrokerProcessManager).GetMethod(
                    "TryReadState",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(method);

                var args = new object[] { paths.PidFilePath, null };
                Assert.IsTrue((bool)method.Invoke(null, args),
                    "Stale protocol records must stay readable so upgrades can shut down old brokers.");
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerProcess_ReplacesPreviousProtocolBrokerOnSamePort()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            Process oldProcess = null;

            try
            {
                var mono = MCPBrokerProcessManager.ResolveMono(string.Empty);
                Assume.That(!string.IsNullOrEmpty(mono), "Unity-bundled Mono is required for broker process tests.");
                var oldProtocol = MCPBrokerProtocol.Version - 1;
                var currentSource = File.ReadAllText(paths.SourcePath);
                var oldSource = currentSource.Replace(
                    "private const int ProtocolVersion = " + MCPBrokerProtocol.Version + ";",
                    "private const int ProtocolVersion = " + oldProtocol + ";");
                Assert.AreNotEqual(currentSource, oldSource, "The old broker must advertise a different protocol.");
                var sourcePath = Path.Combine(root, "previous-broker.cs.txt");
                File.WriteAllText(sourcePath, oldSource);
                var oldPaths = new MCPBrokerProcessManager.MCPBrokerRuntimePaths(
                    root, paths.PidFilePath, Path.Combine(root, "previous-cache"), sourcePath);
                var compile = typeof(MCPBrokerProcessManager).GetMethod(
                    "EnsureBrokerExe", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(compile);
                var oldExe = (string)compile.Invoke(null, new object[] { oldPaths, mono });
                Assert.IsNotEmpty(oldExe, MCPBrokerProcessManager.LastError);

                var token = Guid.NewGuid().ToString("N");
                oldProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = mono,
                    Arguments = "\"" + oldExe + "\" --port " + port + " --token " + token,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                Assert.NotNull(oldProcess);
                var healthTask = WaitForBrokerHealthAsync(port, token);
                yield return WaitForTask(healthTask, 8f);
                Assert.AreEqual(oldProtocol, healthTask.Result.Value<int>("protocol"));
                Assert.AreEqual(oldProcess.Id, healthTask.Result.Value<int>("pid"));
                WriteBrokerState(paths, oldProcess.Id, port, token, oldProtocol);

                Assert.IsFalse(MCPBrokerProcessManager.TryProbeBroker(port, token, out _));
                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var replacement));
                Assert.IsTrue(MCPBrokerProcessManager.TryProbeBroker(port, replacement.Token, out var health));
                Assert.AreEqual(replacement.Pid, health.Pid);
                Assert.AreNotEqual(oldProcess.Id, replacement.Pid);
                Assert.IsTrue(oldProcess.HasExited, "The previous broker must not remain on the port after an upgrade.");
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                StopProcess(oldProcess);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_HandshakeNotificationsAreEmptyAcceptedAndPreserveJsonAndSse()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport transport = null;
            var wasPending = MCPToolListChangeNotifier.TryConsumePending();

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");
                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));
                transport = new MCPBrokerClientTransport(port, connection.Token);
                transport.OnRequestReceived += (request, sendResponse) =>
                {
                    if (request.Method.StartsWith("notifications/", StringComparison.Ordinal)) sendResponse(null);
                    else if (request.Method == "initialize") sendResponse(new MCPResponse
                    {
                        Id = request.Id,
                        Result = new Dictionary<string, object>
                        {
                            ["protocolVersion"] = "2024-11-05",
                            ["capabilities"] = new Dictionary<string, object>(),
                            ["serverInfo"] = new Dictionary<string, object> { ["name"] = "Broker handshake test", ["version"] = "1.0.0" }
                        }
                    });
                    else if (request.Method == "tools/list") sendResponse(new MCPResponse
                    {
                        Id = request.Id,
                        Result = new Dictionary<string, object> { ["tools"] = new[] { new Dictionary<string, object> { ["name"] = "probe", ["inputSchema"] = new Dictionary<string, object> { ["type"] = "object" } } } }
                    });
                    else sendResponse(CreateToolTextResponse(request.Id, "done"));
                };
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);
                MCPToolListChangeNotifier.RestorePending();

                var initialize = SendRpcAsync(port, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", true);
                yield return WaitForTask(initialize);
                Assert.AreEqual(HttpStatusCode.OK, initialize.Result.Status);
                Assert.AreEqual("application/json", initialize.Result.ContentType);
                Assert.AreEqual(1, JObject.Parse(initialize.Result.Body).Value<int>("id"));

                foreach (var acceptsSse in new[] { false, true })
                foreach (var method in new[] { "notifications/initialized", "notifications/cancelled", "notifications/custom" })
                {
                    var notification = SendRpcAsync(port, "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"params\":{}}", acceptsSse);
                    yield return WaitForTask(notification);
                    Assert.AreEqual(HttpStatusCode.Accepted, notification.Result.Status, method);
                    Assert.IsEmpty(notification.Result.Body, method + " must not produce a JSON-RPC response or SSE event.");
                }

                var tools = SendRpcAsync(port, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", false);
                yield return WaitForTask(tools);
                Assert.AreEqual(HttpStatusCode.OK, tools.Result.Status);
                Assert.AreEqual("application/json", tools.Result.ContentType);
                var toolsJson = JObject.Parse(tools.Result.Body);
                Assert.AreEqual(2, toolsJson.Value<int>("id"));
                Assert.AreEqual("probe", toolsJson["result"]["tools"][0].Value<string>("name"));

                var sse = SendRpcAsync(port, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"probe\",\"arguments\":{}}}", true);
                yield return WaitForTask(sse);
                Assert.AreEqual(HttpStatusCode.OK, sse.Result.Status);
                Assert.AreEqual("text/event-stream", sse.Result.ContentType);
                Assert.That(sse.Result.Body, Does.StartWith("data: " + MCPToolListChangeNotifier.NotificationJson + "\n\n"));
                Assert.That(sse.Result.Body, Does.Contain("\"id\":3"));
                Assert.That(sse.Result.Body, Does.Contain("done"));
                Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending());
            }
            finally
            {
                transport?.Dispose();
                MCPToolListChangeNotifier.TryConsumePending();
                if (wasPending) MCPToolListChangeNotifier.RestorePending();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_LongRunningRequestIsNotRedeliveredWithinSameSession()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport transport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                var calls = 0;
                transport = new MCPBrokerClientTransport(port, connection.Token);
                transport.OnRequestReceived += (request, sendResponse) =>
                {
                    calls++;
                    Task.Run(async () =>
                    {
                        await Task.Delay(2200);
                        sendResponse(CreateToolTextResponse(request.Id, "done"));
                    });
                };

                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                var requestTask = SendToolCallAsync(port, "get_editor_state");
                yield return WaitForTask(requestTask, 8f);

                Assert.That(requestTask.Result, Does.Contain("done"));
                Assert.AreEqual(1, calls, "The broker must not redeliver a slow request while the same Unity session is still active.");
            }
            finally
            {
                transport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_RedeliversActiveRequestToNewSession()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport firstTransport = null;
            MCPBrokerClientTransport secondTransport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                var firstReceived = new TaskCompletionSource<bool>();
                var totalCalls = 0;
                firstTransport = new MCPBrokerClientTransport(port, connection.Token);
                firstTransport.OnRequestReceived += (request, sendResponse) =>
                {
                    totalCalls++;
                    firstReceived.TrySetResult(true);
                    // Simulate domain reload: the first AppDomain disappears before it can respond.
                };

                var firstStart = firstTransport.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result);

                var requestTask = SendToolCallAsync(port, "execute_code");
                yield return WaitForTask(firstReceived.Task, 5f);

                firstTransport.Dispose();

                secondTransport = new MCPBrokerClientTransport(port, connection.Token);
                secondTransport.OnRequestReceived += (request, sendResponse) =>
                {
                    totalCalls++;
                    Assert.IsTrue(request.IsBrokerRedelivery);
                    sendResponse(CreateToolTextResponse(request.Id, "redelivered"));
                };

                var secondStart = secondTransport.StartAsync();
                yield return WaitForTask(secondStart);
                Assert.IsTrue(secondStart.Result);

                yield return WaitForTask(requestTask, 8f);

                Assert.That(requestTask.Result, Does.Contain("redelivered"));
                Assert.AreEqual(2, totalCalls);
            }
            finally
            {
                secondTransport?.Dispose();
                firstTransport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_ReturnsRetryableErrorWhenBackendIsUnavailable()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);

                var startedAt = DateTime.UtcNow;
                var requestTask = SendToolCallAsync(port, "get_editor_state");
                yield return WaitForTask(requestTask, 3f);
                var elapsed = DateTime.UtcNow - startedAt;

                Assert.Less(elapsed.TotalSeconds, 2.0, "Unavailable backend responses should not wait for the client timeout.");
                Assert.That(requestTask.Result, Does.Contain("\"id\":\"test\""));
                Assert.That(requestTask.Result, Does.Contain("\"code\":-32001"));
                Assert.That(requestTask.Result, Does.Contain("Unity MCP backend is reloading or reconnecting"));
                Assert.That(requestTask.Result, Does.Contain("\"retryable\":true"));
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_DetachMakesNewRequestsFailFast()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport transport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                transport = new MCPBrokerClientTransport(port, connection.Token);
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                yield return new WaitForSecondsRealtime(0.25f);
                transport.Dispose();
                transport = null;

                var startedAt = DateTime.UtcNow;
                var requestTask = SendToolCallAsync(port, "get_editor_state");
                yield return WaitForTask(requestTask, 3f);
                var elapsed = DateTime.UtcNow - startedAt;

                Assert.Less(elapsed.TotalSeconds, 2.0, "Detached backend responses should not wait for the client timeout.");
                Assert.That(requestTask.Result, Does.Contain("\"code\":-32001"));
                Assert.That(requestTask.Result, Does.Contain("\"retryable\":true"));
            }
            finally
            {
                transport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_StaleSessionIsSweptSoNewRequestsFailFast()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();

            try
            {
                // Shorten the staleness window for the test; the child broker inherits this env var.
                // [SetUp]/[TearDown] capture and restore it so it cannot leak into other tests.
                Environment.SetEnvironmentVariable(SessionStaleEnvVar, "1500");

                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                // Simulate an editor that attached and then CRASHED: it registers a session (so
                // AttachedSessions is non-empty) but never pulls and never sends detach.
                var attachTask = SendAttachAsync(port, connection.Token, Guid.NewGuid().ToString("N"));
                yield return WaitForTask(attachTask);

                // Wait past the (shortened) staleness window so the sweep evicts the dead session.
                yield return new WaitForSecondsRealtime(3f);

                // A new client request must now fail fast instead of being queued until the hold deadline.
                var startedAt = DateTime.UtcNow;
                var requestTask = SendToolCallAsync(port, "get_editor_state");
                yield return WaitForTask(requestTask, 4f);
                var elapsed = DateTime.UtcNow - startedAt;

                Assert.Less(elapsed.TotalSeconds, 2.0, "A crashed (never-detached) session must be swept so new requests fail fast, not hang.");
                Assert.That(requestTask.Result, Does.Contain("\"code\":-32001"));
                Assert.That(requestTask.Result, Does.Contain("\"retryable\":true"));
            }
            finally
            {
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [UnityTest]
        public IEnumerator BrokerTransport_DetachRejectsQueuedRequestsBehindInterruptedSession()
        {
            var root = CreateTempRoot();
            var paths = CreateBrokerPaths(root);
            var port = GetFreeTcpPort();
            MCPBrokerClientTransport firstTransport = null;
            MCPBrokerClientTransport secondTransport = null;

            try
            {
                Assume.That(!string.IsNullOrEmpty(MCPBrokerProcessManager.ResolveMono(string.Empty)),
                    "Unity-bundled Mono is required for broker process tests.");

                Assert.IsTrue(MCPBrokerProcessManager.EnsureRunning(port, string.Empty, paths), MCPBrokerProcessManager.LastError);
                Assert.IsTrue(MCPBrokerProcessManager.TryGetConnectionInfo(paths, port, out var connection));

                var firstReceived = new TaskCompletionSource<bool>();
                firstTransport = new MCPBrokerClientTransport(port, connection.Token);
                firstTransport.OnRequestReceived += (request, sendResponse) =>
                {
                    firstReceived.TrySetResult(true);
                    // Simulate domain reload before the active request can return.
                };

                var firstStart = firstTransport.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result);

                var interruptedRequest = SendToolCallAsync(port, "execute_code");
                yield return WaitForTask(firstReceived.Task, 5f);

                var queuedRequest = SendToolCallAsync(port, "get_editor_state");
                yield return new WaitForSecondsRealtime(0.1f);

                firstTransport.Dispose();
                firstTransport = null;

                yield return WaitForTask(queuedRequest, 3f);
                Assert.That(queuedRequest.Result, Does.Contain("\"code\":-32001"));
                Assert.That(queuedRequest.Result, Does.Contain("\"retryable\":true"));

                secondTransport = new MCPBrokerClientTransport(port, connection.Token);
                secondTransport.OnRequestReceived += (request, sendResponse) =>
                {
                    Assert.IsTrue(request.IsBrokerRedelivery);
                    sendResponse(CreateToolTextResponse(request.Id, "redelivered"));
                };

                var secondStart = secondTransport.StartAsync();
                yield return WaitForTask(secondStart);
                Assert.IsTrue(secondStart.Result);

                yield return WaitForTask(interruptedRequest, 8f);
                Assert.That(interruptedRequest.Result, Does.Contain("redelivered"));
            }
            finally
            {
                secondTransport?.Dispose();
                firstTransport?.Dispose();
                MCPBrokerProcessManager.Stop(paths);
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void BrokerRedeliveryResponse_UsesRecoveryInfoAndDoesNotRerunTool()
        {
            DomainReloadHandler.StoreRecoveryInfo("execute_code", MCPToolCallStatus.Success.ToString(), "Compilation finished after reload.");

            var response = MCPServerService.TryCreateBrokerRedeliveryResponse(new MCPRequest
            {
                Id = "1",
                Method = "tools/call",
                IsBrokerRedelivery = true,
                Params = new Dictionary<string, object> { ["name"] = "execute_code" }
            });

            Assert.NotNull(response);
            Assert.IsNull(response.Error);
            var result = response.Result as Dictionary<string, object>;
            Assert.NotNull(result);
            Assert.AreEqual(false, result["isError"]);
            Assert.That(SimpleJsonHelper.Serialize(result), Does.Contain("Compilation finished after reload."));
        }

        [Test]
        public void BrokerRedeliveryResponse_ReturnsGenericErrorWhenRecoveryIsUnavailable()
        {
            DomainReloadHandler.GetLastRecoveryInfo(consume: true);

            var response = MCPServerService.TryCreateBrokerRedeliveryResponse(new MCPRequest
            {
                Id = "1",
                Method = "tools/call",
                IsBrokerRedelivery = true,
                Params = new Dictionary<string, object> { ["name"] = "execute_code" }
            });

            Assert.NotNull(response);
            var result = response.Result as Dictionary<string, object>;
            Assert.NotNull(result);
            Assert.AreEqual(true, result["isError"]);
            Assert.That(SimpleJsonHelper.Serialize(result), Does.Contain("was not re-run automatically"));
        }

        [Test]
        public void BrokerSource_IsVisibleToAssetDatabaseForUnityPackageExport()
        {
            var assetPath = ResolveBrokerSourceAssetPath();
            var source = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);

            Assert.NotNull(source, "Broker source TextAsset not found at " + assetPath);
            Assert.That(source.text, Does.Contain("funplay-unity-mcp-broker"));
        }

        private static MCPResponse CreateToolTextResponse(object id, string text)
        {
            return new MCPResponse
            {
                Id = id,
                Result = new Dictionary<string, object>
                {
                    ["content"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = text
                        }
                    }
                }
            };
        }

        private static async Task<string> SendToolCallAsync(int port, string toolName)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
            using (var content = new StringContent(
                       "{\"jsonrpc\":\"2.0\",\"id\":\"test\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + toolName + "\",\"arguments\":{}}}",
                       Encoding.UTF8,
                       "application/json"))
            {
                var response = await client.PostAsync("http://127.0.0.1:" + port + "/", content);
                return await response.Content.ReadAsStringAsync();
            }
        }

        private const string SessionStaleEnvVar = "FUNPLAY_BROKER_SESSION_STALE_MS";

        private static async Task<(HttpStatusCode Status, string Body, string ContentType)> SendRpcAsync(int port, string body, bool acceptsSse)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            using (var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port + "/"))
            {
                request.Headers.TryAddWithoutValidation("Accept", acceptsSse ? "application/json, text/event-stream" : "application/json");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request))
                    return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Content.Headers.ContentType?.MediaType);
            }
        }

        private static async Task<JObject> WaitForBrokerHealthAsync(int port, string token)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) })
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(5))
                {
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port + MCPBrokerProtocol.HealthPath))
                        {
                            request.Headers.TryAddWithoutValidation(MCPBrokerProtocol.TokenHeader, token);
                            using (var response = await client.SendAsync(request))
                            {
                                response.EnsureSuccessStatusCode();
                                return JObject.Parse(await response.Content.ReadAsStringAsync());
                            }
                        }
                    }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                    await Task.Delay(50);
                }
            }
            throw new TimeoutException("The previous-protocol broker did not become healthy.");
        }

        private static async Task SendAttachAsync(int port, string token, string session)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            using (var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port + "/_funplay/broker/attach"))
            {
                request.Headers.TryAddWithoutValidation(MCPBrokerProtocol.TokenHeader, token);
                request.Headers.TryAddWithoutValidation(MCPBrokerProtocol.SessionHeader, session);
                request.Content = new StringContent(string.Empty);
                await client.SendAsync(request);
            }
        }

        private static IEnumerator WaitForTask(Task task, float timeoutSeconds = 5f)
        {
            var start = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                    throw new TimeoutException("Timed out waiting for async test task.");

                yield return null;
            }

            if (task.IsFaulted)
                throw task.Exception;
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static MCPBrokerProcessManager.MCPBrokerRuntimePaths CreateBrokerPaths(string root)
        {
            var cache = Path.Combine(root, "cache");
            return new MCPBrokerProcessManager.MCPBrokerRuntimePaths(
                root,
                Path.Combine(root, "broker.pid"),
                cache,
                ResolveBrokerSourcePath());
        }

        // These tests read the broker source straight off disk (and via AssetDatabase) so a
        // running broker/exported package can be verified against the exact same script. The
        // repo checks this package out at Assets/unity-mcp, but consumers install it as a real
        // UPM package (embedded, git, or registry) rooted at Packages/<name> or
        // Library/PackageCache/<name>@version -- resolve through PackageInfo first and only fall
        // back to the repo's own dev layout.
        private const string BrokerSourceRelativePath = "Editor/MCP/Server/Broker/keepalive-broker.cs.txt";

        private static string ResolveBrokerSourcePath()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(MCPBrokerProcessManager).Assembly);
            var path = packageInfo != null
                ? Path.Combine(packageInfo.resolvedPath, BrokerSourceRelativePath.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(Application.dataPath, "unity-mcp", BrokerSourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), "Broker source was not found at " + path);
            return path;
        }

        private static string ResolveBrokerSourceAssetPath()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(MCPBrokerProcessManager).Assembly);
            return packageInfo != null
                ? packageInfo.assetPath + "/" + BrokerSourceRelativePath
                : "Assets/unity-mcp/" + BrokerSourceRelativePath;
        }

        private static void WriteBrokerState(
            MCPBrokerProcessManager.MCPBrokerRuntimePaths paths,
            int pid,
            int port,
            string token,
            int protocol = MCPBrokerProtocol.Version)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.PidFilePath));
            File.WriteAllText(paths.PidFilePath,
                pid + "\n" +
                port + "\n" +
                token + "\n" +
                protocol + "\n");
        }

        private static Process StartLongRunningProcess()
        {
            var startInfo = Application.platform == RuntimePlatform.WindowsEditor
                ? new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/C ping -n 60 127.0.0.1 > nul",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
                : new ProcessStartInfo
                {
                    FileName = "/bin/sleep",
                    Arguments = "60",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            var process = Process.Start(startInfo);
            Assert.NotNull(process, "Failed to start a long-running test process.");
            return process;
        }

        private static void StopProcess(Process process)
        {
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill();
                process.WaitForExit(2000);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private static string CreateTempRoot()
        {
            var path = Path.Combine(Path.GetTempPath(), "FunplayBrokerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempRoot(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
