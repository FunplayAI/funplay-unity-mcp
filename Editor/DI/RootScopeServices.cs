// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using GameBooom.Editor.MCP.Server;
using UnityEditor;
using UnityEngine;

namespace GameBooom.Editor.DI
{
    [InitializeOnLoad]
    internal static class RootScopeServices
    {
        private static ServiceProvider _serviceProvider;

        public static IServiceProvider Services => _serviceProvider;

        static RootScopeServices()
        {
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
                Debug.Log("[GameBooom] Root services initialized.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom] Failed to initialize root services: {ex}");
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
                Debug.LogError($"[GameBooom] Error disposing root services: {ex}");
            }
        }
    }
}
