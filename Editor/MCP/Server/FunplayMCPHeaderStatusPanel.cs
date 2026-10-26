// Copyright (C) Funplay. Licensed under MIT.

using Funplay.Editor.Services;
using Funplay.Editor.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.MCP.Server
{
    internal sealed class FunplayMCPHeaderStatusPanel
    {
        private readonly ISettingsController _settings;
        private readonly MCPServerService _server;
        private Label _statusLabel;
        private Label _versionLabel;

        public FunplayMCPHeaderStatusPanel(ISettingsController settings, MCPServerService server)
        {
            _settings = settings;
            _server = server;
        }

        public void AddTo(VisualElement parent)
        {
            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;
            titleRow.style.marginBottom = 8;
            parent.Add(titleRow);

            var title = new Label("MCP Server");
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.flexGrow = 1;
            titleRow.Add(title);

            _versionLabel = new Label();
            _versionLabel.style.fontSize = 11;
            _versionLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            _versionLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            titleRow.Add(_versionLabel);

            _statusLabel = new Label();
            _statusLabel.style.fontSize = 13;
            _statusLabel.style.marginBottom = 10;
            parent.Add(_statusLabel);

            Refresh();
        }

        public void Refresh()
        {
            RefreshVersion();
            RefreshStatus();
        }

        public void RefreshVersion()
        {
            if (_versionLabel != null)
                _versionLabel.text = $"v{FunplayMCPUpdateChecker.CurrentState.CurrentVersion ?? PackageVersionUtility.CurrentVersion}";
        }

        public void RefreshStatus()
        {
            if (_statusLabel == null)
                return;

            if (_server?.IsRunning == true)
            {
                if (_server.IsAttachedToExistingTransport)
                {
                    _statusLabel.text = $"Attached to existing server on {MCPProjectContext.Endpoint(_server.Port, MCPProjectContext.Current.project_id)} ({_settings.MCPToolExportProfile ?? "core"})";
                    _statusLabel.style.color = new Color(1f, 0.8f, 0.35f);
                }
                else
                {
                    _statusLabel.text = $"Running on {MCPProjectContext.Endpoint(_server.Port, MCPProjectContext.Current.project_id)} ({_settings.MCPToolExportProfile ?? "core"})";
                    _statusLabel.style.color = new Color(0.4f, 1f, 0.4f);
                }
            }
            else
            {
                _statusLabel.text = "Stopped";
                _statusLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            }
        }
    }
}
