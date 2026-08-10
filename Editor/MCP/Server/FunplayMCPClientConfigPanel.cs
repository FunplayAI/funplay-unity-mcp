// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Funplay.Editor.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.MCP.Server
{
    internal sealed class FunplayMCPClientConfigPanel
    {
        private readonly ISettingsController _settings;
        private readonly MCPServerService _server;
        private readonly Action _rebuildWindow;
        private MCPConfigTarget[] _targets;
        private int _selectedTargetIndex;
        private Label _configStatusLabel;
        private Label _configPathLabel;

        public FunplayMCPClientConfigPanel(
            ISettingsController settings,
            MCPServerService server,
            Action rebuildWindow)
        {
            _settings = settings;
            _server = server;
            _rebuildWindow = rebuildWindow;
        }

        public void AddTo(VisualElement parent)
        {
            var label = new Label("One-Click MCP Configuration");
            label.style.fontSize = 12;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = new Color(0.75f, 0.75f, 0.75f);
            label.style.marginBottom = 6;
            parent.Add(label);

            var homePath = GetUserHomePath();
            _targets = CreateTargets(homePath);
            var names = _targets.Select(target => target.Name).ToList();

            _selectedTargetIndex = Mathf.Clamp(_selectedTargetIndex, 0, _targets.Length - 1);
            var persistedTargetName = _settings.MCPSelectedConfigTarget;
            if (!string.IsNullOrWhiteSpace(persistedTargetName))
            {
                var persistedIndex = names.FindIndex(name =>
                    string.Equals(name, persistedTargetName, StringComparison.OrdinalIgnoreCase));
                if (persistedIndex >= 0)
                    _selectedTargetIndex = persistedIndex;
            }

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 4;

            var dropdown = new PopupField<string>(names, _selectedTargetIndex);
            dropdown.style.flexGrow = 1;
            dropdown.style.height = 26;
            dropdown.RegisterValueChangedCallback(evt =>
            {
                _selectedTargetIndex = names.IndexOf(evt.newValue);
                _settings.MCPSelectedConfigTarget = evt.newValue;
                _rebuildWindow?.Invoke();
            });
            row.Add(dropdown);

            var configureButton = new Button(() =>
            {
                ConfigureMCPForTarget(_targets[_selectedTargetIndex]);
                RefreshStatus();
            });
            configureButton.text = "Configure";
            configureButton.style.height = 26;
            configureButton.style.width = 80;
            configureButton.style.marginLeft = 4;
            configureButton.style.backgroundColor = new Color(0.2f, 0.5f, 0.3f);
            configureButton.style.color = Color.white;
            row.Add(configureButton);

            var selectedTarget = _targets[_selectedTargetIndex];
            var skillsSupported = !string.IsNullOrEmpty(MapTargetNameToSkillsPlatformId(selectedTarget.Name));
            var configureSkillsButton = new Button(() =>
            {
                ConfigureMCPAndSkillsForTarget(_targets[_selectedTargetIndex]);
                RefreshStatus();
            });
            configureSkillsButton.text = "Configure + Skills";
            configureSkillsButton.style.height = 26;
            configureSkillsButton.style.width = 130;
            configureSkillsButton.style.marginLeft = 4;
            configureSkillsButton.style.backgroundColor = new Color(0.25f, 0.45f, 0.65f);
            configureSkillsButton.style.color = Color.white;
            configureSkillsButton.SetEnabled(skillsSupported);
            row.Add(configureSkillsButton);

            parent.Add(row);

            var skillsHint = new Label(skillsSupported
                ? "Configure + Skills also installs the project MCP workflow skill."
                : "Project skills are currently available for Claude Code, Cursor, and Codex.");
            skillsHint.style.fontSize = 10;
            skillsHint.style.color = new Color(0.6f, 0.6f, 0.6f);
            skillsHint.style.marginBottom = 4;
            skillsHint.style.whiteSpace = WhiteSpace.Normal;
            parent.Add(skillsHint);

            _configStatusLabel = new Label();
            _configStatusLabel.style.fontSize = 11;
            _configStatusLabel.style.marginBottom = 2;
            parent.Add(_configStatusLabel);

            _configPathLabel = new Label();
            _configPathLabel.style.fontSize = 10;
            _configPathLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            _configPathLabel.style.marginBottom = 6;
            _configPathLabel.style.whiteSpace = WhiteSpace.Normal;
            parent.Add(_configPathLabel);

            RefreshStatus();
        }

        public void RefreshStatus()
        {
            if (_configStatusLabel == null || _configPathLabel == null || _targets == null)
                return;

            var idx = Mathf.Clamp(_selectedTargetIndex, 0, _targets.Length - 1);
            var target = _targets[idx];

            if (IsConfigurationBlockedByFallback())
            {
                _configStatusLabel.text = "Status: Resolve the port conflict before configuring";
                _configStatusLabel.style.color = new Color(1f, 0.45f, 0.35f);
                _configPathLabel.text = BuildFallbackConfigurationBlockedMessage();
                return;
            }

            if (target.IsLMStudio)
            {
                var existingPaths = GetExistingLMStudioConfigPaths(GetUserHomePath());
                bool hasExistingConfig = existingPaths.Count > 0;

                _configStatusLabel.text = hasExistingConfig
                    ? "Status: Existing LM Studio config found"
                    : "Status: Configure opens LM Studio Add MCP link";
                _configStatusLabel.style.color = hasExistingConfig
                    ? new Color(0.4f, 1f, 0.4f)
                    : new Color(1f, 0.75f, 0.4f);

                _configPathLabel.text = hasExistingConfig
                    ? "Existing config: " + string.Join(" | ", existingPaths)
                    : "LM Studio config path varies by version. Configure uses lmstudio://add_mcp and does not create guessed paths.";
                return;
            }

            bool exists = File.Exists(target.ConfigPath);
            _configStatusLabel.text = exists ? "Status: Configured" : "Status: Not configured";
            _configStatusLabel.style.color = exists
                ? new Color(0.4f, 1f, 0.4f)
                : new Color(1f, 0.6f, 0.4f);
            // Name and URL together are what a user needs to check or hand-write an entry, and both
            // are project-specific now.
            var resolvedKey = ResolveServerKeyForTarget(target);
            var details = $"{target.ConfigPath}\nEntry: {resolvedKey} -> {GetServerUrl()}";

            // Say why the name grew a hash, or the user just sees an unexplained hex suffix. The
            // occupant can also be this same project under a lost record (settings file deleted,
            // fresh checkout on the same machine), which we cannot tell apart from another project.
            if (!string.Equals(resolvedKey, GetPreferredServerKey(), StringComparison.Ordinal))
            {
                details +=
                    "\nA project hash was added because another project (or an earlier configuration " +
                    $"of this one) already uses \"{GetPreferredServerKey()}\" in this config.";
            }

            // The legacy shared entry is never deleted automatically (any project could have written
            // it), so say it is there -- otherwise it sits in the client as a server that answers
            // nothing once every project has moved to its own entry.
            if (exists && HasLegacyFunplayEntry(target))
            {
                details +=
                    $"\nA legacy \"{FunplayMCPServerKey.LegacyKey}\" entry is still in this config. " +
                    "No project writes it any more; remove it by hand once every project has been configured.";
            }

            _configPathLabel.text = details;
        }

        // Single-entry cache keyed by (path, mtime). ~/.claude.json grows to multiple MB in practice
        // and these checks run on the UI thread on every window rebuild -- re-parsing it each time was
        // a visible editor hitch scaling with a file this plugin does not own.
        private static string _entryNamesCachePath;
        private static DateTime _entryNamesCacheMtime;
        private static HashSet<string> _entryNamesCache;

        /// <summary>
        /// Funplay entry names already present in a target's config. Used both to report the legacy
        /// entry and to detect that another project has taken the name this project wants.
        /// </summary>
        private static HashSet<string> ReadFunplayEntryNames(MCPConfigTarget target)
        {
            try
            {
                if (!File.Exists(target.ConfigPath))
                    return new HashSet<string>(StringComparer.Ordinal);

                var mtime = File.GetLastWriteTimeUtc(target.ConfigPath);
                if (_entryNamesCache != null &&
                    string.Equals(target.ConfigPath, _entryNamesCachePath, StringComparison.Ordinal) &&
                    mtime == _entryNamesCacheMtime)
                {
                    return _entryNamesCache;
                }

                var names = ParseFunplayEntryNames(target);
                _entryNamesCachePath = target.ConfigPath;
                _entryNamesCacheMtime = mtime;
                _entryNamesCache = names;
                return names;
            }
            catch (Exception)
            {
                // A config we cannot read is not worth a warning here; the write path reports failures.
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private static HashSet<string> ParseFunplayEntryNames(MCPConfigTarget target)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var content = File.ReadAllText(target.ConfigPath);

            // Cheap gate before any parsing: most configs contain no funplay entry at all.
            if (content.IndexOf(FunplayMCPServerKey.LegacyKey, StringComparison.Ordinal) < 0)
                return names;

            if (target.IsToml)
            {
                foreach (Match match in Regex.Matches(content, @"(?m)^\[mcp_servers\.([^\]\s]+)\]"))
                {
                    var name = match.Groups[1].Value;
                    if (FunplayMCPServerKey.IsFunplayKey(name))
                        names.Add(name);
                }

                return names;
            }

            var parsed = SimpleJsonHelper.Deserialize(content) as Dictionary<string, object>;
            object serversValue;
            if (parsed == null || !parsed.TryGetValue(GetRootKey(target), out serversValue))
                return names;

            var servers = serversValue as Dictionary<string, object>;
            if (servers == null)
                return names;

            foreach (var key in servers.Keys)
            {
                if (FunplayMCPServerKey.IsFunplayKey(key))
                    names.Add(key);
            }

            return names;
        }

        private static bool HasLegacyFunplayEntry(MCPConfigTarget target)
        {
            return ReadFunplayEntryNames(target).Contains(FunplayMCPServerKey.LegacyKey);
        }

        private static string GetRootKey(MCPConfigTarget target)
        {
            return string.IsNullOrEmpty(target.RootKey) ? "mcpServers" : target.RootKey;
        }

        private MCPConfigTarget[] CreateTargets(string homePath)
        {
            return new[]
            {
                new MCPConfigTarget
                {
                    Name = "Claude Code",
                    ConfigPath = Path.Combine(homePath, ".claude.json"),
                    IncludeTypeField = true
                },
                new MCPConfigTarget
                {
                    Name = "Cursor",
                    ConfigPath = Path.Combine(homePath, ".cursor", "mcp.json"),
                },
                new MCPConfigTarget
                {
                    Name = "LM Studio",
                    ConfigPath = GetLMStudioDisplayPath(homePath),
                    IsLMStudio = true,
                },
                new MCPConfigTarget
                {
                    Name = "VS Code",
                    ConfigPath = GetVSCodeConfigPath(homePath),
                    IncludeTypeField = true,
                    RootKey = "servers"
                },
                new MCPConfigTarget
                {
                    Name = "Trae",
                    ConfigPath = Path.Combine(homePath, ".trae", "mcp.json"),
                },
                new MCPConfigTarget
                {
                    Name = "Kiro",
                    ConfigPath = Path.Combine(homePath, ".kiro", "settings", "mcp.json"),
                    IncludeTypeField = true,
                    RootKey = "mcpServers"
                },
                new MCPConfigTarget
                {
                    Name = "Codex",
                    ConfigPath = Path.Combine(homePath, ".codex", "config.toml"),
                    IsToml = true,
                },
            };
        }

        private void ConfigureMCPForTarget(MCPConfigTarget target)
        {
            try
            {
                WriteMCPConfigurationForTarget(target);

                var message = target.IsLMStudio
                    ? BuildLMStudioConfiguredMessage()
                    : $"MCP configuration written to:\n{target.ConfigPath}\n\n" +
                      $"Please restart {target.Name} for it to take effect.";

                EditorUtility.DisplayDialog("MCP Configuration", message, "OK");
                _rebuildWindow?.Invoke();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "MCP Configuration Error",
                    $"Configuration failed:\n{ex.Message}",
                    "OK");
            }
        }

        private void ConfigureMCPAndSkillsForTarget(MCPConfigTarget target)
        {
            try
            {
                WriteMCPConfigurationForTarget(target);

                var platformId = MapTargetNameToSkillsPlatformId(target.Name);
                if (string.IsNullOrEmpty(platformId))
                {
                    EditorUtility.DisplayDialog(
                        "MCP Configuration",
                        $"MCP configuration written to:\n{target.ConfigPath}\n\n" +
                        "Project skills are currently available for Claude Code, Cursor, and Codex.",
                        "OK");

                    _rebuildWindow?.Invoke();
                    return;
                }

                if (!ConfigureProjectSkillsForPlatform(platformId))
                    return;

                var projectRoot = GetProjectRootPath();
                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var generatedPaths = ProjectSkillsManager.GetGeneratedPathsForPlatform(projectRoot, manifest, platformId);

                EditorUtility.DisplayDialog(
                    "MCP Configuration",
                    $"MCP configuration written to:\n{target.ConfigPath}\n\n" +
                    "Project MCP workflow skill installed:\n" +
                    string.Join("\n", generatedPaths),
                    "OK");

                _rebuildWindow?.Invoke();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "MCP Configuration Error",
                    $"Configuration failed:\n{ex.Message}",
                    "OK");
            }
        }

        private void WriteMCPConfigurationForTarget(MCPConfigTarget target)
        {
            EnsureConfigurationEndpointIsSafe();

            if (target.IsLMStudio)
            {
                // The lmstudio:// deep link alone writes nothing; record the key only when config
                // files were actually rewritten, or a cancelled dialog would poison the record.
                var lmStudioKey = ConfigureLMStudioTarget(target);
                if (!string.IsNullOrEmpty(lmStudioKey))
                    RecordWrittenServerKey(target, lmStudioKey);
                return;
            }

            var dir = Path.GetDirectoryName(target.ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var writtenKey = target.IsToml
                ? ConfigureTomlTarget(target)
                : ConfigureJsonTarget(target);

            RecordWrittenServerKey(target, writtenKey);
        }

        /// <summary>
        /// Remembers the entry name just written for this target, which is the only evidence a later
        /// write has that an entry belongs to this project rather than to another project sharing the
        /// config file. Per target: one shared slot meant a rename could only ever be cleaned up in
        /// the first target re-configured. Records the name actually written, which may carry an
        /// auto-added project hash.
        /// </summary>
        private void RecordWrittenServerKey(MCPConfigTarget target, string serverKey)
        {
            _settings.SetLastClientConfigKey(target.Name, serverKey);
        }

        private bool ConfigureProjectSkillsForPlatform(string platformId)
        {
            var projectRoot = GetProjectRootPath();
            var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
            var selectedPlatforms = new HashSet<string>(manifest.platforms, StringComparer.OrdinalIgnoreCase)
            {
                platformId
            };

            var conflictPaths = ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, selectedPlatforms);
            if (conflictPaths.Length > 0)
            {
                var overwrite = EditorUtility.DisplayDialog(
                    "Project Skills Configuration",
                    "Existing non-managed project instruction files were found:\n\n" +
                    string.Join("\n", conflictPaths) +
                    "\n\nOverwrite them with Funplay-managed files?",
                    "Overwrite",
                    "Cancel");

                if (!overwrite)
                    return false;
            }

            ProjectSkillsManager.ApplyConfiguration(projectRoot, selectedPlatforms, manifest.optionalSkills);
            return true;
        }

        /// <summary>
        /// Returns the entry name written. <paramref name="presetServerKey"/> lets LM Studio write
        /// one shared name into all of its config copies instead of re-resolving per file.
        /// </summary>
        private string ConfigureJsonTarget(MCPConfigTarget target, string presetServerKey = null)
        {
            var rootKey = GetRootKey(target);
            var serverName = presetServerKey ?? ResolveServerKeyForTarget(target);
            var entry = CreateHttpEntry(target);
            Dictionary<string, object> root;

            if (File.Exists(target.ConfigPath))
            {
                var existingJson = File.ReadAllText(target.ConfigPath);
                var parsed = SimpleJsonHelper.Deserialize(existingJson) as Dictionary<string, object>;

                if (parsed != null && parsed.ContainsKey(rootKey))
                {
                    root = parsed;
                    var servers = root[rootKey] as Dictionary<string, object>;
                    if (servers != null)
                    {
                        servers[serverName] = entry;
                        RemoveSupersededFunplayEntries(
                            servers, serverName, _settings.GetLastClientConfigKey(target.Name));
                    }
                    else
                        root[rootKey] = new Dictionary<string, object> { [serverName] = entry };
                }
                else
                {
                    root = parsed ?? new Dictionary<string, object>();
                    root[rootKey] = new Dictionary<string, object> { [serverName] = entry };
                }
            }
            else
            {
                root = new Dictionary<string, object>
                {
                    [rootKey] = new Dictionary<string, object> { [serverName] = entry }
                };
            }

            File.WriteAllText(target.ConfigPath, SimpleJsonHelper.Serialize(root));
            return serverName;
        }

        /// <summary>
        /// Retires the entry THIS project wrote last, once its name has changed (a renamed product, or
        /// the project-hash toggle). Nothing else is removed: a <c>funplay-*</c> entry this project
        /// never wrote belongs to another project, and deleting it would recreate the very bug the
        /// per-project naming fixes -- configuring project B silently unhooking project A.
        /// The legacy shared <c>funplay</c> entry is deliberately left in place for the same reason:
        /// any project on the machine could have written it, so it is surfaced in the panel instead.
        /// </summary>
        internal static void RemoveSupersededFunplayEntries(
            Dictionary<string, object> servers, string currentServerName, string previousServerName)
        {
            if (string.IsNullOrEmpty(previousServerName) ||
                string.Equals(previousServerName, currentServerName, StringComparison.Ordinal))
            {
                return;
            }

            if (!FunplayMCPServerKey.IsFunplayKey(previousServerName))
                return;

            object previousEntry;
            if (!servers.TryGetValue(previousServerName, out previousEntry))
                return;

            // A recorded key whose entry now points somewhere non-local was edited by hand after we
            // wrote it; leave that alone rather than deleting someone's deliberate change.
            if (!IsLoopbackEntry(previousEntry))
                return;

            servers.Remove(previousServerName);
        }

        private static bool IsLoopbackEntry(object entry)
        {
            var entryMap = entry as Dictionary<string, object>;
            object url;
            if (entryMap == null || !entryMap.TryGetValue("url", out url))
                return false;

            return IsLoopbackUrl(url as string);
        }

        /// <summary>Returns the entry name written.</summary>
        private string ConfigureTomlTarget(MCPConfigTarget target)
        {
            var serverKey = ResolveServerKeyForTarget(target);
            var tomlSection = CreateTomlSection(target, serverKey);
            var content = File.Exists(target.ConfigPath) ? File.ReadAllText(target.ConfigPath) : string.Empty;

            content = RemoveSupersededTomlSection(
                content, serverKey, _settings.GetLastClientConfigKey(target.Name));

            int startIdx;
            int endIdx;
            if (TryFindTomlSection(content, serverKey, out startIdx, out endIdx))
            {
                content = content.Substring(0, startIdx) + tomlSection + content.Substring(endIdx);
            }
            else
            {
                if (content.Length > 0 && !content.EndsWith("\n"))
                    content += "\n";
                content += "\n" + tomlSection;
            }

            File.WriteAllText(target.ConfigPath, content);
            return serverKey;
        }

        /// <summary>
        /// TOML counterpart of <see cref="RemoveSupersededFunplayEntries"/>: drops the section this
        /// project wrote last when its name has changed, and nothing else. Mirrors the JSON path's
        /// hand-edit guard -- a section whose url was repointed at a non-local host was edited
        /// deliberately after we wrote it and is kept.
        /// </summary>
        internal static string RemoveSupersededTomlSection(
            string content, string currentServerName, string previousServerName)
        {
            if (string.IsNullOrEmpty(content) ||
                string.IsNullOrEmpty(previousServerName) ||
                string.Equals(previousServerName, currentServerName, StringComparison.Ordinal) ||
                !FunplayMCPServerKey.IsFunplayKey(previousServerName))
            {
                return content;
            }

            int startIdx;
            int endIdx;
            if (!TryFindTomlSection(content, previousServerName, out startIdx, out endIdx))
                return content;

            var section = content.Substring(startIdx, endIdx - startIdx);
            if (!TomlSectionPointsAtLoopback(section))
                return content;

            return content.Substring(0, startIdx) + content.Substring(endIdx);
        }

        /// <summary>
        /// Locates a <c>[mcp_servers.&lt;name&gt;]</c> section. The single place that owns the
        /// section-boundary rules, shared by the writer and the cleanup so they cannot drift.
        /// Headers only match at the start of a line -- a commented-out
        /// <c># [mcp_servers...]</c> used to match mid-line and made the cleanup cut from inside the
        /// comment. The returned range includes the section's trailing newline; the next section's
        /// <c>[</c> starts at <paramref name="endIdx"/>.
        /// </summary>
        internal static bool TryFindTomlSection(
            string content, string serverName, out int startIdx, out int endIdx)
        {
            startIdx = -1;
            endIdx = -1;
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(serverName))
                return false;

            var header = "[mcp_servers." + serverName + "]";
            var searchFrom = 0;
            while (searchFrom <= content.Length - header.Length)
            {
                var idx = content.IndexOf(header, searchFrom, StringComparison.Ordinal);
                if (idx < 0)
                    return false;

                if (idx == 0 || content[idx - 1] == '\n')
                {
                    startIdx = idx;
                    break;
                }

                searchFrom = idx + 1;
            }

            if (startIdx < 0)
                return false;

            var afterHeader = startIdx + header.Length;
            var nextSection = content.IndexOf("\n[", afterHeader, StringComparison.Ordinal);
            endIdx = nextSection >= 0 ? nextSection + 1 : content.Length;
            return true;
        }

        private static bool TomlSectionPointsAtLoopback(string section)
        {
            var match = Regex.Match(section, "url\\s*=\\s*\"([^\"]*)\"");
            // No parseable url -> no evidence the section is still the one we wrote -> keep it.
            return match.Success && IsLoopbackUrl(match.Groups[1].Value);
        }

        /// <summary>Returns the entry name written, or empty when no config file was rewritten.</summary>
        private string ConfigureLMStudioTarget(MCPConfigTarget target)
        {
            var existingPaths = GetExistingLMStudioConfigPaths(GetUserHomePath());

            // One name for everything LM Studio touches. Resolving per config file could
            // hash-suffix one copy and not another, splitting this project's identity across LM
            // Studio's config copies -- and with only one written name recorded, the cleanup
            // could never retire the others. The deep link needs the shared name too: its target
            // carries a display path (possibly several paths joined for the UI), so resolving
            // against it would always see an unreadable file and never add a needed hash.
            var preferredKey = GetPreferredServerKey();
            var expectedUrl = GetServerUrl();
            var namesAcrossAllFiles = new HashSet<string>(StringComparer.Ordinal);
            var preferredEntryPointsAtCurrentUrl = false;
            foreach (var configPath in existingPaths)
            {
                var fileTarget = target;
                fileTarget.ConfigPath = configPath;
                namesAcrossAllFiles.UnionWith(ReadFunplayEntryNames(fileTarget));
                preferredEntryPointsAtCurrentUrl |=
                    TargetEntryPointsAtUrl(fileTarget, preferredKey, expectedUrl);
            }

            var serverKey = ResolveServerKey(
                _settings.GetLastClientConfigKey(target.Name),
                namesAcrossAllFiles,
                preferredEntryPointsAtCurrentUrl);

            OpenLMStudioAddMCPLink(target, serverKey);

            var wroteAnyFile = false;
            foreach (var configPath in existingPaths)
            {
                var fileTarget = target;
                fileTarget.ConfigPath = configPath;
                ConfigureJsonTarget(fileTarget, serverKey);
                wroteAnyFile = true;
            }

            return wroteAnyFile ? serverKey : string.Empty;
        }

        private static bool TargetEntryPointsAtUrl(
            MCPConfigTarget target, string serverKey, string expectedUrl)
        {
            try
            {
                if (!File.Exists(target.ConfigPath))
                    return false;

                return ConfigEntryPointsAtUrl(
                    File.ReadAllText(target.ConfigPath),
                    target.IsToml,
                    target.RootKey,
                    serverKey,
                    expectedUrl);
            }
            catch (Exception)
            {
                // The write path will report an unreadable or malformed config. Here, lack of a
                // readable exact URL simply means there is no ownership evidence.
                return false;
            }
        }

        /// <summary>
        /// Checks whether a named entry points at the exact endpoint this project would write. LM
        /// Studio's first-time deep link may create the entry after Unity returns without giving us a
        /// write receipt; finding the same key and URL on the next Configure is the evidence that the
        /// entry came from that deep link rather than from another project.
        /// </summary>
        internal static bool ConfigEntryPointsAtUrl(
            string content,
            bool isToml,
            string rootKey,
            string serverKey,
            string expectedUrl)
        {
            if (string.IsNullOrEmpty(content) ||
                string.IsNullOrEmpty(serverKey) ||
                string.IsNullOrEmpty(expectedUrl))
            {
                return false;
            }

            if (isToml)
            {
                int startIdx;
                int endIdx;
                if (!TryFindTomlSection(content, serverKey, out startIdx, out endIdx))
                    return false;

                var section = content.Substring(startIdx, endIdx - startIdx);
                var match = Regex.Match(section, "url\\s*=\\s*\"([^\"]*)\"");
                return match.Success &&
                       string.Equals(match.Groups[1].Value, expectedUrl, StringComparison.OrdinalIgnoreCase);
            }

            var parsed = SimpleJsonHelper.Deserialize(content) as Dictionary<string, object>;
            object serversValue;
            var effectiveRootKey = string.IsNullOrEmpty(rootKey) ? "mcpServers" : rootKey;
            if (parsed == null || !parsed.TryGetValue(effectiveRootKey, out serversValue))
                return false;

            var servers = serversValue as Dictionary<string, object>;
            object entryValue;
            if (servers == null || !servers.TryGetValue(serverKey, out entryValue))
                return false;

            var entry = entryValue as Dictionary<string, object>;
            object urlValue;
            return entry != null &&
                   entry.TryGetValue("url", out urlValue) &&
                   string.Equals(urlValue as string, expectedUrl, StringComparison.OrdinalIgnoreCase);
        }

        private void OpenLMStudioAddMCPLink(MCPConfigTarget target, string serverKey)
        {
            var config = SimpleJsonHelper.Serialize(CreateHttpEntry(target));
            var encodedConfig = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(config)));
            Application.OpenURL(
                $"lmstudio://add_mcp?name={Uri.EscapeDataString(serverKey)}&config={encodedConfig}");
        }

        private string BuildLMStudioConfiguredMessage()
        {
            var existingPaths = GetExistingLMStudioConfigPaths(GetUserHomePath());
            var message = "Opened LM Studio's Add MCP link for Funplay.\n\n";

            if (existingPaths.Count > 0)
            {
                message += "Also updated existing LM Studio config file(s):\n" +
                           string.Join("\n", existingPaths) +
                           "\n\nPlease restart LM Studio or reload MCP integrations if needed.";
            }
            else
            {
                message += "No existing LM Studio mcp.json file was found, so Funplay did not create a guessed path.\n\n" +
                           "If LM Studio did not open automatically, open LM Studio > Program > Install > Edit mcp.json and add Funplay there.";
            }

            return message;
        }

        private Dictionary<string, object> CreateHttpEntry(MCPConfigTarget target)
        {
            var entry = new Dictionary<string, object>
            {
                ["url"] = GetServerUrl()
            };

            if (target.IncludeTypeField)
                entry["type"] = "http";

            return entry;
        }

        private string CreateTomlSection(MCPConfigTarget target, string serverKey)
        {
            if (!target.IsToml)
                return string.Empty;

            return $"[mcp_servers.{serverKey}]\nurl = \"{GetServerUrl()}\"\n";
        }

        /// <summary>
        /// The entry name this project wants: derived from the project directory name, never
        /// hash-suffixed. <see cref="ResolveServerKeyForTarget"/> is what actually gets written.
        /// </summary>
        internal string GetPreferredServerKey()
        {
            return FunplayMCPServerKey.Build(
                GetProjectFolderName(),
                FunplayProjectIdentity.FromProjectPath(GetProjectRootPath()),
                includeProjectHash: false);
        }

        /// <summary>
        /// Entry name to write into <paramref name="target"/>'s config. Normally the preferred name,
        /// but when that name is already in the config and this project is not the one that wrote it,
        /// a project hash is appended automatically: two projects with the same directory name would
        /// otherwise resolve to the same entry name and the second one configured would silently
        /// replace the first one's entry -- with nothing on either side to indicate it happened.
        /// Detecting the collision keeps names clean for everyone else instead of taxing every project
        /// with a hash it does not need (the hash costs 7 of the 25 characters a client tool name can
        /// spare).
        /// </summary>
        private string ResolveServerKeyForTarget(MCPConfigTarget target)
        {
            return ResolveServerKey(
                _settings.GetLastClientConfigKey(target.Name),
                ReadFunplayEntryNames(target));
        }

        private string ResolveServerKey(
            string recordedKey,
            ICollection<string> existingEntryNames,
            bool preferredEntryPointsAtCurrentUrl = false)
        {
            var preferred = GetPreferredServerKey();
            if (!ShouldAddProjectHash(
                    preferred,
                    recordedKey,
                    existingEntryNames,
                    preferredEntryPointsAtCurrentUrl))
            {
                return preferred;
            }

            return FunplayMCPServerKey.Build(
                GetProjectFolderName(),
                FunplayProjectIdentity.FromProjectPath(GetProjectRootPath()),
                includeProjectHash: true);
        }

        /// <summary>
        /// True when the preferred entry name is already taken by a project that is not this one.
        /// The recorded name is the normal ownership evidence: an entry this project wrote is ours
        /// to overwrite, anything else under that name belongs to another project. LM Studio's deep
        /// link is the exception because it creates the file outside Unity; an exact URL match on the
        /// next Configure is accepted as its write receipt.
        /// </summary>
        internal static bool ShouldAddProjectHash(
            string preferredKey,
            string recordedKey,
            ICollection<string> existingEntryNames,
            bool preferredEntryPointsAtCurrentUrl = false)
        {
            if (string.IsNullOrEmpty(preferredKey) || existingEntryNames == null)
                return false;

            if (string.Equals(recordedKey, preferredKey, StringComparison.Ordinal) ||
                preferredEntryPointsAtCurrentUrl)
            {
                return false;
            }

            return existingEntryNames.Contains(preferredKey);
        }

        internal static bool ShouldBlockConfigurationForFallback(
            bool isRunning, int resolvedPort, int activePort)
        {
            return isRunning && resolvedPort > 0 && activePort > 0 && resolvedPort != activePort;
        }

        private bool IsConfigurationBlockedByFallback()
        {
            return _server != null &&
                   ShouldBlockConfigurationForFallback(
                       _server.IsRunning, _server.ResolvedPort, _server.Port);
        }

        private void EnsureConfigurationEndpointIsSafe()
        {
            if (!IsConfigurationBlockedByFallback())
                return;

            throw new InvalidOperationException(BuildFallbackConfigurationBlockedMessage());
        }

        private string BuildFallbackConfigurationBlockedMessage()
        {
            return
                $"This editor resolved stable port {_server.ResolvedPort}, but it is serving on fallback port {_server.Port} " +
                "because the stable port is owned by another process. Writing the stable endpoint could route this " +
                "project's tools to that process, while writing the fallback would leave a stale entry after restart. " +
                "Click Use Per-Project Port or Pin Current Port, wait for the server restart to finish, then Configure again.";
        }

        private string GetServerUrl()
        {
            // ResolvedPort, deliberately not Port: the config file is persistent, so it must carry the
            // project's stable port identity. Port equals it except during a fallback bind, and baking
            // a transient fallback port into the config would leave a dead entry behind the moment the
            // conflict clears. WriteMCPConfigurationForTarget blocks every write while a fallback is
            // active, so this stable URL can never be persisted while another process owns it.
            // ISettingsController.MCPServerPort is only the stored override and is meaningless when
            // nothing was pinned.
            var port = _server != null ? _server.ResolvedPort : _settings.MCPServerPort;
            return $"http://127.0.0.1:{port}/";
        }

        /// <summary>
        /// Recognises the loopback URL shape this plugin writes. Must stay in sync with
        /// <see cref="GetServerUrl"/> -- an entry whose URL no longer matches is treated as
        /// hand-edited and never cleaned up.
        /// </summary>
        internal static bool IsLoopbackUrl(string url)
        {
            return !string.IsNullOrEmpty(url) &&
                   (url.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Project directory name, the source of the entry name. Preferred over
        /// <c>Application.productName</c>: the product name is often left at Unity's default or set to
        /// non-ASCII text, while the directory name always exists and is what developers call the
        /// project.
        /// </summary>
        private static string GetProjectFolderName()
        {
            return Path.GetFileName(GetProjectRootPath());
        }

        private static string GetProjectRootPath()
        {
            return Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
        }

        private static string MapTargetNameToSkillsPlatformId(string targetName)
        {
            switch (targetName?.Trim())
            {
                case "Codex":
                    return "codex";
                case "Claude Code":
                    return "claude";
                case "Cursor":
                    return "cursor";
                default:
                    return null;
            }
        }

        private static string GetUserHomePath()
        {
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(homePath))
                return homePath;

            var homeDrive = Environment.GetEnvironmentVariable("HOMEDRIVE");
            var homeDir = Environment.GetEnvironmentVariable("HOMEPATH");
            if (!string.IsNullOrEmpty(homeDrive) && !string.IsNullOrEmpty(homeDir))
                return homeDrive + homeDir;

            return Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        }

        private static string GetLMStudioDisplayPath(string homePath)
        {
            var existingPaths = GetExistingLMStudioConfigPaths(homePath);
            if (existingPaths.Count > 0)
                return string.Join(" | ", existingPaths);

            return "LM Studio Add MCP link (fallback: Program > Install > Edit mcp.json)";
        }

        private static List<string> GetExistingLMStudioConfigPaths(string homePath)
        {
            return GetLMStudioCandidateConfigPaths(homePath)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> GetLMStudioCandidateConfigPaths(string homePath)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                yield return Path.Combine(homePath, ".cache", "lm-studio", "mcp.json");
                yield return Path.Combine(homePath, ".lmstudio", "mcp.json");
                yield break;
            }

            yield return Path.Combine(homePath, ".lmstudio", "mcp.json");
            yield return Path.Combine(homePath, ".cache", "lm-studio", "mcp.json");
        }

        private static string GetVSCodeConfigPath(string homePath)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    if (!string.IsNullOrEmpty(appData))
                        return Path.Combine(appData, "Code", "User", "mcp.json");
                    break;

                case RuntimePlatform.OSXEditor:
                    var macPrimaryPath = Path.Combine(homePath, "Library", "Application Support", "Code", "User", "mcp.json");
                    var macPrimaryDirectory = Path.GetDirectoryName(macPrimaryPath);
                    if (File.Exists(macPrimaryPath) ||
                        (!string.IsNullOrEmpty(macPrimaryDirectory) && Directory.Exists(macPrimaryDirectory)))
                    {
                        return macPrimaryPath;
                    }

                    return Path.Combine(homePath, ".vscode", "mcp.json");

                case RuntimePlatform.LinuxEditor:
                    return Path.Combine(homePath, ".config", "Code", "User", "mcp.json");
            }

            return Path.Combine(homePath, ".vscode", "mcp.json");
        }

        private struct MCPConfigTarget
        {
            public string Name;
            public string ConfigPath;
            public string RootKey;
            public bool IsToml;
            public bool IncludeTypeField;
            public bool IsLMStudio;
        }
    }
}
