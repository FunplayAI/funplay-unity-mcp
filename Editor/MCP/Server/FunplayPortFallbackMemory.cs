// Copyright (C) Funplay. Licensed under MIT.

using UnityEditor;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// What the transport should assume about the requested port, learned from this session's
    /// previous start. Defaults mean "no knowledge": full retry window, scan for a fallback.
    /// </summary>
    internal readonly struct PortFallbackHints
    {
        /// <summary>
        /// The requested port was owned by a foreign process last time. A foreign owner never
        /// releases the port the way a tearing-down previous AppDomain does, so the long retry
        /// window (sized for teardown) is wasted time and a short one is used instead.
        /// </summary>
        public readonly bool RequestedPortKnownForeign;

        /// <summary>Fallback port served last time; tried first so the port stays stable across reloads.</summary>
        public readonly int PreferredFallbackPort;

        public PortFallbackHints(bool requestedPortKnownForeign, int preferredFallbackPort)
        {
            RequestedPortKnownForeign = requestedPortKnownForeign;
            PreferredFallbackPort = preferredFallbackPort;
        }
    }

    /// <summary>
    /// Session-scoped memory of a port conflict. Without it, every domain reload while a conflict
    /// persists re-paid the full teardown retry window (~10s of MCP outage per recompile) and
    /// re-rolled the fallback port, breaking a client connected to the previous one mid-session.
    /// SessionState is deliberately the store: it survives domain reloads but dies with the editor,
    /// so a remembered fallback can never outlive the conflict into a fresh session -- persisting it
    /// to disk is exactly what the design forbids.
    /// </summary>
    internal static class FunplayPortFallbackMemory
    {
        private const string ForeignOwnedPortKey = "Funplay_MCPServer_ForeignOwnedPort";
        private const string FallbackPortKey = "Funplay_MCPServer_LastFallbackPort";

        /// <summary>Main thread only (SessionState requirement).</summary>
        public static PortFallbackHints ReadHints(int requestedPort)
        {
            var foreignOwnedPort = SessionState.GetInt(ForeignOwnedPortKey, 0);
            if (foreignOwnedPort <= 0 || foreignOwnedPort != requestedPort)
                return default;

            return new PortFallbackHints(true, SessionState.GetInt(FallbackPortKey, 0));
        }

        /// <summary>
        /// Records the outcome of a successful start. Main thread only (SessionState requirement).
        /// </summary>
        public static void RecordStartOutcome(int requestedPort, int actualPort)
        {
            if (actualPort > 0 && actualPort != requestedPort)
            {
                SessionState.SetInt(ForeignOwnedPortKey, requestedPort);
                SessionState.SetInt(FallbackPortKey, actualPort);
                return;
            }

            // Landing on the requested port means the conflict is gone; forgetting it restores the
            // full teardown-tolerant retry window for the next genuine reload race.
            SessionState.EraseInt(ForeignOwnedPortKey);
            SessionState.EraseInt(FallbackPortKey);
        }
    }
}
