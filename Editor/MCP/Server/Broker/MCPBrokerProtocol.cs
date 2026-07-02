// Copyright (C) Funplay. Licensed under MIT.

namespace Funplay.Editor.MCP.Server
{
    internal static class MCPBrokerProtocol
    {
        // v2: pull responses carry AcceptSseHeader (client's Accept: text/event-stream),
        //     push requests may carry ContentTypeHeader to override the client-facing
        //     response content type (used for SSE-piggybacked notifications).
        public const int Version = 2;
        public const string Name = "funplay-unity-mcp-broker";
        public const string HealthPath = "/_funplay/broker/health";
        public const string AttachPath = "/_funplay/broker/attach";
        public const string PullPath = "/_funplay/broker/pull";
        public const string PushPath = "/_funplay/broker/push";
        public const string DetachPath = "/_funplay/broker/detach";
        public const string ShutdownPath = "/_funplay/broker/shutdown";
        public const string TokenHeader = "X-Funplay-Broker-Token";
        public const string SessionHeader = "X-Funplay-Broker-Session";
        public const string ReqIdHeader = "X-Funplay-Broker-ReqId";
        public const string RedeliveryHeader = "X-Funplay-Broker-Redelivery";
        public const string BrokerHeader = "X-Funplay-Broker";
        public const string AcceptSseHeader = "X-Funplay-Broker-Accept-SSE";
        public const string ContentTypeHeader = "X-Funplay-Broker-Content-Type";
    }
}
