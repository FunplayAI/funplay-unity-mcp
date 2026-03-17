// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using GameBooom.Editor.DI;
using GameBooom.Editor.Settings;

namespace GameBooom.Editor.MCP.Server
{
    internal class GameBooomMCPWindow : EditorWindow
    {
        private ISettingsController _settingsController;
        private MCPServerService _mcpServer;
        private VisualElement _mainContainer;
        private Label _statusLabel;
        private ScrollView _logScrollView;
        private MCPConfigTarget[] _mcpTargets;
        private int _selectedTargetIndex;
        private Label _configStatusLabel;
        private Label _configPathLabel;

        [MenuItem("GameBooom/MCP Server")]
        public static void ShowWindow()
        {
            var window = GetWindow<GameBooomMCPWindow>("MCP Server");
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

            BuildUI();
            _mcpServer.InteractionLog.OnEntryAdded += OnLogEntryAdded;
        }

        private void OnDestroy()
        {
            if (_mcpServer?.InteractionLog != null)
                _mcpServer.InteractionLog.OnEntryAdded -= OnLogEntryAdded;
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
            var title = new Label("MCP Server");
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.marginBottom = 8;
            _mainContainer.Add(title);

            // Status
            _statusLabel = new Label();
            _statusLabel.style.fontSize = 13;
            _statusLabel.style.marginBottom = 10;
            _mainContainer.Add(_statusLabel);
            RefreshStatus();

            // Enable toggle
            var toggle = new Toggle("Enable MCP Server");
            toggle.value = _settingsController.MCPServerEnabled;
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
            portField.value = _settingsController.MCPServerPort;
            portField.RegisterValueChangedCallback(evt =>
            {
                _settingsController.MCPServerPort = evt.newValue;
            });
            portField.style.marginBottom = 10;
            _mainContainer.Add(portField);

            // One-Click Config Section
            var configLabel = new Label("One-Click MCP Configuration");
            configLabel.style.fontSize = 12;
            configLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            configLabel.style.color = new Color(0.75f, 0.75f, 0.75f);
            configLabel.style.marginBottom = 6;
            _mainContainer.Add(configLabel);

            var port = _settingsController.MCPServerPort;
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.Personal);

            _mcpTargets = new[]
            {
                new MCPConfigTarget
                {
                    Name = "Claude Code",
                    ConfigPath = Path.Combine(homePath, ".claude.json"),
                    Entry = new Dictionary<string, object> { ["type"] = "http", ["url"] = $"http://localhost:{port}/" },
                },
                new MCPConfigTarget
                {
                    Name = "Cursor",
                    ConfigPath = Path.Combine(homePath, ".cursor", "mcp.json"),
                    Entry = new Dictionary<string, object> { ["url"] = $"http://localhost:{port}/" },
                },
                new MCPConfigTarget
                {
                    Name = "VS Code",
                    ConfigPath = Path.Combine(homePath, ".vscode", "mcp.json"),
                    Entry = new Dictionary<string, object> { ["type"] = "http", ["url"] = $"http://localhost:{port}/" },
                    RootKey = "servers"
                },
                new MCPConfigTarget
                {
                    Name = "Trae",
                    ConfigPath = Path.Combine(homePath, ".trae", "mcp.json"),
                    Entry = new Dictionary<string, object> { ["url"] = $"http://localhost:{port}/" },
                },
                new MCPConfigTarget
                {
                    Name = "Kiro",
                    ConfigPath = Path.Combine(homePath, ".kiro", "settings", "mcp.json"),
                    Entry = new Dictionary<string, object> { ["type"] = "http", ["url"] = $"http://localhost:{port}/" },
                    RootKey = "mcpServers"
                },
                new MCPConfigTarget
                {
                    Name = "Codex",
                    ConfigPath = Path.Combine(homePath, ".codex", "config.toml"),
                    IsToml = true,
                    TomlSection = $"[mcp_servers.gamebooom]\nurl = \"http://localhost:{port}/\"\n"
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
            var dropdown = new PopupField<string>(nameList, _selectedTargetIndex);
            dropdown.style.flexGrow = 1;
            dropdown.style.height = 26;
            dropdown.RegisterValueChangedCallback(evt =>
            {
                _selectedTargetIndex = nameList.IndexOf(evt.newValue);
                RefreshConfigStatus();
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

            _mainContainer.Add(configRow);

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

        private void BuildLogSection()
        {
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

            _logScrollView.contentContainer.Add(card);
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
                _statusLabel.text = $"Running on http://localhost:{_mcpServer.Port}/";
                _statusLabel.style.color = new Color(0.4f, 1f, 0.4f);
            }
            else
            {
                _statusLabel.text = "Stopped";
                _statusLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            }
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
            public Dictionary<string, object> Entry;
            public string RootKey;
            public bool IsToml;
            public string TomlSection;
        }

        private void ConfigureMCPForTarget(MCPConfigTarget target)
        {
            try
            {
                var dir = Path.GetDirectoryName(target.ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (target.IsToml)
                {
                    ConfigureTomlTarget(target);
                }
                else
                {
                    ConfigureJsonTarget(target);
                }

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

        private void ConfigureJsonTarget(MCPConfigTarget target)
        {
            var rootKey = string.IsNullOrEmpty(target.RootKey) ? "mcpServers" : target.RootKey;
            var serverName = "gamebooom";
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
                        servers[serverName] = target.Entry;
                    else
                        root[rootKey] = new Dictionary<string, object> { [serverName] = target.Entry };
                }
                else
                {
                    root = parsed ?? new Dictionary<string, object>();
                    root[rootKey] = new Dictionary<string, object> { [serverName] = target.Entry };
                }
            }
            else
            {
                root = new Dictionary<string, object>
                {
                    [rootKey] = new Dictionary<string, object> { [serverName] = target.Entry }
                };
            }

            File.WriteAllText(target.ConfigPath, SimpleJsonHelper.Serialize(root));
        }

        private void ConfigureTomlTarget(MCPConfigTarget target)
        {
            var sectionHeader = "[mcp_servers.gamebooom]";
            var content = File.Exists(target.ConfigPath) ? File.ReadAllText(target.ConfigPath) : "";

            if (content.Contains(sectionHeader))
            {
                // Replace existing section: find start, find next section or EOF, replace
                var startIdx = content.IndexOf(sectionHeader, StringComparison.Ordinal);
                var afterHeader = startIdx + sectionHeader.Length;
                var nextSection = content.IndexOf("\n[", afterHeader, StringComparison.Ordinal);
                var endIdx = nextSection >= 0 ? nextSection : content.Length;
                content = content.Substring(0, startIdx) + target.TomlSection + content.Substring(endIdx);
            }
            else
            {
                // Append
                if (content.Length > 0 && !content.EndsWith("\n"))
                    content += "\n";
                content += "\n" + target.TomlSection;
            }

            File.WriteAllText(target.ConfigPath, content);
        }
    }
}
