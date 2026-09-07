// Copyright (C) Funplay. Licensed under MIT.

using Funplay.Editor.DI;
using Funplay.Editor.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.MCP.Server
{
    internal class FunplayMCPWindow : EditorWindow
    {
        private ISettingsController _settingsController;
        private MCPServerService _mcpServer;
        private FunplayMCPHeaderStatusPanel _headerStatusPanel;
        private FunplayMCPUpdatePanel _updatePanel;
        private FunplayMCPProjectSkillsNoticePanel _projectSkillsNoticePanel;
        private FunplayMCPRecentActivityPanel _activityPanel;

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

            _mcpServer.InteractionLog.OnEntryAdded -= OnLogEntryAdded;
            _mcpServer.InteractionLog.OnEntryAdded += OnLogEntryAdded;

            BuildUI();
            FunplayMCPUpdateChecker.MaybeCheckForUpdatesInBackground();
        }

        private void OnDestroy()
        {
            if (_mcpServer?.InteractionLog != null)
                _mcpServer.InteractionLog.OnEntryAdded -= OnLogEntryAdded;

            FunplayMCPUpdateChecker.StateChanged -= OnUpdateStateChanged;
            DisposePanels();
        }

        private void BuildUI()
        {
            DisposePanels();

            rootVisualElement.Clear();
            rootVisualElement.style.flexGrow = 1;
            rootVisualElement.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f);

            var mainContainer = new VisualElement();
            mainContainer.style.flexGrow = 1;
            mainContainer.style.paddingLeft = 10;
            mainContainer.style.paddingRight = 10;
            mainContainer.style.paddingTop = 10;
            mainContainer.style.paddingBottom = 10;
            rootVisualElement.Add(mainContainer);

            _headerStatusPanel = new FunplayMCPHeaderStatusPanel(_settingsController, _mcpServer);
            _headerStatusPanel.AddTo(mainContainer);

            _updatePanel = new FunplayMCPUpdatePanel();
            _updatePanel.AddTo(mainContainer);

            _projectSkillsNoticePanel = new FunplayMCPProjectSkillsNoticePanel(_settingsController);
            _projectSkillsNoticePanel.AddTo(mainContainer);

            new FunplayMCPServerControlsPanel(
                    _settingsController,
                    _mcpServer,
                    () => _headerStatusPanel?.RefreshStatus())
                .AddTo(mainContainer);

            new FunplayMCPToolExposurePanel(
                    _settingsController,
                    () => _headerStatusPanel?.RefreshStatus())
                .AddTo(mainContainer);

            new FunplayMCPClientConfigPanel(
                    _settingsController,
                    _mcpServer,
                    BuildUI)
                .AddTo(mainContainer);

            _activityPanel = new FunplayMCPRecentActivityPanel(_mcpServer, _settingsController);
            _activityPanel.AddTo(mainContainer);
        }

        private void DisposePanels()
        {
            _activityPanel?.Dispose();
            _activityPanel = null;
            _projectSkillsNoticePanel = null;
        }

        private void OnFocus()
        {
            _projectSkillsNoticePanel?.Refresh();
        }

        private void OnUpdateStateChanged()
        {
            EditorApplication.delayCall += () =>
            {
                _headerStatusPanel?.RefreshVersion();
                _updatePanel?.Refresh();
            };
        }

        private void OnLogEntryAdded(MCPLogEntry entry)
        {
            _activityPanel?.OnEntryAdded(entry);
        }
    }
}
