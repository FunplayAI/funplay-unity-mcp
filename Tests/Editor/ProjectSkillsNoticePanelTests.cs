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
            Assert.AreEqual("Reconfigure MCP + Skills", button.text);

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
            Assert.AreEqual("Reconfigure MCP + Skills", button.text);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConnectionAndSkillsShareOneCardAndOneCombinedAction(bool skillsNeedUpdate)
        {
            var root = new VisualElement();
            var panel = new FunplayMCPProjectSkillsNoticePanel(null, () => { });
            panel.AddTo(root);
            panel.ApplyState(skillsNeedUpdate
                ? FunplayMCPProjectSkillsNoticePanel.NoticeState.NeedsUpdate("Codex", "codex", 2, 1, 0)
                : FunplayMCPProjectSkillsNoticePanel.NoticeState.NotInstalled("Codex", "codex"));
            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.LegacyEndpoint, "Codex");

            Assert.AreEqual(1, root.childCount);
            Assert.AreEqual(0, root.Query<HelpBox>().ToList().Count);
            var message = root.Q<Label>("project-skills-notice-text").text;
            StringAssert.Contains("MCP connection needs updating for Codex", message);
            StringAssert.Contains(skillsNeedUpdate ? "Project Skills need updating" : "Project Skills are not installed", message);
            Assert.AreEqual(1, root.Query<Button>().ToList().Count);
            var action = root.Q<Button>("project-skills-notice-button");
            Assert.AreEqual("Reconfigure MCP + Skills", action.text);
            Assert.IsTrue(action.enabledSelf);
        }

        [Test]
        public void ResolvingConnectionRetainsTheUnresolvedSkillsNotice()
        {
            var root = new VisualElement();
            var panel = new FunplayMCPProjectSkillsNoticePanel(null);
            panel.AddTo(root);
            panel.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.NotInstalled("Codex", "codex"));
            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.LegacyEndpoint, "Codex");
            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.None, "Codex");

            Assert.AreEqual(DisplayStyle.Flex, root.Q<VisualElement>("project-skills-notice").style.display.value);
            var message = root.Q<Label>("project-skills-notice-text").text;
            StringAssert.Contains("Project Skills are not installed", message);
            StringAssert.DoesNotContain("MCP connection needs", message);
            Assert.AreEqual(1, root.Query<Button>().ToList().Count);
            Assert.AreEqual("Reconfigure MCP + Skills", root.Q<Button>("project-skills-notice-button").text);

            panel.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.None);
            Assert.AreEqual(DisplayStyle.None, root.Q<VisualElement>("project-skills-notice").style.display.value);
        }

        [Test]
        public void ResolvingSkillsKeepsTheConnectionNoticeEvenForAnUnsupportedSkillsTarget()
        {
            var root = new VisualElement();
            var panel = new FunplayMCPProjectSkillsNoticePanel(null, () => { });
            panel.AddTo(root);
            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.LegacyEndpoint, "Kimi");
            panel.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.None);

            Assert.AreEqual(DisplayStyle.Flex, root.Q<VisualElement>("project-skills-notice").style.display.value);
            StringAssert.Contains("needs updating for Kimi", root.Q<Label>("project-skills-notice-text").text);
            Assert.AreEqual(1, root.Query<Button>().ToList().Count);
            var action = root.Q<Button>("project-skills-notice-button");
            Assert.AreEqual("Reconfigure MCP", action.text);
            StringAssert.Contains("does not support generated Project Skills", action.tooltip);
        }

        [Test]
        public void UnreadableConfigurationKeepsSkillsAndDoesNotOfferAnUnverifiedRewrite()
        {
            var root = new VisualElement();
            var panel = new FunplayMCPProjectSkillsNoticePanel(null, () => Assert.Fail("Rendering must not reconfigure."));
            panel.AddTo(root);
            panel.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.NotInstalled("Codex", "codex"));
            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.Unreadable, "Codex");

            var message = root.Q<Label>("project-skills-notice-text").text;
            StringAssert.Contains("No configuration was changed", message);
            StringAssert.Contains("Project Skills are not installed", message);
            Assert.AreEqual(1, root.Query<Button>().ToList().Count);
            Assert.IsFalse(root.Q<Button>("project-skills-notice-button").enabledSelf);
            panel.ConfigureMcpAndSkills();
        }

        [Test]
        public void RenderingAndClearingConnectionStateNeverInvokesTheReconfigureAction()
        {
            int calls = 0;
            var panel = new FunplayMCPProjectSkillsNoticePanel(null, () => calls++);
            panel.AddTo(new VisualElement());
            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.LegacyEndpoint, "Codex");
            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.None, "Codex");
            Assert.AreEqual(0, calls);
        }

        [Test]
        public void CombinedActionInvokesOneCallbackAndCannotRunForUnreadableOrResolvedState()
        {
            int calls = 0;
            var panel = new FunplayMCPProjectSkillsNoticePanel(null, () => calls++);
            panel.AddTo(new VisualElement());
            panel.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.NotInstalled("Codex", "codex"));
            panel.ConfigureMcpAndSkills();
            Assert.AreEqual(1, calls);

            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.Unreadable, "Codex");
            panel.ConfigureMcpAndSkills();
            Assert.AreEqual(1, calls);

            panel.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.None);
            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.None, "Codex");
            panel.ConfigureMcpAndSkills();
            Assert.AreEqual(1, calls);

            panel.ApplyConnectionState(FunplayMCPClientConfigPanel.ConnectionNoticeKind.LegacyEndpoint, "Codex");
            panel.ConfigureMcpAndSkills();
            Assert.AreEqual(2, calls);
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
