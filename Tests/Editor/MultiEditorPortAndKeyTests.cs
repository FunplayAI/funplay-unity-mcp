// Copyright (C) Funplay. Licensed under MIT.

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools;
using NUnit.Framework;

namespace Funplay.Editor
{
    /// <summary>
    /// Covers what makes two editors on two projects able to run side by side: a port derived per
    /// project, a fallback when that port is taken, and a client-config entry name per project.
    /// </summary>
    public sealed class MultiEditorPortAndKeyTests
    {
        private const string ProjectA = "/Users/dev/work/GameAlpha";
        private const string ProjectB = "/Users/dev/work/GameBeta";

        [Test]
        public void DerivedPort_IsStableForAProjectAndInsideTheRange()
        {
            int first;
            int second;
            Assert.IsTrue(FunplayProjectIdentity.TryDerivePortFromProjectPath(ProjectA, out first));
            Assert.IsTrue(FunplayProjectIdentity.TryDerivePortFromProjectPath(ProjectA, out second));

            // Stability is the whole point: a client config entry must stay valid across restarts.
            Assert.AreEqual(first, second);
            Assert.GreaterOrEqual(first, FunplayProjectIdentity.DerivedPortRangeStart);
            Assert.Less(
                first,
                FunplayProjectIdentity.DerivedPortRangeStart + FunplayProjectIdentity.DerivedPortRangeSize);
        }

        [Test]
        public void DerivedPort_IgnoresTrailingSeparatorAndCase()
        {
            int plain;
            int decorated;
            Assert.IsTrue(FunplayProjectIdentity.TryDerivePortFromProjectPath(ProjectA, out plain));
            Assert.IsTrue(FunplayProjectIdentity.TryDerivePortFromProjectPath(ProjectA.ToUpperInvariant() + "/", out decorated));

            Assert.AreEqual(plain, decorated, "The same project reached through a differently spelled path must keep its port.");
        }

        [Test]
        public void DerivedPort_DiffersBetweenProjects()
        {
            int portA;
            int portB;
            Assert.IsTrue(FunplayProjectIdentity.TryDerivePortFromProjectPath(ProjectA, out portA));
            Assert.IsTrue(FunplayProjectIdentity.TryDerivePortFromProjectPath(ProjectB, out portB));

            Assert.AreNotEqual(portA, portB);
        }

        [Test]
        public void DerivedPort_RejectsAnUnusableIdentity()
        {
            int port;
            Assert.IsFalse(FunplayProjectIdentity.TryDerivePort(null, out port));
            Assert.IsFalse(FunplayProjectIdentity.TryDerivePort("abc", out port), "Too short to carry 8 hex digits.");
            Assert.IsFalse(FunplayProjectIdentity.TryDerivePort("zzzzzzzz", out port), "Not hexadecimal.");
            Assert.IsFalse(FunplayProjectIdentity.TryDerivePortFromProjectPath("   ", out port));
        }

        [Test]
        public void FreePortScan_SkipsAnOccupiedPortAndReturnsABindableOne()
        {
            var occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

            try
            {
                Assert.IsFalse(FunplayFreePortScanner.CanBind(occupiedPort));

                int freePort;
                Assert.IsTrue(FunplayFreePortScanner.TryFindFreePort(occupiedPort, out freePort));
                Assert.Greater(freePort, occupiedPort, "The scan walks upwards from the requested port.");
                Assert.IsTrue(FunplayFreePortScanner.CanBind(freePort));
            }
            finally
            {
                occupied.Stop();
            }
        }

        [Test]
        public void FreePortScan_FailsInsteadOfRunningPastTheEndOfThePortRange()
        {
            int freePort;
            Assert.IsFalse(FunplayFreePortScanner.TryFindFreePort(65535, 8, out freePort));
            Assert.IsFalse(FunplayFreePortScanner.TryFindFreePort(0, 8, out freePort));
        }

        [Test]
        public void ServerKey_IsDerivedFromTheProjectFolderName()
        {
            Assert.AreEqual("funplay-love-town", FunplayMCPServerKey.Build("Love Town", "abcdef1234", false));
            Assert.AreEqual("funplay-my-game", FunplayMCPServerKey.Build("  My  Game!!  ", "abcdef1234", false));

            // Nothing usable in the name: the identity hash is a deterministic, always-valid stand-in.
            Assert.AreEqual("funplay-abcdef", FunplayMCPServerKey.Build(null, "abcdef1234", false));
            Assert.AreEqual("funplay-abcdef", FunplayMCPServerKey.Build("***", "abcdef1234", false));
            Assert.AreEqual("funplay-unity", FunplayMCPServerKey.Build("***", null, false),
                "With neither a usable name nor an identity there is still a valid name to write.");
        }

        [Test]
        public void ServerKey_DropsNonAsciiSoClientToolNamesStayValid()
        {
            // char.IsLetterOrDigit accepts CJK and accented letters, but a client tool name is
            // restricted to [a-zA-Z0-9_-]: passing them through produced entry names whose tools were
            // rejected outright. Non-ASCII is dropped, and a fully non-ASCII name falls back to the hash.
            Assert.AreEqual("funplay-merge", FunplayMCPServerKey.Build("合并 Merge 大师", "abcdef1234", false));
            Assert.AreEqual("funplay-abcdef", FunplayMCPServerKey.Build("我的项目", "abcdef1234", false));
            Assert.AreEqual("funplay-caf", FunplayMCPServerKey.Build("Café", "abcdef1234", false));

            foreach (var name in new[] { "合并 Merge 大师", "我的项目", "Café", "Love Town" })
            {
                var key = FunplayMCPServerKey.Build(name, "abcdef1234", false);
                Assert.IsTrue(
                    Regex.IsMatch(key, "^[a-zA-Z0-9_-]+$"),
                    $"Entry name '{key}' from '{name}' must be a valid client tool-name fragment.");
            }
        }

        [Test]
        public void ServerKey_StaysInsideTheClientToolNameBudget()
        {
            var key = FunplayMCPServerKey.Build(
                "An Extremely Long Product Name That Would Overflow", "abcdef1234", true);

            // Clients expose tools as mcp__<key>__<tool> and cap tool names at 64 characters, so a long
            // product name must be truncated rather than pushing the client past that cap -- measured
            // against the real registry so a newly added long tool name fails here instead of silently
            // overflowing every client's tool list.
            var longestToolName = ToolSchemaBuilder.BuildAll()
                .Select(tool => tool.function.name ?? string.Empty)
                .OrderByDescending(name => name.Length)
                .First();

            Assert.LessOrEqual(
                "mcp__".Length + key.Length + "__".Length + longestToolName.Length,
                64,
                $"Longest tool name '{longestToolName}' ({longestToolName.Length}) no longer fits the key budget; " +
                $"lower {nameof(FunplayMCPServerKey.MaxKeyLength)} or shorten the tool name.");
            Assert.LessOrEqual(key.Length, FunplayMCPServerKey.MaxKeyLength);
            StringAssert.StartsWith("funplay-", key);
            StringAssert.EndsWith("-abcdef", key, "Truncation must not eat the disambiguating hash.");
        }

        [Test]
        public void ServerKey_NeverOvershootsTheBudgetWhenTruncationLandsOnASeparator()
        {
            // "MergeTown 2" with the hash on truncates inside the space: appending the collapsed '-'
            // and the '2' used to push the slug one character past its cap.
            var key = FunplayMCPServerKey.Build("MergeTown 2", "abcdef1234567890", true);

            Assert.LessOrEqual(key.Length, FunplayMCPServerKey.MaxKeyLength);
            StringAssert.EndsWith("-abcdef", key);
            Assert.IsFalse(key.Contains("--"), "Separators must not double up when truncated.");
        }

        [Test]
        public void ServerKey_AppendsTheProjectHashWhenRequested()
        {
            var withoutHash = FunplayMCPServerKey.Build("Love Town", "abcdef1234567890", false);
            var withHash = FunplayMCPServerKey.Build("Love Town", "abcdef1234567890", true);

            Assert.AreEqual("funplay-love-town", withoutHash);
            Assert.AreEqual("funplay-love-town-abcdef", withHash);

            // Two checkouts of the same product differ only once the hash is on.
            var otherProject = FunplayMCPServerKey.Build("Love Town", "999999999999", true);
            Assert.AreNotEqual(withHash, otherProject);
            Assert.AreEqual(withoutHash, FunplayMCPServerKey.Build("Love Town", "999999999999", false));
        }

        [Test]
        public void ServerKey_RecognisesTheEntriesThisPluginOwns()
        {
            Assert.IsTrue(FunplayMCPServerKey.IsFunplayKey("funplay"), "The legacy shared key must still be recognised for cleanup.");
            Assert.IsTrue(FunplayMCPServerKey.IsFunplayKey("funplay-love-town"));
            Assert.IsFalse(FunplayMCPServerKey.IsFunplayKey("funplay_manual"));
            Assert.IsFalse(FunplayMCPServerKey.IsFunplayKey("my-funplay"));
            Assert.IsFalse(FunplayMCPServerKey.IsFunplayKey(null));
        }

        [Test]
        public void ProjectHash_IsAddedOnlyWhenAnotherProjectAlreadyTookTheName()
        {
            var existing = new HashSet<string>(new[] { "funplay-love-town", "funplay-other-game" });

            // Same product name in two projects: the second one must not silently replace the first
            // one's entry, so it disambiguates itself with a hash.
            Assert.IsTrue(FunplayMCPClientConfigPanel.ShouldAddProjectHash(
                "funplay-love-town", recordedKey: string.Empty, existingEntryNames: existing));

            // An entry this project wrote before is ours to overwrite -- no hash, no churn.
            Assert.IsFalse(FunplayMCPClientConfigPanel.ShouldAddProjectHash(
                "funplay-love-town", recordedKey: "funplay-love-town", existingEntryNames: existing));

            // Nobody has the name yet: keep it clean.
            Assert.IsFalse(FunplayMCPClientConfigPanel.ShouldAddProjectHash(
                "funplay-brand-new", recordedKey: string.Empty, existingEntryNames: existing));

            // A rename still counts as ours when the new name is free.
            Assert.IsFalse(FunplayMCPClientConfigPanel.ShouldAddProjectHash(
                "funplay-renamed", recordedKey: "funplay-love-town", existingEntryNames: existing));

            Assert.IsFalse(FunplayMCPClientConfigPanel.ShouldAddProjectHash(null, null, existing));
            Assert.IsFalse(FunplayMCPClientConfigPanel.ShouldAddProjectHash("funplay-love-town", null, null));
        }

        [Test]
        public void ConfigCleanup_RetiresOnlyTheEntryThisProjectWroteBefore()
        {
            var servers = new Dictionary<string, object>
            {
                ["funplay"] = Entry("http://127.0.0.1:8765/"),
                ["funplay-love-town"] = Entry("http://127.0.0.1:24312/"),
                ["funplay-old-name"] = Entry("http://localhost:20001/"),
                ["funplay-other-project"] = Entry("http://127.0.0.1:21000/"),
                ["some-other-server"] = Entry("http://127.0.0.1:9000/")
            };

            FunplayMCPClientConfigPanel.RemoveSupersededFunplayEntries(
                servers, "funplay-love-town", "funplay-old-name");

            Assert.IsTrue(servers.ContainsKey("funplay-love-town"), "The entry just written must survive.");
            Assert.IsFalse(servers.ContainsKey("funplay-old-name"), "This project's own previous entry is retired.");
            Assert.IsTrue(
                servers.ContainsKey("funplay-other-project"),
                "Another project's entry must never be removed -- that is the bug per-project naming fixes.");
            Assert.IsTrue(
                servers.ContainsKey("funplay"),
                "The legacy shared entry could belong to any project, so it is reported rather than deleted.");
            Assert.IsTrue(servers.ContainsKey("some-other-server"), "Unrelated servers must never be touched.");
        }

        [Test]
        public void ConfigCleanup_LeavesAHandEditedPreviousEntryAlone()
        {
            var servers = new Dictionary<string, object>
            {
                ["funplay-love-town"] = Entry("http://127.0.0.1:24312/"),
                ["funplay-old-name"] = Entry("https://shared-build-box.internal:24312/")
            };

            FunplayMCPClientConfigPanel.RemoveSupersededFunplayEntries(
                servers, "funplay-love-town", "funplay-old-name");

            Assert.IsTrue(
                servers.ContainsKey("funplay-old-name"),
                "A recorded entry repointed at a non-loopback host was edited deliberately after we wrote it.");
        }

        [Test]
        public void ConfigCleanup_DoesNothingWithoutARecordedPreviousEntry()
        {
            var servers = new Dictionary<string, object>
            {
                ["funplay"] = Entry("http://127.0.0.1:8765/"),
                ["funplay-love-town"] = Entry("http://127.0.0.1:24312/")
            };

            FunplayMCPClientConfigPanel.RemoveSupersededFunplayEntries(servers, "funplay-love-town", null);
            FunplayMCPClientConfigPanel.RemoveSupersededFunplayEntries(
                servers, "funplay-love-town", "funplay-love-town");

            Assert.AreEqual(2, servers.Count, "With no recorded previous name there is no evidence to act on.");
        }

        [Test]
        public void TomlCleanup_LeavesAHandEditedPreviousSectionAlone()
        {
            // Mirrors the JSON path's IsLoopbackEntry guard: a section repointed at a non-local host
            // was edited deliberately after we wrote it and must survive a rename cleanup.
            var content =
                "[mcp_servers.funplay-old-name]\nurl = \"https://shared-build-box.internal:24312/\"\n";

            var cleaned = FunplayMCPClientConfigPanel.RemoveSupersededTomlSection(
                content, "funplay-love-town", "funplay-old-name");

            Assert.AreEqual(content, cleaned);
        }

        [Test]
        public void TomlCleanup_IgnoresACommentedOutHeader()
        {
            // A mid-line match used to cut from inside the comment to the next section, corrupting
            // unrelated content; only a header at the start of a line is a real section.
            var content =
                "# was: [mcp_servers.funplay-old-name] before the rename\n" +
                "[mcp_servers.something-else]\nurl = \"http://127.0.0.1:9000/\"\n";

            var cleaned = FunplayMCPClientConfigPanel.RemoveSupersededTomlSection(
                content, "funplay-love-town", "funplay-old-name");

            Assert.AreEqual(content, cleaned);

            int start;
            int end;
            Assert.IsFalse(
                FunplayMCPClientConfigPanel.TryFindTomlSection(content, "funplay-old-name", out start, out end));
            Assert.IsTrue(
                FunplayMCPClientConfigPanel.TryFindTomlSection(content, "something-else", out start, out end));
        }

        [Test]
        public void TomlCleanup_RemovesOnlyThePreviousSectionAndKeepsNeighbours()
        {
            var content =
                "[mcp_servers.funplay-old-name]\nurl = \"http://127.0.0.1:20001/\"\n\n" +
                "[mcp_servers.funplay-other-project]\nurl = \"http://127.0.0.1:21000/\"\n\n" +
                "[mcp_servers.something-else]\nurl = \"http://127.0.0.1:9000/\"\n";

            var cleaned = FunplayMCPClientConfigPanel.RemoveSupersededTomlSection(
                content, "funplay-love-town", "funplay-old-name");

            StringAssert.DoesNotContain("[mcp_servers.funplay-old-name]", cleaned);
            StringAssert.Contains("[mcp_servers.funplay-other-project]", cleaned);
            StringAssert.Contains("url = \"http://127.0.0.1:21000/\"", cleaned);
            StringAssert.Contains("[mcp_servers.something-else]", cleaned);

            Assert.AreEqual(
                content,
                FunplayMCPClientConfigPanel.RemoveSupersededTomlSection(content, "funplay-love-town", null),
                "Without a recorded previous name the file is left untouched.");
        }

        private static Dictionary<string, object> Entry(string url)
        {
            return new Dictionary<string, object> { ["url"] = url, ["type"] = "http" };
        }

        [Test]
        public void DerivedPorts_DoNotCollideAcrossATypicalNumberOfProjects()
        {
            var ports = new HashSet<int>();
            for (var i = 0; i < 12; i++)
            {
                int port;
                Assert.IsTrue(FunplayProjectIdentity.TryDerivePortFromProjectPath("/Users/dev/work/Project" + i, out port));
                Assert.IsTrue(ports.Add(port), "Derived ports collided across a realistic set of projects.");
            }
        }
    }
}
