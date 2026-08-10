// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Funplay.Editor.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.MCP.Server
{
    internal sealed class FunplayMCPServerControlsPanel
    {
        private const string DirectTransportChoice = "Direct HTTP (default)";
        private const string BrokerTransportChoice = "Broker Mode (Experimental)";
        private static readonly List<string> TransportChoices = new List<string> { DirectTransportChoice, BrokerTransportChoice };

        private readonly ISettingsController _settings;
        private readonly MCPServerService _server;
        private readonly Action _refreshStatus;
        private Label _brokerStatus;
        private TextField _brokerMonoPathField;
        private Label _brokerMonoHint;
        private Label _portOriginHint;
        private Button _releasePortPinButton;
        private Button _pinPortButton;
        private IntegerField _portField;
        private int _statusRefreshGeneration;

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
                Task lifecycleTask;
                if (evt.newValue)
                    lifecycleTask = _server.StartAsync();
                else
                {
                    lifecycleTask = _server.StopAsync();
                    MCPBrokerProcessManager.Stop();
                }

                RefreshStatusWhenSettled(
                    lifecycleTask,
                    evt.newValue ? "Transport: Starting..." : "Transport: Stopping...");
            });
            toggle.style.marginBottom = 4;
            parent.Add(toggle);

            _portField = new IntegerField("Server Port");
            _portField.tooltip =
                "Shows the port this project is on. Without a pin each project derives its own port, so two " +
                "editors never fight over one. Type a port to pin a fixed one (CI, a firewall rule), " +
                "\"Pin Current Port\" to pin the one shown, and \"Use Per-Project Port\" to release the pin. " +
                "Clearing the field also releases the pin.";
            // Commit on Enter/blur rather than per keystroke, since committing triggers a
            // full transport restart below -- typing a multi-digit port would otherwise
            // restart the server once per digit.
            _portField.isDelayed = true;
            _portField.RegisterValueChangedCallback(evt =>
            {
                _settings.MCPServerPort = evt.newValue;
                RefreshStatusWhenSettled(ResolveSettingsLifecycleTask(), "Transport: Restarting...");
            });
            parent.Add(_portField);

            _portOriginHint = new Label();
            _portOriginHint.style.whiteSpace = WhiteSpace.Normal;
            _portOriginHint.style.fontSize = 10;
            _portOriginHint.style.opacity = 0.7f;
            _portOriginHint.style.marginBottom = 2;
            parent.Add(_portOriginHint);

            var portButtonRow = new VisualElement();
            portButtonRow.style.flexDirection = FlexDirection.Row;
            portButtonRow.style.marginBottom = 8;

            _releasePortPinButton = new Button(() =>
            {
                _settings.ClearMCPServerPortOverride();
                RefreshStatusWhenSettled(ResolveSettingsLifecycleTask(), "Transport: Restarting...");
            });
            _releasePortPinButton.text = "Use Per-Project Port";
            portButtonRow.Add(_releasePortPinButton);

            // Typing the port already displayed fires no change event, so pinning the port a project
            // is currently on needs its own gesture rather than an edit that never commits.
            _pinPortButton = new Button(() =>
            {
                _settings.MCPServerPort = _portField.value;
                RefreshStatusWhenSettled(ResolveSettingsLifecycleTask(), "Transport: Restarting...");
            });
            _pinPortButton.text = "Pin Current Port";
            portButtonRow.Add(_pinPortButton);

            parent.Add(portButtonRow);

            UpdatePortOrigin();

            var transportModeDropdown = new DropdownField("Transport Mode");
            transportModeDropdown.choices = TransportChoices;
            transportModeDropdown.tooltip =
                "Direct HTTP (default): the server owns the MCP HTTP port directly. " +
                "Broker Mode (Experimental): runs a tiny local broker process that owns the port instead and keeps " +
                "client requests alive while Unity reloads the scripting domain.";
            transportModeDropdown.SetValueWithoutNotify(_settings.MCPBrokerModeEnabled ? BrokerTransportChoice : DirectTransportChoice);
            transportModeDropdown.RegisterValueChangedCallback(evt =>
            {
                var enabled = evt.newValue == BrokerTransportChoice;
                _settings.MCPBrokerModeEnabled = enabled;
                UpdateBrokerControls(enabled);

                if (_settings.MCPServerEnabled)
                {
                    RefreshStatusWhenSettled(
                        ResolveSettingsLifecycleTask(),
                        enabled
                            ? "Transport: Switching to Broker Mode..."
                            : "Transport: Switching to Direct HTTP...");
                }
                else
                {
                    if (!enabled)
                        MCPBrokerProcessManager.Stop();
                    UpdateBrokerStatus();
                    UpdatePortOrigin();
                    InvokeRefreshStatus();
                }
            });
            transportModeDropdown.style.marginBottom = 4;
            parent.Add(transportModeDropdown);

            _brokerMonoPathField = new TextField("Broker Mono Path");
            _brokerMonoPathField.SetValueWithoutNotify(_settings.MCPBrokerMonoPath);
            _brokerMonoPathField.RegisterValueChangedCallback(evt =>
            {
                _settings.MCPBrokerMonoPath = evt.newValue;
                RefreshStatusWhenSettled(ResolveSettingsLifecycleTask(), "Transport: Restarting...");
            });
            _brokerMonoPathField.style.marginBottom = 4;
            parent.Add(_brokerMonoPathField);

            _brokerMonoHint = new Label();
            _brokerMonoHint.style.whiteSpace = WhiteSpace.Normal;
            _brokerMonoHint.style.color = new Color(0.9f, 0.35f, 0.35f);
            _brokerMonoHint.style.marginBottom = 4;
            parent.Add(_brokerMonoHint);

            RefreshMonoPathAutoDetection();

            _brokerStatus = new Label();
            _brokerStatus.style.whiteSpace = WhiteSpace.Normal;
            _brokerStatus.style.opacity = 0.78f;
            _brokerStatus.style.marginBottom = 10;
            parent.Add(_brokerStatus);

            UpdateBrokerControls(_settings.MCPBrokerModeEnabled);
            UpdateBrokerStatus();
        }

        private void InvokeRefreshStatus()
        {
            _refreshStatus?.Invoke();
        }

        private Task ResolveSettingsLifecycleTask()
        {
            var restartTask = _server.WaitForSettingsRestartAsync();
            if (!restartTask.IsCompleted || !_settings.MCPServerEnabled || _server.IsRunning)
                return restartTask;

            // The setting can be enabled while a previous start failed. In that settled state,
            // applying a transport setting is also a reasonable explicit retry.
            return _server.StartAsync();
        }

        private void RefreshStatusWhenSettled(Task lifecycleTask, string pendingText)
        {
            var generation = ++_statusRefreshGeneration;
            if (lifecycleTask != null && !lifecycleTask.IsCompleted && _brokerStatus != null)
                _brokerStatus.text = pendingText;

            _ = RefreshStatusWhenSettledAsync(lifecycleTask, generation);
        }

        private async Task RefreshStatusWhenSettledAsync(Task lifecycleTask, int generation)
        {
            try
            {
                if (lifecycleTask != null)
                    await lifecycleTask;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Funplay MCP Server] Server lifecycle change failed: " + ex.Message);
            }

            if (generation != _statusRefreshGeneration)
                return;

            UpdateBrokerStatus();
            UpdatePortOrigin();
            InvokeRefreshStatus();
        }

        /// <summary>
        /// Says where the port came from -- a pin the user typed, or this project's derived default --
        /// and flags a fallback bind, since that is the case where the port field and the port clients
        /// must actually use disagree.
        /// </summary>
        private void UpdatePortOrigin()
        {
            if (_portOriginHint == null)
                return;

            var pinned = _settings.MCPServerPortConfigured;
            var resolvedPort = _server != null ? _server.ResolvedPort : _settings.MCPServerPort;
            var activePort = _server != null && _server.IsRunning ? _server.Port : 0;
            // A fallback bind is the only case where the port asked for and the port served differ.
            // Comparing against the stored override instead would call every derived port a fallback.
            var fellBack = activePort > 0 && activePort != resolvedPort;

            if (fellBack)
            {
                _portOriginHint.text = pinned
                    ? $"Pinned to {resolvedPort}, but that port was in use; serving on {activePort}."
                    : $"Derived per project ({resolvedPort}), but that port was in use; serving on {activePort}.";
            }
            else if (pinned)
            {
                // Existing projects are pinned by the upgrade so nothing moves, which means this hint
                // is the only place they learn the per-project port exists at all.
                _portOriginHint.text =
                    $"Pinned to {resolvedPort} for this project. \"Use Per-Project Port\" derives a port from " +
                    "the project path instead, so several editors can serve MCP at the same time.";
            }
            else
            {
                _portOriginHint.text =
                    $"Derived from this project's path ({resolvedPort}), so each project gets its own port.";
            }

            // Keep the field on the port that is actually in play, so a user hand-writing a client
            // entry from it never copies a port nothing serves. During a fallback bind that is the
            // fallback port -- which also makes "Pin Current Port" a one-click way to make the
            // fallback permanent and end the conflict.
            if (_portField != null)
                _portField.SetValueWithoutNotify(activePort > 0 ? activePort : resolvedPort);

            if (_releasePortPinButton != null)
                _releasePortPinButton.style.display = pinned ? DisplayStyle.Flex : DisplayStyle.None;
            if (_pinPortButton != null)
                _pinPortButton.style.display = pinned ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void UpdateBrokerControls(bool enabled)
        {
            if (_brokerMonoPathField != null)
                _brokerMonoPathField.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
            if (_brokerMonoHint != null)
                _brokerMonoHint.style.display = enabled && !string.IsNullOrEmpty(_brokerMonoHint.text)
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
        }

        /// <summary>
        /// Auto-detection is display-only: it never writes to <see cref="ISettingsController.MCPBrokerMonoPath"/>,
        /// so clearing the field (or never touching it) keeps the setting at its real "auto-detect" default.
        /// </summary>
        private void RefreshMonoPathAutoDetection()
        {
            if (_brokerMonoPathField == null)
                return;

            if (!string.IsNullOrEmpty(_settings.MCPBrokerMonoPath))
            {
                _brokerMonoPathField.tooltip =
                    "Optional override for Unity's bundled Mono executable. Leave empty to auto-detect it from the Unity editor install.";
                SetMonoHint(null);
                return;
            }

            var detected = MCPBrokerProcessManager.ResolveMono(null);
            if (!string.IsNullOrEmpty(detected))
            {
                _brokerMonoPathField.SetValueWithoutNotify(detected);
                _brokerMonoPathField.tooltip =
                    "Auto-detected from the Unity editor install. Fill this in only if you need to override it.";
                SetMonoHint(null);
            }
            else
            {
                _brokerMonoPathField.tooltip =
                    "Optional override for Unity's bundled Mono executable. Leave empty to auto-detect it from the Unity editor install.";
                SetMonoHint("Could not auto-detect Unity's bundled Mono executable. Broker mode needs this path set manually.");
            }
        }

        private void SetMonoHint(string text)
        {
            if (_brokerMonoHint == null)
                return;

            _brokerMonoHint.text = text ?? string.Empty;
            _brokerMonoHint.style.display = !string.IsNullOrEmpty(text) && _settings.MCPBrokerModeEnabled
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }

        private void UpdateBrokerStatus()
        {
            if (_brokerStatus == null)
                return;

            if (!_settings.MCPBrokerModeEnabled)
            {
                _brokerStatus.text = "Transport: Direct HTTP.";
                return;
            }

            if (MCPBrokerProcessManager.IsRunning(out var pid, out var port))
            {
                _brokerStatus.text = "Transport: Broker running (pid " + pid + ", port " + port + ").";
                return;
            }

            var error = MCPBrokerProcessManager.LastError;
            _brokerStatus.text = string.IsNullOrEmpty(error)
                ? "Transport: Broker will start with the MCP server."
                : "Transport: Broker not running - " + error;
        }
    }
}
