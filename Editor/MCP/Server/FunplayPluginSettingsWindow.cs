// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Funplay.Editor.DI;
using Funplay.Editor.Services;
using Funplay.Editor.Settings;
using Funplay.Editor.Tools;

namespace Funplay.Editor.MCP.Server
{
    internal class FunplayPluginSettingsWindow : EditorWindow
    {
        private ISettingsController _settingsController;
        private Toggle _recentActivityExpandedToggle;
        private Toggle _debugLoggingToggle;
        private Label _debugStatusLabel;

        [MenuItem("Funplay/MCP Settings")]
        public static void ShowWindow()
        {
            var window = GetWindow<FunplayPluginSettingsWindow>("MCP Settings");
            window.minSize = new Vector2(360, 320);
            window.Show();
        }

        public void CreateGUI()
        {
            if (_settingsController != null)
                _settingsController.OnSettingsChanged -= RefreshStatus;
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

            var title = new Label("MCP Settings");
            title.style.fontSize = 17;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.marginBottom = 4;
            rootVisualElement.Add(title);

            var hint = new Label("Project-level settings for the Funplay Unity MCP plugin. Preferences are saved per project.");
            hint.style.fontSize = 11;
            hint.style.color = new Color(0.65f, 0.65f, 0.65f);
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginBottom = 10;
            rootVisualElement.Add(hint);

            var content = new ScrollView(ScrollViewMode.Vertical) { name = "mcp-settings-scroll" };
            content.style.flexGrow = 1;
            content.style.minHeight = 0;
            rootVisualElement.Add(content);

            var safetySection = CreateSection();
            safetySection.style.marginBottom = 8;
            FunplayMCPSafetyPanel.AddTo(safetySection, _settingsController);
            content.Add(safetySection);

            var activitySection = CreateSection();
            activitySection.style.marginBottom = 8;
            activitySection.Add(CreateSectionHeader("Recent Activity"));
            _recentActivityExpandedToggle = new Toggle("Expand all entries by default")
            {
                name = "recent-activity-expanded-by-default"
            };
            _recentActivityExpandedToggle.style.marginBottom = 5;
            _recentActivityExpandedToggle.labelElement.style.whiteSpace = WhiteSpace.Normal;
            _recentActivityExpandedToggle.labelElement.style.flexShrink = 1;
            _recentActivityExpandedToggle.SetValueWithoutNotify(_settingsController.MCPRecentActivityExpandedByDefault);
            _recentActivityExpandedToggle.RegisterValueChangedCallback(evt =>
                _settingsController.MCPRecentActivityExpandedByDefault = evt.newValue);
            activitySection.Add(_recentActivityExpandedToggle);
            activitySection.Add(CreateHint(
                "On: expand all entries. Off: collapse history and expand only the latest entry. " +
                "Applies immediately; manually expanded or collapsed entries keep your choice until the panel is reopened."));
            content.Add(activitySection);

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

            content.Add(debugSection);
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (_settingsController == null)
                return;

            _recentActivityExpandedToggle?.SetValueWithoutNotify(_settingsController.MCPRecentActivityExpandedByDefault);

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
            section.style.flexShrink = 0;
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
}
