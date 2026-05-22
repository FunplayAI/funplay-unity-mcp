// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class HttpMCPTransportLifecycleTests
    {
        [Test]
        public async Task StartAsync_AttachesToExistingSameProjectFunplayServer()
        {
            var port = GetFreeTcpPort();
            var serverName = "Funplay MCP Server - " + Application.productName;
            var firstTransport = new HttpMCPTransport(port, serverName);
            var secondTransport = new HttpMCPTransport(port, serverName);

            firstTransport.OnRequestReceived += HandleInitializeRequest;

            try
            {
                Assert.IsTrue(await firstTransport.StartAsync(), "The first transport should bind a free port.");

                var stopwatch = Stopwatch.StartNew();
                Assert.IsTrue(
                    await secondTransport.StartAsync(),
                    "A second transport for the same project should attach to the existing Funplay server instead of timing out.");
                stopwatch.Stop();

                Assert.Less(
                    stopwatch.Elapsed,
                    TimeSpan.FromSeconds(2),
                    "Attach should avoid the 10 second address-in-use retry window.");

                secondTransport.Stop();

                Assert.That(
                    await SendInitializeRequestAsync(port),
                    Does.Contain("Funplay MCP Server - " + Application.productName),
                    "Stopping an attached transport must not stop the owning listener.");
            }
            finally
            {
                secondTransport.Dispose();
                firstTransport.Dispose();
            }
        }

        private static void HandleInitializeRequest(MCPRequest request, Action<MCPResponse> sendResponse)
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
                        ["name"] = "Funplay MCP Server - " + Application.productName,
                        ["version"] = "test"
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
    }
}
