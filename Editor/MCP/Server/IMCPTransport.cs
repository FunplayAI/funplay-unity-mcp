// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameBooom.Editor.MCP.Server
{
    /// <summary>
    /// Interface for MCP transport layer implementations (HTTP, stdio, etc.)
    /// </summary>
    internal interface IMCPTransport : IDisposable
    {
        bool IsRunning { get; }
        Task<bool> StartAsync(CancellationToken ct = default);
        Task StopAsync();
        event Action<MCPRequest, Action<MCPResponse>> OnRequestReceived;
    }

    internal class MCPRequest
    {
        public string JsonRpc { get; set; } = "2.0";
        public object Id { get; set; }
        public string Method { get; set; }
        public Dictionary<string, object> Params { get; set; }
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
