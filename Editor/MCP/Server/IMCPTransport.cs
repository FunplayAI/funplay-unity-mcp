// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Interface for MCP transport layer implementations (HTTP, stdio, etc.)
    /// </summary>
    internal interface IMCPTransport : IDisposable
    {
        bool IsRunning { get; }
        bool IsAttachedToExistingServer { get; }

        /// <summary>
        /// Port clients must actually reach this transport on. It can differ from the port the
        /// transport was constructed with when that one was taken and a free port was used instead.
        /// </summary>
        int Port { get; }
        Task<bool> StartAsync(CancellationToken ct = default);
        Task StopAsync();

        /// <summary>
        /// Synchronously release transport resources. Required during
        /// <c>AssemblyReloadEvents.beforeAssemblyReload</c> because Unity unloads the AppDomain
        /// immediately after callbacks return and does not await pending Tasks.
        /// </summary>
        void Stop();

        event Action<MCPRequest, Action<MCPResponse>> OnRequestReceived;
    }

    internal class MCPRequest
    {
        public string JsonRpc { get; set; } = "2.0";
        public object Id { get; set; }
        public string Method { get; set; }
        public Dictionary<string, object> Params { get; set; }
        public bool IsBrokerRedelivery { get; set; }
    }

    internal class MCPResponse
    {
        public string JsonRpc { get; set; } = "2.0";
        public object Id { get; set; }
        public object Result { get; set; }
        public MCPError Error { get; set; }
    }

    internal class MCPError
    {
        public int Code { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }
    }
}
