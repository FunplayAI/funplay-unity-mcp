// Copyright (C) Funplay. Licensed under MIT.

using System;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Settings;
using Funplay.Editor.Services.UnityLogs;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.DI
{
    [InitializeOnLoad]
    internal static class RootScopeServices
    {
        private static ServiceProvider _serviceProvider;

        public static IServiceProvider Services => _serviceProvider;

        static RootScopeServices()
        {
            if (Application.isBatchMode)
            {
                PluginDebugLogger.Log("[Funplay] Root services skipped in Unity batch mode process.");
                return;
            }

            Initialize();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void Initialize()
        {
            try
            {
                var services = new ServiceCollection();
                services.RegisterServices();
                _serviceProvider = services.BuildServiceProvider();
                PluginDebugLogger.Log("[Funplay] Root services initialized.");

                var unityLogsRepository =
                    _serviceProvider.GetService(typeof(UnityLogsRepository)) as UnityLogsRepository;
                unityLogsRepository?.StartListening();

                var settings = _serviceProvider.GetService(typeof(ISettingsController)) as ISettingsController;
                if (settings != null)
                {
                    FunplayMCPClientConfigPanel.TryMigrateLegacyClaudeCodeEntryOnce(settings);
                }

                if (settings?.MCPServerEnabled == true &&
                    !MCPServerDomainReloadHandler.IsPendingPostReloadRestart())
                {
                    // Cold-start path only. During a domain reload, OnAfterReload owns the restart
                    // so the previous AppDomain's listener has time to release the port.
                    var mcpServer = _serviceProvider.GetService(typeof(MCPServerService)) as MCPServerService;
                    if (mcpServer != null)
                    {
                        _ = mcpServer.StartAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay] Failed to initialize root services: {ex}");
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            try
            {
                MCPServerDomainReloadHandler.PrepareForReload(_serviceProvider);
                _serviceProvider?.Dispose();
                _serviceProvider = null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay] Error disposing root services: {ex}");
            }
        }
    }
}
