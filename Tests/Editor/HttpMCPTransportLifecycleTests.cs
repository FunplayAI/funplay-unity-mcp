// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Funplay.Editor
{
    public sealed class HttpMCPTransportLifecycleTests
    {
        private const string ServerName = "Funplay MCP Server - Test Project";
        private const string ProjectIdentityA = "project-a";
        private const string ProjectIdentityB = "project-b";

        [Test]
        public void ClientDisconnectDetection_CoversExpectedResponseWriteFailures()
        {
            Assert.IsTrue(HttpMCPTransport.IsClientDisconnectException(
                new IOException("Unable to read data from the transport connection: The socket has been shut down.")));
            Assert.IsTrue(HttpMCPTransport.IsClientDisconnectException(
                new ObjectDisposedException("NetworkStream")));
            Assert.IsFalse(HttpMCPTransport.IsClientDisconnectException(
                new InvalidOperationException("Unexpected transport failure.")));
        }

        [Test]
        public void RecentActivityBadge_InterruptedIsNotDisplayedAsOk()
        {
            Assert.AreEqual("OK", FunplayMCPRecentActivityPanel.GetBadgeText(MCPToolCallStatus.Success));
            Assert.AreEqual("INT", FunplayMCPRecentActivityPanel.GetBadgeText(MCPToolCallStatus.Interrupted));
            Assert.AreEqual("ERR", FunplayMCPRecentActivityPanel.GetBadgeText(MCPToolCallStatus.Error));
        }

        [Test]
        public void RecentActivityResult_RendersResponseEnvelopeWithoutJsonSyntax()
        {
            const string json = "{\"success\":true,\"message\":\"Loaded 2 items.\",\"data\":{\"count\":2,\"next_page\":null},\"items\":[{\"name\":\"First\"},{\"name\":\"Second\"}]}";
            var log = new MCPInteractionLog(1);

            log.Add("test_json", MCPToolCallStatus.Success, json);

            var entry = log.GetEntries()[0];
            Assert.IsTrue(entry.IsJsonResult);
            Assert.AreEqual(json, entry.ResultSummary);
            Assert.That(entry.DisplayResult, Does.StartWith("Loaded 2 items."));
            Assert.That(entry.DisplayResult, Does.Contain("Count: 2"));
            Assert.That(entry.DisplayResult, Does.Contain("Next page: —"));
            Assert.That(entry.DisplayResult, Does.Contain("Items:"));
            Assert.That(entry.DisplayResult, Does.Contain("Name: First"));
            Assert.That(entry.DisplayResult, Does.Not.Contain("\"success\""));
            Assert.That(entry.DisplayResult, Does.Not.Contain("{"));
            Assert.That(entry.DisplayResult, Does.Not.Contain("}"));
            Assert.That(entry.DisplayResult, Does.Not.Contain("["));
            Assert.That(entry.DisplayResult, Does.Not.Contain("]"));
        }

        [Test]
        public void RecentActivityResult_RendersErrorDetailsAsLabelsAndValues()
        {
            const string json = "{\"success\":false,\"code\":\"COMPILATION_FAILED\",\"error\":\"COMPILATION_FAILED\",\"message\":\"Compilation failed.\",\"data\":{\"compiler\":\"Roslyn\",\"errors\":[{\"line\":11,\"column\":13,\"text\":\"Missing semicolon\"}]}}";
            var log = new MCPInteractionLog(1);

            log.Add("execute_code", MCPToolCallStatus.Error, json);

            var display = log.GetEntries()[0].DisplayResult;
            Assert.That(display, Does.StartWith("Compilation failed."));
            Assert.That(display, Does.Contain("Code: COMPILATION_FAILED"));
            Assert.That(display, Does.Contain("Compiler: Roslyn"));
            Assert.That(display, Does.Contain("Errors:"));
            Assert.That(display, Does.Contain("Line: 11"));
            Assert.That(display, Does.Contain("Text: Missing semicolon"));
            Assert.AreEqual(1, display.Split(new[] { "COMPILATION_FAILED" }, StringSplitOptions.None).Length - 1);
        }

        [Test]
        public void RecentActivityResult_RendersRootArrayAsNumberedItems()
        {
            const string json = "[{\"tool_name\":\"first\",\"enabled\":true},\"ready\"]";
            var log = new MCPInteractionLog(1);

            log.Add("array", MCPToolCallStatus.Success, json);

            var display = log.GetEntries()[0].DisplayResult;
            Assert.That(display, Does.Contain("1."));
            Assert.That(display, Does.Contain("Tool name: first"));
            Assert.That(display, Does.Contain("Enabled: Yes"));
            Assert.That(display, Does.Contain("2. ready"));
            Assert.That(display, Does.Not.Contain("["));
            Assert.That(display, Does.Not.Contain("]"));
        }

        [Test]
        public void RecentActivityResult_RendersNestedJsonStringAsStructuredDetails()
        {
            const string json = "{\"success\":true,\"message\":\"Recovery status loaded.\",\"data\":{\"result\":\"{\\\"recorded\\\":true,\\\"tool\\\":\\\"request_recompile\\\",\\\"details\\\":[1,2]}\",\"compiler\":\"Roslyn\"}}";
            var log = new MCPInteractionLog(1);

            log.Add("execute_code", MCPToolCallStatus.Success, json);

            var display = log.GetEntries()[0].DisplayResult;
            Assert.That(display, Does.Contain("Result:"));
            Assert.That(display, Does.Contain("Recorded: Yes"));
            Assert.That(display, Does.Contain("Tool: request_recompile"));
            Assert.That(display, Does.Contain("Details:"));
            Assert.That(display, Does.Contain("1. 1"));
            Assert.That(display, Does.Contain("Compiler: Roslyn"));
            Assert.That(display, Does.Not.Contain("\\\""));
            Assert.That(display, Does.Not.Contain("{"));
            Assert.That(display, Does.Not.Contain("}"));
        }

        [Test]
        public void RecentActivityResult_LeavesPlainTextAndMalformedJsonCompact()
        {
            var log = new MCPInteractionLog(2);
            log.Add("plain", MCPToolCallStatus.Success, "No compilation errors detected.");
            log.Add("malformed", MCPToolCallStatus.Error, "{\"success\":false");

            var entries = log.GetEntries();
            Assert.IsFalse(entries[0].IsJsonResult);
            Assert.AreEqual(entries[0].ResultSummary, entries[0].DisplayResult);
            Assert.IsFalse(entries[1].IsJsonResult);
            Assert.AreEqual(entries[1].ResultSummary, entries[1].DisplayResult);
        }

        [Test]
        public void RecentActivityResult_BoundsLargeFormattedJsonPreview()
        {
            var json = "{\"value\":\"" + new string('x', 5000) + "\"}";
            var log = new MCPInteractionLog(1);

            log.Add("large_json", MCPToolCallStatus.Success, json);

            var entry = log.GetEntries()[0];
            Assert.IsTrue(entry.IsJsonResult);
            Assert.AreEqual(200, entry.ResultSummary.Length);
            Assert.LessOrEqual(entry.DisplayResult.Length, MCPInteractionLog.MaxDisplayResultCharacters);
            Assert.That(entry.DisplayResult, Does.EndWith("... (truncated)"));
        }

        [Test]
        public void RecentActivityDisplay_SeparatesMessagesSectionsPropertiesAndValues()
        {
            const string display =
                "Loaded successfully.\n\n" +
                "Result:\n" +
                "  Job ID: abc-123\n" +
                "  Status: passed\n" +
                "  Has filters: Yes\n" +
                "Items:\n" +
                "  1. ready\n" +
                "... (truncated)";

            var lines = FunplayMCPRecentActivityPanel.ParseStructuredDisplay(display);

            Assert.AreEqual(9, lines.Count);
            Assert.AreEqual(MCPActivityDisplayLineKind.Message, lines[0].Kind);
            Assert.AreEqual("Loaded successfully.", lines[0].Value);
            Assert.AreEqual(MCPActivityDisplayLineKind.Spacer, lines[1].Kind);
            Assert.AreEqual(MCPActivityDisplayLineKind.Section, lines[2].Kind);
            Assert.AreEqual("Result", lines[2].Label);
            Assert.AreEqual(MCPActivityDisplayLineKind.Property, lines[3].Kind);
            Assert.AreEqual(1, lines[3].Depth);
            Assert.AreEqual("Job ID", lines[3].Label);
            Assert.AreEqual("abc-123", lines[3].Value);
            Assert.AreEqual(MCPActivityDisplayLineKind.Property, lines[4].Kind);
            Assert.AreEqual("Status", lines[4].Label);
            Assert.AreEqual("passed", lines[4].Value);
            Assert.AreEqual(MCPActivityDisplayLineKind.NumberedItem, lines[7].Kind);
            Assert.AreEqual(1, lines[7].Depth);
            Assert.AreEqual("1.", lines[7].Label);
            Assert.AreEqual("ready", lines[7].Value);
            Assert.AreEqual(MCPActivityDisplayLineKind.Truncated, lines[8].Kind);
        }

        [Test]
        public void RecentActivityDisplay_KeepsColonsInsidePropertyValues()
        {
            const string display = "Endpoint: http://127.0.0.1:25144/mcp";

            var lines = FunplayMCPRecentActivityPanel.ParseStructuredDisplay(display);

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual(MCPActivityDisplayLineKind.Property, lines[0].Kind);
            Assert.AreEqual("Endpoint", lines[0].Label);
            Assert.AreEqual("http://127.0.0.1:25144/mcp", lines[0].Value);
        }

        [Test]
        public void InterruptedToolRecoveryStatus_EmptyContinuationIsInterrupted()
        {
            Assert.AreEqual(
                MCPToolCallStatus.Interrupted,
                MCPServerService.DetermineInterruptedToolRecoveryStatus(null));
            Assert.AreEqual(
                MCPToolCallStatus.Success,
                MCPServerService.DetermineInterruptedToolRecoveryStatus("Continuation completed."));
            Assert.AreEqual(
                MCPToolCallStatus.Error,
                MCPServerService.DetermineInterruptedToolRecoveryStatus(ToolResultFormatter.Error("TEST_ERROR")));
        }

        [Test]
        public void ShutdownCancellation_CoversTaskCancellationButNotRealFailures()
        {
            // A disposed editor-thread pump cancels the queued TaskCompletionSource, so the awaiting
            // request observes TaskCanceledException -- that must not be reported as a server error.
            Assert.IsTrue(MCPServerService.IsShutdownCancellation(new TaskCanceledException()));
            Assert.IsTrue(MCPServerService.IsShutdownCancellation(new OperationCanceledException()));
            Assert.IsFalse(MCPServerService.IsShutdownCancellation(new InvalidOperationException("boom")));
            Assert.IsFalse(MCPServerService.IsShutdownCancellation(null));
        }

        [Test]
        public void BackendUnavailableResponse_MatchesBrokerRetryablePayload()
        {
            var response = MCPServerService.CreateBackendUnavailableResponse("req-7");

            Assert.AreEqual("req-7", response.Id);
            Assert.IsNotNull(response.Error);
            Assert.AreEqual(-32001, response.Error.Code);
            Assert.AreEqual(
                "Unity MCP backend is reloading or reconnecting. Retry shortly.",
                response.Error.Message);

            var data = response.Error.Data as Dictionary<string, object>;
            Assert.IsNotNull(data, "Error data should carry the broker's retryable payload.");
            Assert.AreEqual(true, data["retryable"]);
            Assert.AreEqual("unity_backend_reloading", data["reason"]);
        }

        [Test]
        public void SettingsRestartCompletion_CoalescesWaitersUntilFinalCompletion()
        {
            var completion = new MCPServerRestartCompletion();

            var first = completion.Begin();
            var second = completion.Begin();

            Assert.AreSame(first, second);
            Assert.IsFalse(first.IsCompleted);

            completion.Complete(true);

            Assert.IsTrue(first.IsCompleted);
            Assert.IsTrue(first.Result);
        }

        [Test]
        public void SettingsRestartCompletion_AfterCompletionReturnsSettledStateUntilNextRestart()
        {
            var completion = new MCPServerRestartCompletion();
            var first = completion.Begin();
            completion.Complete(false);

            Assert.IsFalse(first.Result);
            Assert.IsTrue(completion.CurrentOrCompleted(true).Result);

            var next = completion.Begin();
            Assert.AreNotSame(first, next);
            Assert.IsFalse(next.IsCompleted);
        }

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
        public IEnumerator StartAsync_KnownForeignConflictSkipsTheLongRetryAndPrefersLastFallback()
        {
            // The conflict was learned on a previous start (this session already fell back), so the
            // full ~10s teardown retry window is skipped and the previous fallback port is tried
            // first -- a client connected to it survives the domain reload on the same port.
            var requestedPort = GetFreeTcpPort();
            var owner = new HttpMCPTransport(requestedPort, ServerName, ProjectIdentityA);
            var preferredPort = GetFreeTcpPort();
            var transport = new HttpMCPTransport(
                requestedPort, ServerName, ProjectIdentityA,
                new PortFallbackHints(requestedPortKnownForeign: true, preferredFallbackPort: preferredPort));

            try
            {
                var ownerStart = owner.StartAsync();
                yield return WaitForTask(ownerStart);
                Assert.IsTrue(ownerStart.Result);

                var stopwatch = Stopwatch.StartNew();
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask, 10f);
                stopwatch.Stop();

                Assert.IsTrue(startTask.Result, "The fallback bind should succeed on the preferred port.");
                Assert.AreEqual(preferredPort, transport.Port);
                Assert.Less(
                    stopwatch.Elapsed,
                    TimeSpan.FromSeconds(6),
                    "A known-foreign conflict must not wait out the full teardown retry window.");
            }
            finally
            {
                transport.Dispose();
                owner.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator StartAsync_TwoProjectsContendingForOneStablePort_SecondUsesFallback()
        {
            // Models two upgraded projects that both retained the old pinned port. Project A owns
            // the stable endpoint; project B must serve elsewhere and its one-click configuration
            // must remain blocked until the user gives it a stable, unambiguous port.
            int stablePort;
            Assert.IsTrue(FunplayFreePortScanner.TryFindFreePort(24000, 256, out stablePort));

            var projectA = new HttpMCPTransport(stablePort, ServerName, ProjectIdentityA);
            var projectB = new HttpMCPTransport(
                stablePort,
                ServerName,
                ProjectIdentityB,
                new PortFallbackHints(requestedPortKnownForeign: true, preferredFallbackPort: 0));

            projectA.OnRequestReceived += (request, sendResponse) =>
                HandleInitializeRequest(request, sendResponse, ProjectIdentityA);
            projectB.OnRequestReceived += (request, sendResponse) =>
                HandleInitializeRequest(request, sendResponse, ProjectIdentityB);

            try
            {
                var firstStart = projectA.StartAsync();
                yield return WaitForTask(firstStart);
                Assert.IsTrue(firstStart.Result);

                var secondStart = projectB.StartAsync();
                yield return WaitForTask(secondStart, 10f);
                Assert.IsTrue(secondStart.Result, "The second project should bind a nearby fallback port.");
                Assert.AreNotEqual(stablePort, projectB.Port);
                Assert.IsTrue(FunplayMCPClientConfigPanel.ShouldBlockConfigurationForFallback(
                    isRunning: true,
                    resolvedPort: stablePort,
                    activePort: projectB.Port));

                var projectAProbe = SendInitializeRequestAsync(stablePort);
                var projectBProbe = SendInitializeRequestAsync(projectB.Port);
                yield return WaitForTask(projectAProbe);
                yield return WaitForTask(projectBProbe);
                Assert.That(projectAProbe.Result, Does.Contain(ProjectIdentityA));
                Assert.That(projectBProbe.Result, Does.Contain(ProjectIdentityB));
            }
            finally
            {
                projectB.Dispose();
                projectA.Dispose();
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

        [UnityTest]
        public IEnumerator SseAcceptingRequest_DeliversToolsListChangedOnce()
        {
            var port = GetFreeTcpPort();
            var transport = new HttpMCPTransport(port, ServerName, ProjectIdentityA);
            transport.OnRequestReceived += (request, sendResponse) =>
            {
                sendResponse(new MCPResponse
                {
                    Id = request.Id,
                    Result = new Dictionary<string, object> { ["ok"] = true }
                });
            };

            try
            {
                var startTask = transport.StartAsync();
                yield return WaitForTask(startTask);
                Assert.IsTrue(startTask.Result);

                MCPToolListChangeNotifier.RestorePending();

                var firstRequest = SendToolListRequestAsync(port, acceptSse: true);
                yield return WaitForTask(firstRequest, 2f);
                Assert.AreEqual("text/event-stream", firstRequest.Result.ContentType);
                Assert.That(firstRequest.Result.Body, Does.Contain(MCPToolListChangeNotifier.NotificationJson));
                Assert.That(firstRequest.Result.Body, Does.Contain("\"id\":\"test\""));
                Assert.Less(
                    firstRequest.Result.Body.IndexOf(MCPToolListChangeNotifier.NotificationJson, StringComparison.Ordinal),
                    firstRequest.Result.Body.IndexOf("\"id\":\"test\"", StringComparison.Ordinal));

                var secondRequest = SendToolListRequestAsync(port, acceptSse: true);
                yield return WaitForTask(secondRequest, 2f);
                Assert.AreEqual("application/json", secondRequest.Result.ContentType);
                Assert.That(secondRequest.Result.Body, Does.Not.Contain(MCPToolListChangeNotifier.NotificationJson));
                Assert.That(secondRequest.Result.Body, Does.Contain("\"id\":\"test\""));
            }
            finally
            {
                while (MCPToolListChangeNotifier.TryConsumePending())
                {
                }

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

        private static async Task<HttpResult> SendToolListRequestAsync(int port, bool acceptSse)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
            using (var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/"))
            {
                request.Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":\"test\",\"method\":\"tools/list\",\"params\":{}}",
                    Encoding.UTF8,
                    "application/json");

                if (acceptSse)
                    request.Headers.Accept.ParseAdd("text/event-stream");

                var response = await client.SendAsync(request);
                return new HttpResult
                {
                    ContentType = response.Content.Headers.ContentType?.MediaType,
                    Body = await response.Content.ReadAsStringAsync()
                };
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

        private sealed class HttpResult
        {
            public string ContentType;
            public string Body;
        }
    }
}
