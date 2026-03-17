// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using UnityEditor;
using UnityEngine;

namespace GameBooom.Editor.DI
{
    /// <summary>
    /// Manages the per-window service scope. Only one window scope exists at a time.
    /// </summary>
    internal static class WindowScopeServices
    {
        private static ServiceScope _serviceScope;

        public static IServiceProvider Services => _serviceScope;

        static WindowScopeServices()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        public static void Register(ServiceScope scope = null)
        {
            if (scope != null)
            {
                _serviceScope?.Dispose();
                _serviceScope = scope;
            }
            else if (_serviceScope == null)
            {
                var rootProvider = RootScopeServices.Services as ServiceProvider;
                if (rootProvider != null)
                {
                    _serviceScope = rootProvider.CreateScope();
                }
                else
                {
                    Debug.LogError("[GameBooom] Root service provider is not initialized.");
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            try
            {
                _serviceScope?.Dispose();
                _serviceScope = null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom] Error disposing window scope: {ex}");
            }
        }

        /// <summary>
        /// Resets the window scope, disposing the current scope so it can be recreated.
        /// Typically called from the window's OnDestroy.
        /// </summary>
        public static void Reset()
        {
            try
            {
                _serviceScope?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom] Error disposing window scope during reset: {ex}");
            }
            finally
            {
                _serviceScope = null;
            }
        }
    }
}
