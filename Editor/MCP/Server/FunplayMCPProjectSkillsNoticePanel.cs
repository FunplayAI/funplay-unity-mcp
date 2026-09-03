// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Linq;
using Funplay.Editor.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.MCP.Server
{
    internal sealed class FunplayMCPProjectSkillsNoticePanel
    {
        private const string DefaultConfigTarget = "Claude Code";

        private readonly ISettingsController _settings;
        private VisualElement _container;
        private Label _statusLabel;
        private Button _manageButton;

        public FunplayMCPProjectSkillsNoticePanel(ISettingsController settings)
        {
            _settings = settings;
        }

        public void AddTo(VisualElement parent)
        {
            _container = new VisualElement { name = "project-skills-notice" };
            _container.style.display = DisplayStyle.None;
            _container.style.backgroundColor = new Color(0.16f, 0.21f, 0.27f);
            _container.style.borderLeftWidth = 3;
            _container.style.borderLeftColor = new Color(0.35f, 0.68f, 1f);
            _container.style.borderTopLeftRadius = 4;
            _container.style.borderTopRightRadius = 4;
            _container.style.borderBottomLeftRadius = 4;
            _container.style.borderBottomRightRadius = 4;
            _container.style.paddingLeft = 8;
            _container.style.paddingRight = 8;
            _container.style.paddingTop = 6;
            _container.style.paddingBottom = 6;
            _container.style.marginBottom = 10;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            _container.Add(row);

            _statusLabel = new Label { name = "project-skills-notice-text" };
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.color = new Color(0.76f, 0.87f, 1f);
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.flexGrow = 1;
            _statusLabel.style.flexShrink = 1;
            row.Add(_statusLabel);

            _manageButton = new Button(FunplayProjectSkillsWindow.ShowWindow)
            {
                name = "project-skills-notice-button",
                text = "Open Project Skills",
                tooltip = "Review, install, or update the generated Project Skills files."
            };
            _manageButton.style.height = 24;
            _manageButton.style.minWidth = 116;
            _manageButton.style.marginLeft = 8;
            _manageButton.style.backgroundColor = new Color(0.24f, 0.48f, 0.72f);
            _manageButton.style.color = Color.white;
            row.Add(_manageButton);

            parent.Add(_container);
            Refresh();
        }

        public void Refresh()
        {
            if (_container == null || _statusLabel == null || _manageButton == null)
                return;

            var selectedTarget = string.IsNullOrWhiteSpace(_settings?.MCPSelectedConfigTarget)
                ? DefaultConfigTarget
                : _settings.MCPSelectedConfigTarget;
            var state = Evaluate(GetProjectRootPath(), selectedTarget);

            ApplyState(state);
        }

        internal void ApplyState(NoticeState state)
        {
            if (_container == null || _statusLabel == null || _manageButton == null)
                return;

            state ??= NoticeState.None;
            _container.style.display = state.Kind == NoticeKind.None
                ? DisplayStyle.None
                : DisplayStyle.Flex;
            if (state.Kind == NoticeKind.None)
                return;

            _statusLabel.text = BuildMessage(state);
            _manageButton.text = state.Kind == NoticeKind.NotInstalled
                ? "Open Project Skills"
                : "Review Skills";
        }

        internal static NoticeState Evaluate(string projectRoot, string targetName)
        {
            var platformId = ProjectSkillsManager.GetPlatformIdForConfigTarget(targetName);
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(platformId))
                return NoticeState.None;

            var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
            if (!manifest.platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase))
                return NoticeState.NotInstalled(targetName.Trim(), platformId);

            var status = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, platformId);
            if (!status.HasUpdates)
                return NoticeState.None;

            var affectedFiles = status.Files.Where(file => file.RequiresUpgrade).ToArray();
            return NoticeState.NeedsUpdate(
                targetName.Trim(),
                platformId,
                affectedFiles.Length,
                affectedFiles.Count(file => file.Missing),
                affectedFiles.Count(file => file.Unmanaged));
        }

        private static string BuildMessage(NoticeState state)
        {
            if (state.Kind == NoticeKind.NotInstalled)
            {
                return $"Project Skills are not installed for {state.TargetDisplayName}. " +
                       "Add the built-in Unity workflow and UI guidance for this project.";
            }

            var fileLabel = state.AffectedFileCount == 1 ? "file needs" : "files need";
            var reason = state.ConflictFileCount > 0
                ? $"; {state.ConflictFileCount} conflict with unmanaged content"
                : state.MissingFileCount > 0
                    ? $"; {state.MissingFileCount} missing"
                    : string.Empty;
            return $"Project Skills need updating for {state.TargetDisplayName}: " +
                   $"{state.AffectedFileCount} managed {fileLabel} attention{reason}.";
        }

        private static string GetProjectRootPath()
        {
            return Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
        }

        internal enum NoticeKind
        {
            None,
            NotInstalled,
            NeedsUpdate
        }

        internal sealed class NoticeState
        {
            private NoticeState(
                NoticeKind kind,
                string targetDisplayName,
                string platformId,
                int affectedFileCount,
                int missingFileCount,
                int conflictFileCount)
            {
                Kind = kind;
                TargetDisplayName = targetDisplayName;
                PlatformId = platformId;
                AffectedFileCount = affectedFileCount;
                MissingFileCount = missingFileCount;
                ConflictFileCount = conflictFileCount;
            }

            public static NoticeState None { get; } = new NoticeState(
                NoticeKind.None, null, null, 0, 0, 0);

            public static NoticeState NotInstalled(string targetDisplayName, string platformId)
            {
                return new NoticeState(
                    NoticeKind.NotInstalled, targetDisplayName, platformId, 0, 0, 0);
            }

            public static NoticeState NeedsUpdate(
                string targetDisplayName,
                string platformId,
                int affectedFileCount,
                int missingFileCount,
                int conflictFileCount)
            {
                return new NoticeState(
                    NoticeKind.NeedsUpdate,
                    targetDisplayName,
                    platformId,
                    affectedFileCount,
                    missingFileCount,
                    conflictFileCount);
            }

            public NoticeKind Kind { get; }
            public string TargetDisplayName { get; }
            public string PlatformId { get; }
            public int AffectedFileCount { get; }
            public int MissingFileCount { get; }
            public int ConflictFileCount { get; }
        }
    }
}
