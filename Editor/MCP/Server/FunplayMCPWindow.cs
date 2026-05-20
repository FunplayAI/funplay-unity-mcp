// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Funplay.Editor.DI;
using Funplay.Editor.Services;
using Funplay.Editor.Settings;
using Funplay.Editor.Tools;

namespace Funplay.Editor.MCP.Server
{
    internal class FunplayMCPWindow : EditorWindow
    {
        private ISettingsController _settingsController;
        private MCPServerService _mcpServer;
        private VisualElement _mainContainer;
        private VisualElement _updateContainer;
        private Label _statusLabel;
        private Label _versionLabel;
        private Label _updateStatusLabel;
        private Button _updateButton;
        private ProgressBar _updateProgressBar;
        private ScrollView _logScrollView;
        private MCPConfigTarget[] _mcpTargets;
        private int _selectedTargetIndex;
        private Label _configStatusLabel;
        private Label _configPathLabel;
        private readonly List<Texture2D> _logPreviewTextures = new List<Texture2D>();

        [MenuItem("Funplay/MCP Server")]
        public static void ShowWindow()
        {
            var window = GetWindow<FunplayMCPWindow>("MCP Server");
            window.minSize = new Vector2(360, 400);
            window.Show();
        }

        public void CreateGUI()
        {
            _settingsController = RootScopeServices.Services?.GetService(typeof(ISettingsController))
                as ISettingsController;
            _mcpServer = RootScopeServices.Services?.GetService(typeof(MCPServerService))
                as MCPServerService;

            if (_settingsController == null || _mcpServer == null)
            {
                rootVisualElement.Add(new Label("Failed to initialize services."));
                return;
            }

            FunplayMCPUpdateChecker.StateChanged -= OnUpdateStateChanged;
            FunplayMCPUpdateChecker.StateChanged += OnUpdateStateChanged;

            BuildUI();
            FunplayMCPUpdateChecker.MaybeCheckForUpdatesInBackground();
            _mcpServer.InteractionLog.OnEntryAdded += OnLogEntryAdded;
        }

        private void OnDestroy()
        {
            if (_mcpServer?.InteractionLog != null)
                _mcpServer.InteractionLog.OnEntryAdded -= OnLogEntryAdded;

            FunplayMCPUpdateChecker.StateChanged -= OnUpdateStateChanged;
            ClearLogPreviewTextures();
        }

        private void BuildUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexGrow = 1;
            rootVisualElement.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f);

            _mainContainer = new VisualElement();
            _mainContainer.style.flexGrow = 1;
            _mainContainer.style.paddingLeft = 10;
            _mainContainer.style.paddingRight = 10;
            _mainContainer.style.paddingTop = 10;
            _mainContainer.style.paddingBottom = 10;
            rootVisualElement.Add(_mainContainer);

            // Title
            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;
            titleRow.style.marginBottom = 8;
            _mainContainer.Add(titleRow);

            var title = new Label("MCP Server");
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.flexGrow = 1;
            titleRow.Add(title);

            _versionLabel = new Label($"v{PackageVersionUtility.CurrentVersion}");
            _versionLabel.style.fontSize = 11;
            _versionLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            _versionLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            titleRow.Add(_versionLabel);

            // Status
            _statusLabel = new Label();
            _statusLabel.style.fontSize = 13;
            _statusLabel.style.marginBottom = 10;
            _mainContainer.Add(_statusLabel);
            RefreshStatus();

            BuildUpdateSection();

            // Enable toggle
            var toggle = new Toggle("Enable MCP Server");
            toggle.SetValueWithoutNotify(_settingsController.MCPServerEnabled);
            toggle.RegisterValueChangedCallback(evt =>
            {
                _settingsController.MCPServerEnabled = evt.newValue;
                if (evt.newValue)
                    _ = _mcpServer.StartAsync();
                else
                    _ = _mcpServer.StopAsync();

                EditorApplication.delayCall += () =>
                    EditorApplication.delayCall += RefreshStatus;
            });
            toggle.style.marginBottom = 4;
            _mainContainer.Add(toggle);

            // Port
            var portField = new IntegerField("Server Port");
            portField.SetValueWithoutNotify(_settingsController.MCPServerPort);
            portField.RegisterValueChangedCallback(evt =>
            {
                _settingsController.MCPServerPort = evt.newValue;
            });
            portField.style.marginBottom = 10;
            _mainContainer.Add(portField);

            var toolProfileChoices = new List<string> { "core", "full" };
            var toolProfileField = new PopupField<string>("Tool Exposure", toolProfileChoices,
                Mathf.Max(0, toolProfileChoices.IndexOf(_settingsController.MCPToolExportProfile ?? "core")));
            toolProfileField.SetValueWithoutNotify(_settingsController.MCPToolExportProfile ?? "core");
            toolProfileField.RegisterValueChangedCallback(evt =>
            {
                _settingsController.MCPToolExportProfile = evt.newValue;
            });
            toolProfileField.style.marginBottom = 4;
            _mainContainer.Add(toolProfileField);

            var toolProfileHint = new Label("core reduces tool-list noise for AI clients. full exposes every tool.");
            toolProfileHint.style.fontSize = 10;
            toolProfileHint.style.color = new Color(0.65f, 0.65f, 0.65f);
            toolProfileHint.style.marginBottom = 10;
            _mainContainer.Add(toolProfileHint);

            // One-Click Config Section
            var configLabel = new Label("One-Click MCP Configuration");
            configLabel.style.fontSize = 12;
            configLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            configLabel.style.color = new Color(0.75f, 0.75f, 0.75f);
            configLabel.style.marginBottom = 6;
            _mainContainer.Add(configLabel);

            var homePath = GetUserHomePath();

            _mcpTargets = new[]
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
                    ConfigPath = Path.Combine(homePath, ".lmstudio", "mcp.json"),
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

            // Dropdown + Configure button row
            var configRow = new VisualElement();
            configRow.style.flexDirection = FlexDirection.Row;
            configRow.style.alignItems = Align.Center;
            configRow.style.marginBottom = 4;

            var nameList = new List<string>();
            foreach (var t in _mcpTargets) nameList.Add(t.Name);

            _selectedTargetIndex = Mathf.Clamp(_selectedTargetIndex, 0, _mcpTargets.Length - 1);
            var persistedTargetName = _settingsController.MCPSelectedConfigTarget;
            if (!string.IsNullOrWhiteSpace(persistedTargetName))
            {
                var persistedIndex = nameList.FindIndex(name => string.Equals(name, persistedTargetName, StringComparison.OrdinalIgnoreCase));
                if (persistedIndex >= 0)
                    _selectedTargetIndex = persistedIndex;
            }

            var dropdown = new PopupField<string>(nameList, _selectedTargetIndex);
            dropdown.style.flexGrow = 1;
            dropdown.style.height = 26;
            dropdown.RegisterValueChangedCallback(evt =>
            {
                _selectedTargetIndex = nameList.IndexOf(evt.newValue);
                _settingsController.MCPSelectedConfigTarget = evt.newValue;
                BuildUI();
            });
            configRow.Add(dropdown);

            var configBtn = new Button(() =>
            {
                ConfigureMCPForTarget(_mcpTargets[_selectedTargetIndex]);
                RefreshConfigStatus();
            });
            configBtn.text = "Configure";
            configBtn.style.height = 26;
            configBtn.style.width = 80;
            configBtn.style.marginLeft = 4;
            configBtn.style.backgroundColor = new Color(0.2f, 0.5f, 0.3f);
            configBtn.style.color = Color.white;
            configRow.Add(configBtn);

            var selectedTarget = _mcpTargets[_selectedTargetIndex];
            var skillsSupported = !string.IsNullOrEmpty(MapTargetNameToSkillsPlatformId(selectedTarget.Name));
            var configWithSkillsBtn = new Button(() =>
            {
                ConfigureMCPAndSkillsForTarget(_mcpTargets[_selectedTargetIndex]);
                RefreshConfigStatus();
            });
            configWithSkillsBtn.text = "Configure + Skills";
            configWithSkillsBtn.style.height = 26;
            configWithSkillsBtn.style.width = 130;
            configWithSkillsBtn.style.marginLeft = 4;
            configWithSkillsBtn.style.backgroundColor = new Color(0.25f, 0.45f, 0.65f);
            configWithSkillsBtn.style.color = Color.white;
            configWithSkillsBtn.SetEnabled(skillsSupported);
            configRow.Add(configWithSkillsBtn);

            _mainContainer.Add(configRow);

            var skillsHint = new Label(skillsSupported
                ? "Configure + Skills also installs the project MCP workflow skill."
                : "Project skills are currently available for Claude Code, Cursor, and Codex.");
            skillsHint.style.fontSize = 10;
            skillsHint.style.color = new Color(0.6f, 0.6f, 0.6f);
            skillsHint.style.marginBottom = 4;
            skillsHint.style.whiteSpace = WhiteSpace.Normal;
            _mainContainer.Add(skillsHint);

            _configStatusLabel = new Label();
            _configStatusLabel.style.fontSize = 11;
            _configStatusLabel.style.marginBottom = 2;
            _mainContainer.Add(_configStatusLabel);

            _configPathLabel = new Label();
            _configPathLabel.style.fontSize = 10;
            _configPathLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            _configPathLabel.style.marginBottom = 6;
            _configPathLabel.style.whiteSpace = WhiteSpace.Normal;
            _mainContainer.Add(_configPathLabel);

            RefreshConfigStatus();

            // Interaction Log Section
            BuildLogSection();
        }

        private void BuildUpdateSection()
        {
            _updateContainer = new VisualElement();
            _updateContainer.style.display = DisplayStyle.None;
            _updateContainer.style.backgroundColor = new Color(0.23f, 0.20f, 0.13f);
            _updateContainer.style.borderLeftWidth = 3;
            _updateContainer.style.borderLeftColor = new Color(1f, 0.75f, 0.3f);
            _updateContainer.style.borderTopLeftRadius = 4;
            _updateContainer.style.borderTopRightRadius = 4;
            _updateContainer.style.borderBottomLeftRadius = 4;
            _updateContainer.style.borderBottomRightRadius = 4;
            _updateContainer.style.paddingLeft = 8;
            _updateContainer.style.paddingRight = 8;
            _updateContainer.style.paddingTop = 6;
            _updateContainer.style.paddingBottom = 6;
            _updateContainer.style.marginBottom = 10;

            var updateRow = new VisualElement();
            updateRow.style.flexDirection = FlexDirection.Row;
            updateRow.style.alignItems = Align.Center;
            _updateContainer.Add(updateRow);

            _updateStatusLabel = new Label();
            _updateStatusLabel.style.fontSize = 11;
            _updateStatusLabel.style.color = new Color(0.95f, 0.88f, 0.68f);
            _updateStatusLabel.style.whiteSpace = WhiteSpace.Normal;
            _updateStatusLabel.style.flexGrow = 1;
            updateRow.Add(_updateStatusLabel);

            _updateButton = new Button(FunplayMCPUpdateChecker.UpdateToLatestFromWindow);
            _updateButton.text = "Update";
            _updateButton.style.height = 24;
            _updateButton.style.minWidth = 86;
            _updateButton.style.marginLeft = 8;
            _updateButton.style.backgroundColor = new Color(0.82f, 0.48f, 0.18f);
            _updateButton.style.color = Color.white;
            updateRow.Add(_updateButton);

            _updateProgressBar = new ProgressBar
            {
                lowValue = 0f,
                highValue = 1f,
                value = 0f
            };
            _updateProgressBar.style.display = DisplayStyle.None;
            _updateProgressBar.style.height = 16;
            _updateProgressBar.style.marginTop = 6;
            _updateContainer.Add(_updateProgressBar);

            _mainContainer.Add(_updateContainer);
            RefreshUpdateUI();
        }

        private void BuildLogSection()
        {
            ClearLogPreviewTextures();

            var logHeader = new VisualElement();
            logHeader.style.flexDirection = FlexDirection.Row;
            logHeader.style.alignItems = Align.Center;
            logHeader.style.marginTop = 12;
            logHeader.style.marginBottom = 4;

            var logLabel = new Label("Recent Activity");
            logLabel.style.fontSize = 12;
            logLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            logLabel.style.color = new Color(0.75f, 0.75f, 0.75f);
            logLabel.style.flexGrow = 1;
            logHeader.Add(logLabel);

            var clearBtn = new Button(() =>
            {
                _mcpServer.InteractionLog.Clear();
                ClearLogPreviewTextures();
                _logScrollView.contentContainer.Clear();
            });
            clearBtn.text = "Clear";
            clearBtn.style.height = 20;
            clearBtn.style.width = 50;
            logHeader.Add(clearBtn);

            _mainContainer.Add(logHeader);

            _logScrollView = new ScrollView(ScrollViewMode.Vertical);
            _logScrollView.style.flexGrow = 1;
            _logScrollView.style.backgroundColor = new Color(0.14f, 0.14f, 0.14f);
            _logScrollView.style.borderTopLeftRadius = 4;
            _logScrollView.style.borderTopRightRadius = 4;
            _logScrollView.style.borderBottomLeftRadius = 4;
            _logScrollView.style.borderBottomRightRadius = 4;
            _logScrollView.style.paddingLeft = 6;
            _logScrollView.style.paddingRight = 6;
            _logScrollView.style.paddingTop = 4;
            _logScrollView.style.paddingBottom = 4;
            _mainContainer.Add(_logScrollView);

            var entries = _mcpServer.InteractionLog.GetEntries();
            for (int i = entries.Count - 1; i >= 0; i--)
                AddLogRow(entries[i]);
        }

        private void AddLogRow(MCPLogEntry entry)
        {
            bool isOk = entry.Status == MCPToolCallStatus.Success;
            var accentColor = isOk ? new Color(0.3f, 0.75f, 0.4f) : new Color(0.9f, 0.35f, 0.35f);

            var card = new VisualElement();
            card.style.backgroundColor = new Color(0.19f, 0.19f, 0.19f);
            card.style.borderTopLeftRadius = 4;
            card.style.borderTopRightRadius = 4;
            card.style.borderBottomLeftRadius = 4;
            card.style.borderBottomRightRadius = 4;
            card.style.borderLeftWidth = 3;
            card.style.borderLeftColor = accentColor;
            card.style.paddingLeft = 8;
            card.style.paddingRight = 8;
            card.style.paddingTop = 5;
            card.style.paddingBottom = 5;
            card.style.marginBottom = 3;

            var topRow = new VisualElement();
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.alignItems = Align.Center;

            var timeLabel = new Label(entry.Timestamp.ToString("HH:mm:ss"));
            timeLabel.style.fontSize = 10;
            timeLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            timeLabel.style.marginRight = 6;
            timeLabel.style.minWidth = 48;
            topRow.Add(timeLabel);

            var toolLabel = new Label(entry.ToolName);
            toolLabel.style.fontSize = 12;
            toolLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            toolLabel.style.color = new Color(0.88f, 0.88f, 0.88f);
            toolLabel.style.flexGrow = 1;
            topRow.Add(toolLabel);

            var badge = new Label(isOk ? "OK" : "ERR");
            badge.style.fontSize = 9;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.color = Color.white;
            badge.style.backgroundColor = accentColor;
            badge.style.borderTopLeftRadius = 3;
            badge.style.borderTopRightRadius = 3;
            badge.style.borderBottomLeftRadius = 3;
            badge.style.borderBottomRightRadius = 3;
            badge.style.paddingLeft = 5;
            badge.style.paddingRight = 5;
            badge.style.paddingTop = 1;
            badge.style.paddingBottom = 1;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            topRow.Add(badge);

            card.Add(topRow);

            if (!string.IsNullOrEmpty(entry.ResultSummary))
            {
                var summaryLabel = new Label(entry.ResultSummary);
                summaryLabel.style.fontSize = 11;
                summaryLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
                summaryLabel.style.marginTop = 3;
                summaryLabel.style.whiteSpace = WhiteSpace.Normal;
                summaryLabel.style.overflow = Overflow.Hidden;
                card.Add(summaryLabel);
            }

            if (!string.IsNullOrEmpty(entry.ImageDataUri) && TryCreateImagePreview(entry.ImageDataUri, out var preview))
                card.Add(preview);

            _logScrollView.contentContainer.Add(card);
        }

        private bool TryCreateImagePreview(string imageDataUri, out Image preview)
        {
            preview = null;
            const string prefix = "data:image/png;base64,";
            if (string.IsNullOrEmpty(imageDataUri) || !imageDataUri.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            try
            {
                var bytes = Convert.FromBase64String(imageDataUri.Substring(prefix.Length));
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    return false;
                }

                _logPreviewTextures.Add(texture);

                preview = new Image
                {
                    image = texture,
                    scaleMode = ScaleMode.ScaleToFit
                };
                preview.style.height = 150;
                preview.style.marginTop = 6;
                preview.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f);
                preview.style.borderTopLeftRadius = 3;
                preview.style.borderTopRightRadius = 3;
                preview.style.borderBottomLeftRadius = 3;
                preview.style.borderBottomRightRadius = 3;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ClearLogPreviewTextures()
        {
            foreach (var texture in _logPreviewTextures)
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }

            _logPreviewTextures.Clear();
        }

        private void OnLogEntryAdded(MCPLogEntry entry)
        {
            EditorApplication.delayCall += () =>
            {
                if (_logScrollView == null) return;
                AddLogRow(entry);
                EditorApplication.delayCall += () =>
                {
                    if (_logScrollView != null)
                        _logScrollView.scrollOffset = new Vector2(0, float.MaxValue);
                };
            };
        }

        private void RefreshStatus()
        {
            if (_statusLabel == null) return;
            if (_mcpServer?.IsRunning == true)
            {
                _statusLabel.text = $"Running on http://127.0.0.1:{_mcpServer.Port}/ ({_settingsController.MCPToolExportProfile ?? "core"})";
                _statusLabel.style.color = new Color(0.4f, 1f, 0.4f);
            }
            else
            {
                _statusLabel.text = "Stopped";
                _statusLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            }
        }

        private void OnUpdateStateChanged()
        {
            EditorApplication.delayCall += RefreshUpdateUI;
        }

        private void RefreshUpdateUI()
        {
            if (_updateContainer == null || _updateStatusLabel == null || _updateButton == null || _updateProgressBar == null)
                return;

            var state = FunplayMCPUpdateChecker.CurrentState;
            if (_versionLabel != null)
                _versionLabel.text = $"v{state.CurrentVersion}";

            var showUpdatePanel = state.HasUpdateAvailable || state.IsUpdating || state.UpdateStarted;
            _updateContainer.style.display = showUpdatePanel ? DisplayStyle.Flex : DisplayStyle.None;
            if (!showUpdatePanel)
                return;

            if (state.IsUpdating || state.UpdateStarted)
            {
                _updateStatusLabel.text = string.IsNullOrEmpty(state.StatusMessage)
                    ? $"Updating to version {state.LatestVersion}..."
                    : state.StatusMessage;
            }
            else
            {
                var installDescription = string.IsNullOrEmpty(state.InstallDescription)
                    ? string.Empty
                    : $" ({state.InstallDescription})";
                _updateStatusLabel.text = $"Version {state.LatestVersion} is available{installDescription}.";
            }

            var showButton = state.HasUpdateAvailable && !state.IsUpdating && !state.UpdateStarted;
            _updateButton.style.display = showButton ? DisplayStyle.Flex : DisplayStyle.None;
            _updateButton.text = string.IsNullOrEmpty(state.LatestVersion)
                ? "Update"
                : $"Update to v{state.LatestVersion}";

            var showProgress = state.IsUpdating || state.UpdateStarted;
            _updateProgressBar.style.display = showProgress ? DisplayStyle.Flex : DisplayStyle.None;
            _updateProgressBar.value = Mathf.Clamp01(state.Progress);
            _updateProgressBar.title = string.IsNullOrEmpty(state.StatusMessage)
                ? "Updating..."
                : state.StatusMessage;
        }

        private void RefreshConfigStatus()
        {
            if (_configStatusLabel == null || _configPathLabel == null || _mcpTargets == null) return;
            var idx = Mathf.Clamp(_selectedTargetIndex, 0, _mcpTargets.Length - 1);
            var target = _mcpTargets[idx];
            bool exists = File.Exists(target.ConfigPath);

            _configStatusLabel.text = exists ? "Status: Configured" : "Status: Not configured";
            _configStatusLabel.style.color = exists
                ? new Color(0.4f, 1f, 0.4f)
                : new Color(1f, 0.6f, 0.4f);

            _configPathLabel.text = target.ConfigPath;
        }

        private struct MCPConfigTarget
        {
            public string Name;
            public string ConfigPath;
            public string RootKey;
            public bool IsToml;
            public bool IncludeTypeField;
        }

        private void ConfigureMCPForTarget(MCPConfigTarget target)
        {
            try
            {
                WriteMCPConfigurationForTarget(target);

                EditorUtility.DisplayDialog(
                    "MCP Configuration",
                    $"MCP configuration written to:\n{target.ConfigPath}\n\n" +
                    $"Please restart {target.Name} for it to take effect.",
                    "OK");

                BuildUI();
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

                    BuildUI();
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

                BuildUI();
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
            var dir = Path.GetDirectoryName(target.ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (target.IsToml)
                ConfigureTomlTarget(target);
            else
                ConfigureJsonTarget(target);
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

        private void ConfigureJsonTarget(MCPConfigTarget target)
        {
            var rootKey = string.IsNullOrEmpty(target.RootKey) ? "mcpServers" : target.RootKey;
            var serverName = "funplay";
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
                        servers[serverName] = entry;
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
        }

        private void ConfigureTomlTarget(MCPConfigTarget target)
        {
            var sectionHeader = "[mcp_servers.funplay]";
            var tomlSection = CreateTomlSection(target);
            var content = File.Exists(target.ConfigPath) ? File.ReadAllText(target.ConfigPath) : "";

            if (content.Contains(sectionHeader))
            {
                // Replace existing section: find start, find next section or EOF, replace
                var startIdx = content.IndexOf(sectionHeader, StringComparison.Ordinal);
                var afterHeader = startIdx + sectionHeader.Length;
                var nextSection = content.IndexOf("\n[", afterHeader, StringComparison.Ordinal);
                var endIdx = nextSection >= 0 ? nextSection : content.Length;
                content = content.Substring(0, startIdx) + tomlSection + content.Substring(endIdx);
            }
            else
            {
                // Append
                if (content.Length > 0 && !content.EndsWith("\n"))
                    content += "\n";
                content += "\n" + tomlSection;
            }

            File.WriteAllText(target.ConfigPath, content);
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

        private string CreateTomlSection(MCPConfigTarget target)
        {
            if (!target.IsToml)
                return string.Empty;

            return $"[mcp_servers.funplay]\nurl = \"{GetServerUrl()}\"\n";
        }

        private string GetServerUrl()
        {
            var port = _mcpServer != null && _mcpServer.IsRunning
                ? _mcpServer.Port
                : _settingsController.MCPServerPort;
            return $"http://127.0.0.1:{port}/";
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
                    if (File.Exists(macPrimaryPath) || (!string.IsNullOrEmpty(macPrimaryDirectory) && Directory.Exists(macPrimaryDirectory)))
                        return macPrimaryPath;

                    return Path.Combine(homePath, ".vscode", "mcp.json");

                case RuntimePlatform.LinuxEditor:
                    return Path.Combine(homePath, ".config", "Code", "User", "mcp.json");
            }

            return Path.Combine(homePath, ".vscode", "mcp.json");
        }
    }

    internal class FunplayPluginSettingsWindow : EditorWindow
    {
        private ISettingsController _settingsController;
        private Toggle _debugLoggingToggle;
        private Label _debugStatusLabel;

        [MenuItem("Funplay/Plugin Settings")]
        public static void ShowWindow()
        {
            var window = GetWindow<FunplayPluginSettingsWindow>("Plugin Settings");
            window.minSize = new Vector2(360, 220);
            window.Show();
        }

        public void CreateGUI()
        {
            _settingsController = RootScopeServices.Services?.GetService(typeof(ISettingsController))
                as ISettingsController;

            if (_settingsController == null)
            {
                rootVisualElement.Add(new Label("Failed to initialize services."));
                return;
            }

            _settingsController.OnSettingsChanged += RefreshStatus;
            BuildUI();
        }

        private void OnDestroy()
        {
            if (_settingsController != null)
                _settingsController.OnSettingsChanged -= RefreshStatus;
        }

        private void BuildUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexGrow = 1;
            rootVisualElement.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f);
            rootVisualElement.style.paddingLeft = 10;
            rootVisualElement.style.paddingRight = 10;
            rootVisualElement.style.paddingTop = 10;
            rootVisualElement.style.paddingBottom = 10;

            var title = new Label("Plugin Settings");
            title.style.fontSize = 17;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.marginBottom = 4;
            rootVisualElement.Add(title);

            var hint = new Label("Project-level settings for the Funplay Unity MCP plugin. Debug logging is enabled by default.");
            hint.style.fontSize = 11;
            hint.style.color = new Color(0.65f, 0.65f, 0.65f);
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginBottom = 10;
            rootVisualElement.Add(hint);

            var debugSection = CreateSection();
            debugSection.Add(CreateSectionHeader("Debug"));

            _debugLoggingToggle = new Toggle("Enable debug logging");
            _debugLoggingToggle.SetValueWithoutNotify(_settingsController.PluginDebugLoggingEnabled);
            _debugLoggingToggle.style.marginBottom = 5;
            _debugLoggingToggle.RegisterValueChangedCallback(evt =>
            {
                _settingsController.PluginDebugLoggingEnabled = evt.newValue;
                RefreshStatus();
            });
            debugSection.Add(_debugLoggingToggle);

            _debugStatusLabel = CreateHint(string.Empty);
            debugSection.Add(_debugStatusLabel);

            rootVisualElement.Add(debugSection);
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (_settingsController == null)
                return;

            var enabled = _settingsController.PluginDebugLoggingEnabled;
            if (_debugLoggingToggle != null)
                _debugLoggingToggle.SetValueWithoutNotify(enabled);

            if (_debugStatusLabel != null)
            {
                _debugStatusLabel.text = enabled
                    ? "Debug logging is enabled. Plugin lifecycle, MCP request, transport, and tool execution traces are written to the Unity Console."
                    : "Debug logging is disabled. Warnings and errors are still written to the Unity Console.";
                _debugStatusLabel.style.color = enabled
                    ? new Color(0.55f, 0.85f, 0.55f)
                    : new Color(0.65f, 0.65f, 0.65f);
            }
        }

        private static VisualElement CreateSection()
        {
            var section = new VisualElement();
            section.style.backgroundColor = new Color(0.14f, 0.14f, 0.14f);
            section.style.borderTopLeftRadius = 4;
            section.style.borderTopRightRadius = 4;
            section.style.borderBottomLeftRadius = 4;
            section.style.borderBottomRightRadius = 4;
            section.style.paddingLeft = 8;
            section.style.paddingRight = 8;
            section.style.paddingTop = 8;
            section.style.paddingBottom = 8;
            return section;
        }

        private static Label CreateSectionHeader(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 12;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = new Color(0.78f, 0.78f, 0.78f);
            label.style.marginBottom = 6;
            return label;
        }

        private static Label CreateHint(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 11;
            label.style.color = new Color(0.65f, 0.65f, 0.65f);
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }
    }

    internal class FunplayToolExposureWindow : EditorWindow
    {
        private static readonly List<string> ProfileChoices = new List<string> { "core", "full" };

        private ISettingsController _settingsController;
        private MCPServerService _mcpServer;
        private PopupField<string> _editProfileField;
        private Label _statusLabel;
        private Label _descriptionLabel;
        private ScrollView _toolScrollView;
        private List<string> _allToolNames = new List<string>();
        private readonly Dictionary<string, string> _toolCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Toggle> _toolToggles = new Dictionary<string, Toggle>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _editingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _editingProfile = "core";

        [MenuItem("Funplay/Tool Exposure")]
        public static void ShowWindow()
        {
            var window = GetWindow<FunplayToolExposureWindow>("Tool Exposure");
            window.minSize = new Vector2(460, 560);
            window.Show();
        }

        public void CreateGUI()
        {
            _settingsController = RootScopeServices.Services?.GetService(typeof(ISettingsController))
                as ISettingsController;
            _mcpServer = RootScopeServices.Services?.GetService(typeof(MCPServerService))
                as MCPServerService;

            if (_settingsController == null)
            {
                rootVisualElement.Add(new Label("Failed to initialize services."));
                return;
            }

            _settingsController.OnSettingsChanged += RefreshStatus;
            BuildUI();
        }

        private void OnDestroy()
        {
            if (_settingsController != null)
                _settingsController.OnSettingsChanged -= RefreshStatus;
        }

        private void BuildUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexGrow = 1;
            rootVisualElement.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f);
            rootVisualElement.style.paddingLeft = 10;
            rootVisualElement.style.paddingRight = 10;
            rootVisualElement.style.paddingTop = 10;
            rootVisualElement.style.paddingBottom = 10;

            var title = new Label("Tool Exposure");
            title.style.fontSize = 17;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.marginBottom = 4;
            rootVisualElement.Add(title);

            var hint = new Label("Edit exactly which tools each MCP profile exposes. Choose the active profile from the MCP Server window. Saving changes restarts the running server automatically.");
            hint.style.fontSize = 11;
            hint.style.color = new Color(0.65f, 0.65f, 0.65f);
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginBottom = 10;
            rootVisualElement.Add(hint);

            LoadAllTools();

            var activeProfile = GetActiveProfile();
            _editingProfile = ProfileChoices.Contains(_editingProfile) ? _editingProfile : activeProfile;
            _editProfileField = new PopupField<string>("Edit Tool List", ProfileChoices, ProfileChoices.IndexOf(_editingProfile));
            _editProfileField.style.marginBottom = 8;
            _editProfileField.RegisterValueChangedCallback(evt =>
            {
                _editingProfile = evt.newValue;
                LoadEditingTools();
                RebuildToolList();
                RefreshStatus();
            });
            rootVisualElement.Add(_editProfileField);

            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.marginBottom = 10;

            var selectAllButton = CreateActionButton("Select All", SelectAllTools, 88, new Color(0.24f, 0.42f, 0.58f));
            buttonRow.Add(selectAllButton);

            var clearButton = CreateActionButton("Clear", ClearTools, 64, new Color(0.46f, 0.36f, 0.24f));
            clearButton.style.marginLeft = 6;
            buttonRow.Add(clearButton);

            var defaultButton = CreateActionButton("Use Default", UseDefaultTools, 92, new Color(0.34f, 0.34f, 0.34f));
            defaultButton.style.marginLeft = 6;
            buttonRow.Add(defaultButton);

            var saveButton = CreateActionButton("Save", SaveEditingTools, 64, new Color(0.2f, 0.5f, 0.3f));
            saveButton.style.marginLeft = 6;
            buttonRow.Add(saveButton);

            rootVisualElement.Add(buttonRow);

            _statusLabel = new Label();
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.marginBottom = 6;
            rootVisualElement.Add(_statusLabel);

            _descriptionLabel = new Label();
            _descriptionLabel.style.fontSize = 11;
            _descriptionLabel.style.color = new Color(0.68f, 0.68f, 0.68f);
            _descriptionLabel.style.whiteSpace = WhiteSpace.Normal;
            rootVisualElement.Add(_descriptionLabel);

            _toolScrollView = new ScrollView(ScrollViewMode.Vertical);
            _toolScrollView.style.flexGrow = 1;
            _toolScrollView.style.backgroundColor = new Color(0.14f, 0.14f, 0.14f);
            _toolScrollView.style.borderTopLeftRadius = 4;
            _toolScrollView.style.borderTopRightRadius = 4;
            _toolScrollView.style.borderBottomLeftRadius = 4;
            _toolScrollView.style.borderBottomRightRadius = 4;
            _toolScrollView.style.paddingLeft = 6;
            _toolScrollView.style.paddingRight = 6;
            _toolScrollView.style.paddingTop = 5;
            _toolScrollView.style.paddingBottom = 5;
            rootVisualElement.Add(_toolScrollView);

            LoadEditingTools();
            RebuildToolList();
            RefreshStatus();
        }

        private Button CreateActionButton(string text, Action action, int width, Color color)
        {
            var button = new Button(action);
            button.text = text;
            button.style.height = 26;
            button.style.width = width;
            button.style.backgroundColor = color;
            button.style.color = Color.white;
            return button;
        }

        private void LoadAllTools()
        {
            _toolCategories.Clear();

            _allToolNames = ToolSchemaBuilder.BuildAll()
                .Select(tool => tool.function.name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var toolName in _allToolNames)
                _toolCategories[toolName] = GetToolCategory(toolName);
        }

        private void LoadEditingTools()
        {
            _editingTools = new HashSet<string>(GetEffectiveTools(_editingProfile), StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<string> GetEffectiveTools(string profile)
        {
            if (string.Equals(profile, "full", StringComparison.OrdinalIgnoreCase))
            {
                return _settingsController.MCPFullToolsConfigured
                    ? _settingsController.MCPFullTools
                    : _allToolNames;
            }

            return _settingsController.MCPCoreToolsConfigured
                ? _settingsController.MCPCoreTools
                : MCPToolExportPolicy.DefaultCoreTools.Where(tool => _allToolNames.Contains(tool, StringComparer.OrdinalIgnoreCase));
        }

        private bool IsEditingProfileConfigured()
        {
            return string.Equals(_editingProfile, "full", StringComparison.OrdinalIgnoreCase)
                ? _settingsController.MCPFullToolsConfigured
                : _settingsController.MCPCoreToolsConfigured;
        }

        private string GetActiveProfile()
        {
            var currentProfile = MCPToolExportPolicy.ToSettingValue(
                MCPToolExportPolicy.Parse(_settingsController.MCPToolExportProfile));
            return ProfileChoices.Contains(currentProfile) ? currentProfile : "core";
        }

        private void RebuildToolList()
        {
            if (_toolScrollView == null)
                return;

            _toolScrollView.contentContainer.Clear();
            _toolToggles.Clear();

            var groupedTools = _allToolNames
                .GroupBy(GetCachedToolCategory)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groupedTools)
            {
                var categoryTools = group
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _toolScrollView.Add(CreateCategorySection(group.Key, categoryTools));
            }
        }

        private VisualElement CreateCategorySection(string category, IReadOnlyList<string> categoryTools)
        {
            var selectedCount = categoryTools.Count(tool => _editingTools.Contains(tool));

            var foldout = new Foldout
            {
                text = $"{category} ({selectedCount}/{categoryTools.Count})",
                value = true
            };
            foldout.style.marginBottom = 5;

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginLeft = 16;
            actions.style.marginTop = 2;
            actions.style.marginBottom = 4;

            var selectButton = CreateCategoryButton("Select", () => SetCategoryTools(categoryTools, true));
            actions.Add(selectButton);

            var clearButton = CreateCategoryButton("Clear", () => SetCategoryTools(categoryTools, false));
            clearButton.style.marginLeft = 4;
            actions.Add(clearButton);

            foldout.Add(actions);

            foreach (var toolName in categoryTools)
            {
                var toggle = new Toggle(toolName);
                toggle.SetValueWithoutNotify(_editingTools.Contains(toolName));
                toggle.style.marginLeft = 16;
                toggle.style.marginBottom = 2;
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue)
                        _editingTools.Add(toolName);
                    else
                        _editingTools.Remove(toolName);

                    RebuildToolList();
                    RefreshStatus();
                });

                _toolToggles[toolName] = toggle;
                foldout.Add(toggle);
            }

            return foldout;
        }

        private Button CreateCategoryButton(string text, Action action)
        {
            var button = new Button(action);
            button.text = text;
            button.style.height = 20;
            button.style.width = 58;
            button.style.fontSize = 10;
            return button;
        }

        private void SetCategoryTools(IEnumerable<string> categoryTools, bool enabled)
        {
            foreach (var toolName in categoryTools)
            {
                if (enabled)
                    _editingTools.Add(toolName);
                else
                    _editingTools.Remove(toolName);
            }

            RebuildToolList();
            RefreshStatus();
        }

        private void SetAllToolToggles(bool enabled)
        {
            if (enabled)
                _editingTools = new HashSet<string>(_allToolNames, StringComparer.OrdinalIgnoreCase);
            else
                _editingTools.Clear();

            foreach (var entry in _toolToggles)
                entry.Value.SetValueWithoutNotify(_editingTools.Contains(entry.Key));

            RefreshStatus();
        }

        private void SelectAllTools()
        {
            SetAllToolToggles(true);
        }

        private void ClearTools()
        {
            SetAllToolToggles(false);
        }

        private void UseDefaultTools()
        {
            if (string.Equals(_editingProfile, "full", StringComparison.OrdinalIgnoreCase))
                _settingsController.MCPFullTools = null;
            else
                _settingsController.MCPCoreTools = null;

            LoadEditingTools();
            RebuildToolList();
            RefreshStatus();
        }

        private void SaveEditingTools()
        {
            var selected = _editingTools
                .Where(tool => _allToolNames.Contains(tool, StringComparer.OrdinalIgnoreCase))
                .OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (string.Equals(_editingProfile, "full", StringComparison.OrdinalIgnoreCase))
                _settingsController.MCPFullTools = selected;
            else
                _settingsController.MCPCoreTools = selected;

            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (_settingsController == null)
                return;

            var activeProfile = GetActiveProfile();
            if (_editProfileField != null)
                _editProfileField.SetValueWithoutNotify(_editingProfile);

            var selectedCount = _editingTools?.Count ?? 0;
            var totalCount = _allToolNames?.Count ?? 0;
            var source = IsEditingProfileConfigured() ? "custom" : "default";

            if (_statusLabel != null)
            {
                var serverState = _mcpServer != null && _mcpServer.IsRunning
                    ? $"Server running on http://127.0.0.1:{_mcpServer.Port}/"
                    : "Server stopped";
                _statusLabel.text = $"Active: {activeProfile} | Editing {_editingProfile}: {selectedCount}/{totalCount} tools ({source}) | {serverState}";
                _statusLabel.style.color = activeProfile == "core"
                    ? new Color(0.55f, 0.85f, 0.55f)
                    : new Color(0.55f, 0.75f, 1f);
            }

            if (_descriptionLabel != null)
            {
                _descriptionLabel.text = string.Equals(_editingProfile, "core", StringComparison.OrdinalIgnoreCase)
                    ? "core defaults to the focused Unity workflow tool set. Select tools below and click Save to override that list."
                    : "full defaults to every registered MCP tool. Select tools below and click Save to make full expose only that custom list.";
            }
        }

        private string GetCachedToolCategory(string toolName)
        {
            return _toolCategories.TryGetValue(toolName, out var category) ? category : "Other";
        }

        private string GetToolCategory(string toolName)
        {
            if (ToolRegistry.MethodCache.TryGetValue(toolName, out var method))
            {
                var provider = method.DeclaringType?.GetCustomAttribute<ToolProviderAttribute>();
                return FormatCategory(provider?.Category ?? method.DeclaringType?.Name ?? "Other");
            }

            if (ToolRegistry.ManualTools.ContainsKey(toolName))
                return "Manual";

            return "Other";
        }

        private static string FormatCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return "Other";

            var trimmed = category.Trim();
            var result = new System.Text.StringBuilder();
            for (var i = 0; i < trimmed.Length; i++)
            {
                var current = trimmed[i];
                if (i > 0 &&
                    char.IsUpper(current) &&
                    !char.IsWhiteSpace(trimmed[i - 1]) &&
                    (char.IsLower(trimmed[i - 1]) || (i + 1 < trimmed.Length && char.IsLower(trimmed[i + 1]))))
                {
                    result.Append(' ');
                }

                result.Append(current);
            }

            return result.ToString();
        }
    }

    internal class FunplayProjectSkillsWindow : EditorWindow
    {
        private readonly Dictionary<string, Toggle> _optionalSkillToggles = new Dictionary<string, Toggle>(StringComparer.OrdinalIgnoreCase);
        private ISettingsController _settingsController;
        private VisualElement _mainContainer;
        private Label _statusLabel;
        private Label _manifestPathLabel;
        private VisualElement _generatedFilesContainer;
        private Toggle _enableCurrentPlatformToggle;
        private PopupField<string> _platformDropdown;
        private string[] _platformTargets;
        private int _selectedTargetIndex;

        [MenuItem("Funplay/Project Skills")]
        public static void ShowWindow()
        {
            var window = GetWindow<FunplayProjectSkillsWindow>("Project Skills");
            window.minSize = new Vector2(420, 520);
            window.Show();
        }

        public void CreateGUI()
        {
            _settingsController = RootScopeServices.Services?.GetService(typeof(ISettingsController))
                as ISettingsController;

            if (_settingsController == null)
            {
                rootVisualElement.Add(new Label("Failed to initialize services."));
                return;
            }

            BuildUI();
        }

        private void BuildUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexGrow = 1;
            rootVisualElement.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f);

            _mainContainer = new VisualElement();
            _mainContainer.style.flexGrow = 1;
            _mainContainer.style.paddingLeft = 10;
            _mainContainer.style.paddingRight = 10;
            _mainContainer.style.paddingTop = 10;
            _mainContainer.style.paddingBottom = 10;
            rootVisualElement.Add(_mainContainer);

            var header = CreateSection();
            header.style.marginBottom = 10;
            var title = new Label("Project Skills");
            title.style.fontSize = 17;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.marginBottom = 4;
            header.Add(title);

            var hintLabel = new Label("Configure project-level skills for supported AI clients. Built-in skills are always installed. Optional skills will be added after verification.");
            hintLabel.style.fontSize = 11;
            hintLabel.style.color = new Color(0.65f, 0.65f, 0.65f);
            hintLabel.style.whiteSpace = WhiteSpace.Normal;
            header.Add(hintLabel);
            _mainContainer.Add(header);

            var scrollView = new ScrollView(ScrollViewMode.Vertical);
            scrollView.style.flexGrow = 1;
            scrollView.style.marginBottom = 8;
            _mainContainer.Add(scrollView);

            _mainContainer = scrollView.contentContainer;

            BuildPlatformSection();
            BuildSkillsSection();
            BuildStatusSection();
            BuildActionsSection(rootVisualElement);

            RefreshStatus();
        }

        private void BuildPlatformSection()
        {
            var section = CreateSection();
            section.Add(CreateSectionHeader("Current Platform"));

            _platformTargets = new[] { "Claude Code", "Cursor", "VS Code", "Trae", "Kiro", "Codex" };
            _selectedTargetIndex = Mathf.Clamp(_selectedTargetIndex, 0, _platformTargets.Length - 1);
            var persistedTargetName = _settingsController.MCPSelectedConfigTarget;
            if (!string.IsNullOrWhiteSpace(persistedTargetName))
            {
                var persistedIndex = Array.FindIndex(_platformTargets, name => string.Equals(name, persistedTargetName, StringComparison.OrdinalIgnoreCase));
                if (persistedIndex >= 0)
                    _selectedTargetIndex = persistedIndex;
            }

            _platformDropdown = new PopupField<string>(new List<string>(_platformTargets), _selectedTargetIndex);
            _platformDropdown.style.marginBottom = 6;
            _platformDropdown.RegisterValueChangedCallback(evt =>
            {
                _selectedTargetIndex = Array.IndexOf(_platformTargets, evt.newValue);
                _settingsController.MCPSelectedConfigTarget = evt.newValue;
                BuildUI();
            });
            section.Add(_platformDropdown);

            var currentPlatformId = GetCurrentSkillsPlatformId();
            var currentPlatformSupported = !string.IsNullOrEmpty(currentPlatformId);
            var manifest = ProjectSkillsManager.LoadManifest(GetProjectRootPath());

            _enableCurrentPlatformToggle = new Toggle("Enable skills for current platform");
            _enableCurrentPlatformToggle.SetValueWithoutNotify(
                currentPlatformSupported &&
                manifest.platforms.Contains(currentPlatformId, StringComparer.OrdinalIgnoreCase));
            _enableCurrentPlatformToggle.SetEnabled(currentPlatformSupported);
            _enableCurrentPlatformToggle.style.marginBottom = 4;
            section.Add(_enableCurrentPlatformToggle);

            if (!currentPlatformSupported)
            {
                section.Add(CreateHint("Project skills integration is not available for this platform yet. Supported platforms: Codex, Claude Code, Cursor.", new Color(1f, 0.75f, 0.45f)));
            }

            _mainContainer.Add(section);
        }

        private void BuildSkillsSection()
        {
            var manifest = ProjectSkillsManager.LoadManifest(GetProjectRootPath());
            _optionalSkillToggles.Clear();

            var builtInSection = CreateSection();
            builtInSection.Add(CreateSectionHeader("Built-in Skills"));

            foreach (var skill in ProjectSkillsManager.GetBuiltInSkills())
            {
                builtInSection.Add(CreateSkillRow(skill.Title, skill.Description, "Required"));
            }
            _mainContainer.Add(builtInSection);

            var optionalSection = CreateSection();
            optionalSection.Add(CreateSectionHeader("Optional Skills"));

            var optionalSkills = ProjectSkillsManager.GetOptionalSkills();
            foreach (var skill in optionalSkills)
            {
                var toggle = new Toggle(skill.Title);
                toggle.SetValueWithoutNotify(manifest.optionalSkills.Contains(skill.Id, StringComparer.OrdinalIgnoreCase));
                toggle.style.marginBottom = 0;
                toggle.style.unityFontStyleAndWeight = FontStyle.Bold;
                optionalSection.Add(toggle);

                var description = CreateHint(skill.Description, new Color(0.58f, 0.58f, 0.58f));
                description.style.marginLeft = 18;
                description.style.marginBottom = 6;
                optionalSection.Add(description);

                _optionalSkillToggles[skill.Id] = toggle;
            }

            var optionalHint = optionalSkills.Count > 0
                ? "Uncheck optional skills and click Apply Skills to remove them. Built-in skills cannot be removed."
                : "No optional skills are available yet. Additional skills will be added after verification.";
            optionalSection.Add(CreateHint(optionalHint, new Color(0.65f, 0.65f, 0.65f)));
            _mainContainer.Add(optionalSection);
        }

        private void BuildActionsSection(VisualElement root)
        {
            var actionRow = new VisualElement();
            actionRow.style.flexDirection = FlexDirection.Row;
            actionRow.style.alignItems = Align.Center;
            actionRow.style.paddingLeft = 10;
            actionRow.style.paddingRight = 10;
            actionRow.style.paddingTop = 8;
            actionRow.style.paddingBottom = 8;
            actionRow.style.backgroundColor = new Color(0.16f, 0.16f, 0.16f);

            var applyButton = new Button(() =>
            {
                ApplyProjectSkillsConfiguration();
                RefreshStatus();
            });
            applyButton.text = "Apply Skills";
            applyButton.style.height = 26;
            applyButton.style.width = 100;
            applyButton.style.backgroundColor = new Color(0.25f, 0.45f, 0.65f);
            applyButton.style.color = Color.white;
            actionRow.Add(applyButton);

            var refreshButton = new Button(RefreshStatus);
            refreshButton.text = "Refresh";
            refreshButton.style.height = 26;
            refreshButton.style.width = 80;
            refreshButton.style.marginLeft = 6;
            actionRow.Add(refreshButton);

            root.Add(actionRow);
        }

        private void BuildStatusSection()
        {
            var section = CreateSection();
            section.Add(CreateSectionHeader("Installed Files"));

            _statusLabel = new Label();
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.marginBottom = 4;
            section.Add(_statusLabel);

            _manifestPathLabel = new Label();
            _manifestPathLabel.style.fontSize = 10;
            _manifestPathLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            _manifestPathLabel.style.marginBottom = 6;
            _manifestPathLabel.style.whiteSpace = WhiteSpace.Normal;
            section.Add(_manifestPathLabel);

            _generatedFilesContainer = new VisualElement();
            section.Add(_generatedFilesContainer);
            _mainContainer.Add(section);
        }

        private void RefreshStatus()
        {
            if (_statusLabel == null || _manifestPathLabel == null || _generatedFilesContainer == null)
                return;

            var projectRoot = GetProjectRootPath();
            var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
            var installedSkills = ProjectSkillsManager.GetInstalledSkills(manifest);
            var currentPlatformId = GetCurrentSkillsPlatformId();
            var currentPlatformDisplayName = GetCurrentSkillsPlatformDisplayName();
            var currentPlatformSupported = !string.IsNullOrEmpty(currentPlatformId);
            var currentPlatformConfigured = currentPlatformSupported &&
                                            manifest.platforms.Contains(currentPlatformId, StringComparer.OrdinalIgnoreCase);
            var manifestPath = ProjectSkillsManager.GetManifestPath(projectRoot);
            var manifestExists = File.Exists(manifestPath);

            if (_enableCurrentPlatformToggle != null)
            {
                _enableCurrentPlatformToggle.SetEnabled(currentPlatformSupported);
                _enableCurrentPlatformToggle.SetValueWithoutNotify(currentPlatformConfigured);
            }

            if (!currentPlatformSupported)
            {
                _statusLabel.text = $"Status: Unsupported current platform | Built-in: {ProjectSkillsManager.GetBuiltInSkills().Count} | Optional installed: {manifest.optionalSkills.Count}";
                _statusLabel.style.color = new Color(1f, 0.6f, 0.4f);
            }
            else if (!currentPlatformConfigured)
            {
                _statusLabel.text = $"Status: Not configured for {currentPlatformDisplayName} | Built-in: {ProjectSkillsManager.GetBuiltInSkills().Count} | Optional installed: {manifest.optionalSkills.Count}";
                _statusLabel.style.color = new Color(1f, 0.6f, 0.4f);
            }
            else
            {
                _statusLabel.text = $"Status: Configured for {currentPlatformDisplayName} | Skills: {installedSkills.Count}";
                _statusLabel.style.color = new Color(0.4f, 1f, 0.4f);
            }

            _manifestPathLabel.text = manifestExists
                ? $"Manifest: {manifestPath}"
                : $"Manifest will be created at: {manifestPath}";
            RefreshGeneratedFiles(projectRoot, manifest, currentPlatformId, currentPlatformDisplayName, currentPlatformConfigured);
        }

        private void ApplyProjectSkillsConfiguration()
        {
            var projectRoot = GetProjectRootPath();
            var currentPlatformId = GetCurrentSkillsPlatformId();
            var selectedOptionalSkills = _optionalSkillToggles
                .Where(entry => entry.Value.value)
                .Select(entry => entry.Key)
                .ToArray();

            try
            {
                if (string.IsNullOrEmpty(currentPlatformId))
                {
                    EditorUtility.DisplayDialog(
                        "Project Skills Configuration",
                        "Project skills are not supported for the currently selected platform yet.\n\nPlease select Codex, Claude Code, or Cursor.",
                        "OK");
                    return;
                }

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var selectedPlatforms = new HashSet<string>(manifest.platforms, StringComparer.OrdinalIgnoreCase);
                if (_enableCurrentPlatformToggle != null && _enableCurrentPlatformToggle.value)
                    selectedPlatforms.Add(currentPlatformId);
                else
                    selectedPlatforms.Remove(currentPlatformId);

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
                        return;
                }

                ProjectSkillsManager.ApplyConfiguration(projectRoot, selectedPlatforms, selectedOptionalSkills);

                EditorUtility.DisplayDialog(
                    "Project Skills Configuration",
                    "Project skills configuration updated successfully.\n\n" +
                    $"Manifest:\n{ProjectSkillsManager.GetManifestPath(projectRoot)}",
                    "OK");

                BuildUI();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "Project Skills Configuration Error",
                    $"Configuration failed:\n{ex.Message}",
                    "OK");
            }
        }

        private string GetCurrentSkillsPlatformId()
        {
            if (_platformTargets == null || _platformTargets.Length == 0)
                return null;

            var idx = Mathf.Clamp(_selectedTargetIndex, 0, _platformTargets.Length - 1);
            return MapTargetNameToSkillsPlatformId(_platformTargets[idx]);
        }

        private string GetCurrentSkillsPlatformDisplayName()
        {
            if (_platformTargets == null || _platformTargets.Length == 0)
                return "Unknown";

            var idx = Mathf.Clamp(_selectedTargetIndex, 0, _platformTargets.Length - 1);
            return _platformTargets[idx];
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

        private void RefreshGeneratedFiles(
            string projectRoot,
            ProjectSkillsManager.ProjectSkillsManifest manifest,
            string currentPlatformId,
            string currentPlatformDisplayName,
            bool currentPlatformConfigured)
        {
            _generatedFilesContainer.Clear();

            if (string.IsNullOrEmpty(currentPlatformId))
            {
                _generatedFilesContainer.Add(CreateHint($"{currentPlatformDisplayName} is not supported for project skills yet.", new Color(0.6f, 0.6f, 0.6f)));
                return;
            }

            if (!currentPlatformConfigured)
            {
                _generatedFilesContainer.Add(CreateHint($"{currentPlatformDisplayName} skills are not configured yet. Enable skills for the current platform, then click Apply Skills to generate files.", new Color(0.7f, 0.7f, 0.7f)));
                return;
            }

            var paths = ProjectSkillsManager.GetGeneratedPathsForPlatform(projectRoot, manifest, currentPlatformId);
            if (paths.Count == 0)
            {
                _generatedFilesContainer.Add(CreateHint($"Generated files for {currentPlatformDisplayName}: none.", new Color(0.6f, 0.6f, 0.6f)));
                return;
            }

            _generatedFilesContainer.Add(CreateHint($"Generated files for {currentPlatformDisplayName}:", new Color(0.7f, 0.7f, 0.7f)));
            foreach (var path in paths)
            {
                var exists = File.Exists(path) || Directory.Exists(path);
                var row = new Label($"{(exists ? "OK" : "Missing")}  {path}");
                row.style.fontSize = 10;
                row.style.color = exists ? new Color(0.55f, 0.85f, 0.55f) : new Color(1f, 0.65f, 0.45f);
                row.style.marginLeft = 8;
                row.style.marginBottom = 2;
                row.style.whiteSpace = WhiteSpace.Normal;
                _generatedFilesContainer.Add(row);
            }
        }

        private static string GetProjectRootPath()
        {
            return Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
        }

        private static VisualElement CreateSection()
        {
            var section = new VisualElement();
            section.style.backgroundColor = new Color(0.205f, 0.205f, 0.205f);
            section.style.borderTopLeftRadius = 5;
            section.style.borderTopRightRadius = 5;
            section.style.borderBottomLeftRadius = 5;
            section.style.borderBottomRightRadius = 5;
            section.style.paddingLeft = 8;
            section.style.paddingRight = 8;
            section.style.paddingTop = 7;
            section.style.paddingBottom = 7;
            section.style.marginBottom = 8;
            return section;
        }

        private static Label CreateSectionHeader(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 12;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = new Color(0.82f, 0.82f, 0.82f);
            label.style.marginBottom = 5;
            return label;
        }

        private static Label CreateHint(string text, Color color)
        {
            var label = new Label(text);
            label.style.fontSize = 10;
            label.style.color = color;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 4;
            return label;
        }

        private static VisualElement CreateSkillRow(string title, string description, string badgeText)
        {
            var row = new VisualElement();
            row.style.backgroundColor = new Color(0.17f, 0.17f, 0.17f);
            row.style.borderTopLeftRadius = 4;
            row.style.borderTopRightRadius = 4;
            row.style.borderBottomLeftRadius = 4;
            row.style.borderBottomRightRadius = 4;
            row.style.paddingLeft = 7;
            row.style.paddingRight = 7;
            row.style.paddingTop = 5;
            row.style.paddingBottom = 5;
            row.style.marginBottom = 4;

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;

            var titleLabel = new Label(title);
            titleLabel.style.flexGrow = 1;
            titleLabel.style.fontSize = 11;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.color = new Color(0.88f, 0.88f, 0.88f);
            titleRow.Add(titleLabel);

            var badge = new Label(badgeText);
            badge.style.fontSize = 9;
            badge.style.color = Color.white;
            badge.style.backgroundColor = new Color(0.32f, 0.48f, 0.7f);
            badge.style.borderTopLeftRadius = 3;
            badge.style.borderTopRightRadius = 3;
            badge.style.borderBottomLeftRadius = 3;
            badge.style.borderBottomRightRadius = 3;
            badge.style.paddingLeft = 5;
            badge.style.paddingRight = 5;
            badge.style.paddingTop = 1;
            badge.style.paddingBottom = 1;
            titleRow.Add(badge);

            row.Add(titleRow);

            var descriptionLabel = new Label(description);
            descriptionLabel.style.fontSize = 10;
            descriptionLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            descriptionLabel.style.marginTop = 3;
            descriptionLabel.style.whiteSpace = WhiteSpace.Normal;
            row.Add(descriptionLabel);

            return row;
        }
    }

    internal static class ProjectSkillsManager
    {
        internal const string ManagedMarker = "<!-- Funplay Unity MCP managed project skills -->";

        private const string ManifestDirectory = ".funplay/skills";
        private const string ManifestFileName = "manifest.json";

        private static readonly string[] SupportedPlatforms = { "codex", "claude", "cursor" };

        private static readonly SkillDefinition[] SkillCatalog =
        {
            new SkillDefinition(
                "unity-mcp-workflow",
                "Unity MCP Workflow",
                "Efficient workflow for using Unity MCP to edit, import, compile, inspect, and test Unity projects.",
                true,
                "Use this skill when Codex or another AI agent is working in a Unity project and needs to verify code, prefabs, UI, Play Mode behavior, screenshots, scene hierarchy, console logs, domain reloads, or MCP connection issues.",
                new[]
                {
                    "Use Unity MCP as the source of truth for Editor state, scene hierarchy, prefab references, runtime objects, compilation status, and Play Mode behavior.",
                    "Locate the real Unity project root and active scene before editing.",
                    "Inspect hierarchy, prefab paths, selected objects, and relevant component references through MCP before changing user-named objects.",
                    "Tool returns are structured JSON: `{success, message, data}` for success and `{success: false, code, error, data}` for errors. Parse `data` and check `code` (UPPERCASE_SNAKE_CASE) for branching — do not pattern-match free-form text.",
                    "Prefer `instanceId` returned by tools for follow-up calls. Pass it back via the `find_method=by_id` (or auto-detect when the value parses as an integer). This is more reliable than re-resolving by `name` when scenes contain duplicates.",
                    "Use the `find_method` parameter on GameObject/Component tools to choose how a target is resolved: `by_id`, `by_name`, `by_path`, `by_tag`, `by_layer`, `by_component`, or `by_id_or_name_or_path`. Default auto-detect picks `by_id` for integers, `by_path` for slashed strings, otherwise `by_name`.",
                    "When a GameObject has multiple components of the same type, target a specific one with `component_instance_id` instead of the type name to avoid hitting the wrong component.",
                    "Set component fields with `set_component_property(ies)`: it now writes through SerializedObject, so `[SerializeField] private` fields are reachable. Pass Object references as JSON `{\"fileID\": <instanceId>}` (preferred) or `{\"assetPath\": \"Assets/...\"}`. The response reports per-field success/failure.",
                    "Inspect editor-level state through dedicated tools: `get_selection`, `set_selection`, `get_prefab_stage`, `get_active_tool`, `get_windows`, `get_tags`, `get_layers`, `get_build_settings`. Do not write `execute_code` snippets just to read this.",
                    "When no specialized MCP tool covers an editor action, try `execute_menu_item` (e.g. 'GameObject/2D Object/Sprite', 'Window/Layouts/Default', 'Edit/Project Settings...') before falling back to `execute_code`.",
                    "When Tool Exposure uses the default `core` profile, rely on the focused workflow tools: `execute_code`, recompilation, Play Mode control, hierarchy, console logs, screenshots, input simulation, and performance inspection.",
                    "When Tool Exposure uses the default `full` profile, all registered MCP tools are available. Prefer specific tools for simple scene, asset, GameObject, component, prefab, camera, UI, package, animation, file, or visual-feedback operations.",
                    "If Tool Exposure has been customized and a named tool is unavailable, adapt to the exposed tool list and report which expected tool is missing.",
                    "Choose the correct edit surface: source files with normal repo tools, scene objects through Unity APIs and saved scenes, prefab assets through `PrefabUtility.LoadPrefabContents` and `SaveAsPrefabAsset`.",
                    "For `execute_code`, prefer the IFunplayCommand template over the legacy `static string Run()`: implement `IFunplayCommand` and use `ctx.RegisterObjectCreation`, `ctx.RegisterObjectModification`, `ctx.DestroyObject` so created/modified objects participate in editor Undo automatically. Use `ctx.Log` / `ctx.LogWarning` / `ctx.LogError` for traceable output that comes back in the response (without polluting the Unity console).",
                    "Batch related Unity-side changes in one guarded `execute_code` snippet, with explicit missing-object reports and concise before/after values.",
                    "`execute_code` now refreshes the asset database and waits for compilation to finish before compiling the snippet, so external file edits are picked up automatically. For other tools that depend on the latest assemblies (e.g. `get_compilation_errors`), still call `request_recompile` after external file edits.",
                    "Call `wait_for_compilation` before Play Mode, screenshots, or conclusions when a previous edit has not yet been confirmed.",
                    "After `enter_play_mode`, the MCP HTTP server briefly drops while Unity reloads the domain. Before the next tool call, poll a cheap tool such as `tools/list` or `get_reload_recovery_status` until the server responds again — do not assume the connection is immediately ready.",
                    "`request_recompile` is rejected while Unity is in Play Mode — Unity does not process script compilation or domain reloads while playing. Call `exit_play_mode` first, then retry `request_recompile`.",
                    "Read back exact values from Unity after changes, not only success messages.",
                    "Test actual behavior in Unity through hierarchy, console logs, Play Mode, UI interactions, screenshots, or targeted `execute_code` checks.",
                    "When Unity readback and text files disagree for serialized scene or prefab state, trust Unity readback and investigate the asset path.",
                    "If Play Mode is entered, exit Play Mode before finishing unless the user explicitly wants it left running."
                }),
        };

        internal static IReadOnlyList<SkillDefinition> GetBuiltInSkills()
        {
            return SkillCatalog.Where(skill => skill.IsBuiltIn).ToArray();
        }

        internal static IReadOnlyList<SkillDefinition> GetOptionalSkills()
        {
            return SkillCatalog.Where(skill => !skill.IsBuiltIn).ToArray();
        }

        internal static IReadOnlyList<string> GetSupportedPlatforms()
        {
            return SupportedPlatforms;
        }

        internal static ProjectSkillsManifest LoadManifest(string projectRoot)
        {
            var manifestPath = GetManifestPath(projectRoot);
            try
            {
                if (File.Exists(manifestPath))
                {
                    var json = File.ReadAllText(manifestPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonUtility.FromJson<ProjectSkillsManifest>(json);
                        if (loaded != null)
                            return NormalizeManifest(loaded);
                    }
                }
            }
            catch
            {
            }

            return CreateDefaultManifest();
        }

        internal static void SaveManifest(string projectRoot, ProjectSkillsManifest manifest)
        {
            var normalized = NormalizeManifest(manifest);
            var manifestPath = GetManifestPath(projectRoot);
            var directory = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(manifestPath, JsonUtility.ToJson(normalized, true));
        }

        internal static string GetManifestPath(string projectRoot)
        {
            return Path.Combine(projectRoot, ManifestDirectory, ManifestFileName);
        }

        internal static string GetCodexAgentsPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "AGENTS.md");
        }

        internal static string GetClaudeInstructionsPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "CLAUDE.md");
        }

        internal static string GetCursorRulesPath(string projectRoot)
        {
            return Path.Combine(projectRoot, ".cursor", "rules");
        }

        internal static string GetCodexSkillsRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, ".codex", "skills");
        }

        internal static string GetClaudeSkillsRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, ".claude", "skills");
        }

        internal static void ApplyConfiguration(string projectRoot, IEnumerable<string> selectedPlatforms, IEnumerable<string> selectedOptionalSkills)
        {
            var manifest = new ProjectSkillsManifest
            {
                platforms = selectedPlatforms?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
                optionalSkills = selectedOptionalSkills?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>()
            };

            SaveManifest(projectRoot, manifest);

            var normalized = LoadManifest(projectRoot);
            SyncCodex(projectRoot, normalized);
            SyncClaude(projectRoot, normalized);
            SyncCursor(projectRoot, normalized);
        }

        internal static bool IsPlatformConfigured(string projectRoot, string platformId)
        {
            var manifest = LoadManifest(projectRoot);
            return manifest.platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase);
        }

        internal static IReadOnlyList<SkillDefinition> GetInstalledSkills(ProjectSkillsManifest manifest)
        {
            var installedIds = new HashSet<string>(
                GetBuiltInSkills().Select(skill => skill.Id),
                StringComparer.OrdinalIgnoreCase);

            if (manifest?.optionalSkills != null)
            {
                foreach (var id in manifest.optionalSkills)
                    installedIds.Add(id);
            }

            return SkillCatalog.Where(skill => installedIds.Contains(skill.Id)).ToArray();
        }

        internal static bool IsManagedFile(string path)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).Contains(ManagedMarker, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        internal static string[] GetPlatformConflictPaths(string projectRoot, IEnumerable<string> selectedPlatforms)
        {
            var conflicts = new List<string>();
            var platforms = new HashSet<string>(selectedPlatforms ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (platforms.Contains("codex"))
            {
                var path = GetCodexAgentsPath(projectRoot);
                if (File.Exists(path) && !IsManagedFile(path))
                    conflicts.Add(path);
            }

            if (platforms.Contains("claude"))
            {
                var path = GetClaudeInstructionsPath(projectRoot);
                if (File.Exists(path) && !IsManagedFile(path))
                    conflicts.Add(path);
            }

            return conflicts.ToArray();
        }

        internal static IReadOnlyList<string> GetGeneratedPathsForPlatform(string projectRoot, ProjectSkillsManifest manifest, string platformId)
        {
            var enabled = manifest != null && manifest.platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase);
            if (!enabled)
                return Array.Empty<string>();

            var paths = new List<string> { GetManifestPath(projectRoot) };

            switch (platformId?.Trim().ToLowerInvariant())
            {
                case "codex":
                    paths.Add(GetCodexAgentsPath(projectRoot));
                    paths.Add(GetCodexSkillsRoot(projectRoot));
                    break;
                case "claude":
                    paths.Add(GetClaudeInstructionsPath(projectRoot));
                    paths.Add(GetClaudeSkillsRoot(projectRoot));
                    break;
                case "cursor":
                    paths.Add(GetCursorRulesPath(projectRoot));
                    break;
            }

            return paths;
        }

        private static void SyncCodex(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("codex", StringComparer.OrdinalIgnoreCase);
            var agentsPath = GetCodexAgentsPath(projectRoot);
            var skillsRoot = GetCodexSkillsRoot(projectRoot);

            if (!enabled)
            {
                DeleteManagedFile(agentsPath);
                DeleteManagedSkillDirectories(skillsRoot);
                return;
            }

            Directory.CreateDirectory(skillsRoot);

            File.WriteAllText(agentsPath, BuildCodexAgentsContent(projectRoot, manifest));
            WriteManagedSkillDirectories(skillsRoot, manifest, SkillPlatform.Codex);
        }

        private static void SyncClaude(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("claude", StringComparer.OrdinalIgnoreCase);
            var claudePath = GetClaudeInstructionsPath(projectRoot);
            var skillsRoot = GetClaudeSkillsRoot(projectRoot);

            if (!enabled)
            {
                DeleteManagedFile(claudePath);
                DeleteManagedSkillDirectories(skillsRoot);
                return;
            }

            Directory.CreateDirectory(skillsRoot);

            File.WriteAllText(claudePath, BuildClaudeInstructionsContent(projectRoot, manifest));
            WriteManagedSkillDirectories(skillsRoot, manifest, SkillPlatform.Claude);
        }

        private static void SyncCursor(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("cursor", StringComparer.OrdinalIgnoreCase);
            var rulesRoot = GetCursorRulesPath(projectRoot);

            if (!enabled)
            {
                DeleteManagedCursorRules(rulesRoot);
                return;
            }

            Directory.CreateDirectory(rulesRoot);
            WriteManagedCursorRules(rulesRoot, manifest);
        }

        private static void WriteManagedSkillDirectories(string skillsRoot, ProjectSkillsManifest manifest, SkillPlatform platform)
        {
            DeleteManagedSkillDirectories(skillsRoot);

            foreach (var skill in GetInstalledSkills(manifest))
            {
                var directory = Path.Combine(skillsRoot, $"funplay-{skill.Id}");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "SKILL.md"), BuildSkillDocument(skill, platform));
            }
        }

        private static void WriteManagedCursorRules(string rulesRoot, ProjectSkillsManifest manifest)
        {
            DeleteManagedCursorRules(rulesRoot);

            foreach (var skill in GetInstalledSkills(manifest))
            {
                var path = Path.Combine(rulesRoot, $"funplay-{skill.Id}.mdc");
                File.WriteAllText(path, BuildCursorRuleContent(skill));
            }
        }

        private static void DeleteManagedSkillDirectories(string skillsRoot)
        {
            if (!Directory.Exists(skillsRoot))
                return;

            foreach (var directory in Directory.GetDirectories(skillsRoot, "funplay-*", SearchOption.TopDirectoryOnly))
            {
                var skillPath = Path.Combine(directory, "SKILL.md");
                if (IsManagedFile(skillPath))
                    Directory.Delete(directory, true);
            }
        }

        private static void DeleteManagedCursorRules(string rulesRoot)
        {
            if (!Directory.Exists(rulesRoot))
                return;

            foreach (var file in Directory.GetFiles(rulesRoot, "funplay-*.mdc", SearchOption.TopDirectoryOnly))
            {
                if (IsManagedFile(file))
                    File.Delete(file);
            }
        }

        private static void DeleteManagedFile(string path)
        {
            if (IsManagedFile(path))
                File.Delete(path);
        }

        private static string BuildCodexAgentsContent(string projectRoot, ProjectSkillsManifest manifest)
        {
            var installed = GetInstalledSkills(manifest);
            return
$@"# AGENTS.md
{ManagedMarker}

# Funplay Unity MCP Project Guidance

This file is managed by Funplay MCP for Unity.

## Installed project skills

{string.Join("\n", installed.Select(skill => $"- `funplay-{skill.Id}` - {skill.Description}"))}

## Codex workflow rules

- Prefer project-local Funplay skills under `.codex/skills/`.
- Use `execute_code` as the primary Unity automation tool. For new snippets, implement `IFunplayCommand` and use `ctx.RegisterObjectCreation` / `RegisterObjectModification` / `DestroyObject` so changes participate in Undo automatically.
- Inspect Unity objects through MCP before changing user-named scene or prefab targets. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- Tool returns are structured JSON (`{{success, message, data}}` / `{{success: false, code, error, data}}`). Branch on `code`, not free-form text.
- Set component fields with `set_component_property(ies)` — it picks up `[SerializeField] private` fields and accepts Object references as `{{""fileID"": <instanceId>}}` or `{{""assetPath"": ""Assets/...""}}`.
- Read editor state through dedicated tools (`get_selection`, `get_prefab_stage`, `get_tags`, `get_layers`, `get_build_settings`); use `execute_menu_item` before falling back to ad-hoc `execute_code`.
- Save only the scene or prefab assets intentionally modified, then read back exact values.
- With default `core` exposure, use the focused workflow tools. With default `full` exposure, prefer specific MCP tools for simple editor operations.
- `execute_code` refreshes the asset database and waits for compilation before running. For other tools that depend on freshly compiled code, still call `request_recompile` after external script edits.
- `request_recompile` is rejected while Unity is in Play Mode. Call `exit_play_mode` first, then retry.
- After `enter_play_mode`, the HTTP server briefly drops while Unity reloads the domain. Poll `tools/list` or `get_reload_recovery_status` until it responds again before issuing the next tool call.
- If recompilation triggers a domain reload, call `get_reload_recovery_status`.
- Avoid changing `Library/`, `Temp/`, `Logs/`, or `obj/`.

## Project

- Project root: `{projectRoot}`
- Product name: `{Application.productName}`

## Notes

- Re-run `Funplay > Project Skills` after changing selected skills or platforms.
";
        }

        private static string BuildClaudeInstructionsContent(string projectRoot, ProjectSkillsManifest manifest)
        {
            var installed = GetInstalledSkills(manifest);
            return
$@"# CLAUDE.md
{ManagedMarker}

# Funplay Unity MCP Project Guidance

This file is managed by Funplay MCP for Unity for Claude Code.

## Installed skills

{string.Join("\n", installed.Select(skill => $"- `{skill.Id}` - {skill.Description}"))}

## Preferred workflow

- Use Funplay MCP tools for Unity editor state and automation.
- Use `execute_code` for non-trivial Unity orchestration. For new snippets, implement `IFunplayCommand` and use `ctx.RegisterObjectCreation` / `RegisterObjectModification` / `DestroyObject` so changes participate in Undo and `ctx.Log` for traceable output.
- Inspect Unity objects through MCP before changing user-named scene or prefab targets. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- Tool returns are structured JSON (`{{success, message, data}}` / `{{success: false, code, error, data}}`). Branch on `code`, not free-form text.
- Set component fields with `set_component_property(ies)` — it picks up `[SerializeField] private` fields and accepts Object references as `{{""fileID"": <instanceId>}}` or `{{""assetPath"": ""Assets/...""}}`.
- Read editor state through `get_selection`, `get_prefab_stage`, `get_tags`, `get_layers`, `get_build_settings`; try `execute_menu_item` before writing ad-hoc `execute_code`.
- Save only the scene or prefab assets intentionally modified, then read back exact values.
- With default `core` exposure, use the focused workflow tools. With default `full` exposure, prefer specific MCP tools for simple editor operations.
- `execute_code` refreshes assets and waits for compilation before running. For other tools that depend on freshly compiled code, still call `request_recompile` after external script edits.
- `request_recompile` is rejected while Unity is in Play Mode. Call `exit_play_mode` first, then retry.
- After `enter_play_mode`, the HTTP server briefly drops while Unity reloads the domain. Poll `tools/list` or `get_reload_recovery_status` until it responds again before issuing the next tool call.
- If domain reload interrupts a request, follow with `get_reload_recovery_status`.
- Additional installed skills are available under `.claude/skills/`.

## Project

- Project root: `{projectRoot}`
- Product name: `{Application.productName}`
";
        }

        private static string BuildCursorRuleContent(SkillDefinition skill)
        {
            var alwaysApply = skill.IsBuiltIn ? "true" : "false";
            return
$@"---
description: {skill.Description}
alwaysApply: {alwaysApply}
---
{ManagedMarker}

# {skill.Title}

{skill.WhenToUse}

## Rules

{string.Join("\n", skill.Rules.Select(rule => $"- {rule}"))}

## Metadata

- Skill id: `{skill.Id}`
- Built-in: `{skill.IsBuiltIn}`
- Source: `https://github.com/FunplayAI/funplay-unity-mcp`
";
        }

        private static string BuildSkillDocument(SkillDefinition skill, SkillPlatform platform)
        {
            if (string.Equals(skill.Id, "unity-mcp-workflow", StringComparison.OrdinalIgnoreCase))
                return BuildUnityMcpWorkflowSkillDocument(skill, platform);

            return
$@"---
name: funplay-{skill.Id}
description: {skill.Description}
platform: {platform.ToString().ToLowerInvariant()}
---
{ManagedMarker}

# {skill.Title}

{skill.WhenToUse}

## Rules

{string.Join("\n", skill.Rules.Select(rule => $"- {rule}"))}

## Metadata

- Original skill id: `{skill.Id}`
- Source repository: `https://github.com/FunplayAI/funplay-unity-mcp`
";
        }

        private static string BuildUnityMcpWorkflowSkillDocument(SkillDefinition skill, SkillPlatform platform)
        {
            var header =
$@"---
name: funplay-{skill.Id}
description: {skill.Description}
platform: {platform.ToString().ToLowerInvariant()}
---
{ManagedMarker}

# {skill.Title}

{skill.WhenToUse}
";

            var body =
@"
## Operating Loop

1. Establish context.
   - Confirm the Unity project root and active scene.
   - Check that Unity MCP is reachable before assuming Editor state.
   - Inspect hierarchy, prefab paths, selected objects, and relevant component references through MCP.
   - If the user names an object, verify the real Unity object path before editing.
2. Choose the edit surface.
   - Edit source files with normal repo tools, then trigger Unity recompilation.
   - Edit scene objects through Unity APIs, mark the scene dirty, and save the scene.
   - Edit prefab assets with `PrefabUtility.LoadPrefabContents`, `PrefabUtility.SaveAsPrefabAsset`, and `PrefabUtility.UnloadPrefabContents`.
   - If the user is looking at an open scene instance, update the visible scene instance as well as the prefab asset when appropriate.
3. Execute changes.
   - Prefer one well-guarded `execute_code` batch over many fragile UI clicks.
   - Use null guards for every object lookup and return explicit missing-path messages.
   - Return concise before/after values from snippets.
   - Save only the assets or scenes intentionally modified.
4. Validate.
   - Read back the changed objects through MCP.
   - For file edits, call `request_recompile`, then `wait_for_compilation`, then inspect console or compilation errors.
   - For runtime behavior, enter Play Mode or inspect live objects when needed.
   - Report exactly what was verified and what still requires device, store, network, or manual validation.

## Tool Exposure

- With the default `core` profile, rely on the focused workflow tools: `execute_code`, recompilation, Play Mode control, hierarchy, console logs, screenshots, input simulation, and performance inspection.
- With the default `full` profile, prefer specific MCP tools for simple scene, asset, GameObject, component, prefab, camera, UI, package, animation, file, or visual-feedback operations.
- If Tool Exposure is customized and a named tool is unavailable, adapt to the exposed tool list and report which expected tool is missing.

## MCP Call Pattern

If native MCP tools are not directly available, probe the local HTTP endpoint:

```bash
curl -sS -m 1 -X POST http://127.0.0.1:8765/mcp \
  -H 'Content-Type: application/json' \
  -d '{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/list""}'
```

For multi-line `execute_code` calls over curl, generate JSON with a real encoder instead of hand-escaping C#:

```bash
node - <<'NODE'
const code = String.raw`
using UnityEngine;

public class InspectSomething
{
    public static string Run()
    {
        var obj = GameObject.Find(""PracticeInGameUiRoot"");
        return obj != null ? obj.name : ""not found"";
    }
}
`;
const payload = {
  jsonrpc: ""2.0"",
  id: 1,
  method: ""tools/call"",
  params: { name: ""execute_code"", arguments: { code } }
};
process.stdout.write(JSON.stringify(payload));
NODE
```

## Unity C# Patterns

Use fully qualified types if the snippet environment or injected project code makes `using` statements unreliable:

```csharp
var root = UnityEngine.GameObject.Find(""PracticeInGameUiRoot"");
var rect = root.GetComponent<UnityEngine.RectTransform>();
```

Use Unity null semantics for `UnityEngine.Object` references:

```csharp
if (image == null)
{
    return ""Image missing"";
}
```

For prefab edits:

```csharp
var path = ""Assets/MyGame/UI/Prefabs/PF_PracticeInGameUiRoot.prefab"";
var prefab = UnityEditor.PrefabUtility.LoadPrefabContents(path);
try
{
    var target = prefab.transform.Find(""SafeArea/SwingCancelZone"");
    if (target == null)
    {
        return ""SwingCancelZone not found in prefab"";
    }

    var rect = target.GetComponent<UnityEngine.RectTransform>();
    var before = rect.anchoredPosition;
    rect.anchoredPosition = new UnityEngine.Vector2(-76f, 448f);

    UnityEditor.EditorUtility.SetDirty(rect);
    UnityEditor.PrefabUtility.SaveAsPrefabAsset(prefab, path);
    UnityEditor.AssetDatabase.SaveAssets();
    return ""Prefab saved: pos "" + before + "" -> "" + rect.anchoredPosition;
}
finally
{
    UnityEditor.PrefabUtility.UnloadPrefabContents(prefab);
}
```

For scene edits:

```csharp
var obj = UnityEngine.GameObject.Find(""PracticeInGameUiRoot/SafeArea/SwingCancelZone"");
if (obj == null)
{
    return ""Scene object not found"";
}

var rect = obj.GetComponent<UnityEngine.RectTransform>();
var before = rect.sizeDelta;
UnityEditor.Undo.RecordObject(rect, ""Update cancel zone"");
rect.sizeDelta = new UnityEngine.Vector2(220f, 116f);
UnityEditor.EditorUtility.SetDirty(rect);
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(obj.scene);
UnityEditor.SceneManagement.EditorSceneManager.SaveScene(obj.scene);
return ""Scene saved: size "" + before + "" -> "" + rect.sizeDelta;
```

## Recompile And Reload

After external C# or asset file edits:

1. If Unity is in Play Mode, call `exit_play_mode` first — `request_recompile` is rejected during play because Unity does not run script compilation or domain reloads while playing.
2. Call `request_recompile`.
3. Call `wait_for_compilation`.
4. Read console or compilation errors before continuing.
5. If a domain reload drops the request, call `get_reload_recovery_status` when available, re-scan the MCP endpoint if needed, then continue from `wait_for_compilation`.

Do not treat a disconnected request as a successful compile.

After `enter_play_mode`, the HTTP server is briefly unreachable while Unity reloads the domain. Before issuing the next tool call, poll a cheap endpoint such as `tools/list` (or `get_reload_recovery_status` if exposed) until you get a response — do not assume the connection survives the Play Mode transition.

## Verification Checklist

Use readback snippets that print exact values, not only `success`:

```csharp
var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Transform>();
UnityEngine.Transform target = null;
for (int i = 0; i < all.Length; i++)
{
    if (all[i].name == ""SwingCancelZone"")
    {
        target = all[i];
        break;
    }
}

if (target == null)
{
    return ""SwingCancelZone not found"";
}

var rect = target.GetComponent<UnityEngine.RectTransform>();
return ""path="" + target.name + ""; pos="" + rect.anchoredPosition + ""; size="" + rect.sizeDelta;
```

For UI work, verify prefab or scene hierarchy, sprite references, anchors, sorting order, active state, text fit, and button listeners. A populated `Content` hierarchy does not prove the user can see the UI.

For gameplay or network work, verify object identity, ownership, live instance existence, transform values, animation state, visibility, and whether client-side filters are discarding valid data.

## Failure Handling

- If MCP is unreachable, say so and fall back only to safe filesystem inspection or code edits. Do not claim scene, prefab, or runtime verification without Unity readback.
- If an object lookup fails, inspect hierarchy and prefab contents instead of inventing a path.
- If multiple matching objects exist, print their paths and choose the one matching the user-visible UI or current scene.
- If compile errors appear after a change, fix them before Play Mode validation.
- When Unity and text files disagree for serialized scene or prefab state, trust Unity readback and inspect the asset path.
";

            var footer =
$@"
## Metadata

- Original skill id: `{skill.Id}`
- Source repository: `https://github.com/FunplayAI/funplay-unity-mcp`
";

            return header + body + footer;
        }

        private static ProjectSkillsManifest CreateDefaultManifest()
        {
            return new ProjectSkillsManifest
            {
                platforms = new List<string>(),
                optionalSkills = new List<string>()
            };
        }

        private static ProjectSkillsManifest NormalizeManifest(ProjectSkillsManifest manifest)
        {
            manifest ??= CreateDefaultManifest();
            manifest.platforms ??= new List<string>();
            manifest.optionalSkills ??= new List<string>();

            manifest.platforms = manifest.platforms
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(value => SupportedPlatforms.Contains(value, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var optionalIds = new HashSet<string>(
                GetOptionalSkills().Select(skill => skill.Id),
                StringComparer.OrdinalIgnoreCase);

            manifest.optionalSkills = manifest.optionalSkills
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Where(value => optionalIds.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return manifest;
        }

        internal enum SkillPlatform
        {
            Codex,
            Claude,
            Cursor
        }

        [Serializable]
        internal sealed class ProjectSkillsManifest
        {
            public List<string> platforms = new List<string>();
            public List<string> optionalSkills = new List<string>();
        }

        internal sealed class SkillDefinition
        {
            public SkillDefinition(string id, string title, string description, bool isBuiltIn, string whenToUse, IReadOnlyList<string> rules)
            {
                Id = id;
                Title = title;
                Description = description;
                IsBuiltIn = isBuiltIn;
                WhenToUse = whenToUse;
                Rules = rules ?? Array.Empty<string>();
            }

            public string Id { get; }
            public string Title { get; }
            public string Description { get; }
            public bool IsBuiltIn { get; }
            public string WhenToUse { get; }
            public IReadOnlyList<string> Rules { get; }
        }
    }
}
