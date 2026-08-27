// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor
{
    public sealed class ClaudeCodeConfigMigrationTests
    {
        private const string RecordedKey = "funplay-example";
        private const string LegacyProjectPath = "/repo/UnityProject";
        private const string ProjectScopePath = "/repo";

        private string _sandbox;
        private string _configPath;

        [SetUp]
        public void CreateSandbox()
        {
            _sandbox = Path.Combine(
                Path.GetTempPath(),
                "FunplayClaudeCodeConfigMigrationTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);
            _configPath = Path.Combine(_sandbox, ".claude.json");
        }

        [TearDown]
        public void DeleteSandbox()
        {
            if (Directory.Exists(_sandbox))
                Directory.Delete(_sandbox, recursive: true);
        }

        [Test]
        public void MigratesTopLevelEntryAndPreservesUnrelatedConfiguration()
        {
            var root = new Dictionary<string, object>
            {
                ["customSetting"] = "keep-me",
                ["mcpServers"] = new Dictionary<string, object>
                {
                    [RecordedKey] = Entry("http://127.0.0.1:24001/mcp"),
                    ["other-server"] = Entry("https://example.com/mcp")
                },
                ["projects"] = new Dictionary<string, object>
                {
                    ["/another/repo"] = new Dictionary<string, object>
                    {
                        ["allowedTools"] = new List<object> { "Read" }
                    }
                }
            };
            Write(root);

            string migratedFrom;
            var migrated = FunplayMCPClientConfigPanel.TryMigrateLegacyClaudeCodeEntryFile(
                _configPath, RecordedKey, LegacyProjectPath, ProjectScopePath, out migratedFrom);

            Assert.IsTrue(migrated);
            Assert.AreEqual("the top level", migratedFrom);

            var result = Read();
            Assert.AreEqual("keep-me", result["customSetting"]);
            Assert.IsFalse(Map(result, "mcpServers").ContainsKey(RecordedKey));
            Assert.IsTrue(Map(result, "mcpServers").ContainsKey("other-server"));
            Assert.AreEqual(
                "Read",
                ((List<object>)Map(Map(result, "projects"), "/another/repo")["allowedTools"])[0]);
            Assert.AreEqual(
                "http://127.0.0.1:24001/mcp",
                EntryUrl(ProjectServers(result)[RecordedKey]));
        }

        [Test]
        public void MigratesBothLegacyLocationsWithoutOverwritingExistingDestination()
        {
            var destination = Entry("http://127.0.0.1:24999/mcp");
            destination["note"] = "keep-current";
            var root = RootWithScopes(
                Entry("http://127.0.0.1:24001/mcp"),
                Entry("http://127.0.0.1:24002/mcp"),
                destination);
            Write(root);

            string migratedFrom;
            var migrated = FunplayMCPClientConfigPanel.TryMigrateLegacyClaudeCodeEntryFile(
                _configPath, RecordedKey, LegacyProjectPath, ProjectScopePath, out migratedFrom);

            Assert.IsTrue(migrated);
            StringAssert.Contains("the top level", migratedFrom);
            StringAssert.Contains(LegacyProjectPath, migratedFrom);

            var result = Read();
            Assert.IsFalse(Map(result, "mcpServers").ContainsKey(RecordedKey));
            Assert.IsFalse(LegacyProjectServers(result).ContainsKey(RecordedKey));
            var current = (Dictionary<string, object>)ProjectServers(result)[RecordedKey];
            Assert.AreEqual("http://127.0.0.1:24999/mcp", current["url"]);
            Assert.AreEqual("keep-current", current["note"]);
        }

        [Test]
        public void PrefersTheNewerLegacyProjectEntryWhenBothSourcesExist()
        {
            var root = RootWithScopes(
                Entry("http://127.0.0.1:24001/mcp"),
                Entry("http://127.0.0.1:24002/mcp"),
                destinationEntry: null);
            Write(root);

            string migratedFrom;
            Assert.IsTrue(FunplayMCPClientConfigPanel.TryMigrateLegacyClaudeCodeEntryFile(
                _configPath, RecordedKey, LegacyProjectPath, ProjectScopePath, out migratedFrom));

            Assert.AreEqual(
                "http://127.0.0.1:24002/mcp",
                EntryUrl(ProjectServers(Read())[RecordedKey]));
        }

        [Test]
        public void LeavesHandEditedNonLoopbackEntryUntouched()
        {
            var root = new Dictionary<string, object>
            {
                ["mcpServers"] = new Dictionary<string, object>
                {
                    [RecordedKey] = Entry("https://example.com/mcp")
                }
            };
            var original = Write(root);

            string migratedFrom;
            var migrated = FunplayMCPClientConfigPanel.TryMigrateLegacyClaudeCodeEntryFile(
                _configPath, RecordedKey, LegacyProjectPath, ProjectScopePath, out migratedFrom);

            Assert.IsFalse(migrated);
            Assert.IsNull(migratedFrom);
            Assert.AreEqual(original, File.ReadAllText(_configPath));
        }

        [Test]
        public void MalformedDestinationLeavesSourceAndFileUntouched()
        {
            var root = new Dictionary<string, object>
            {
                ["mcpServers"] = new Dictionary<string, object>
                {
                    [RecordedKey] = Entry("http://127.0.0.1:24001/mcp")
                },
                ["projects"] = new Dictionary<string, object>
                {
                    [ProjectScopePath] = new Dictionary<string, object>
                    {
                        ["mcpServers"] = "foreign-value"
                    }
                }
            };
            var original = Write(root);

            string migratedFrom;
            var migrated = FunplayMCPClientConfigPanel.TryMigrateLegacyClaudeCodeEntryFile(
                _configPath, RecordedKey, LegacyProjectPath, ProjectScopePath, out migratedFrom);

            Assert.IsFalse(migrated);
            Assert.IsNull(migratedFrom);
            Assert.AreEqual(original, File.ReadAllText(_configPath));
        }

        [Test]
        public void RefusesToMigrateARecordedKeyOutsideTheFunplayNamespace()
        {
            var root = new Dictionary<string, object>
            {
                ["mcpServers"] = new Dictionary<string, object>
                {
                    ["other-server"] = Entry("http://127.0.0.1:24001/mcp")
                }
            };
            var original = Write(root);

            string migratedFrom;
            Assert.IsFalse(FunplayMCPClientConfigPanel.TryMigrateLegacyClaudeCodeEntryFile(
                _configPath, "other-server", LegacyProjectPath, ProjectScopePath, out migratedFrom));
            Assert.AreEqual(original, File.ReadAllText(_configPath));
        }

        [Test]
        public void InvalidJsonIsLeftUntouched()
        {
            const string original = "{ this is not valid json";
            File.WriteAllText(_configPath, original);

            string migratedFrom;
            Assert.IsFalse(FunplayMCPClientConfigPanel.TryMigrateLegacyClaudeCodeEntryFile(
                _configPath, RecordedKey, LegacyProjectPath, ProjectScopePath, out migratedFrom));

            Assert.IsNull(migratedFrom);
            Assert.AreEqual(original, File.ReadAllText(_configPath));
        }

        [Test]
        public void AtomicWriteReplacesTheExpectedFileAndRemovesItsTemporaryFile()
        {
            File.WriteAllText(_configPath, "old");

            Assert.IsTrue(FunplayMCPClientConfigPanel.TryWriteTextAtomicallyIfUnchanged(
                _configPath, "old", "new"));

            Assert.AreEqual("new", File.ReadAllText(_configPath));
            Assert.AreEqual(new[] { _configPath }, Directory.GetFiles(_sandbox));
        }

        [Test]
        public void AtomicWriteDoesNotOverwriteContentThatChangedAfterItWasRead()
        {
            File.WriteAllText(_configPath, "changed-by-another-process");

            Assert.IsFalse(FunplayMCPClientConfigPanel.TryWriteTextAtomicallyIfUnchanged(
                _configPath, "stale-content", "funplay-update"));

            Assert.AreEqual("changed-by-another-process", File.ReadAllText(_configPath));
        }

        private string Write(Dictionary<string, object> root)
        {
            var content = SimpleJsonHelper.Serialize(root);
            File.WriteAllText(_configPath, content);
            return content;
        }

        private Dictionary<string, object> Read()
        {
            return (Dictionary<string, object>)SimpleJsonHelper.Deserialize(File.ReadAllText(_configPath));
        }

        private static Dictionary<string, object> RootWithScopes(
            Dictionary<string, object> topLevelEntry,
            Dictionary<string, object> legacyProjectEntry,
            Dictionary<string, object> destinationEntry)
        {
            var root = new Dictionary<string, object>
            {
                ["mcpServers"] = new Dictionary<string, object>
                {
                    [RecordedKey] = topLevelEntry
                },
                ["projects"] = new Dictionary<string, object>
                {
                    [LegacyProjectPath] = new Dictionary<string, object>
                    {
                        ["mcpServers"] = new Dictionary<string, object>
                        {
                            [RecordedKey] = legacyProjectEntry
                        }
                    },
                    [ProjectScopePath] = new Dictionary<string, object>
                    {
                        ["mcpServers"] = new Dictionary<string, object>()
                    }
                }
            };

            if (destinationEntry != null)
                ProjectServers(root)[RecordedKey] = destinationEntry;

            return root;
        }

        private static Dictionary<string, object> Entry(string url)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "http",
                ["url"] = url
            };
        }

        private static string EntryUrl(object entry)
        {
            return (string)((Dictionary<string, object>)entry)["url"];
        }

        private static Dictionary<string, object> ProjectServers(Dictionary<string, object> root)
        {
            return Map(Map(Map(root, "projects"), ProjectScopePath), "mcpServers");
        }

        private static Dictionary<string, object> LegacyProjectServers(Dictionary<string, object> root)
        {
            return Map(Map(Map(root, "projects"), LegacyProjectPath), "mcpServers");
        }

        private static Dictionary<string, object> Map(Dictionary<string, object> parent, string key)
        {
            return (Dictionary<string, object>)parent[key];
        }
    }
}
