// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Linq;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    /// <summary>
    /// Covers <see cref="FunplayDeepSeekHarnessPatch"/>: the managed-block writer that puts this
    /// project's MCP entry into a DeepSeek Harness profile patch file. A patch file is a top-level
    /// YAML array the user may have filled with unrelated plugin entries, so every test here pins
    /// the same guarantee: only the span between our marker comments is ever written or removed.
    /// </summary>
    public sealed class DeepSeekHarnessPatchTests
    {
        private const string Key = "funplay-mergex11";
        private const string Url = "http://127.0.0.1:8675/";

        private static readonly string UnrelatedContent =
            "# Your patch layer for this dsh profile, applied after every bundle layer:\n" +
            "- insert:\n" +
            "    - id: recall-unread\n" +
            "      name: dsh-recall-unread\n";

        [Test]
        public void BuildManagedBlock_FollowsTheDocumentedPluginConfigShape()
        {
            var block = FunplayDeepSeekHarnessPatch.BuildManagedBlock(Key, Url);

            StringAssert.Contains("- insert:", block);
            StringAssert.Contains("    - id: mcp-" + Key, block);
            StringAssert.Contains("      name: '@deepseek-ai/dsh-mcp-client'", block);
            StringAssert.Contains("        serverName: " + Key, block);
            StringAssert.Contains("        transport: streamable-http", block);
            StringAssert.Contains("        url: " + Url, block);
        }

        [Test]
        public void Upsert_AppendsAfterUnrelatedEntries_WithoutTouchingThem()
        {
            var updated = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(UnrelatedContent, Key, Url);

            // The pre-existing content survives byte-for-byte and stays ahead of our entry.
            Assert.That(updated.StartsWith(UnrelatedContent, StringComparison.Ordinal), Is.True);
            Assert.That(
                updated.IndexOf("id: recall-unread", StringComparison.Ordinal),
                Is.LessThan(updated.IndexOf(FunplayDeepSeekHarnessPatch.BeginMarker(Key), StringComparison.Ordinal)));

            string url;
            Assert.IsTrue(FunplayDeepSeekHarnessPatch.TryGetManagedBlockUrl(updated, Key, out url));
            Assert.AreEqual(Url, url);
        }

        [Test]
        public void Upsert_ReplacesTheExistingBlock_LeavingExactlyOne()
        {
            var first = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(UnrelatedContent, Key, "http://127.0.0.1:1/");
            var second = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(first, Key, Url);

            Assert.AreEqual(1, CountOccurrences(second, FunplayDeepSeekHarnessPatch.BeginMarker(Key)));
            Assert.AreEqual(1, CountOccurrences(second, FunplayDeepSeekHarnessPatch.EndMarker(Key)));
            StringAssert.DoesNotContain("http://127.0.0.1:1/", second);
            StringAssert.Contains("url: " + Url, second);
            StringAssert.Contains("id: recall-unread", second);
        }

        [Test]
        public void Upsert_IsIdempotent()
        {
            var once = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(UnrelatedContent, Key, Url);
            var twice = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(once, Key, Url);

            Assert.AreEqual(once, twice);
        }

        [Test]
        public void Upsert_DoesNotAccumulateBlankLinesOnRepeatedUpdates()
        {
            var content = UnrelatedContent;
            for (var i = 0; i < 3; i++)
                content = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(content, Key, "http://127.0.0.1:" + (8670 + i) + "/");

            StringAssert.DoesNotContain("\n\n\n", content);
        }

        [Test]
        public void Upsert_KeyedBlocksFromDifferentProjects_Coexist()
        {
            var otherKey = "funplay-otherproject";
            var both = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(
                FunplayDeepSeekHarnessPatch.UpsertManagedBlock(string.Empty, Key, Url),
                otherKey,
                "http://127.0.0.1:9999/");

            StringAssert.Contains("serverName: " + Key, both);
            StringAssert.Contains("serverName: " + otherKey, both);

            // Removing one project's block leaves the other intact.
            var remaining = FunplayDeepSeekHarnessPatch.RemoveManagedBlock(both, otherKey);
            StringAssert.DoesNotContain(otherKey, remaining);
            StringAssert.Contains("serverName: " + Key, remaining);
        }

        [Test]
        public void RemoveManagedBlock_RemovesOnlyOurSpan()
        {
            var withBlock = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(UnrelatedContent, Key, Url);
            var removed = FunplayDeepSeekHarnessPatch.RemoveManagedBlock(withBlock, Key);

            Assert.AreEqual(UnrelatedContent, removed);
        }

        [Test]
        public void RemoveManagedBlock_NoBlockPresent_ReturnsContentUnchanged()
        {
            Assert.AreEqual(UnrelatedContent,
                FunplayDeepSeekHarnessPatch.RemoveManagedBlock(UnrelatedContent, Key));
        }

        [Test]
        public void TryGetManagedBlockUrl_MissingOrUrllessBlock_ReturnsFalse()
        {
            string url;
            Assert.IsFalse(FunplayDeepSeekHarnessPatch.TryGetManagedBlockUrl(UnrelatedContent, Key, out url));

            var beginOnly = FunplayDeepSeekHarnessPatch.BuildManagedBlock(Key, Url)
                .Replace("        url: " + Url + "\n", string.Empty);
            Assert.IsFalse(FunplayDeepSeekHarnessPatch.TryGetManagedBlockUrl(beginOnly, Key, out url));
        }

        [Test]
        public void Upsert_UnclosedBlock_ThrowsInsteadOfCorruptingTheFile()
        {
            var unclosed = FunplayDeepSeekHarnessPatch.BuildManagedBlock(Key, Url)
                .Replace(FunplayDeepSeekHarnessPatch.EndMarker(Key) + "\n", string.Empty);

            Assert.Throws<InvalidOperationException>(
                () => FunplayDeepSeekHarnessPatch.UpsertManagedBlock(unclosed, Key, Url));
        }

        [Test]
        public void ReadFunplayServerNames_FindsFunplayNames_IgnoresForeignOnes()
        {
            var content = FunplayDeepSeekHarnessPatch.BuildManagedBlock(Key, Url) +
                          "  - id: mcp-github\n    name: '@deepseek-ai/dsh-mcp-client'\n    config:\n" +
                          "      serverName: github\n" +
                          "      transport: stdio\n" +
                          "      serverName2: not-a-real-key\n" +
                          "# serverName: funplay-commented-out\n";

            var names = FunplayDeepSeekHarnessPatch.ReadFunplayServerNames(content);

            Assert.That(names, Does.Contain(Key));
            Assert.That(names, Has.Count.EqualTo(1));
        }

        [Test]
        public void ReadFunplayServerNames_IncludesLegacyAndOtherProjectEntries()
        {
            var content =
                "      serverName: funplay\n" +
                "      serverName: funplay-otherproject\n";

            var names = FunplayDeepSeekHarnessPatch.ReadFunplayServerNames(content);

            Assert.That(names, Does.Contain("funplay"));
            Assert.That(names, Does.Contain("funplay-otherproject"));
        }

        /// <summary>
        /// The cheap pre-parse gate has to match IsFunplayKey's widest accepted form. Gating on the
        /// project-scoped prefix skipped a file whose only funplay entry was the legacy bare key --
        /// invisible in the test above, where a prefixed sibling name held the gate open.
        /// </summary>
        [Test]
        public void ReadFunplayServerNames_FindsTheLegacyKeyWhenItIsTheOnlyEntry()
        {
            var names = FunplayDeepSeekHarnessPatch.ReadFunplayServerNames("      serverName: funplay\n");

            Assert.That(names, Does.Contain("funplay"));
        }

        [Test]
        public void HasServerNameOutsideManagedBlock_TrueForAHandWrittenEntry_EvenWithoutOurBlock()
        {
            var handWritten =
                "- insert:\n" +
                "    - id: mcp-funplay\n" +
                "      name: '@deepseek-ai/dsh-mcp-client'\n" +
                "      config:\n" +
                "        serverName: " + Key + "\n" +
                "        transport: streamable-http\n" +
                "        url: http://127.0.0.1:8675/\n";

            Assert.IsTrue(FunplayDeepSeekHarnessPatch.HasServerNameOutsideManagedBlock(handWritten, Key));
        }

        [Test]
        public void HasServerNameOutsideManagedBlock_FalseWhenTheNameLivesOnlyInsideOurBlock()
        {
            var withBlock = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(UnrelatedContent, Key, Url);

            Assert.IsFalse(FunplayDeepSeekHarnessPatch.HasServerNameOutsideManagedBlock(withBlock, Key));
        }

        [Test]
        public void HasServerNameOutsideManagedBlock_TrueWhenBothOurBlockAndAManualEntryExist()
        {
            var manualThenBlock = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(
                "- insert:\n    - id: mcp-funplay\n      name: '@deepseek-ai/dsh-mcp-client'\n" +
                "      config:\n        serverName: " + Key + "\n" +
                "        transport: streamable-http\n        url: " + Url + "\n",
                Key,
                Url);

            Assert.IsTrue(FunplayDeepSeekHarnessPatch.HasServerNameOutsideManagedBlock(manualThenBlock, Key));
        }

        /// <summary>
        /// A profile that exists but has no patch file yet is still a profile <c>--profile</c> can
        /// select. Reporting only profiles whose file already exists left that launch mode without
        /// the MCP entry while Configure reported success, and made the default-profile seed fire on
        /// a home that already had real profiles.
        /// </summary>
        [Test]
        public void GetProfilePatchPaths_IncludesProfilesWhosePatchFileDoesNotExistYet()
        {
            var home = Path.Combine(Path.GetTempPath(), "funplay-dsh-" + Guid.NewGuid().ToString("N"));
            var profiles = FunplayDeepSeekHarnessPatch.GetProfilesRoot(home);

            try
            {
                // "web" is a fully set-up profile; "desktop" is a real profile that has not been
                // patched yet; "node_modules" is the shared dependency folder that actually sits
                // beside them and must never be treated as a profile.
                Directory.CreateDirectory(Path.Combine(profiles, "web"));
                File.WriteAllText(Path.Combine(profiles, "web", "package.json"), "{}");
                File.WriteAllText(
                    Path.Combine(profiles, "web", FunplayDeepSeekHarnessPatch.PatchFileName), "- insert:\n");

                Directory.CreateDirectory(Path.Combine(profiles, "desktop"));
                File.WriteAllText(Path.Combine(profiles, "desktop", "package.json"), "{}");

                Directory.CreateDirectory(Path.Combine(profiles, "node_modules"));

                var paths = FunplayDeepSeekHarnessPatch.GetProfilePatchPaths(home);

                Assert.That(paths, Has.Count.EqualTo(2), string.Join(" | ", paths.ToArray()));
                Assert.That(
                    paths,
                    Does.Contain(Path.Combine(profiles, "desktop", FunplayDeepSeekHarnessPatch.PatchFileName)));
                Assert.That(
                    paths,
                    Does.Contain(Path.Combine(profiles, "web", FunplayDeepSeekHarnessPatch.PatchFileName)));
            }
            finally
            {
                if (Directory.Exists(home))
                    Directory.Delete(home, true);
            }
        }

        /// <summary>
        /// The block is spliced into files whose other lines use LF, and every offset this class
        /// computes -- the single trailing newline consumed after the end marker above all -- assumes
        /// that one terminator. Building it with <c>Environment.NewLine</c> made the block CRLF on
        /// Windows only.
        /// </summary>
        [Test]
        public void BuildManagedBlock_UsesLineFeedsOnEveryPlatform()
        {
            var block = FunplayDeepSeekHarnessPatch.BuildManagedBlock(Key, Url);

            StringAssert.DoesNotContain("\r", block);
            Assert.That(block.EndsWith(FunplayDeepSeekHarnessPatch.EndMarker(Key) + "\n", StringComparison.Ordinal));
        }

        /// <summary>
        /// A patch file this plugin wrote uses LF, but the file belongs to the user: an editor on
        /// Windows saving it back with CRLF must not blind the two scans that keep DSH loadable.
        /// Multiline "$" matches before the "\n" without consuming the "\r", so a serverName line
        /// with no trailing comment -- the ordinary case -- used to miss on every CRLF line. That
        /// silently reported no name as taken: two projects could resolve to one serverName, which
        /// makes DSH reject whichever plugin instance loads second.
        /// </summary>
        [Test]
        public void ReadFunplayServerNames_FindsNamesInACrlfFile()
        {
            var crlf = FunplayDeepSeekHarnessPatch.BuildManagedBlock(Key, Url).Replace("\n", "\r\n");

            Assert.That(FunplayDeepSeekHarnessPatch.ReadFunplayServerNames(crlf), Is.EquivalentTo(new[] { Key }));
        }

        [Test]
        public void ReadFunplayServerNames_FindsQuotedAndCommentedNamesInACrlfFile()
        {
            var crlf = ("- insert:\n" +
                        "    - id: mcp-a\n" +
                        "      config:\n" +
                        "        serverName: '" + Key + "'\n" +
                        "    - id: mcp-b\n" +
                        "      config:\n" +
                        "        serverName: " + FunplayMCPServerKey.LegacyKey + " # hand-added\n")
                       .Replace("\n", "\r\n");

            Assert.That(
                FunplayDeepSeekHarnessPatch.ReadFunplayServerNames(crlf),
                Is.EquivalentTo(new[] { Key, FunplayMCPServerKey.LegacyKey }));
        }

        [Test]
        public void HasServerNameOutsideManagedBlock_TrueForAHandWrittenEntryInACrlfFile()
        {
            var crlf = ("- insert:\n" +
                        "    - id: mcp-funplay\n" +
                        "      name: '@deepseek-ai/dsh-mcp-client'\n" +
                        "      config:\n" +
                        "        serverName: " + Key + "\n" +
                        "        transport: streamable-http\n" +
                        "        url: http://127.0.0.1:8675/\n").Replace("\n", "\r\n");

            Assert.IsTrue(FunplayDeepSeekHarnessPatch.HasServerNameOutsideManagedBlock(crlf, Key));
        }

        /// <summary>
        /// The splice offsets already consume an optional "\r" before the "\n" after the end marker;
        /// this pins that a CRLF file still ends up with exactly one block and no drift on reapply.
        /// </summary>
        [Test]
        public void Upsert_IsIdempotentOnACrlfFile()
        {
            var crlf = (UnrelatedContent + FunplayDeepSeekHarnessPatch.BuildManagedBlock(Key, Url))
                .Replace("\n", "\r\n");

            var once = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(crlf, Key, Url);
            var twice = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(once, Key, Url);

            Assert.AreEqual(once, twice);
            Assert.AreEqual(1, CountOccurrences(once, FunplayDeepSeekHarnessPatch.BeginMarker(Key)));
        }

        /// <summary>
        /// The patch file a freshly created profile ships with is three comment lines and a "[]"
        /// body. That "[]" is a complete flow sequence acting as the document root, so appending a
        /// block sequence after it produces YAML that DSH's parser rejects outright ("Unexpected
        /// seq-item-ind token") -- failing the entire patch layer, not just this entry, while the
        /// Unity side reports a successful write. The placeholder is dropped on the first write.
        /// </summary>
        [Test]
        public void Upsert_OnAFreshProfileTemplate_DropsTheEmptySequencePlaceholder()
        {
            const string template =
                "# Your patch layer for this dsh profile, applied after every bundle layer:\n" +
                "# a top-level YAML array of loader patch entries (id-targeted config\n" +
                "# overrides, disables, and insert lists; `!!js` expressions allowed).\n" +
                "[]\n";

            var updated = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(template, Key, Url);

            // The explanatory comments survive; the bare "[]" document body does not.
            StringAssert.Contains("# Your patch layer for this dsh profile", updated);
            Assert.That(
                updated.Split('\n').Any(line => line.Trim() == "[]"),
                Is.False,
                "the placeholder document body must not survive alongside a block sequence");
            StringAssert.Contains("- insert:", updated);
            Assert.AreEqual(1, CountOccurrences(updated, FunplayDeepSeekHarnessPatch.BeginMarker(Key)));
        }

        [Test]
        public void Upsert_OnAFreshProfileTemplate_IsIdempotent()
        {
            const string template = "# comment\n[]\n";

            var once = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(template, Key, Url);
            var twice = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(once, Key, Url);

            Assert.AreEqual(once, twice);
        }

        /// <summary>
        /// A "[]" that is not the whole document is real content -- someone's deliberate empty
        /// value -- and must be left exactly where it is.
        /// </summary>
        [Test]
        public void Upsert_LeavesAnEmptySequenceThatIsNotTheWholeDocument()
        {
            const string inUse =
                "- id: recall-unread\n" +
                "  config:\n" +
                "    tags: []\n";

            var updated = FunplayDeepSeekHarnessPatch.UpsertManagedBlock(inUse, Key, Url);

            Assert.That(updated.StartsWith(inUse, StringComparison.Ordinal), Is.True);
            StringAssert.Contains("    tags: []", updated);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var offset = 0;
            while ((offset = haystack.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += needle.Length;
            }

            return count;
        }
    }
}
