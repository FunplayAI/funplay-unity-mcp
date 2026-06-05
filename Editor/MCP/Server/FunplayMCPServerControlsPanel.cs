// Copyright (C) Funplay. Licensed under MIT.

using System;
using Funplay.Editor.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.MCP.Server
{
    internal sealed class FunplayMCPServerControlsPanel
    {
        private readonly ISettingsController _settings;
        private readonly MCPServerService _server;
        private readonly Action _refreshStatus;

        private Label _brokerStatus;
        private TextField _monoPathField;

        public FunplayMCPServerControlsPanel(
            ISettingsController settings,
            MCPServerService server,
            Action refreshStatus)
        {
            _settings = settings;
            _server = server;
            _refreshStatus = refreshStatus;
        }

        public void AddTo(VisualElement parent)
        {
            var toggle = new Toggle("Enable MCP Server");
            toggle.SetValueWithoutNotify(_settings.MCPServerEnabled);
            toggle.RegisterValueChangedCallback(evt =>
            {
                _settings.MCPServerEnabled = evt.newValue;
                if (evt.newValue)
                    _ = _server.StartAsync();
                else
                    _ = _server.StopAsync();

                EditorApplication.delayCall += () =>
                    EditorApplication.delayCall += () => { UpdateBrokerStatus(); InvokeRefreshStatus(); };
            });
            toggle.style.marginBottom = 4;
            parent.Add(toggle);

            var portField = new IntegerField("Server Port");
            portField.SetValueWithoutNotify(_settings.MCPServerPort);
            portField.RegisterValueChangedCallback(evt =>
            {
                _settings.MCPServerPort = evt.newValue;
            });
            portField.style.marginBottom = 10;
            parent.Add(portField);

            // ---- Connection mode ----
            var modeToggle = new Toggle("Broker mode (survive domain reloads)");
            modeToggle.tooltip =
                "Direct: in-process HTTP server (drops on every domain reload).\n" +
                "Broker: a separate process owns the same port and holds client requests across\n" +
                "reloads, so MCP clients never see 'connection refused'. The broker process is\n" +
                "started/stopped automatically. Clients connect to the same port — no change needed.";
            modeToggle.SetValueWithoutNotify(_settings.BrokerModeEnabled);
            modeToggle.RegisterValueChangedCallback(evt =>
            {
                _settings.BrokerModeEnabled = evt.newValue;
                UpdateNodeFieldVisibility(evt.newValue);

                // Restart the server so the right transport is chosen. StartCoreAsync handles the
                // port handoff: broker-on => launch broker on the port; broker-off => kill broker
                // then bind the in-process listener.
                if (_settings.MCPServerEnabled)
                {
                    _ = _server.StopAsync();
                    _ = _server.StartAsync();
                }
                else if (!evt.newValue)
                {
                    BrokerProcessManager.Stop();
                }

                EditorApplication.delayCall += () =>
                    EditorApplication.delayCall += () => { UpdateBrokerStatus(); InvokeRefreshStatus(); };
            });
            modeToggle.style.marginBottom = 4;
            parent.Add(modeToggle);

            _monoPathField = new TextField("Mono Path (optional)");
            _monoPathField.tooltip = "Override the auto-detected Unity-bundled Mono executable used to launch the broker. " +
                                     "Leave empty to auto-detect from the editor install.";
            _monoPathField.SetValueWithoutNotify(_settings.BrokerMonoPath);
            _monoPathField.RegisterValueChangedCallback(evt => { _settings.BrokerMonoPath = evt.newValue; });
            _monoPathField.style.marginBottom = 4;
            parent.Add(_monoPathField);

            _brokerStatus = new Label();
            _brokerStatus.style.whiteSpace = WhiteSpace.Normal;
            _brokerStatus.style.opacity = 0.8f;
            _brokerStatus.style.marginBottom = 10;
            parent.Add(_brokerStatus);

            UpdateNodeFieldVisibility(_settings.BrokerModeEnabled);
            UpdateBrokerStatus();
        }

        private void UpdateNodeFieldVisibility(bool brokerMode)
        {
            if (_monoPathField != null)
                _monoPathField.style.display = brokerMode ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void UpdateBrokerStatus()
        {
            if (_brokerStatus == null)
                return;

            if (!_settings.BrokerModeEnabled)
            {
                _brokerStatus.text = "Mode: Direct (in-process). Drops on domain reload.";
                return;
            }

            if (BrokerProcessManager.IsRunning(out var pid, out var port))
                _brokerStatus.text = $"Mode: Broker — running (pid {pid}, port {port}). Survives domain reloads.";
            else
            {
                var err = BrokerProcessManager.LastError;
                _brokerStatus.text = string.IsNullOrEmpty(err)
                    ? "Mode: Broker — not running yet (starts when the server starts)."
                    : $"Mode: Broker — could not start: {err}";
            }
        }

        private void InvokeRefreshStatus()
        {
            _refreshStatus?.Invoke();
        }
    }
}
