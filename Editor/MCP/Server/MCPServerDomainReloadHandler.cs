// Copyright (C) Funplay. Licensed under MIT.

using System;
using Funplay.Editor.Settings;
using UnityEditor;
using Funplay.Editor.DI;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Handles Unity domain reload for the MCP server.
    /// Saves server state before reload and restarts after reload if it was running.
    /// </summary>
    [InitializeOnLoad]
    internal static class MCPServerDomainReloadHandler
    {
        private const string WasRunningKey = "Funplay_MCPServer_WasRunning";
        private const string RestartDeadlineTicksKey = "Funplay_MCPServer_RestartDeadlineTicks";
        private static readonly TimeSpan RestartRetryWindow = TimeSpan.FromMinutes(5);
        private static bool _restartScheduled;
        private static bool _restartInProgress;

        static MCPServerDomainReloadHandler()
        {
            if (Application.isBatchMode)
                return;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;

            if (IsPendingPostReloadRestart())
                SchedulePostReloadRestart();
        }

        internal static void PrepareForReload(IServiceProvider services)
        {
            try
            {
                var mcpServer = services?.GetService(typeof(MCPServerService)) as MCPServerService;
                if (mcpServer?.IsRunning != true)
                    return;

                PluginDebugLogger.Log("[Funplay MCP Server] Saving state before domain reload");
                SessionState.SetBool(WasRunningKey, true);
                SessionState.SetString(RestartDeadlineTicksKey, DateTime.UtcNow.Add(RestartRetryWindow).Ticks.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error preparing reload state: {ex.Message}");
            }
        }

        private static void OnBeforeReload()
        {
            try
            {
                PrepareForReload(RootScopeServices.Services);

                var mcpServer = RootScopeServices.Services?.GetService(typeof(MCPServerService)) as MCPServerService;
                if (mcpServer?.IsRunning == true)
                {
                    // Must run synchronously: Unity unloads the AppDomain right after this callback
                    // returns and will not wait for a fire-and-forget Stop task. Awaiting was the
                    // root cause of port 8765 staying bound across reloads (issue #1).
                    mcpServer.StopSync();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error in OnBeforeReload: {ex.Message}");
            }
        }

        /// <summary>
        /// True when a domain reload just happened and the post-reload auto-restart in
        /// <see cref="OnAfterReload"/> is going to (re)start the server. Other startup paths
        /// (eg. <c>RootScopeServices.Initialize</c>) should skip auto-start in this case to avoid
        /// racing two <c>StartAsync</c> calls against a port that may still be releasing.
        /// </summary>
        internal static bool IsPendingPostReloadRestart()
        {
            return SessionState.GetBool(WasRunningKey, false);
        }

        private static void OnAfterReload()
        {
            try
            {
                if (SessionState.GetBool(WasRunningKey, false))
                {
                    PluginDebugLogger.Log("[Funplay MCP Server] Restarting server after domain reload");
                    SchedulePostReloadRestart();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error in OnAfterReload: {ex.Message}");
            }
        }

        private static void SchedulePostReloadRestart()
        {
            if (_restartScheduled)
                return;

            _restartScheduled = true;
            EditorApplication.delayCall += RestartWhenEditorIsReady;
            EditorApplication.update += RestartWhenEditorIsReady;
            RestartWhenEditorIsReady();
        }

        private static async void RestartWhenEditorIsReady()
        {
            if (_restartInProgress)
                return;

            if (!SessionState.GetBool(WasRunningKey, false))
            {
                ClearScheduledRestart();
                return;
            }

            if (EditorApplication.isCompiling)
                return;

            if (RestartDeadlineExpired())
            {
                Debug.LogError("[Funplay MCP Server] Timed out restarting after domain reload.");
                ClearPendingRestart();
                ClearScheduledRestart();
                return;
            }

            var services = RootScopeServices.Services;
            if (services == null)
                return;

            var mcpServer = services.GetService(typeof(MCPServerService)) as MCPServerService;
            if (mcpServer == null)
                return;

            _restartInProgress = true;

            try
            {
                // The restart deliberately does not force the pre-reload port back into the settings.
                // Port resolution is deterministic (an explicit setting, otherwise derived from the
                // project identity), so the restart lands on the same port on its own; writing the
                // pre-reload port back would instead persist it as an explicit user choice -- turning
                // a derived or fallback port into a pin, and reverting a port the user changed while
                // the reload was in flight.
                if (!mcpServer.IsRunning)
                {
                    var started = await mcpServer.StartAsync();
                    if (started)
                    {
                        ClearPendingRestart();
                        ClearScheduledRestart();
                    }
                    else if (RestartDeadlineExpired())
                    {
                        Debug.LogError("[Funplay MCP Server] Failed to restart after domain reload.");
                        ClearPendingRestart();
                        ClearScheduledRestart();
                    }
                }
                else
                {
                    ClearPendingRestart();
                    ClearScheduledRestart();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error restarting after reload: {ex.Message}");
            }
            finally
            {
                _restartInProgress = false;
            }
        }

        private static bool RestartDeadlineExpired()
        {
            var deadlineText = SessionState.GetString(RestartDeadlineTicksKey, string.Empty);
            if (!long.TryParse(deadlineText, out var deadlineTicks))
            {
                SessionState.SetString(RestartDeadlineTicksKey, DateTime.UtcNow.Add(RestartRetryWindow).Ticks.ToString());
                return false;
            }

            return DateTime.UtcNow.Ticks >= deadlineTicks;
        }

        private static void ClearPendingRestart()
        {
            SessionState.EraseBool(WasRunningKey);
            SessionState.EraseString(RestartDeadlineTicksKey);
        }

        private static void ClearScheduledRestart()
        {
            EditorApplication.delayCall -= RestartWhenEditorIsReady;
            EditorApplication.update -= RestartWhenEditorIsReady;
            _restartScheduled = false;
        }
    }
}
