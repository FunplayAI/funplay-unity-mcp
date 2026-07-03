// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Text;
using Funplay.Editor.Settings;
using UnityEditor;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Tracks changes to the exposed MCP tool list across server restarts and domain
    /// reloads, so transports can piggyback a <c>notifications/tools/list_changed</c>
    /// message onto the next client request (as an SSE-formatted response). Connected
    /// clients then refresh their tool list without reconnecting -- previously the only
    /// way for an already-connected client to see newly added or re-exposed tools was
    /// a full client restart.
    /// Threading: transports consume the pending flag from background threads, so the
    /// hot path only touches volatile statics. SessionState (main-thread-only API) is
    /// read/written exclusively on the main thread -- at server start and via an
    /// EditorApplication.update sync -- and is what lets the flag survive domain
    /// reloads within an editor session.
    /// </summary>
    internal static class MCPToolListChangeNotifier
    {
        private const string HashKey = "Funplay.MCP.ExposedToolsHash";
        private const string PendingKey = "Funplay.MCP.ToolsChangedPending";

        internal const string NotificationJson =
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}";

        private static volatile bool _pending;
        private static volatile bool _persistedFlagDirty;
        private static bool _updateHooked;

        /// <summary>
        /// Compare the currently exposed tool names against the last observed set.
        /// Called on the main thread when the server (re)starts with a freshly built
        /// exporter. The first observation in an editor session only records the baseline.
        /// </summary>
        public static void CheckForChanges(MCPToolExporter toolExporter)
        {
            try
            {
                var hash = ComputeToolListHash(toolExporter);
                var previous = SessionState.GetString(HashKey, null);
                var pending = SessionState.GetBool(PendingKey, false);

                if (string.IsNullOrEmpty(previous))
                {
                    SessionState.SetString(HashKey, hash);
                }
                else if (!string.Equals(previous, hash, StringComparison.Ordinal))
                {
                    SessionState.SetString(HashKey, hash);
                    SessionState.SetBool(PendingKey, true);
                    pending = true;
                    PluginDebugLogger.Log(
                        "[Funplay MCP Server] Exposed tool list changed; clients will be notified via tools/list_changed.");
                }

                _pending = pending;
                _persistedFlagDirty = false;

                if (!_updateHooked)
                {
                    _updateHooked = true;
                    EditorApplication.update += SyncPersistedFlag;
                }
            }
            catch (Exception ex)
            {
                PluginDebugLogger.Log("[Funplay MCP Server] Tool list change check failed: " + ex.Message);
            }
        }

        /// <summary>Consume the pending flag. Thread-safe; returns true at most once per change.</summary>
        public static bool TryConsumePending()
        {
            if (!_pending)
                return false;

            lock (typeof(MCPToolListChangeNotifier))
            {
                if (!_pending)
                    return false;
                _pending = false;
            }

            _persistedFlagDirty = true;
            return true;
        }

        /// <summary>Re-arm the pending flag when a piggybacked send failed before reaching the client. Thread-safe.</summary>
        public static void RestorePending()
        {
            _pending = true;
            _persistedFlagDirty = true;
        }

        /// <summary>
        /// Wrap a JSON-RPC response as an SSE body that first delivers the
        /// tools/list_changed notification, then the response itself.
        /// </summary>
        public static string BuildSseBody(string responseJson)
        {
            return "data: " + NotificationJson + "\n\n" +
                   "data: " + (responseJson ?? string.Empty) + "\n\n";
        }

        /// <summary>Main-thread pump that mirrors the volatile flag into SessionState.</summary>
        private static void SyncPersistedFlag()
        {
            if (!_persistedFlagDirty)
                return;

            _persistedFlagDirty = false;
            SessionState.SetBool(PendingKey, _pending);
        }

        private static string ComputeToolListHash(MCPToolExporter toolExporter)
        {
            var tools = toolExporter.ExportTools();
            var names = new List<string>(tools.Count);
            foreach (var tool in tools)
            {
                if (tool.TryGetValue("name", out var name) && name is string s)
                    names.Add(s);
            }
            names.Sort(StringComparer.Ordinal);

            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", names)));
                return Convert.ToBase64String(bytes);
            }
        }
    }
}
