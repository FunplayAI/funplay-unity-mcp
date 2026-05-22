// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Funplay.Editor
{
    public sealed class HttpMCPTransportLifecycleTests
    {
        private const string ServerName = "Funplay MCP Server - Test Project";
        private const string ProjectIdentityA = "project-a";

        [UnityTest]
        public IEnumerator StartAsync_WhenPortIsAlreadyOwned_ReturnsFalseWithoutStoppingOwner()
        {
            var port = GetFreeTcpPort();
            var firstTransport = new HttpMCPTransport(port, ServerName, ProjectIdentityA);
            var secondTransport = new HttpMCPTransport(port, ServerName, ProjectIdentityA);

            firstTransport.OnRequestReceived += (request, sendResponse) =>
                HandleInitializeRequest(request, sendResponse, ProjectIdentityA);

            try
            {
                var firstStart = firstTransport.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result, "The first transport should bind a free port.");

                var stopwatch = Stopwatch.StartNew();
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900)))
                {
                    var secondStart = secondTransport.StartAsync(cts.Token);
                    yield return WaitForTask(secondStart);
                    Assert.IsFalse(secondStart.Result, "A second transport must not report running when it does not own the listener.");
                }
                stopwatch.Stop();

                Assert.IsFalse(secondTransport.IsAttachedToExistingServer);
                Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(2));

                secondTransport.Stop();

                var probeTask = SendInitializeRequestAsync(port);
                yield return WaitForTask(probeTask);
                Assert.That(
                    probeTask.Result,
                    Does.Contain(ProjectIdentityA),
                    "Stopping a failed second transport must not stop the owning listener.");
            }
            finally
            {
                secondTransport.Dispose();
                firstTransport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Stop_ReleasesOwnedPortForRestart()
        {
            var port = GetFreeTcpPort();
            var firstTransport = new HttpMCPTransport(port, ServerName, ProjectIdentityA);
            var secondTransport = new HttpMCPTransport(port, ServerName, ProjectIdentityA);

            firstTransport.OnRequestReceived += (request, sendResponse) =>
                HandleInitializeRequest(request, sendResponse, ProjectIdentityA);

            try
            {
                var firstStart = firstTransport.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result);

                firstTransport.Stop();

                var secondStart = secondTransport.StartAsync();
                yield return WaitForTask(secondStart);
                Assert.IsTrue(secondStart.Result, "Stopping the owner should release the port for a fresh transport.");
            }
            finally
            {
                secondTransport.Dispose();
                firstTransport.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator StartAsync_UnresponsivePortOwnerFailsWithoutReportingRunning()
        {
            var port = GetFreeTcpPort();
            using (var listener = CreateHttpListener(port))
            using (var listenerCts = new CancellationTokenSource())
            {
                listener.Start();
                var serverTask = HoldRequestsOpenAsync(listener, listenerCts.Token);
                var transport = new HttpMCPTransport(port, ServerName, ProjectIdentityA);

                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200)))
                    {
                        var startTask = transport.StartAsync(cts.Token);
                        yield return WaitForTask(startTask);
                        Assert.IsFalse(startTask.Result);
                    }

                    Assert.IsFalse(transport.IsRunning);
                }
                finally
                {
                    transport.Dispose();
                    listenerCts.Cancel();
                    listener.Close();
                    serverTask.Wait(100);
                }
            }
        }

        [UnityTest]
        public IEnumerator RequestWithoutSubscriber_ReturnsServerNotReadyErrorWithoutWaitingForTimeout()
        {
            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ServerName, ProjectIdentityA);

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                var stopwatch = Stopwatch.StartNew();
                var probeTask = SendInitializeRequestAsync(port);
                yield return WaitForTask(probeTask, 2f);
                stopwatch.Stop();

                Assert.That(probeTask.Result, Does.Contain("MCP server is stopping or not ready."));
                Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(2));
            }
            finally
            {
                transport.Dispose();
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

        private static void HandleInitializeRequest(
            MCPRequest request,
            Action<MCPResponse> sendResponse,
            string projectIdentity)
        {
            if (request.Method != "initialize")
            {
                sendResponse(new MCPResponse
                {
                    Id = request.Id,
                    Error = new MCPError { Code = -32601, Message = "Method not found" }
                });
                return;
            }

            sendResponse(new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object>
                {
                    ["serverInfo"] = new Dictionary<string, object>
                    {
                        ["name"] = ServerName,
                        ["version"] = "test"
                    },
                    ["funplay"] = new Dictionary<string, object>
                    {
                        ["projectIdentity"] = projectIdentity,
                        ["projectIdentityVersion"] = FunplayProjectIdentity.IdentityVersion
                    }
                }
            });
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

        private static HttpListener CreateHttpListener(int port)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add($"http://localhost:{port}/");
            return listener;
        }

        private static async Task<string> SendInitializeRequestAsync(int port)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) })
            using (var content = new StringContent(
                       "{\"jsonrpc\":\"2.0\",\"id\":\"test\",\"method\":\"initialize\",\"params\":{}}",
                       Encoding.UTF8,
                       "application/json"))
            {
                var response = await client.PostAsync($"http://127.0.0.1:{port}/", content);
                return await response.Content.ReadAsStringAsync();
            }
        }

        private static async Task HoldRequestsOpenAsync(HttpListener listener, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), ct);
                            context.Response.StatusCode = 204;
                            context.Response.Close();
                        }
                        catch
                        {
                            try { context.Response.Close(); } catch { }
                        }
                    }, ct);
                }
            }
            catch
            {
                // Listener shutdown during test cleanup.
            }
        }
    }
}
