// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Net;
using System.Net.Sockets;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Finds a bindable loopback port when the port a project resolved to is taken by something else
    /// -- an unrelated process, or another project whose derived port collided with this one. Shared
    /// by the in-process transport and the broker process manager so both fall back the same way.
    /// </summary>
    internal static class FunplayFreePortScanner
    {
        public const int DefaultScanAttempts = 64;

        /// <summary>
        /// Shared tail of every fallback-bind warning (in-process transport, broker launch, broker
        /// keep-path), so all three tell the user the same recovery story. Existing clients may still
        /// target the occupied stable port, and one-click configuration is blocked until the conflict
        /// is resolved; this prevents calls from being routed to the process that owns that port and
        /// avoids persisting a transient fallback.
        /// </summary>
        public const string FallbackGuidance =
            "Do not send requests through a client entry that still targets the occupied stable port; it may reach the other process. " +
            "One-click client configuration is blocked while this fallback is active. Open the MCP Server window and either click " +
            "\"Use Per-Project Port\" to derive a port from this project's path, or click \"Pin Current Port\" to keep the active port; " +
            "wait for the restart, then re-run the one-click configuration.";

        /// <summary>
        /// Scans upwards from <paramref name="startPort"/> for a port that can actually be bound on
        /// loopback, skipping <paramref name="startPort"/> itself (the caller already failed on it).
        /// Binding is the test rather than "is something listening", because a port can be reserved
        /// without accepting connections.
        /// </summary>
        public static bool TryFindFreePort(int startPort, int attempts, out int freePort)
        {
            freePort = 0;
            if (startPort <= 0)
                return false;

            for (var offset = 1; offset <= attempts; offset++)
            {
                var candidate = startPort + offset;
                if (candidate > 65535)
                    return false;

                if (!CanBind(candidate))
                    continue;

                freePort = candidate;
                return true;
            }

            return false;
        }

        public static bool TryFindFreePort(int startPort, out int freePort)
        {
            return TryFindFreePort(startPort, DefaultScanAttempts, out freePort);
        }

        public static bool CanBind(int port)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try
                {
                    listener?.Stop();
                }
                catch (Exception)
                {
                    // Nothing to recover: the probe listener is discarded either way.
                }
            }
        }
    }
}
