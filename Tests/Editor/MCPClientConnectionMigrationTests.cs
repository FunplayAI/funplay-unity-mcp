// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.MCP.Server;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tests
{
    public sealed class MCPClientConnectionMigrationTests
    {
        private const string OwnKey = "funplay-own";
        private const string PreviousKey = "funplay-previous";
        private const string Scope = "/workspace/MyProject";
        private const string LegacyUrl = "http://127.0.0.1:25144/mcp";
        private static readonly string ScopedUrl = MCPProjectContext.Endpoint(25144, new string('a', 64));

        [TestCase("http://127.0.0.1:25144", true)]
        [TestCase("http://127.0.0.1:25144/", true)]
        [TestCase("http://localhost:25144/mcp", true)]
        [TestCase("http://127.0.0.1:25144/mcp/", true)]
        [TestCase("http://[::1]:25144/mcp", true)]
        [TestCase("http://127.0.0.1:25144/mcp?transport=http", true)]
        [TestCase("https://example.com/mcp", false)]
        [TestCase("http://127.0.0.1.example.com:25144/mcp", false)]
        [TestCase("http://localhost:25144/other", false)]
        [TestCase("not a URL", false)]
        [TestCase(null, false)]
        public void RecognizesOnlyLegacyLoopbackRoutes(string url, bool expected)
        {
            Assert.AreEqual(expected, MCPClientConnectionMigration.IsLegacyEndpoint(url));
            Assert.IsFalse(MCPClientConnectionMigration.IsLegacyEndpoint(ScopedUrl));
        }

        [TestCase("mcpServers", "url")]
        [TestCase("servers", "url")]
        [TestCase("mcp", "url")]
        [TestCase("mcpServers", "serverUrl")]
        public void JsonClientShapesDetectTheSavedLegacyAddress(string rootKey, string field)
        {
            var target = new FunplayMCPClientConfigPanel.MCPConfigTarget { RootKey = rootKey, UrlFieldName = field };
            var root = new JObject { [rootKey] = new JObject { [OwnKey] = new JObject { [field] = LegacyUrl } } };

            Assert.IsTrue(Evaluate(root.ToString(), target));
            root[rootKey][OwnKey][field] = ScopedUrl;
            Assert.IsFalse(Evaluate(root.ToString(), target));
        }

        [Test]
        public void ClaudeChecksTheCurrentScopeAndInheritedGlobalEntryButNotOtherProjects()
        {
            var target = new FunplayMCPClientConfigPanel.MCPConfigTarget { UseProjectScope = true };
            var root = new JObject { ["projects"] = new JObject {
                ["/another/project"] = new JObject { ["mcpServers"] = new JObject { [OwnKey] = Entry(LegacyUrl) } }
            } };
            Assert.IsFalse(Evaluate(root.ToString(), target));

            root["mcpServers"] = new JObject { [OwnKey] = Entry(LegacyUrl) };
            Assert.IsTrue(Evaluate(root.ToString(), target));

            root["projects"][Scope] = new JObject { ["mcpServers"] = new JObject { [OwnKey] = Entry(ScopedUrl) } };
            Assert.IsFalse(Evaluate(root.ToString(), target), "A current project-scoped entry overrides its old global entry.");

            root["projects"][Scope]["mcpServers"][OwnKey]["url"] = LegacyUrl;
            Assert.IsTrue(Evaluate(root.ToString(), target));
        }

        [Test]
        public void RecordedEntryTakesPrecedenceOverAnUnownedNameCollision()
        {
            var root = new JObject { ["mcpServers"] = new JObject {
                [OwnKey] = Entry(ScopedUrl), [PreviousKey] = Entry(LegacyUrl)
            } };
            Assert.IsTrue(Evaluate(root.ToString(), recorded: PreviousKey));

            root["mcpServers"][PreviousKey]["url"] = ScopedUrl;
            root["mcpServers"][OwnKey]["url"] = LegacyUrl;
            Assert.IsFalse(Evaluate(root.ToString(), recorded: PreviousKey));
        }

        [Test]
        public void CurrentEntryClearsTheNoticeWithoutDeletingOtherProjectsLegacyEntries()
        {
            var root = new JObject { ["mcpServers"] = new JObject {
                [FunplayMCPServerKey.LegacyKey] = Entry(LegacyUrl), ["funplay-other"] = Entry(LegacyUrl)
            } };
            Assert.IsTrue(Evaluate(root.ToString()));

            root["mcpServers"][OwnKey] = Entry(ScopedUrl);
            Assert.IsFalse(Evaluate(root.ToString()));
            ((JObject)root["mcpServers"]).Remove(FunplayMCPServerKey.LegacyKey);
            ((JObject)root["mcpServers"]).Remove(OwnKey);
            Assert.IsFalse(Evaluate(root.ToString()), "An unrelated project's old address is not this project's migration.");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("{}")]
        [TestCase("{\"mcpServers\":{\"custom\":{\"command\":\"node\"}}}")]
        [TestCase("{\"mcpServers\":{\"funplay-own\":{\"url\":17}}}")]
        public void FreshOrUnrelatedConfigurationsDoNotShowAnUpgradeNotice(string content)
        {
            Assert.IsFalse(Evaluate(content));
        }

        [TestCase('"')]
        [TestCase('\'')]
        public void CodexReadsActiveUrlWithEitherTomlQuoteStyle(char quote)
        {
            var target = new FunplayMCPClientConfigPanel.MCPConfigTarget { IsToml = true };
            var content = $"[mcp_servers.{OwnKey}]\nurl = {quote}{LegacyUrl}{quote} # active\n";
            Assert.IsTrue(Evaluate(content, target));
            Assert.IsFalse(Evaluate(content.Replace(LegacyUrl, ScopedUrl), target));
        }

        [Test]
        public void CodexIgnoresCommentedUrlsAndTheNextServersAddress()
        {
            var target = new FunplayMCPClientConfigPanel.MCPConfigTarget { IsToml = true };
            var content = $"# [mcp_servers.{OwnKey}]\n# url = \"{LegacyUrl}\"\n" +
                $"[mcp_servers.{OwnKey}]\n# url = \"{LegacyUrl}\"\ncommand = \"node\"\n" +
                $"[mcp_servers.funplay-other]\nurl = \"{LegacyUrl}\"\n";
            Assert.IsFalse(Evaluate(content, target));
        }

        [Test]
        public void AnExistingStdioEntryDoesNotInheritTheSharedLegacyHttpNotice()
        {
            var target = new FunplayMCPClientConfigPanel.MCPConfigTarget { IsToml = true };
            var content = $"[mcp_servers.{OwnKey}]\ncommand = \"node\"\n" +
                $"[mcp_servers.{FunplayMCPServerKey.LegacyKey}]\nurl = \"{LegacyUrl}\"\n";
            Assert.IsFalse(Evaluate(content, target));
        }

        [Test]
        public void DeepSeekManagedBlocksDetectLegacyAndClearAfterReconfiguration()
        {
            var target = new FunplayMCPClientConfigPanel.MCPConfigTarget { IsDeepSeekHarness = true };
            var content = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(string.Empty, OwnKey, LegacyUrl);
            Assert.IsTrue(Evaluate(content, target));
            content = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(content, OwnKey, ScopedUrl);
            content = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(content, "funplay-other", LegacyUrl);
            Assert.IsFalse(Evaluate(content, target));
        }

        [Test]
        public void JsonReconfigurationClearsTheNoticeAndPreservesUnrelatedEntries()
        {
            var directory = Path.Combine(Path.GetTempPath(), "FunplayConnectionNoticeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var target = new FunplayMCPClientConfigPanel.MCPConfigTarget { ConfigPath = Path.Combine(directory, "mcp.json") };
                File.WriteAllText(target.ConfigPath, new JObject { ["mcpServers"] = new JObject {
                    [OwnKey] = Entry(LegacyUrl), ["funplay-other"] = Entry(LegacyUrl)
                } }.ToString());
                var before = File.ReadAllText(target.ConfigPath);
                Assert.IsTrue(Evaluate(before, target, OwnKey));
                Assert.AreEqual(before, File.ReadAllText(target.ConfigPath), "Inspecting must not write the user's config.");

                FunplayMCPClientConfigPanel.WriteJsonConfiguration(target, OwnKey, OwnKey,
                    FunplayMCPClientConfigPanel.CreateHttpEntry(ScopedUrl, null, false, null, false));
                var after = File.ReadAllText(target.ConfigPath);
                Assert.IsFalse(Evaluate(after, target, OwnKey));
                Assert.AreEqual(LegacyUrl, (string)JObject.Parse(after)["mcpServers"]["funplay-other"]["url"]);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void ConnectionStateUsesTheExistingSkillsCardWithoutASecondBanner()
        {
            var root = new VisualElement();
            var sharedNotice = new FunplayMCPProjectSkillsNoticePanel(null, () => { });
            sharedNotice.AddTo(root);
            sharedNotice.ApplyState(FunplayMCPProjectSkillsNoticePanel.NoticeState.None);
            var panel = new FunplayMCPClientConfigPanel(null, null, null, sharedNotice.ApplyConnectionState);
            var notice = root.Q<VisualElement>("project-skills-notice");
            var label = root.Q<Label>("project-skills-notice-text");
            Assert.AreEqual(DisplayStyle.None, notice.style.display.value);

            panel.ApplyConnectionMigrationNotice(FunplayMCPClientConfigPanel.ConnectionNoticeKind.LegacyEndpoint, "Codex");
            Assert.AreEqual(DisplayStyle.Flex, notice.style.display.value);
            StringAssert.Contains("needs updating for Codex", label.text);
            StringAssert.Contains("Reconfigure to bind", label.text);
            StringAssert.Contains("this Unity project", label.text);
            StringAssert.Contains("reconnect or restart Codex", label.text);
            Assert.AreEqual(1, root.childCount, "The connection must reuse the Skills card, not add another banner.");
            Assert.AreEqual(0, root.Query<HelpBox>().ToList().Count);
            Assert.AreEqual("Reconfigure MCP + Skills", root.Q<Button>("project-skills-notice-button").text);
            Assert.AreEqual(1, root.Query<Button>().ToList().Count);

            panel.ApplyConnectionMigrationNotice(FunplayMCPClientConfigPanel.ConnectionNoticeKind.Unreadable, "Codex");
            StringAssert.Contains("No configuration was changed", label.text);
            panel.ApplyConnectionMigrationNotice(FunplayMCPClientConfigPanel.ConnectionNoticeKind.None, "Codex");
            Assert.AreEqual(DisplayStyle.None, notice.style.display.value);
        }

        private static JObject Entry(string url) => new JObject { ["url"] = url };

        private static bool Evaluate(string content,
            FunplayMCPClientConfigPanel.MCPConfigTarget target = default, string recorded = null)
            => MCPClientConnectionMigration.NeedsReconfiguration(content, target, Scope, OwnKey, recorded);
    }
}
