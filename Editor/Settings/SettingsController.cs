// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Funplay.Editor.Services;
using UnityEngine;

namespace Funplay.Editor.Settings
{
    internal class SettingsController : ISettingsController
    {
        private const string SettingsDirectoryName = "UserSettings";
        private const string SettingsFileName = "FunplayMcpSettings.json";
        private const int DefaultPort = 8765;
        private const string DefaultToolExportProfile = "core";
        private const string DefaultSelectedConfigTarget = "Claude Code";
        private const bool DefaultExecuteCodeSafetyChecksEnabled = true;
        private const bool DefaultExecuteCodeStrictFilesystemSafetyEnabled = true;
        private const bool DefaultExecuteCodeProjectNamespaceInjectionEnabled = false;
        private const bool DefaultPluginDebugLoggingEnabled = false;

        private readonly string _settingsPath;
        private readonly object _lock = new object();
        private SettingsData _settings;

        public SettingsController(IApplicationPaths applicationPaths)
        {
            if (applicationPaths == null) throw new ArgumentNullException(nameof(applicationPaths));

            _settingsPath = Path.Combine(
                applicationPaths.ProjectPath,
                SettingsDirectoryName,
                SettingsFileName);
            _settings = LoadSettings();
        }

        public event Action OnSettingsChanged;

        public bool MCPServerEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.enabled;
            }
            set
            {
                UpdateSettings(data => data.enabled = value);
            }
        }

        public int MCPServerPort
        {
            get
            {
                lock (_lock)
                    return _settings.port;
            }
            set
            {
                var normalized = value > 0 ? value : DefaultPort;
                UpdateSettings(data => data.port = normalized);
            }
        }

        public string MCPToolExportProfile
        {
            get
            {
                lock (_lock)
                    return _settings.toolExportProfile;
            }
            set
            {
                var normalized = NormalizeToolExportProfile(value);
                UpdateSettings(data => data.toolExportProfile = normalized);
            }
        }

        public bool MCPCoreToolsConfigured
        {
            get
            {
                lock (_lock)
                    return _settings.coreToolsCustom;
            }
        }

        public string[] MCPCoreTools
        {
            get
            {
                lock (_lock)
                    return _settings.coreTools?.ToArray() ?? Array.Empty<string>();
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.coreToolsCustom = value != null;
                    data.coreTools = NormalizeToolNames(value);
                });
            }
        }

        public bool MCPFullToolsConfigured
        {
            get
            {
                lock (_lock)
                    return _settings.fullToolsCustom;
            }
        }

        public string[] MCPFullTools
        {
            get
            {
                lock (_lock)
                    return _settings.fullTools?.ToArray() ?? Array.Empty<string>();
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.fullToolsCustom = value != null;
                    data.fullTools = NormalizeToolNames(value);
                });
            }
        }

        public string MCPSelectedConfigTarget
        {
            get
            {
                lock (_lock)
                    return _settings.selectedConfigTarget;
            }
            set
            {
                var normalized = NormalizeSelectedConfigTarget(value);
                UpdateSettings(data => data.selectedConfigTarget = normalized);
            }
        }

        public bool ExecuteCodeSafetyChecksEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.executeCodeSafetyChecksEnabled;
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.executeCodeSafetyChecksEnabled = value;
                    data.executeCodeSafetyChecksConfigured = true;
                });
            }
        }

        public bool ExecuteCodeStrictFilesystemSafetyEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.executeCodeStrictFilesystemSafetyEnabled;
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.executeCodeStrictFilesystemSafetyEnabled = value;
                    data.executeCodeStrictFilesystemSafetyConfigured = true;
                });
            }
        }

        public bool ExecuteCodeProjectNamespaceInjectionEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.executeCodeProjectNamespaceInjectionEnabled;
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.executeCodeProjectNamespaceInjectionEnabled = value;
                    data.executeCodeProjectNamespaceInjectionConfigured = true;
                });
            }
        }

        public bool PluginDebugLoggingEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.pluginDebugLoggingEnabled;
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.pluginDebugLoggingEnabled = value;
                    data.pluginDebugLoggingConfigured = true;
                });
            }
        }

        public bool BrokerModeEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.brokerModeEnabled;
            }
            set
            {
                UpdateSettings(data => data.brokerModeEnabled = value);
            }
        }

        public string BrokerMonoPath
        {
            get
            {
                lock (_lock)
                    return _settings.brokerMonoPath ?? string.Empty;
            }
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
                UpdateSettings(data => data.brokerMonoPath = normalized);
            }
        }

        private void UpdateSettings(Action<SettingsData> apply)
        {
            if (apply == null) return;

            var changed = false;
            lock (_lock)
            {
                var beforeJson = JsonUtility.ToJson(_settings);
                apply(_settings);
                NormalizeInPlace(_settings);
                var afterJson = JsonUtility.ToJson(_settings);
                if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
                    return;

                SaveSettings(_settings);
                changed = true;
            }

            if (changed)
                OnSettingsChanged?.Invoke();
        }

        private SettingsData LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonUtility.FromJson<SettingsData>(json);
                        if (loaded != null)
                        {
                            var beforeNormalizeJson = JsonUtility.ToJson(loaded);
                            NormalizeInPlace(loaded);
                            var afterNormalizeJson = JsonUtility.ToJson(loaded);
                            if (!string.Equals(beforeNormalizeJson, afterNormalizeJson, StringComparison.Ordinal))
                                SaveSettings(loaded);
                            return loaded;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Funplay] Failed to read MCP settings file '{_settingsPath}': {ex.Message}");
            }

            var defaults = CreateDefaultSettings();
            SaveSettings(defaults);
            return defaults;
        }

        private void SaveSettings(SettingsData settings)
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var json = JsonUtility.ToJson(settings, true);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay] Failed to write MCP settings file '{_settingsPath}': {ex.Message}");
            }
        }

        private static SettingsData CreateDefaultSettings()
        {
            return new SettingsData
            {
                enabled = false,
                port = DefaultPort,
                toolExportProfile = DefaultToolExportProfile,
                selectedConfigTarget = DefaultSelectedConfigTarget,
                executeCodeSafetyChecksEnabled = DefaultExecuteCodeSafetyChecksEnabled,
                executeCodeSafetyChecksConfigured = true,
                executeCodeStrictFilesystemSafetyEnabled = DefaultExecuteCodeStrictFilesystemSafetyEnabled,
                executeCodeStrictFilesystemSafetyConfigured = true,
                executeCodeProjectNamespaceInjectionEnabled = DefaultExecuteCodeProjectNamespaceInjectionEnabled,
                executeCodeProjectNamespaceInjectionConfigured = true,
                pluginDebugLoggingEnabled = DefaultPluginDebugLoggingEnabled,
                pluginDebugLoggingConfigured = true
            };
        }

        private static void NormalizeInPlace(SettingsData settings)
        {
            if (settings == null)
                return;

            settings.port = settings.port > 0 ? settings.port : DefaultPort;
            settings.brokerMonoPath = settings.brokerMonoPath ?? string.Empty;
            settings.toolExportProfile = NormalizeToolExportProfile(settings.toolExportProfile);
            settings.coreTools = settings.coreToolsCustom ? NormalizeToolNames(settings.coreTools) : null;
            settings.fullTools = settings.fullToolsCustom ? NormalizeToolNames(settings.fullTools) : null;
            settings.selectedConfigTarget = NormalizeSelectedConfigTarget(settings.selectedConfigTarget);
            if (!settings.executeCodeSafetyChecksConfigured)
            {
                settings.executeCodeSafetyChecksEnabled = DefaultExecuteCodeSafetyChecksEnabled;
                settings.executeCodeSafetyChecksConfigured = true;
            }
            if (!settings.executeCodeStrictFilesystemSafetyConfigured)
            {
                settings.executeCodeStrictFilesystemSafetyEnabled = DefaultExecuteCodeStrictFilesystemSafetyEnabled;
                settings.executeCodeStrictFilesystemSafetyConfigured = true;
            }
            if (!settings.executeCodeProjectNamespaceInjectionConfigured)
            {
                settings.executeCodeProjectNamespaceInjectionEnabled = DefaultExecuteCodeProjectNamespaceInjectionEnabled;
                settings.executeCodeProjectNamespaceInjectionConfigured = true;
            }
            if (!settings.pluginDebugLoggingConfigured)
            {
                settings.pluginDebugLoggingEnabled = DefaultPluginDebugLoggingEnabled;
                settings.pluginDebugLoggingConfigured = true;
            }
        }

        private static string NormalizeToolExportProfile(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? DefaultToolExportProfile : value.Trim().ToLowerInvariant();
        }

        private static string NormalizeSelectedConfigTarget(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? DefaultSelectedConfigTarget : value.Trim();
        }

        private static List<string> NormalizeToolNames(IEnumerable<string> values)
        {
            if (values == null)
                return null;

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [Serializable]
        private class SettingsData
        {
            public bool enabled = false;
            public int port = DefaultPort;
            public string toolExportProfile = DefaultToolExportProfile;
            public bool coreToolsCustom = false;
            public List<string> coreTools;
            public bool fullToolsCustom = false;
            public List<string> fullTools;
            public string selectedConfigTarget = DefaultSelectedConfigTarget;
            public bool executeCodeSafetyChecksEnabled = DefaultExecuteCodeSafetyChecksEnabled;
            public bool executeCodeSafetyChecksConfigured = false;
            public bool executeCodeStrictFilesystemSafetyEnabled = DefaultExecuteCodeStrictFilesystemSafetyEnabled;
            public bool executeCodeStrictFilesystemSafetyConfigured = false;
            public bool executeCodeProjectNamespaceInjectionEnabled = DefaultExecuteCodeProjectNamespaceInjectionEnabled;
            public bool executeCodeProjectNamespaceInjectionConfigured = false;
            public bool pluginDebugLoggingEnabled = DefaultPluginDebugLoggingEnabled;
            public bool pluginDebugLoggingConfigured = false;
            public bool brokerModeEnabled = false;
            public string brokerMonoPath = string.Empty;
        }
    }
}
