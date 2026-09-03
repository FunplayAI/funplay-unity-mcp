// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tests
{
    public sealed class ProjectSkillsNoticePanelTests
    {
        [TestCase("Codex", "codex")]
        [TestCase(" codex ", "codex")]
        [TestCase("Claude Code", "claude")]
        [TestCase("Cursor", "cursor")]
        [TestCase("OpenCode", "opencode")]
        [TestCase("DeepSeek Harness", "dsh")]
        public void ConfigTargetMapping_ReturnsSupportedPlatform(string targetName, string expectedPlatformId)
        {
            Assert.AreEqual(expectedPlatformId, ProjectSkillsManager.GetPlatformIdForConfigTarget(targetName));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Kimi")]
        [TestCase("VS Code")]
        public void Evaluate_HidesNoticeForUnsupportedTarget(string targetName)
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var state = FunplayMCPProjectSkillsNoticePanel.Evaluate(projectRoot, targetName);

                Assert.AreEqual(FunplayMCPProjectSkillsNoticePanel.NoticeKind.None, state.Kind);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void Evaluate_ShowsNotInstalledForSupportedUnconfiguredTarget()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var state = FunplayMCPProjectSkillsNoticePanel.Evaluate(projectRoot, "Codex");

                Assert.AreEqual(FunplayMCPProjectSkillsNoticePanel.NoticeKind.NotInstalled, state.Kind);
                Assert.AreEqual("Codex", state.TargetDisplayName);
                Assert.AreEqual("codex", state.PlatformId);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void Evaluate_HidesNoticeWhenInstalledSkillsAreCurrent()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(
                    projectRoot,
                    new[] { "codex" },
                    Array.Empty<string>());

                var state = FunplayMCPProjectSkillsNoticePanel.Evaluate(projectRoot, "Codex");

                Assert.AreEqual(FunplayMCPProjectSkillsNoticePanel.NoticeKind.None, state.Kind);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void Evaluate_ShowsUpdateWhenManagedSkillFileIsMissing()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(
                    projectRoot,
                    new[] { "codex" },
                    Array.Empty<string>());
                var skillPath = Path.Combine(
                    ProjectSkillsManager.GetCodexSkillsRoot(projectRoot),
                    "funplay-unity-mcp-workflow",
                    "SKILL.md");
                File.Delete(skillPath);

                var state = FunplayMCPProjectSkillsNoticePanel.Evaluate(projectRoot, "Codex");

                Assert.AreEqual(FunplayMCPProjectSkillsNoticePanel.NoticeKind.NeedsUpdate, state.Kind);
                Assert.AreEqual(1, state.AffectedFileCount);
                Assert.AreEqual(1, state.MissingFileCount);
                Assert.AreEqual(0, state.ConflictFileCount);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyState_RendersActionableNotInstalledNoticeAndHidesNoopState()
        {
            var root = new VisualElement();
            var panel = new FunplayMCPProjectSkillsNoticePanel(null);
            panel.AddTo(root);

            panel.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.NotInstalled(
                "Codex",
                "codex"));

            var container = root.Q<VisualElement>("project-skills-notice");
            var label = root.Q<Label>("project-skills-notice-text");
            var button = root.Q<Button>("project-skills-notice-button");
            Assert.NotNull(container);
            Assert.NotNull(label);
            Assert.NotNull(button);
            Assert.AreEqual(DisplayStyle.Flex, container.style.display.value);
            StringAssert.Contains("not installed for Codex", label.text);
            Assert.AreEqual("Open Project Skills", button.text);

            panel.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.None);

            Assert.AreEqual(DisplayStyle.None, container.style.display.value);
        }

        [Test]
        public void ApplyState_RendersUpdateCountsAndReviewAction()
        {
            var root = new VisualElement();
            var panel = new FunplayMCPProjectSkillsNoticePanel(null);
            panel.AddTo(root);

            panel.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.NeedsUpdate(
                "Claude Code",
                "claude",
                3,
                2,
                0));

            var container = root.Q<VisualElement>("project-skills-notice");
            var label = root.Q<Label>("project-skills-notice-text");
            var button = root.Q<Button>("project-skills-notice-button");
            Assert.AreEqual(DisplayStyle.Flex, container.style.display.value);
            StringAssert.Contains("need updating for Claude Code", label.text);
            StringAssert.Contains("3 managed files need attention", label.text);
            StringAssert.Contains("2 missing", label.text);
            Assert.AreEqual("Review Skills", button.text);
        }

        private static string CreateTempProjectPath()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "FunplayProjectSkillsNoticeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempProjectPath(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
