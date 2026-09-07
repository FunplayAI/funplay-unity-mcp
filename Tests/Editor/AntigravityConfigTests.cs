// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor
{
    public sealed class AntigravityConfigTests
    {
        private string _sandbox;
        private string _projectRoot;

        [SetUp]
        public void SetUp()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "FunplayAntigravityTests_" + Guid.NewGuid().ToString("N"));
            _projectRoot = Path.Combine(_sandbox, "Repo");
            Directory.CreateDirectory(_projectRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_sandbox))
                Directory.Delete(_sandbox, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NestedProjectUsesOneWorkspaceForConfigSkillsAndInstructions(bool worktree)
        {
            var gitPath = Path.Combine(_projectRoot, ".git");
            if (worktree)
                File.WriteAllText(gitPath, "gitdir: ../git-worktrees/example\n");
            else
                Directory.CreateDirectory(gitPath);
            var nestedProject = Path.Combine(_projectRoot, "Games", "UnityProject");
            Directory.CreateDirectory(nestedProject);

            var target = FunplayMCPClientConfigPanel.CreateAntigravityTarget(nestedProject);

            Assert.AreEqual(Path.Combine(_projectRoot, ".agents", "mcp_config.json"), target.ConfigPath);
            Assert.AreEqual("serverUrl", target.UrlFieldName);
            Assert.IsFalse(target.UseProjectScope, "Workspace JSON must not use Claude's projects[...] wrapper.");
            Assert.AreEqual(Path.Combine(_projectRoot, ".agents", "skills"),
                ProjectSkillsManager.GetAntigravitySkillsRoot(nestedProject));
            Assert.AreEqual(Path.Combine(_projectRoot, "AGENTS.md"),
                ProjectSkillsManager.GetAntigravityAgentsPath(nestedProject));
        }

        [Test]
        public void ProjectWithoutGitUsesItsOwnDirectoryAndRejectsAMissingRoot()
        {
            Assert.AreEqual(Path.Combine(_projectRoot, ".agents", "mcp_config.json"),
                FunplayMCPClientConfigPanel.GetAntigravityConfigPath(_projectRoot));
            Assert.Throws<ArgumentException>(() => FunplayMCPClientConfigPanel.GetAntigravityConfigPath(""));
        }

        [Test]
        public void WritesWorkspaceConfigPreservingOtherServersAndRetiringOnlyTheRecordedName()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".git"));
            var target = FunplayMCPClientConfigPanel.CreateAntigravityTarget(_projectRoot);
            Write(target.ConfigPath, @"{
                ""theme"": {""name"": ""dark""},
                ""mcpServers"": {
                    ""funplay-old"": {""serverUrl"": ""http://127.0.0.1:24312/mcp""},
                    ""funplay-other"": {""serverUrl"": ""http://127.0.0.1:21000/mcp""},
                    ""custom"": {""command"": ""node"", ""args"": [""server.js""]}
                }
            }");
            var otherBefore = ReadObject(File.ReadAllText(target.ConfigPath));
            var entry = FunplayMCPClientConfigPanel.CreateHttpEntry(
                "http://127.0.0.1:24312/mcp", target.UrlFieldName, false, null, false);

            FunplayMCPClientConfigPanel.WriteJsonConfiguration(target, "funplay-new", "funplay-old", entry);
            var firstWrite = File.ReadAllText(target.ConfigPath);
            FunplayMCPClientConfigPanel.WriteJsonConfiguration(target, "funplay-new", "funplay-new", entry);

            Assert.AreEqual(firstWrite, File.ReadAllText(target.ConfigPath), "Reconfiguring must be idempotent.");
            var actual = ReadObject(firstWrite);
            var servers = GetObject(actual, "mcpServers");
            Assert.AreEqual("http://127.0.0.1:24312/mcp", GetObject(servers, "funplay-new")["serverUrl"]);
            Assert.IsFalse(GetObject(servers, "funplay-new").ContainsKey("url"));
            Assert.IsFalse(servers.ContainsKey("funplay-old"));
            Assert.IsFalse(actual.ContainsKey("projects"));
            Assert.AreEqual(SimpleJsonHelper.Serialize(otherBefore["theme"]), SimpleJsonHelper.Serialize(actual["theme"]));
            Assert.AreEqual(SimpleJsonHelper.Serialize(GetObject(otherBefore, "mcpServers")["custom"]),
                SimpleJsonHelper.Serialize(servers["custom"]));
            Assert.AreEqual(SimpleJsonHelper.Serialize(GetObject(otherBefore, "mcpServers")["funplay-other"]),
                SimpleJsonHelper.Serialize(servers["funplay-other"]));
            Assert.IsTrue(FunplayMCPClientConfigPanel.ConfigEntryPointsAtUrl(
                firstWrite, false, null, "funplay-new", "http://127.0.0.1:24312/mcp", "serverUrl"));
        }

        [Test]
        public void IndependentWorkspacesCanUseTheSameNameWithoutChangingEachOthersConfig()
        {
            Directory.CreateDirectory(Path.Combine(_projectRoot, ".git"));
            var otherRoot = Path.Combine(_sandbox, "OtherRepo");
            Directory.CreateDirectory(Path.Combine(otherRoot, ".git"));
            var first = FunplayMCPClientConfigPanel.CreateAntigravityTarget(_projectRoot);
            var second = FunplayMCPClientConfigPanel.CreateAntigravityTarget(otherRoot);
            FunplayMCPClientConfigPanel.WriteJsonConfiguration(first, "funplay-game", null,
                FunplayMCPClientConfigPanel.CreateHttpEntry("http://127.0.0.1:24312/mcp", "serverUrl", false, null, false));
            var before = File.ReadAllText(first.ConfigPath);

            FunplayMCPClientConfigPanel.WriteJsonConfiguration(second, "funplay-game", null,
                FunplayMCPClientConfigPanel.CreateHttpEntry("http://127.0.0.1:21000/mcp", "serverUrl", false, null, false));

            Assert.AreEqual(before, File.ReadAllText(first.ConfigPath));
            Assert.AreEqual("http://127.0.0.1:21000/mcp",
                GetObject(GetObject(ReadObject(File.ReadAllText(second.ConfigPath)), "mcpServers"), "funplay-game")["serverUrl"]);
        }

        [Test]
        public void CommentedConfigIsLeftUnchanged()
        {
            var target = FunplayMCPClientConfigPanel.CreateAntigravityTarget(_projectRoot);
            const string original = "{\n // preserve this comment\n \"mcpServers\": {}\n}";
            Write(target.ConfigPath, original);

            Assert.Throws<InvalidOperationException>(() => FunplayMCPClientConfigPanel.WriteJsonConfiguration(
                target, "funplay-game", null,
                FunplayMCPClientConfigPanel.CreateHttpEntry("http://127.0.0.1:24312/mcp", "serverUrl", false, null, false)));
            Assert.AreEqual(original, File.ReadAllText(target.ConfigPath));
        }

        [TestCase("config")]
        [TestCase("antigravity")]
        public void ExistingGlobalEntriesAreReportedAndNeverUsedAsTheWriteTarget(string directory)
        {
            var homePath = Path.Combine(_sandbox, "home");
            var globalPath = Path.Combine(homePath, ".gemini", directory, "mcp_config.json");
            const string globalContent = "{\"mcpServers\":{\"funplay-old\":{\"serverUrl\":\"http://127.0.0.1:21000/mcp\"}}}";
            Write(globalPath, globalContent);

            var notice = FunplayMCPClientConfigPanel.GetAntigravityGlobalConfigNotice(homePath);
            StringAssert.Contains(globalPath, notice);
            StringAssert.Contains("funplay-old", notice);
            var target = FunplayMCPClientConfigPanel.CreateAntigravityTarget(_projectRoot);
            FunplayMCPClientConfigPanel.WriteJsonConfiguration(target, "funplay-game", null,
                FunplayMCPClientConfigPanel.CreateHttpEntry("http://127.0.0.1:24312/mcp", "serverUrl", false, null, false));

            Assert.AreEqual(globalContent, File.ReadAllText(globalPath));
            Assert.IsTrue(File.Exists(Path.Combine(_projectRoot, ".agents", "mcp_config.json")));
        }

        private static Dictionary<string, object> ReadObject(string content)
        {
            return (Dictionary<string, object>)SimpleJsonHelper.Deserialize(content);
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> parent, string key)
        {
            return (Dictionary<string, object>)parent[key];
        }

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }
    }
}
