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
        private Button _upgradeButton;
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

            _platformTargets = new[] { "Claude Code", "Cursor", "VS Code", "Trae", "Kiro", "Codex", "OpenCode", "DeepSeek Harness", "Antigravity" };
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
                section.Add(CreateHint("Project skills integration is not available for this platform yet. Supported platforms: Codex, Claude Code, Cursor, OpenCode, DeepSeek Harness, Antigravity.", new Color(1f, 0.75f, 0.45f)));
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
                builtInSection.Add(CreateSkillRow(skill.Title, skill.Description, $"v{skill.Version} Required"));
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

                var description = CreateHint($"v{skill.Version} - {skill.Description}", new Color(0.58f, 0.58f, 0.58f));
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

            _upgradeButton = new Button(() =>
            {
                UpgradeProjectSkills();
                RefreshStatus();
            });
            _upgradeButton.text = "Upgrade Skills";
            _upgradeButton.style.height = 26;
            _upgradeButton.style.width = 110;
            _upgradeButton.style.marginLeft = 6;
            _upgradeButton.style.backgroundColor = new Color(0.55f, 0.42f, 0.18f);
            _upgradeButton.style.color = Color.white;
            _upgradeButton.SetEnabled(false);
            actionRow.Add(_upgradeButton);

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
            var upgradeStatus = currentPlatformConfigured
                ? ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, currentPlatformId)
                : null;

            if (_enableCurrentPlatformToggle != null)
            {
                _enableCurrentPlatformToggle.SetEnabled(currentPlatformSupported);
                _enableCurrentPlatformToggle.SetValueWithoutNotify(currentPlatformConfigured);
            }

            if (_upgradeButton != null)
            {
                _upgradeButton.style.display = currentPlatformConfigured ? DisplayStyle.Flex : DisplayStyle.None;
                _upgradeButton.SetEnabled(upgradeStatus != null && upgradeStatus.HasUpdates);
                _upgradeButton.tooltip = upgradeStatus != null && upgradeStatus.HasUpdates
                    ? "Regenerate Funplay-managed project skills with the versions bundled in this package."
                    : "Installed Funplay-managed project skills are already up to date for the selected platform.";
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
                if (upgradeStatus != null && upgradeStatus.HasUpdates)
                {
                    _statusLabel.text = $"Status: Configured for {currentPlatformDisplayName} | Skills: {installedSkills.Count} | Updates available";
                    _statusLabel.style.color = new Color(1f, 0.72f, 0.32f);
                }
                else
                {
                    _statusLabel.text = $"Status: Configured for {currentPlatformDisplayName} | Skills: {installedSkills.Count} | Up to date";
                    _statusLabel.style.color = new Color(0.4f, 1f, 0.4f);
                }
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
                        "Project skills are not supported for the currently selected platform yet.\n\nPlease select Codex, Claude Code, Cursor, OpenCode, DeepSeek Harness, or Antigravity.",
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

        private void UpgradeProjectSkills()
        {
            var projectRoot = GetProjectRootPath();
            var currentPlatformId = GetCurrentSkillsPlatformId();

            try
            {
                if (string.IsNullOrEmpty(currentPlatformId))
                {
                    EditorUtility.DisplayDialog(
                        "Project Skills Upgrade",
                        "Project skills are not supported for the currently selected platform yet.\n\nPlease select Codex, Claude Code, Cursor, OpenCode, DeepSeek Harness, or Antigravity.",
                        "OK");
                    return;
                }

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                if (!manifest.platforms.Contains(currentPlatformId, StringComparer.OrdinalIgnoreCase))
                {
                    EditorUtility.DisplayDialog(
                        "Project Skills Upgrade",
                        "Skills are not configured for the currently selected platform yet.\n\nEnable skills and click Apply Skills first.",
                        "OK");
                    return;
                }

                var status = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, currentPlatformId);
                if (!status.HasUpdates)
                {
                    EditorUtility.DisplayDialog(
                        "Project Skills Upgrade",
                        "Project skills are already up to date for the selected platform.",
                        "OK");
                    return;
                }

                var conflictPaths = ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, manifest.platforms);
                if (conflictPaths.Length > 0)
                {
                    var overwrite = EditorUtility.DisplayDialog(
                        "Project Skills Upgrade",
                        "Existing non-managed project instruction files were found:\n\n" +
                        string.Join("\n", conflictPaths) +
                        "\n\nOverwrite them with Funplay-managed files?",
                        "Overwrite",
                        "Cancel");

                    if (!overwrite)
                        return;
                }

                ProjectSkillsManager.ApplyConfiguration(projectRoot, manifest.platforms, manifest.optionalSkills);

                EditorUtility.DisplayDialog(
                    "Project Skills Upgrade",
                    "Project skills upgraded successfully.\n\n" +
                    $"Manifest:\n{ProjectSkillsManager.GetManifestPath(projectRoot)}",
                    "OK");

                BuildUI();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "Project Skills Upgrade Error",
                    $"Upgrade failed:\n{ex.Message}",
                    "OK");
            }
        }

        private string GetCurrentSkillsPlatformId()
        {
            if (_platformTargets == null || _platformTargets.Length == 0)
                return null;

            var idx = Mathf.Clamp(_selectedTargetIndex, 0, _platformTargets.Length - 1);
            return ProjectSkillsManager.GetPlatformIdForConfigTarget(_platformTargets[idx]);
        }

        private string GetCurrentSkillsPlatformDisplayName()
        {
            if (_platformTargets == null || _platformTargets.Length == 0)
                return "Unknown";

            var idx = Mathf.Clamp(_selectedTargetIndex, 0, _platformTargets.Length - 1);
            return _platformTargets[idx];
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

            var upgradeStatus = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, currentPlatformId);
            if (upgradeStatus.Files.Count > 0)
            {
                _generatedFilesContainer.Add(CreateHint($"Versioned files for {currentPlatformDisplayName}:", new Color(0.7f, 0.7f, 0.7f)));
                foreach (var file in upgradeStatus.Files)
                {
                    var row = new Label(FormatVersionStatus(file));
                    row.style.fontSize = 10;
                    row.style.color = file.RequiresUpgrade
                        ? new Color(1f, 0.72f, 0.32f)
                        : new Color(0.55f, 0.85f, 0.55f);
                    row.style.marginLeft = 8;
                    row.style.marginBottom = 2;
                    row.style.whiteSpace = WhiteSpace.Normal;
                    _generatedFilesContainer.Add(row);
                }
            }

            var paths = ProjectSkillsManager.GetGeneratedPathsForPlatform(projectRoot, manifest, currentPlatformId);
            if (paths.Count == 0)
            {
                _generatedFilesContainer.Add(CreateHint($"Generated files for {currentPlatformDisplayName}: none.", new Color(0.6f, 0.6f, 0.6f)));
                return;
            }

            _generatedFilesContainer.Add(CreateHint($"Generated paths for {currentPlatformDisplayName}:", new Color(0.7f, 0.7f, 0.7f)));
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

        private static string FormatVersionStatus(ProjectSkillsManager.SkillFileVersionStatus status)
        {
            if (status == null)
                return "Unknown skill file status";

            if (status.Missing)
                return $"Missing  {status.Path}  (expected {status.ExpectedVersion})";

            if (status.Unmanaged)
                return $"Conflict  {status.Path}  (not Funplay-managed, expected {status.ExpectedVersion})";

            if (status.RequiresUpgrade)
                return $"Update  {status.Path}  ({status.InstalledVersion} -> {status.ExpectedVersion})";

            return $"OK  {status.Path}  ({status.ExpectedVersion})";
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
}
