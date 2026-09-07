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

        private const string DeepSeekHarnessTargetName = "DeepSeek Harness";

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
            var skillsSupported = !string.IsNullOrEmpty(
                ProjectSkillsManager.GetPlatformIdForConfigTarget(selectedTarget.Name));
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
                : "Project skills are currently available for Claude Code, Cursor, Codex, OpenCode, DeepSeek Harness, and Antigravity.");
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

            if (target.IsDeepSeekHarness)
            {
                // RefreshStatus runs inside AddTo, so an exception here aborts the rest of the
                // window build (the activity panel never gets added) and repeats on every rebuild.
                // A patch file with a broken managed block is exactly the state this target invites
                // -- deleting the block by hand is the documented uninstall -- so the failure is
                // reported in the label the user is already looking at, never thrown. Reading is
                // best-effort here; only Configure refuses to touch a file it cannot parse.
                try
                {
                    DescribeDeepSeekHarnessStatus();
                }
                catch (Exception ex)
                {
                    _configStatusLabel.text = "Status: patch file needs attention";
                    _configStatusLabel.style.color = new Color(1f, 0.6f, 0.4f);
                    _configPathLabel.text = ex.Message;
                }

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

            // Entries this plugin used to leave at the config's top level (before it started writing
            // Claude Code entries under projects["<path>"]) are visible to every session on this
            // machine, not just this project's -- surface them so they get cleaned up by hand instead
            // of quietly leaking a different project's tools into an unrelated session.
            if (exists && target.UseProjectScope)
            {
                var strayEntries = ReadTopLevelFunplayEntryNames(target);
                if (strayEntries.Count > 0)
                {
                    details +=
                        $"\n⚠ {string.Join(", ", strayEntries)} still at the top level of {target.ConfigPath}. " +
                        "Every Claude Code session on this machine sees those, not just this project's -- " +
                        "remove them by hand once each project you use Funplay in has been re-configured.";
                }
            }

            _configPathLabel.text = details;
        }

        // Same (path, mtime) cache shape as _entryNamesCache below, kept separate rather than shared:
        // a Claude Code RefreshStatus() call queries both this and the project-scoped names for the
        // same file in one pass, and a single shared slot would just have the second query evict the
        // first's result.
        private static string _topLevelEntryNamesCachePath;
        private static DateTime _topLevelEntryNamesCacheMtime;
        private static HashSet<string> _topLevelEntryNamesCache;

        /// <summary>
        /// Funplay-owned entries at the config's literal top level, ignoring the per-project
        /// <c>projects["&lt;path&gt;"]</c> section this plugin writes to for <see
        /// cref="MCPConfigTarget.UseProjectScope"/> targets. Used only to flag leftovers from before
        /// that section existed; unlike <see cref="ReadFunplayEntryNames"/> it never mixes the two.
        /// </summary>
        private static HashSet<string> ReadTopLevelFunplayEntryNames(MCPConfigTarget target)
        {
            try
            {
                if (!File.Exists(target.ConfigPath))
                    return new HashSet<string>(StringComparer.Ordinal);

                var mtime = File.GetLastWriteTimeUtc(target.ConfigPath);
                if (_topLevelEntryNamesCache != null &&
                    string.Equals(target.ConfigPath, _topLevelEntryNamesCachePath, StringComparison.Ordinal) &&
                    mtime == _topLevelEntryNamesCacheMtime)
                {
                    return _topLevelEntryNamesCache;
                }

                var names = ParseTopLevelFunplayEntryNames(target);
                _topLevelEntryNamesCachePath = target.ConfigPath;
                _topLevelEntryNamesCacheMtime = mtime;
                _topLevelEntryNamesCache = names;
                return names;
            }
            catch (Exception)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private static HashSet<string> ParseTopLevelFunplayEntryNames(MCPConfigTarget target)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var content = File.ReadAllText(target.ConfigPath);
            if (content.IndexOf(FunplayMCPServerKey.LegacyKey, StringComparison.Ordinal) < 0)
                return names;

            var parsed = SimpleJsonHelper.Deserialize(content) as Dictionary<string, object>;
            var servers = parsed != null ? FindNestedDictionary(parsed, GetRootKey(target)) : null;
            if (servers == null)
                return names;

            foreach (var key in servers.Keys)
            {
                if (FunplayMCPServerKey.IsFunplayKey(key))
                    names.Add(key);
            }

            return names;
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
            if (parsed == null)
                return names;

            var servers = target.UseProjectScope
                ? FindProjectScopedServers(parsed, GetRootKey(target))
                : FindNestedDictionary(parsed, GetRootKey(target));
            if (servers == null)
                return names;

            foreach (var key in servers.Keys)
            {
                if (FunplayMCPServerKey.IsFunplayKey(key))
                    names.Add(key);
            }

            return names;
        }

        private static Dictionary<string, object> FindNestedDictionary(Dictionary<string, object> parent, string key)
        {
            object value;
            return parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static Dictionary<string, object> FindProjectScopedServers(
            Dictionary<string, object> root, string rootKey)
        {
            var projects = FindNestedDictionary(root, "projects");
            var projectEntry = projects != null ? FindNestedDictionary(projects, GetProjectScopeKeyPath()) : null;
            return projectEntry != null ? FindNestedDictionary(projectEntry, rootKey) : null;
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
            var kimiConfigPath = GetKimiConfigPath(
                homePath,
                GetProjectRootPath(),
                Environment.GetEnvironmentVariable("KIMI_CODE_HOME"));

            return new[]
            {
                new MCPConfigTarget
                {
                    Name = "Claude Code",
                    ConfigPath = Path.Combine(homePath, ".claude.json"),
                    IncludeTypeField = true,
                    UseProjectScope = true
                },
                new MCPConfigTarget
                {
                    Name = "Cursor",
                    ConfigPath = Path.Combine(homePath, ".cursor", "mcp.json"),
                },
                new MCPConfigTarget
                {
                    Name = "Kimi",
                    ConfigPath = kimiConfigPath,
                    ActivationHint = "Start a new Kimi session in this Unity project for it to take effect.",
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
                new MCPConfigTarget
                {
                    Name = "OpenCode",
                    ConfigPath = GetOpenCodeConfigPath(),
                    RootKey = "mcp",
                    IncludeTypeField = true,
                    TypeFieldValue = "remote",
                    IncludeEnabledField = true
                },
                new MCPConfigTarget
                {
                    Name = DeepSeekHarnessTargetName,
                    ConfigPath = FunplayDeepSeekHarnessPatch.GetDisplayPath(homePath),
                    IsDeepSeekHarness = true,
                },
                new MCPConfigTarget
                {
                    Name = "Antigravity",
                    ConfigPath = GetAntigravityConfigPath(homePath),
                    UrlFieldName = "serverUrl",
                    ActivationHint =
                        "Restart Antigravity for it to take effect. " +
                        "Its active servers are listed under Additional Options (...) > MCP Servers.",
                },
            };
        }

        private void ConfigureMCPForTarget(MCPConfigTarget target)
        {
            try
            {
                var customMessage = WriteMCPConfigurationForTarget(target);

                var message = customMessage ??
                              (target.IsLMStudio
                                  ? BuildLMStudioConfiguredMessage()
                                  : $"MCP configuration written to:\n{target.ConfigPath}\n\n" +
                                    (string.IsNullOrEmpty(target.ActivationHint)
                                        ? $"Please restart {target.Name} for it to take effect."
                                        : target.ActivationHint));

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
                var customMessage = WriteMCPConfigurationForTarget(target);

                var platformId = ProjectSkillsManager.GetPlatformIdForConfigTarget(target.Name);
                if (string.IsNullOrEmpty(platformId))
                {
                    var configSummary = customMessage ??
                                        $"MCP configuration written to:\n{target.ConfigPath}";
                    EditorUtility.DisplayDialog(
                        "MCP Configuration",
                        configSummary + "\n\n" +
                        "Project skills are currently available for Claude Code, Cursor, Codex, OpenCode, DeepSeek Harness, and Antigravity.",
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

        /// <summary>
        /// Writes this project's entry for <paramref name="target"/>. Returns a target-specific
        /// completion message, or null to let the caller build the generic one.
        /// </summary>
        private string WriteMCPConfigurationForTarget(MCPConfigTarget target)
        {
            EnsureConfigurationEndpointIsSafe();

            if (target.IsLMStudio)
            {
                // The lmstudio:// deep link alone writes nothing; record the key only when config
                // files were actually rewritten, or a cancelled dialog would poison the record.
                var lmStudioKey = ConfigureLMStudioTarget(target);
                if (!string.IsNullOrEmpty(lmStudioKey))
                    RecordWrittenServerKey(target, lmStudioKey);
                return null;
            }

            if (target.IsDeepSeekHarness)
                return ConfigureDeepSeekHarnessTarget(target);

            var dir = Path.GetDirectoryName(target.ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var writtenKey = target.IsToml
                ? ConfigureTomlTarget(target)
                : ConfigureJsonTarget(target);

            RecordWrittenServerKey(target, writtenKey);
            return null;
        }

        /// <summary>
        /// DeepSeek Harness keeps one composition per profile under ~/.dsh/profiles, selected at
        /// launch with --profile and with no single "active" one visible from outside, so the entry
        /// is written into every existing profile's patch file -- one Configure click then covers a
        /// developer who switches between the web backend and the desktop app. When ~/.dsh exists but
        /// holds no profile at all (a fresh install), the default "web" profile is seeded.
        /// Returns the completion message shown in the dialog.
        /// </summary>
        private string ConfigureDeepSeekHarnessTarget(MCPConfigTarget target)
        {
            var homePath = GetUserHomePath();
            var patchPaths = FunplayDeepSeekHarnessPatch.GetProfilePatchPaths(homePath);
            if (patchPaths.Count == 0)
            {
                if (!Directory.Exists(FunplayDeepSeekHarnessPatch.GetProfilesRoot(homePath)))
                {
                    throw new InvalidOperationException(
                        "DeepSeek Harness was not found (~/.dsh does not exist). " +
                        "Start it once so it creates its home directory, then Configure again.");
                }

                patchPaths.Add(FunplayDeepSeekHarnessPatch.GetDefaultPatchPath(homePath));
            }

            var serverKey = ResolveDeepSeekHarnessServerKey(patchPaths);
            var url = GetServerUrl();
            var supersededKey = _settings.GetLastClientConfigKey(DeepSeekHarnessTargetName);

            var written = new List<string>();
            foreach (var patchPath in patchPaths)
            {
                var content = ReadAllTextIfExists(patchPath);

                // A rename (product folder changed, project hash added or dropped) retires the block
                // this project wrote under its previous key; only our own managed span is removed,
                // mirroring RemoveSupersededFunplayEntries for the JSON/TOML clients.
                if (!string.IsNullOrEmpty(supersededKey) &&
                    !string.Equals(supersededKey, serverKey, StringComparison.Ordinal))
                {
                    content = FunplayDeepSeekHarnessPatch.RemoveManagedBlock(content, supersededKey);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(patchPath));
                File.WriteAllText(
                    patchPath,
                    FunplayDeepSeekHarnessPatch.UpsertManagedBlock(content, serverKey, url));
                written.Add(patchPath);

                // Recorded as soon as one profile carries the key, not after the whole loop: a later
                // profile failing to write must not leave the name unrecorded, or the next Configure
                // would read the block just written as another project's and resolve to a hashed
                // name -- adding a second live entry beside it instead of updating it.
                if (written.Count == 1)
                    RecordWrittenServerKey(target, serverKey);
            }

            var builder = new StringBuilder();
            builder.AppendLine("MCP configuration written to:");
            foreach (var path in written)
                builder.AppendLine(path);
            builder.AppendLine();
            builder.AppendLine("Entry: mcp-" + serverKey + " (" +
                               FunplayDeepSeekHarnessPatch.ClientPluginName +
                               ", streamable HTTP)");
            builder.AppendLine("Tools appear as mcp__" + serverKey + "__<tool>.");
            builder.AppendLine("Restart DeepSeek Harness (or reload its profile) for the entry to take effect.");

            foreach (var path in written)
            {
                if (FunplayDeepSeekHarnessPatch.HasServerNameOutsideManagedBlock(
                        File.ReadAllText(path), serverKey))
                {
                    builder.AppendLine()
                        .Append("⚠ \"").Append(path).Append("\" also contains a hand-written dsh-mcp-client entry ")
                        .Append("with serverName \"").Append(serverKey).Append("\" outside Funplay's managed block. ")
                        .AppendLine("Duplicate serverName values make DeepSeek Harness reject the later instance at load -- remove the manual entry.");
                }
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Per-profile status for the panel's path label: each profile reports ok / stale / missing,
        /// followed by the resolved entry name and endpoint. Also surfaces a hand-written entry that
        /// duplicates our serverName, since that combination makes DSH reject the later instance.
        /// </summary>
        private void DescribeDeepSeekHarnessStatus()
        {
            var homePath = GetUserHomePath();
            if (!Directory.Exists(FunplayDeepSeekHarnessPatch.GetProfilesRoot(homePath)))
            {
                _configStatusLabel.text = "Status: DeepSeek Harness not found (~/.dsh)";
                _configStatusLabel.style.color = new Color(1f, 0.6f, 0.4f);
                _configPathLabel.text =
                    "Start DeepSeek Harness once so it creates ~/.dsh, then Configure.";
                return;
            }

            var patchPaths = FunplayDeepSeekHarnessPatch.GetProfilePatchPaths(homePath);
            var resolvedKey = ResolveDeepSeekHarnessServerKey(patchPaths);
            var expectedUrl = GetServerUrl();

            var configuredCount = 0;
            var lines = new List<string>();
            foreach (var patchPath in patchPaths)
            {
                var profileName = Path.GetFileName(Path.GetDirectoryName(patchPath));
                var content = ReadAllTextIfExists(patchPath);

                string blockUrl;
                if (!FunplayDeepSeekHarnessPatch.TryGetManagedBlockUrl(content, resolvedKey, out blockUrl))
                {
                    lines.Add(profileName + ": no managed entry");
                }
                else if (string.Equals(blockUrl, expectedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(profileName + ": ok (" + blockUrl + ")");
                    configuredCount++;
                }
                else
                {
                    lines.Add(profileName + ": stale (" + blockUrl + " -> configure to update to " + expectedUrl + ")");
                }

                if (FunplayDeepSeekHarnessPatch.HasServerNameOutsideManagedBlock(content, resolvedKey))
                {
                    lines.Add("⚠ hand-written entry with serverName \"" + resolvedKey + "\" found in " +
                              patchPath + " -- duplicate serverName makes DeepSeek Harness reject the later instance; remove it by hand");
                }
            }

            var allConfigured = patchPaths.Count > 0 && configuredCount == patchPaths.Count;
            _configStatusLabel.text = allConfigured ? "Status: Configured" : "Status: Not configured";
            _configStatusLabel.style.color = allConfigured
                ? new Color(0.4f, 1f, 0.4f)
                : new Color(1f, 0.6f, 0.4f);

            var details = patchPaths.Count > 0
                ? string.Join("\n", lines.ToArray())
                : "~/.dsh/profiles holds no profile yet; Configure seeds the default 'web' one.";
            details += "\nEntry: mcp-" + resolvedKey + " (" +
                       FunplayDeepSeekHarnessPatch.ClientPluginName + ") -> " + expectedUrl;

            // Same collision note the JSON/TOML targets show: explain an unexpected hash suffix.
            if (!string.Equals(resolvedKey, GetPreferredServerKey(), StringComparison.Ordinal))
            {
                details +=
                    "\nA project hash was added because another project (or an earlier configuration " +
                    $"of this one) already uses \"{GetPreferredServerKey()}\" in this config.";
            }

            _configPathLabel.text = details;
        }

        /// <summary>
        /// Entry name to write into DSH, resolved against every funplay-named serverName across all
        /// profile patch files -- DSH loads profiles side by side, so a name another project took in
        /// any of them collides exactly like the JSON/TOML clients' per-file scan.
        ///
        /// The scan necessarily also sees this project's own block, so a managed block under the
        /// preferred name pointing at this editor's current URL is accepted as our own write receipt
        /// (the same evidence the LM Studio deep link relies on). Without it, losing the recorded name
        /// -- a machine-local UserSettings file, a fresh clone, a settings reset -- made the next
        /// Configure read our own entry as a foreign collision and write a hash-suffixed *second*
        /// block beside it. Nothing removed the first one, both stayed live against the same endpoint,
        /// and the resolution was self-locking: every later Configure re-derived the same hashed name.
        /// </summary>
        private string ResolveDeepSeekHarnessServerKey(List<string> patchPaths)
        {
            var existingNames = new HashSet<string>(StringComparer.Ordinal);
            var preferredKey = GetPreferredServerKey();
            var url = GetServerUrl();
            var preferredEntryPointsAtCurrentUrl = false;

            foreach (var patchPath in patchPaths)
            {
                var content = ReadAllTextIfExists(patchPath);
                existingNames.UnionWith(FunplayDeepSeekHarnessPatch.ReadFunplayServerNames(content));

                string blockUrl;
                if (FunplayDeepSeekHarnessPatch.TryGetManagedBlockUrl(content, preferredKey, out blockUrl) &&
                    string.Equals(blockUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    preferredEntryPointsAtCurrentUrl = true;
                }
            }

            return ResolveServerKey(
                _settings.GetLastClientConfigKey(DeepSeekHarnessTargetName),
                existingNames,
                preferredEntryPointsAtCurrentUrl);
        }

        private static string ReadAllTextIfExists(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
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

            Dictionary<string, object> root = null;
            if (File.Exists(target.ConfigPath))
                root = ParseRewritableConfig(target, serverName, entry);
            root = root ?? new Dictionary<string, object>();

            var servers = target.UseProjectScope
                ? GetOrCreateProjectScopedServers(root, rootKey)
                : GetOrCreateNestedDictionary(root, rootKey);

            servers[serverName] = entry;
            RemoveSupersededFunplayEntries(
                servers, serverName, _settings.GetLastClientConfigKey(target.Name), target.UrlFieldName);

            File.WriteAllText(target.ConfigPath, SimpleJsonHelper.Serialize(root));
            return serverName;
        }

        /// <summary>
        /// Reads an existing config file, but only when its full contents survive a
        /// <see cref="SimpleJsonHelper"/> round-trip. The whole file is rewritten from what the parse
        /// returns, and that parser is strict-JSON only: a config carrying JSONC comments (legal in
        /// OpenCode's <c>opencode.json</c> and VS Code's <c>mcp.json</c>, both of which are read with
        /// real JSONC parsers) stops the key scan dead at the comment, so writing the parse result
        /// back would silently delete every key past it -- providers, models, keybinds, agents. Rather
        /// than clobber a file it cannot represent, this reports what to add by hand and changes
        /// nothing, the same "stop instead of destroy" stance <c>WriteManagedBlock</c> takes for a
        /// legacy AGENTS.md. Returns null for an empty file (nothing to preserve).
        /// </summary>
        private Dictionary<string, object> ParseRewritableConfig(
            MCPConfigTarget target, string serverName, Dictionary<string, object> entry)
        {
            var content = File.ReadAllText(target.ConfigPath);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            string problem = null;
            Dictionary<string, object> parsed = null;

            if (ContainsJsonComment(content))
                problem = "it contains JSON comments, which Funplay's strict-JSON writer cannot preserve";
            else
            {
                parsed = SimpleJsonHelper.Deserialize(content) as Dictionary<string, object>;
                if (parsed == null)
                    problem = "it could not be read as a JSON object";
            }

            if (problem == null)
                return parsed;

            throw new InvalidOperationException(
                $"'{target.ConfigPath}' was left unchanged because {problem}. " +
                $"Add this entry by hand under \"{GetRootKey(target)}\" instead:\n\n" +
                $"\"{serverName}\": {SimpleJsonHelper.Serialize(entry)}");
        }

        /// <summary>
        /// True if <paramref name="content"/> has a <c>//</c> or <c>/*</c> comment outside a string
        /// literal. Deliberately only detects them -- rewriting a commented file correctly would take
        /// a real JSONC parser, and the point here is to refuse, not to reformat.
        /// Internal so the scan can be exercised in EditMode tests without a config file.
        /// </summary>
        internal static bool ContainsJsonComment(string content)
        {
            var inString = false;
            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];

                if (inString)
                {
                    if (c == '\\')
                        i++;
                    else if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '"')
                    inString = true;
                else if (c == '/' && i + 1 < content.Length && (content[i + 1] == '/' || content[i + 1] == '*'))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets (or creates) the server-name -> entry dictionary at <paramref name="root"/>[<paramref
        /// name="key"/>], replacing whatever is there if it is not already a dictionary (a malformed
        /// or foreign value under that key).
        /// </summary>
        private static Dictionary<string, object> GetOrCreateNestedDictionary(
            Dictionary<string, object> root, string key)
        {
            object existing;
            if (root.TryGetValue(key, out existing) && existing is Dictionary<string, object> servers)
                return servers;

            servers = new Dictionary<string, object>();
            root[key] = servers;
            return servers;
        }

        /// <summary>
        /// Claude Code applies <c>projects["&lt;path&gt;"]</c> only to sessions opened at that exact
        /// path. Writing Funplay's entry there -- instead of at the config's top level, which every
        /// session on the machine sees regardless of which project it is in -- is what keeps one
        /// project's Funplay tools from being usable (and mistaken for the right project's) from an
        /// unrelated project's session. See docs/funplay-mcp-enhancement-backlog.md §I.
        /// </summary>
        private static Dictionary<string, object> GetOrCreateProjectScopedServers(
            Dictionary<string, object> root, string rootKey)
        {
            var projects = GetOrCreateNestedDictionary(root, "projects");
            var projectEntry = GetOrCreateNestedDictionary(projects, GetProjectScopeKeyPath());
            return GetOrCreateNestedDictionary(projectEntry, rootKey);
        }

        private const string ProjectScopeMigrationSessionStateKey =
            "Funplay.ClaudeCodeConfig.ProjectScopeMigrationDone";

        /// <summary>
        /// One-time, best-effort self-heal for a Claude Code entry this project wrote before entries
        /// moved under <c>projects["&lt;path&gt;"]</c> (see CHANGELOG [Unreleased]). Without this, a
        /// project whose Funplay panel is never reopened would keep leaking its tools into every Claude
        /// Code session on the machine indefinitely -- clicking Configure again is the only other way
        /// the old top-level entry gets moved. Called once from <c>RootScopeServices</c> on editor
        /// startup, so it reaches that project the next time its own Editor happens to be open, with no
        /// action required from the developer.
        ///
        /// Session-gated rather than running on every domain reload: <c>~/.claude.json</c> can be
        /// multi-megabyte (see the read-side cache above) and this only ever needs to run once per
        /// Editor process. Only the single entry this project itself previously recorded writing is
        /// touched -- same ownership proof <see cref="RemoveSupersededFunplayEntries"/> already uses --
        /// so another project's entry, or one hand-edited after Funplay wrote it, is left alone.
        /// </summary>
        internal static void TryMigrateLegacyClaudeCodeEntryOnce(ISettingsController settings)
        {
            if (SessionState.GetBool(ProjectScopeMigrationSessionStateKey, false))
                return;
            SessionState.SetBool(ProjectScopeMigrationSessionStateKey, true);

            try
            {
                var target = new MCPConfigTarget
                {
                    Name = "Claude Code",
                    ConfigPath = Path.Combine(GetUserHomePath(), ".claude.json"),
                    IncludeTypeField = true,
                    UseProjectScope = true
                };

                var recordedKey = settings?.GetLastClientConfigKey(target.Name);
                if (string.IsNullOrEmpty(recordedKey) || !File.Exists(target.ConfigPath))
                    return;

                string migratedFrom;
                if (!TryMigrateLegacyClaudeCodeEntryFile(
                        target.ConfigPath,
                        recordedKey,
                        GetProjectRootPath(),
                        GetProjectScopeKeyPath(),
                        out migratedFrom))
                {
                    return;
                }

                PluginDebugLogger.Log(
                    $"[Funplay MCP Server] Migrated Claude Code entry \"{recordedKey}\" out of {migratedFrom} in " +
                    $"{target.ConfigPath} into this project's correct projects[...] scope.");
            }
            catch (Exception ex)
            {
                PluginDebugLogger.Log("[Funplay MCP Server] Claude Code config scope migration skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Migrates the recorded Funplay entry out of Claude Code's legacy global or Unity-project
        /// scopes and into the git-root scope. The transform is written only if the config still
        /// contains the exact text that was parsed, so a concurrent Claude process cannot have its
        /// update silently overwritten. The final replacement is atomic and takes place in the same
        /// directory as the config file.
        /// </summary>
        internal static bool TryMigrateLegacyClaudeCodeEntryFile(
            string configPath,
            string recordedKey,
            string legacyProjectPath,
            string projectScopePath,
            out string migratedFrom)
        {
            migratedFrom = null;
            if (string.IsNullOrEmpty(configPath) ||
                string.IsNullOrEmpty(recordedKey) ||
                !FunplayMCPServerKey.IsFunplayKey(recordedKey) ||
                string.IsNullOrEmpty(projectScopePath) ||
                !File.Exists(configPath))
            {
                return false;
            }

            var originalContent = File.ReadAllText(configPath);

            // Same "stop instead of destroy" guard ConfigureJsonTarget applies through
            // ParseRewritableConfig: the file is rewritten from a strict-JSON parse, so one carrying
            // JSONC comments would lose every key past the first comment. This path runs
            // unattended at editor startup and has no way to ask, so it migrates nothing rather
            // than truncate a config it cannot reproduce; clicking Configure reports the entry to
            // add by hand. Claude Code writes ~/.claude.json itself and does not put comments in
            // it, so this is a guard against a hand-edited file, not an expected shape.
            if (ContainsJsonComment(originalContent))
                return false;

            var root = SimpleJsonHelper.Deserialize(originalContent) as Dictionary<string, object>;
            if (root == null)
                return false;

            if (!TryMigrateLegacyClaudeCodeEntry(
                    root,
                    "mcpServers",
                    recordedKey,
                    legacyProjectPath,
                    projectScopePath,
                    out migratedFrom))
            {
                return false;
            }

            var wroteConfig = TryWriteTextAtomicallyIfUnchanged(
                configPath,
                originalContent,
                SimpleJsonHelper.Serialize(root));
            if (!wroteConfig)
                migratedFrom = null;
            return wroteConfig;
        }

        private static bool TryMigrateLegacyClaudeCodeEntry(
            Dictionary<string, object> root,
            string rootKey,
            string recordedKey,
            string legacyProjectPath,
            string projectScopePath,
            out string migratedFrom)
        {
            migratedFrom = null;

            var topLevelServers = FindNestedDictionary(root, rootKey);
            object topLevelValue;
            var hasTopLevelEntry = TryGetLoopbackEntry(topLevelServers, recordedKey, out topLevelValue);

            Dictionary<string, object> legacyServers = null;
            object legacyValue = null;
            var hasLegacyProjectEntry = false;
            if (!string.IsNullOrEmpty(legacyProjectPath) &&
                !string.Equals(legacyProjectPath, projectScopePath, StringComparison.Ordinal))
            {
                var projects = FindNestedDictionary(root, "projects");
                var legacyEntry = projects != null ? FindNestedDictionary(projects, legacyProjectPath) : null;
                legacyServers = legacyEntry != null ? FindNestedDictionary(legacyEntry, rootKey) : null;
                hasLegacyProjectEntry = TryGetLoopbackEntry(legacyServers, recordedKey, out legacyValue);
            }

            if (!hasTopLevelEntry && !hasLegacyProjectEntry)
                return false;

            Dictionary<string, object> destinationServers;
            if (!TryGetOrCreateProjectScopedServersForMigration(
                    root, rootKey, projectScopePath, out destinationServers))
            {
                return false;
            }

            // Preserve a destination entry that already exists. It may have been reconfigured more
            // recently or deliberately edited by the user; a stale source must never overwrite it.
            if (!destinationServers.ContainsKey(recordedKey))
            {
                // The Unity-project scope was the newer of the two legacy layouts, so prefer its
                // endpoint when both stale copies exist.
                destinationServers[recordedKey] = hasLegacyProjectEntry ? legacyValue : topLevelValue;
            }

            var sources = new List<string>();
            if (hasTopLevelEntry)
            {
                topLevelServers.Remove(recordedKey);
                sources.Add("the top level");
            }

            if (hasLegacyProjectEntry)
            {
                legacyServers.Remove(recordedKey);
                sources.Add($"projects[\"{legacyProjectPath}\"]");
            }

            migratedFrom = string.Join(" and ", sources);
            return true;
        }

        /// <summary>
        /// Creates the destination dictionaries only when every existing value on the path is also
        /// a dictionary. Automatic startup migration must not replace malformed or foreign config
        /// values merely to make room for Funplay.
        /// </summary>
        private static bool TryGetOrCreateProjectScopedServersForMigration(
            Dictionary<string, object> root,
            string rootKey,
            string projectScopePath,
            out Dictionary<string, object> servers)
        {
            servers = null;

            Dictionary<string, object> projects;
            if (!TryGetOrCreateNestedDictionaryForMigration(root, "projects", out projects))
                return false;

            Dictionary<string, object> projectEntry;
            if (!TryGetOrCreateNestedDictionaryForMigration(projects, projectScopePath, out projectEntry))
                return false;

            return TryGetOrCreateNestedDictionaryForMigration(projectEntry, rootKey, out servers);
        }

        private static bool TryGetOrCreateNestedDictionaryForMigration(
            Dictionary<string, object> parent,
            string key,
            out Dictionary<string, object> child)
        {
            object existing;
            if (parent.TryGetValue(key, out existing))
            {
                child = existing as Dictionary<string, object>;
                return child != null;
            }

            child = new Dictionary<string, object>();
            parent[key] = child;
            return true;
        }

        private static bool TryGetLoopbackEntry(
            Dictionary<string, object> servers, string key, out object value)
        {
            value = null;
            return servers != null &&
                   servers.TryGetValue(key, out value) &&
                   IsLoopbackEntry(value);
        }

        /// <summary>
        /// Best-effort compare-and-swap for a JSON config. A temporary file in the destination
        /// directory is fully written first, then atomically replaces the original. Symbolic links
        /// are skipped because replacing one would replace the link itself rather than its target.
        /// </summary>
        internal static bool TryWriteTextAtomicallyIfUnchanged(
            string path, string expectedContent, string updatedContent)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;

            if (!string.Equals(File.ReadAllText(path), expectedContent, StringComparison.Ordinal))
                return false;

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                return false;

            var tempPath = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + ".funplay-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                File.WriteAllText(tempPath, updatedContent, new UTF8Encoding(false));

                // Narrow the race window once more after the potentially expensive temp write.
                if (!string.Equals(File.ReadAllText(path), expectedContent, StringComparison.Ordinal))
                    return false;

                File.Replace(tempPath, path, null);
                return true;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
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
            Dictionary<string, object> servers,
            string currentServerName,
            string previousServerName,
            string urlFieldName = null)
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
            if (!IsLoopbackEntry(previousEntry, urlFieldName))
                return;

            servers.Remove(previousServerName);
        }

        // The endpoint key follows the target (Antigravity writes serverUrl); reading only "url"
        // here would make an Antigravity entry look hand-edited and never retire it.
        private static bool IsLoopbackEntry(object entry, string urlFieldName = null)
        {
            var entryMap = entry as Dictionary<string, object>;
            object url;
            if (entryMap == null || !entryMap.TryGetValue(GetUrlFieldName(urlFieldName), out url))
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
            return CreateHttpEntry(
                GetServerUrl(),
                target.UrlFieldName,
                target.IncludeTypeField,
                target.TypeFieldValue,
                target.IncludeEnabledField);
        }

        internal static Dictionary<string, object> CreateHttpEntry(
            string serverUrl,
            string urlFieldName,
            bool includeTypeField,
            string typeFieldValue,
            bool includeEnabledField)
        {
            var entry = new Dictionary<string, object>
            {
                [GetUrlFieldName(urlFieldName)] = serverUrl
            };

            if (includeTypeField)
                entry["type"] = string.IsNullOrEmpty(typeFieldValue) ? "http" : typeFieldValue;

            if (includeEnabledField)
                entry["enabled"] = true;

            return entry;
        }

        /// <summary>
        /// The JSON key the endpoint lives under: <c>url</c> unless the target names another one
        /// (Antigravity's <c>serverUrl</c>). Every reader of an entry's endpoint must go through this
        /// too, or an entry written under the alternate key is invisible to it.
        /// </summary>
        internal static string GetUrlFieldName(string urlFieldName)
        {
            return string.IsNullOrEmpty(urlFieldName) ? "url" : urlFieldName;
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

        /// <summary>
        /// The path Claude Code actually uses as the <c>projects["&lt;path&gt;"]</c> key: the git
        /// repository root, not this Unity project's own directory. Verified empirically against the
        /// official <c>claude mcp add --scope local</c> CLI, which -- run from a Unity project folder
        /// that is itself a git subdirectory (a monorepo layout, e.g. this repo, where the git root is
        /// the parent of the Unity project) -- writes to <c>projects[gitRoot]</c>, not
        /// <c>projects[unityProjectPath]</c>. Writing under the Unity project path instead leaves the
        /// entry at a key Claude Code never reads for that session, so its tools silently never appear
        /// even though the config and the server are both otherwise correct.
        /// Walks upward from the Unity project directory looking for a <c>.git</c> entry (a directory
        /// for a normal repo, a file for a submodule/worktree); returns the first ancestor that has one,
        /// or falls back to the Unity project path itself if none is found (a project not under git).
        /// </summary>
        private static string GetProjectScopeKeyPath()
        {
            return FindGitRootOrSelf(GetProjectRootPath());
        }

        /// <summary>
        /// Walks upward from <paramref name="startDirectory"/> looking for a <c>.git</c> entry (a
        /// directory for a normal repo, a file for a submodule/worktree); returns the first ancestor
        /// that has one, or <paramref name="startDirectory"/> itself if none is found within the walk.
        /// Split out from <see cref="GetProjectScopeKeyPath"/> so the walk can be exercised in EditMode
        /// tests against real temporary directories instead of <c>Application.dataPath</c>.
        /// </summary>
        internal static string FindGitRootOrSelf(string startDirectory)
        {
            var dir = startDirectory;
            for (var depth = 0; depth < 64 && !string.IsNullOrEmpty(dir); depth++)
            {
                if (File.Exists(Path.Combine(dir, ".git")) || Directory.Exists(Path.Combine(dir, ".git")))
                    return dir;

                var parent = Directory.GetParent(dir);
                if (parent == null)
                    break;
                dir = parent.FullName;
            }

            return startDirectory;
        }

        /// <summary>
        /// OpenCode's entry goes into this repository's own <c>.opencode/opencode.json</c>, not the
        /// global <c>~/.config/opencode/opencode.json</c>. OpenCode merges every config location it
        /// finds (global <c>config.json</c> + <c>opencode.json</c> + <c>opencode.jsonc</c>, then the
        /// project's, then <c>.opencode</c> directories, later ones overriding conflicting keys), and
        /// it discovers the project file by walking upward from the directory the session was started
        /// in -- so a file at the repository root is reachable from anywhere inside the repo, while a
        /// global entry is visible to *every* session on the machine regardless of which project it
        /// was opened in. That global visibility is the same cross-project leak project-scoping the
        /// Claude Code entry fixes (see <see cref="GetOrCreateProjectScopedServers"/>): with two
        /// Funplay projects configured, whichever Unity Editor happens to be running would expose its
        /// tools inside an unrelated project's OpenCode session, indistinguishable from that project's
        /// own entry. OpenCode is written per project for the same reason.
        /// The repository root is used rather than the Unity project directory so the entry is found
        /// whether OpenCode is started at the repo root or inside the Unity project folder (a monorepo
        /// layout, e.g. this repo, where the git root is the Unity project's parent); the walk is
        /// upward only, so the reverse placement would miss.
        /// </summary>
        private static string GetOpenCodeConfigPath()
        {
            return Path.Combine(GetProjectScopeKeyPath(), ".opencode", "opencode.json");
        }

        /// <summary>
        /// Antigravity keeps its MCP servers in <c>~/.gemini/config/mcp_config.json</c> -- one global
        /// map of server id to spec, plus per-plugin <c>plugins/&lt;name&gt;/mcp_config.json</c> files
        /// that only load with their plugin, which is not a place a Unity plugin should be writing to.
        /// The remote spec carries a single <c>serverUrl</c>; Antigravity's own documentation calls
        /// that "SSE transport", but the language server has exactly two connectors --
        /// <c>LocalSubprocessConnector</c> for <c>command</c> and <c>StreamableHTTPConnector</c> for
        /// <c>serverUrl</c> -- so a streamable-HTTP endpoint like this one is what it actually speaks.
        /// There is no project-scoping concept here, so like Cursor/VS Code/Trae/Kiro the entry is
        /// global and stays distinguishable only by its per-project name.
        /// </summary>
        private static string GetAntigravityConfigPath(string homePath)
        {
            return Path.Combine(homePath, ".gemini", "config", "mcp_config.json");
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

        /// <summary>
        /// Kimi Code's current format supports a project-local <c>.kimi-code/mcp.json</c>, which
        /// keeps this Unity project's loopback server out of unrelated Kimi sessions. Older Kimi CLI
        /// releases only load <c>~/.kimi/mcp.json</c>, so a machine with only that legacy data root
        /// keeps using it. A configured <c>KIMI_CODE_HOME</c> or an existing <c>~/.kimi-code</c>
        /// identifies the current client; when neither client has run yet, prefer the current format.
        /// </summary>
        internal static string GetKimiConfigPath(
            string homePath,
            string projectRoot,
            string kimiCodeHomeOverride)
        {
            var modernHome = !string.IsNullOrWhiteSpace(kimiCodeHomeOverride)
                ? kimiCodeHomeOverride.Trim()
                : Path.Combine(homePath, ".kimi-code");
            var legacyHome = Path.Combine(homePath, ".kimi");
            var modernDetected = !string.IsNullOrWhiteSpace(kimiCodeHomeOverride) || Directory.Exists(modernHome);
            var legacyDetected = Directory.Exists(legacyHome);

            if (modernDetected || !legacyDetected)
            {
                if (!string.IsNullOrWhiteSpace(projectRoot))
                    return Path.Combine(projectRoot, ".kimi-code", "mcp.json");

                return Path.Combine(modernHome, "mcp.json");
            }

            return Path.Combine(legacyHome, "mcp.json");
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
            public string ActivationHint;
            public string RootKey;
            public bool IsToml;
            public bool IncludeTypeField;
            public bool IsLMStudio;

            /// <summary>
            /// True only for DeepSeek Harness. Its entry lives in one or more profile patch files
            /// (<c>~/.dsh/profiles/&lt;profile&gt;/cordis.patch.yml</c>) written as a managed YAML
            /// block by <see cref="FunplayDeepSeekHarnessPatch"/> -- not as a single JSON/TOML key
            /// like every other client here, so <see cref="ConfigPath"/> carries a display string
            /// and all reads/writes go through dedicated methods.
            /// </summary>
            public bool IsDeepSeekHarness;

            /// <summary>
            /// Value written for the <c>type</c> field when <see cref="IncludeTypeField"/> is set.
            /// Empty means "http" (the Claude Code / VS Code shape); OpenCode uses "remote".
            /// </summary>
            public string TypeFieldValue;

            /// <summary>
            /// Key the endpoint is written under inside the entry. Empty means <c>url</c>, which is
            /// what every client here uses except Antigravity: its <c>McpServerSpec</c> names the
            /// remote endpoint <c>serverUrl</c> and ignores an entry carrying neither that nor
            /// <c>command</c>.
            /// </summary>
            public string UrlFieldName;

            /// <summary>
            /// True for OpenCode, whose documented remote-server example spells the flag out
            /// (<c>{"type":"remote","url":...,"enabled":true}</c>). The field is optional in its schema
            /// and its default is not documented, so it is written explicitly rather than relied on.
            /// </summary>
            public bool IncludeEnabledField;

            /// <summary>
            /// True only for Claude Code. Its config file supports a <c>projects["&lt;path&gt;"]</c>
            /// section that Claude Code applies only to sessions opened at that path; every other
            /// client here has no such concept and keeps writing at the config's top level.
            /// </summary>
            public bool UseProjectScope;
        }
    }
}
