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
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Funplay.Editor
{
    public sealed class MCPBrokerTransportTests
    {
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
            var source = AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Assets/unity-mcp/Editor/MCP/Server/Broker/keepalive-broker.cs.txt");

            Assert.NotNull(source);
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

        private static string ResolveBrokerSourcePath()
        {
            var path = Path.Combine(
                Application.dataPath,
                "unity-mcp",
                "Editor",
                "MCP",
                "Server",
                "Broker",
                "keepalive-broker.cs.txt");
            Assert.IsTrue(File.Exists(path), "Broker source was not found at " + path);
            return path;
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
