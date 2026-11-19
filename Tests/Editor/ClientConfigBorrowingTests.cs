// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class ClientConfigBorrowingTests
    {
        private string sandbox;
        private static readonly string Url = "http://127.0.0.1:24312/mcp/project/" + new string('a', 64) + "/";

        [SetUp]
        public void SetUp()
        {
            sandbox = Path.Combine(Path.GetTempPath(), "FunplayClientConfig_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RoundTripLeavesUnrelatedTextNumbersUnicodeAndBomUntouched(bool jsonc)
        {
            var original = "\uFEFF{\r\n\t\"date\": \"2026-11-19\",\r\n\t\"emoji\": \"🍎中文\",\r\n" +
                "\t\"huge\": 123456789012345678901234567890,\r\n\t\"scientific\": 1.2500e+2,\r\n" +
                (jsonc ? "\t// keep exactly\r\n" : "") + "\t\"mcpServers\": {}\r\n}\r\n";
            var document = new MCPConfigJson(original, jsonc);
            var root = document.Object;
            Assert.AreEqual(JTokenType.String, root["date"].Type);
            root["mcpServers"]["funplay-own"] = new JObject { ["url"] = Url };
            var result = document.Update(root);
            StringAssert.StartsWith(original.Substring(0, original.IndexOf("{}", StringComparison.Ordinal)), result);
            Assert.AreEqual(Url, (string)new MCPConfigJson(result, jsonc).Object["mcpServers"]["funplay-own"]["url"]);
            Assert.AreEqual(result, new MCPConfigJson(result, jsonc).Update(new MCPConfigJson(result, jsonc).Object));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EveryPropertyRemovalAndAdditionCombinationProducesTheExactDesiredObject(bool jsonc)
        {
            for (var mask = 0; mask < 8; mask++)
            for (var add = 0; add < 2; add++)
            {
                var original = jsonc ? "{ /*before*/ \"a\":1, /*middle*/ \"b\":2, \"c\":3, /*after*/ }" :
                    "{\"a\":1,\"b\":2,\"c\":3}";
                var document = new MCPConfigJson(original, jsonc);
                var desired = document.Object;
                for (var i = 0; i < 3; i++) if ((mask & (1 << i)) != 0) desired.Remove(((char)('a' + i)).ToString());
                if (add != 0) desired["new"] = new JObject { ["nested"] = new JArray(true, "x") };
                var result = document.Update(desired);
                Assert.IsTrue(JToken.DeepEquals(desired, new MCPConfigJson(result, jsonc).Object), result);
                if (jsonc) foreach (var comment in new[] { "/*before*/", "/*middle*/", "/*after*/" })
                    StringAssert.Contains(comment, result);
            }
        }

        [TestCase("{\"a\":1,\"a\":2}")]
        [TestCase("{\"a\":{\"x\":1,\"\\u0078\":2}}")]
        [TestCase("{} garbage")]
        [TestCase("{\"a\":1e}")]
        [TestCase("{\"a\":01}")]
        [TestCase("{\"a\":undefined}")]
        [TestCase("{\"a\":NaN}")]
        [TestCase("{\"a\":'x'}")]
        [TestCase("{\"a\":\"\\x41\"}")]
        [TestCase("{\"a\":\"line\nbreak\"}")]
        [TestCase("{\"a\":[1,,2]}")]
        [TestCase("{\"a\":1,,}")]
        [TestCase("{ /* never ends")]
        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{\"a\":")]
        public void MalformedOrAmbiguousDocumentsAreRejectedBeforeWriting(string original)
        {
            var target = Target(jsonc: true);
            File.WriteAllText(target.ConfigPath, original);
            Assert.Throws<InvalidOperationException>(() => Write(target));
            Assert.AreEqual(original, File.ReadAllText(target.ConfigPath));
        }

        [TestCase("{ // comment\n \"a\":1 }")]
        [TestCase("{\"a\":1,}")]
        [TestCase("{\"a\":[1,]}")]
        public void StrictClientsRejectJsoncRatherThanNormalizingIt(string original)
        {
            Assert.Throws<InvalidOperationException>(() => new MCPConfigJson(original));
            Assert.DoesNotThrow(() => new MCPConfigJson(original, true));
        }

        [TestCase("// comment-only\n")]
        [TestCase("/* comment-only */")]
        [TestCase("")]
        public void EmptyJsoncDocumentsRetainTheirComments(string original)
        {
            var target = Target(jsonc: true);
            File.WriteAllText(target.ConfigPath, original);
            Write(target);
            StringAssert.StartsWith(original, File.ReadAllText(target.ConfigPath));
            Assert.AreEqual(Url, (string)new MCPConfigJson(File.ReadAllText(target.ConfigPath), true)
                .Object["mcpServers"]["funplay-own"]["url"]);
        }

        [Test]
        public void JsoncConfigurationPreservesCustomOptionsAndOnlyUpdatesTheOwnedEndpoint()
        {
            var target = Target(jsonc: true);
            var old = Url.Replace(":24312/", ":25144/");
            var original = "{\n  // keep providers\n  \"provider\": {\"n\":1.2500e+2},\n  \"mcpServers\": {\n" +
                "    \"funplay-own\": {\"url\":\"" + old + "\", /*keep token template*/ \"headers\":{\"Authorization\":\"${TOKEN}\"}, \"timeout\":42},\n" +
                "    \"other\": {\"command\":\"keep\"},\n  },\n}\n";
            File.WriteAllText(target.ConfigPath, original);
            Write(target);
            var result = File.ReadAllText(target.ConfigPath);
            Assert.AreEqual(original.Replace(old, Url), result);
            Write(target);
            Assert.AreEqual(result, File.ReadAllText(target.ConfigPath));
        }

        [TestCase("{\"url\":\"https://elsewhere.example/mcp\"}")]
        [TestCase("{\"url\":\"http://127.0.0.1:24312/mcp/project/other/\"}")]
        [TestCase("{\"enabled\":false}")]
        [TestCase("{\"command\":\"node\",\"url\":\"http://127.0.0.1:24312/mcp\"}")]
        [TestCase("{\"url\":\"http://127.0.0.1:24312/mcp\",\"disabled\":true}")]
        [TestCase("{\"url\":\"http://127.0.0.1:24312/mcp\",\"enabled\":\"false\"}")]
        [TestCase("17")]
        public void RepointedDisabledOrLocalEntriesNeedReview(string value)
        {
            var target = Target();
            var original = "{\"mcpServers\":{\"funplay-own\":" + value + "}}";
            File.WriteAllText(target.ConfigPath, original);
            Assert.Throws<InvalidOperationException>(() => Write(target));
            Assert.AreEqual(original, File.ReadAllText(target.ConfigPath));
        }

        [TestCase("null")]
        [TestCase("[]")]
        [TestCase("false")]
        [TestCase("\"keep\"")]
        public void NonObjectServerContainersAreNeverReplaced(string container)
        {
            var target = Target();
            var original = "{\"mcpServers\":" + container + "}";
            File.WriteAllText(target.ConfigPath, original);
            Assert.Throws<InvalidOperationException>(() => Write(target));
            Assert.AreEqual(original, File.ReadAllText(target.ConfigPath));
        }

        [TestCase("sse")]
        [TestCase("stdio")]
        public void UrlOnlyClientsRemoveObsoleteTransportWithoutLosingOptions(string transport)
        {
            var target = Target();
            File.WriteAllText(target.ConfigPath, new JObject { ["mcpServers"] = new JObject {
                ["funplay-own"] = new JObject { ["url"] = Url, ["type"] = transport, ["timeout"] = 42 }
            }}.ToString());
            Write(target);
            var entry = new MCPConfigJson(File.ReadAllText(target.ConfigPath)).Object["mcpServers"]["funplay-own"];
            Assert.IsNull(entry["type"]);
            Assert.AreEqual(42, (int)entry["timeout"]);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConcurrentEditsAndNewFileRacesLeaveTheExternalContentIntact(bool initiallyExists)
        {
            var path = Path.Combine(sandbox, "mcp.json");
            var original = initiallyExists ? "{\"old\":1}" : null;
            if (initiallyExists) File.WriteAllText(path, original);
            Assert.Throws<InvalidOperationException>(() => MCPClientConfigFile.Write(path, original, "{}",
                () => File.WriteAllText(path, "{\"external\":true}")));
            Assert.AreEqual("{\"external\":true}", File.ReadAllText(path));
            Assert.IsEmpty(Directory.GetFiles(sandbox, "*.tmp"));
        }

        [Test]
        public void ReadOnlyConfigIsPreserved()
        {
            var target = Target();
            File.WriteAllText(target.ConfigPath, "{}");
            File.SetAttributes(target.ConfigPath, File.GetAttributes(target.ConfigPath) | FileAttributes.ReadOnly);
            try
            {
                Assert.Throws<InvalidOperationException>(() => Write(target));
                Assert.AreEqual("{}", File.ReadAllText(target.ConfigPath));
            }
            finally { File.SetAttributes(target.ConfigPath, FileAttributes.Normal); }
        }

        [Test]
        public void SymlinkedConfigIsNeverReplacedAndItsTargetIsUntouched()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor) Assert.Ignore("Unix symlink fixture.");
            var actual = Path.Combine(sandbox, "actual.json");
            var target = Target();
            File.WriteAllText(actual, "{}");
            using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/ln", Arguments = "-s \"" + actual + "\" \"" + target.ConfigPath + "\"",
                UseShellExecute = false, CreateNoWindow = true
            }))
            {
                process.WaitForExit();
                Assert.AreEqual(0, process.ExitCode);
            }
            Assert.Throws<InvalidOperationException>(() => Write(target));
            Assert.AreEqual("{}", File.ReadAllText(actual));
            Assert.IsTrue((File.GetAttributes(target.ConfigPath) & FileAttributes.ReparsePoint) != 0);
        }

        [Test]
        public void LargeConfigKeepsAllFieldsAndRejectsExcessiveDepthAndSize()
        {
            var text = "{\"values\":[" + string.Join(",", Enumerable.Range(0, 20000)) + "],\"mcpServers\":{}}";
            var document = new MCPConfigJson(text);
            var desired = document.Object;
            desired["mcpServers"]["funplay-own"] = new JObject { ["url"] = Url };
            StringAssert.StartsWith(text.Substring(0, text.Length - 3), document.Update(desired));
            Assert.Throws<InvalidOperationException>(() => new MCPConfigJson(new string(' ', MCPConfigJson.MaximumLength + 1)));
            var deep = "{\"a\":" + new string('[', 130) + "0" + new string(']', 130) + "}";
            Assert.Throws<InvalidOperationException>(() => new MCPConfigJson(deep));
        }

        [TestCase("{}", false)]
        [TestCase("{\"enabled\":false}", false)]
        [TestCase("{\"type\":\"sse\"}", false)]
        [TestCase("{\"command\":\"node\"}", false)]
        [TestCase("{\"timeout\":42}", true)]
        public void StatusInspectsTheActualEntryNotJustFileExistence(string options, bool expected)
        {
            var target = Target(jsonc: true);
            var entry = new MCPConfigJson(options).Object;
            if (options != "{}") entry["url"] = Url;
            File.WriteAllText(target.ConfigPath, new JObject { ["mcpServers"] = new JObject { ["funplay-own"] = entry } }.ToString());
            Assert.AreEqual(expected, FunplayMCPClientConfigPanel.IsTargetConfigured(target, "funplay-own", Url, sandbox));
        }

        [Test]
        public void ProjectDefaultJsonSettingsDoNotAffectClientConfiguration()
        {
            var defaults = JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings = () => throw new InvalidOperationException("must not consult project defaults");
                var target = Target();
                Write(target);
                Write(target);
                Assert.AreEqual(Url, (string)new MCPConfigJson(File.ReadAllText(target.ConfigPath))
                    .Object["mcpServers"]["funplay-own"]["url"]);
            }
            finally { JsonConvert.DefaultSettings = defaults; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PiUsesOneWorkspaceForConfigAndSharedSkillsAndReusesItsNormalizedNamespace(bool worktree)
        {
            if (worktree) File.WriteAllText(Path.Combine(sandbox, ".git"), "gitdir: ../owned-worktree\n");
            else Directory.CreateDirectory(Path.Combine(sandbox, ".git"));
            var project = Path.Combine(sandbox, "Games", "Unity");
            Directory.CreateDirectory(project);
            var target = FunplayMCPClientConfigPanel.CreatePiTarget(project);
            Assert.AreEqual(Path.Combine(sandbox, ".pi", "mcp.json"), target.ConfigPath);
            Assert.AreEqual(Path.Combine(sandbox, ".agents", "skills"), ProjectSkillsManager.GetAdditionalSkillsRoot(project, "pi"));
            Directory.CreateDirectory(Path.GetDirectoryName(target.ConfigPath));
            File.WriteAllText(target.ConfigPath, "{\"mcpServers\":{\"funplay_own\":{\"url\":\"" + Url + "\",\"timeout\":42}}}");
            Write(target);
            var servers = new MCPConfigJson(File.ReadAllText(target.ConfigPath)).Object["mcpServers"] as JObject;
            Assert.AreEqual(1, servers.Count);
            Assert.AreEqual(42, (int)servers["funplay_own"]["timeout"]);
            Assert.AreEqual("pi", ProjectSkillsManager.GetPlatformIdForConfigTarget("Pi"));
        }

        [Test]
        public void PiRejectsAmbiguousNormalizedNames()
        {
            var target = Target();
            target.IsPi = true;
            var original = "{\"mcpServers\":{\"funplay_own\":{\"url\":\"" + Url + "\"},\"funplay-own\":{\"url\":\"" + Url + "\"}}}";
            File.WriteAllText(target.ConfigPath, original);
            Assert.Throws<InvalidOperationException>(() => Write(target));
            Assert.AreEqual(original, File.ReadAllText(target.ConfigPath));
        }

        [Test]
        public void PathsRespectTheClientScopeAndPlatformRatherThanDirectoryExistence()
        {
            Assert.AreEqual(Path.Combine(sandbox, "Library", "Application Support", "Code", "User", "mcp.json"),
                FunplayMCPClientConfigPanel.GetVSCodeConfigPath(sandbox, RuntimePlatform.OSXEditor, _ => null));
            Assert.AreEqual(Path.Combine(sandbox, "xdg", "Code", "User", "mcp.json"),
                FunplayMCPClientConfigPanel.GetVSCodeConfigPath(sandbox, RuntimePlatform.LinuxEditor,
                    key => key == "XDG_CONFIG_HOME" ? Path.Combine(sandbox, "xdg") : null));
            Assert.AreEqual(Path.Combine(sandbox, "portable", "user-data", "User", "mcp.json"),
                FunplayMCPClientConfigPanel.GetVSCodeConfigPath(sandbox, RuntimePlatform.OSXEditor,
                    key => key == "VSCODE_PORTABLE" ? Path.Combine(sandbox, "portable") : null));
            Assert.AreEqual(Path.Combine(sandbox, "custom", "Code", "User", "mcp.json"),
                FunplayMCPClientConfigPanel.GetVSCodeConfigPath(sandbox, RuntimePlatform.WindowsEditor,
                    key => key == "VSCODE_APPDATA" ? Path.Combine(sandbox, "custom") : null));
            Assert.AreEqual(Path.Combine(sandbox, "AppData", "Roaming", "Code", "User", "mcp.json"),
                FunplayMCPClientConfigPanel.GetVSCodeConfigPath(sandbox, RuntimePlatform.WindowsEditor, _ => null));
            Assert.AreEqual(Path.Combine(sandbox, ".trae", "mcp.json"), FunplayMCPClientConfigPanel.GetTraeConfigPath(sandbox));
            Assert.AreEqual(Path.Combine(sandbox, "codex-home", "config.toml"),
                FunplayMCPClientConfigPanel.GetCodexConfigPath("unused", Path.Combine(sandbox, "codex-home")));
        }

        [Test]
        public void SharedSkillsRemainUntilTheLastClientIsDisabled()
        {
            Directory.CreateDirectory(Path.Combine(sandbox, ".git"));
            ProjectSkillsManager.ApplyConfiguration(sandbox, new[] { "antigravity", "pi" }, Array.Empty<string>());
            var skill = Path.Combine(ProjectSkillsManager.GetAntigravitySkillsRoot(sandbox), "funplay-unity-ui-composition", "SKILL.md");
            Assert.IsTrue(File.Exists(skill));
            foreach (var platform in new[] { "antigravity", "pi" })
                Assert.IsFalse(ProjectSkillsManager.GetUpgradeStatus(sandbox, ProjectSkillsManager.LoadManifest(sandbox), platform).HasUpdates);
            ProjectSkillsManager.ApplyConfiguration(sandbox, new[] { "pi" }, Array.Empty<string>());
            Assert.IsTrue(File.Exists(skill));
            Assert.IsTrue(File.Exists(Path.Combine(sandbox, "AGENTS.md")));
            ProjectSkillsManager.ApplyConfiguration(sandbox, Array.Empty<string>(), Array.Empty<string>());
            Assert.IsFalse(File.Exists(skill));
        }

        private FunplayMCPClientConfigPanel.MCPConfigTarget Target(bool jsonc = false) =>
            new FunplayMCPClientConfigPanel.MCPConfigTarget { Name = "test", ConfigPath = Path.Combine(sandbox, "mcp.json"), IsJsonc = jsonc };

        private static void Write(FunplayMCPClientConfigPanel.MCPConfigTarget target) =>
            FunplayMCPClientConfigPanel.WriteJsonConfiguration(target, "funplay-own", "funplay-own",
                new Dictionary<string, object> { ["url"] = Url });
    }
}
