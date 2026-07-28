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
        private const int CurrentSettingsVersion = 1;
        // Target names ("Claude Code") never contain '|'.
        private const string LastClientConfigKeySeparator = "|";
        private const string DefaultToolExportProfile = "core";
        private const string DefaultSelectedConfigTarget = "Claude Code";
        private const bool DefaultExecuteCodeSafetyChecksEnabled = true;
        private const bool DefaultExecuteCodeStrictFilesystemSafetyEnabled = true;
        private const bool DefaultExecuteCodeProjectNamespaceInjectionEnabled = false;
        private const bool DefaultPluginDebugLoggingEnabled = false;
        private const bool DefaultMCPBrokerModeEnabled = false;

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
                // A cleared field commits 0, and a typo past 65535 cannot be bound at all (it would
                // reach TcpListener as an ArgumentOutOfRangeException classified as a hard failure
                // with no fallback). Both mean "no usable port chosen": release the pin rather than
                // pinning DefaultPort, which would silently put this project back on the old shared
                // port and re-create the collision with every other project on the machine.
                if (value <= 0 || value > 65535)
                {
                    ClearMCPServerPortOverride();
                    return;
                }

                // Writing a real port is the user picking one, so it outranks the per-project derived
                // default from here on -- including when the value happens to equal DefaultPort.
                UpdateSettings(data =>
                {
                    data.port = value;
                    data.portConfigured = true;
                });
            }
        }

        public bool MCPServerPortConfigured
        {
            get
            {
                lock (_lock)
                    return _settings.portConfigured;
            }
        }

        public void ClearMCPServerPortOverride()
        {
            // Also reset the stored value: "unpinned" must serialize one way only. Leaving the old
            // pinned port behind created a second unpinned shape on disk that the one-shot migration
            // machinery then had to defend against re-interpreting as a pin.
            UpdateSettings(data =>
            {
                data.port = DefaultPort;
                data.portConfigured = false;
            });
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

        public bool MCPBrokerModeEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.mcpBrokerModeEnabled;
            }
            set
            {
                UpdateSettings(data => data.mcpBrokerModeEnabled = value);
            }
        }

        public string MCPBrokerMonoPath
        {
            get
            {
                lock (_lock)
                    return _settings.mcpBrokerMonoPath ?? string.Empty;
            }
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
                UpdateSettings(data => data.mcpBrokerMonoPath = normalized);
            }
        }

        public string GetLastClientConfigKey(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return string.Empty;

            var prefix = targetName.Trim() + LastClientConfigKeySeparator;
            lock (_lock)
            {
                var entries = _settings.mcpLastClientConfigKeys;
                if (entries == null)
                    return string.Empty;

                foreach (var entry in entries)
                {
                    if (entry != null && entry.StartsWith(prefix, StringComparison.Ordinal))
                        return entry.Substring(prefix.Length);
                }
            }

            return string.Empty;
        }

        public void SetLastClientConfigKey(string targetName, string serverKey)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return;

            var name = targetName.Trim();
            var key = string.IsNullOrWhiteSpace(serverKey) ? string.Empty : serverKey.Trim();
            var prefix = name + LastClientConfigKeySeparator;

            UpdateSettings(data =>
            {
                if (data.mcpLastClientConfigKeys == null)
                    data.mcpLastClientConfigKeys = new List<string>();

                data.mcpLastClientConfigKeys.RemoveAll(
                    entry => entry == null || entry.StartsWith(prefix, StringComparison.Ordinal));
                if (key.Length > 0)
                    data.mcpLastClientConfigKeys.Add(prefix + key);
            });
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
                settingsVersion = CurrentSettingsVersion,
                enabled = false,
                port = DefaultPort,
                portConfigured = false,
                toolExportProfile = DefaultToolExportProfile,
                selectedConfigTarget = DefaultSelectedConfigTarget,
                executeCodeSafetyChecksEnabled = DefaultExecuteCodeSafetyChecksEnabled,
                executeCodeSafetyChecksConfigured = true,
                executeCodeStrictFilesystemSafetyEnabled = DefaultExecuteCodeStrictFilesystemSafetyEnabled,
                executeCodeStrictFilesystemSafetyConfigured = true,
                executeCodeProjectNamespaceInjectionEnabled = DefaultExecuteCodeProjectNamespaceInjectionEnabled,
                executeCodeProjectNamespaceInjectionConfigured = true,
                pluginDebugLoggingEnabled = DefaultPluginDebugLoggingEnabled,
                pluginDebugLoggingConfigured = true,
                mcpBrokerModeEnabled = DefaultMCPBrokerModeEnabled,
                mcpBrokerMonoPath = string.Empty
            };
        }

        private static void NormalizeInPlace(SettingsData settings)
        {
            if (settings == null)
                return;

            settings.port = settings.port > 0 ? settings.port : DefaultPort;

            if (settings.settingsVersion < 1)
            {
                // A settings file written before portConfigured existed means this project was already
                // serving on a port its clients are configured against. Upgrading keeps that port,
                // pinned: the stored value becomes an explicit choice and nothing moves, so an upgrade
                // never breaks a working setup (whether the port is the old shared default or one the
                // user picked for CI or a firewall rule).
                //
                // Per-project derived ports are therefore the default for NEW projects only. An
                // existing project opts in with "Use Per-Project Port" when it actually needs to run
                // beside another editor -- which is also the fix the port-conflict warning points at.
                settings.portConfigured = true;
            }

            // Loading persists the normalized data, so each migration above runs once and cannot
            // later re-derive a value the user has since changed.
            settings.settingsVersion = CurrentSettingsVersion;

            settings.mcpBrokerMonoPath = settings.mcpBrokerMonoPath ?? string.Empty;
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
            /// <summary>
            /// 0 = written before <see cref="portConfigured"/> existed. Bumping this drives one-shot
            /// migrations in <see cref="NormalizeInPlace"/> so they cannot re-run and overwrite a
            /// later user choice.
            /// </summary>
            public int settingsVersion = 0;
            public bool enabled = false;
            public int port = DefaultPort;
            public bool portConfigured = false;
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
            public bool mcpBrokerModeEnabled = DefaultMCPBrokerModeEnabled;
            public string mcpBrokerMonoPath = string.Empty;
            public List<string> mcpLastClientConfigKeys;
        }
    }
}
