// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class ProjectSkillReferencesTests
    {
        private static readonly string[] Platforms =
            { "codex", "claude", "cursor", "opencode", "dsh", "antigravity" };
        private string projectRoot;

        [SetUp]
        public void SetUp()
        {
            projectRoot = Path.Combine(Path.GetTempPath(), "FunplaySkillReferences_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(projectRoot))
                Directory.Delete(projectRoot, true);
        }

        [TestCaseSource(nameof(Platforms))]
        public void ExportsLinkedVersionedReferencesWithoutAddingSkills(string platform)
        {
            Apply(platform);
            var files = Current(platform).Files.Where(file => file.SkillId != "project").ToArray();
            Assert.AreEqual(2, files.Length);
            Assert.IsEmpty(ProjectSkillsManager.GetOptionalSkills());
            var referenceCount = 0;
            foreach (var file in files)
            {
                var entry = File.ReadAllText(file.Path);
                var references = ProjectSkillReferences.GetForSkill(file.SkillId);
                var links = Regex.Matches(entry, @"\]\((references/[^)]+)\)")
                    .Cast<Match>().Select(match => match.Groups[1].Value).Distinct().ToArray();
                CollectionAssert.AreEquivalent(references.Keys, links);
                foreach (var reference in references)
                {
                    var path = Path.Combine(Path.GetDirectoryName(file.Path), reference.Key);
                    var content = File.ReadAllText(path);
                    StringAssert.Contains(ProjectSkillsManager.ManagedMarker, content);
                    StringAssert.Contains(file.SkillId + "@" + file.ExpectedVersion, content);
                    StringAssert.EndsWith(reference.Value, content);
                    StringAssert.Contains(ProjectSkillReferences.OfficialPluginRevision, content);
                    StringAssert.DoesNotContain(reference.Value, entry, "Conditional detail should not be inlined.");
                    referenceCount++;
                }
            }
            Assert.AreEqual(5, referenceCount);
            Assert.IsFalse(Current(platform).HasUpdates);
        }

        [Test]
        public void CombinedConfigurationExportsEveryTargetWithoutReferenceCollisions()
        {
            ProjectSkillsManager.ApplyConfiguration(projectRoot, Platforms, Array.Empty<string>());
            foreach (var platform in Platforms)
                Assert.IsFalse(Current(platform).HasUpdates, platform);
            var paths = Directory.GetFiles(projectRoot, "*.md", SearchOption.AllDirectories)
                .Where(path => path.Contains(Path.DirectorySeparatorChar + "references" + Path.DirectorySeparatorChar))
                .ToArray();
            Assert.AreEqual(30, paths.Length);
        }

        [TestCaseSource(nameof(Platforms))]
        public void ExportsInputCompatibilityAdviceWithoutChangingProjectInputConfiguration(string platform)
        {
            // Exported recommendations must not apply the recommended migration themselves.
            var settings = Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset");
            var packages = Path.Combine(projectRoot, "Packages", "manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settings));
            Directory.CreateDirectory(Path.GetDirectoryName(packages));
            const string legacySettings = "%YAML 1.1\nPlayerSettings:\n  activeInputHandler: 0\n  runInBackground: 0\n";
            const string noInputSystem = "{\"dependencies\":{}}\n";
            File.WriteAllText(settings, legacySettings);
            File.WriteAllText(packages, noInputSystem);

            Apply(platform);

            Assert.AreEqual(legacySettings, File.ReadAllText(settings));
            Assert.AreEqual(noInputSystem, File.ReadAllText(packages));
            foreach (var file in Current(platform).Files.Where(file => file.SkillId != "project"))
            {
                var content = File.ReadAllText(file.Path);
                // Verify both entry points receive the shared policy once, without duplicating prose.
                foreach (var guidance in new[]
                {
                    ProjectSkillReferences.InputBackendCompatibilityGuidance,
                    ProjectSkillReferences.BackgroundExecutionGuidance
                })
                {
                    var first = content.IndexOf(guidance, StringComparison.Ordinal);
                    Assert.GreaterOrEqual(first, 0, file.Path);
                    Assert.AreEqual(-1, content.IndexOf(guidance, first + guidance.Length,
                        StringComparison.Ordinal), file.Path);
                }
            }
        }

        [Test, Combinatorial]
        public void DetectsAndRepairsMissingOrStaleReferences(
            [Values("codex", "claude", "cursor", "opencode", "dsh", "antigravity")] string platform,
            [Values("unity-mcp-workflow", "unity-ui-composition")] string skillId,
            [Values(false, true)] bool stale)
        {
            Apply(platform);
            var primary = Current(platform).Files.Single(file => file.SkillId == skillId);
            var path = ReferencePath(primary.Path, skillId);
            var primaryContent = File.ReadAllText(primary.Path);
            if (stale)
                File.WriteAllText(path, File.ReadAllText(path)
                    .Replace(skillId + "@" + primary.ExpectedVersion, skillId + "@0.0.1"));
            else
                File.Delete(path);

            var status = Current(platform);
            Assert.IsTrue(status.HasUpdates);
            var affected = status.Files.Single(file => file.SkillId == skillId);
            Assert.AreEqual(path, affected.Path);
            Assert.AreEqual(!stale, affected.Missing);
            Assert.IsFalse(affected.Unmanaged);
            Assert.AreEqual(stale ? "0.0.1" : "missing", affected.InstalledVersion);
            Assert.IsTrue(affected.RequiresUpgrade);
            Assert.IsFalse(status.Files.Single(file => file.SkillId != skillId && file.SkillId != "project").RequiresUpgrade);

            Apply(platform);
            Assert.IsFalse(Current(platform).HasUpdates);
            Assert.AreEqual(primaryContent, File.ReadAllText(primary.Path));
            Assert.IsTrue(File.Exists(path));
        }

        [Test, Combinatorial]
        public void RejectsUnmanagedReferenceBeforeAnyConfigurationWrites(
            [Values("codex", "claude", "cursor", "opencode", "dsh", "antigravity")] string platform,
            [Values("unity-mcp-workflow", "unity-ui-composition")] string skillId)
        {
            Apply(platform);
            var entries = Current(platform).Files.ToDictionary(file => file.Path, file => File.ReadAllText(file.Path));
            var primary = Current(platform).Files.Single(file => file.SkillId == skillId);
            var path = ReferencePath(primary.Path, skillId);
            const string userContent = "# User-owned reference\nKeep this content exactly.\n";
            File.WriteAllText(path, userContent);
            var manifestPath = ProjectSkillsManager.GetManifestPath(projectRoot);
            var manifest = File.ReadAllText(manifestPath);

            Assert.IsTrue(Current(platform).Files.Single(file => file.SkillId == skillId).Unmanaged);
            CollectionAssert.Contains(ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, new[] { platform }), path);
            var error = Assert.Throws<InvalidOperationException>(() => Apply(platform));
            StringAssert.Contains(path, error.Message);
            Assert.AreEqual(userContent, File.ReadAllText(path));
            Assert.AreEqual(manifest, File.ReadAllText(manifestPath));
            foreach (var entry in entries)
                Assert.AreEqual(entry.Value, File.ReadAllText(entry.Key));
        }

        [TestCaseSource(nameof(Platforms))]
        public void UpgradeAndDisablePreserveUserFilesAndRemoveOnlyOwnedReferences(string platform)
        {
            Apply(platform);
            var ui = Current(platform).Files.Single(file => file.SkillId == "unity-ui-composition");
            var root = Path.GetDirectoryName(ui.Path);
            var reference = ReferencePath(ui.Path, ui.SkillId);
            var userNote = Path.Combine(Path.GetDirectoryName(reference), "user-notes.md");
            const string note = "Project-specific notes, not owned by Funplay.";
            File.WriteAllText(userNote, note);
            var otherProvider = Path.Combine(root, "references", "other-provider", "instructions.md");
            Directory.CreateDirectory(Path.GetDirectoryName(otherProvider));
            File.WriteAllText(otherProvider, note);

            Apply(platform);
            Assert.AreEqual(note, File.ReadAllText(userNote));
            Assert.AreEqual(note, File.ReadAllText(otherProvider));
            Assert.IsFalse(Current(platform).HasUpdates);

            // An edited reference becomes user-owned and must survive disabling as well.
            File.WriteAllText(reference, note);
            ProjectSkillsManager.ApplyConfiguration(projectRoot, Array.Empty<string>(), Array.Empty<string>());
            Assert.IsFalse(File.Exists(ui.Path));
            Assert.AreEqual(note, File.ReadAllText(reference));
            Assert.AreEqual(note, File.ReadAllText(userNote));
            Assert.AreEqual(note, File.ReadAllText(otherProvider));
            foreach (var relative in ProjectSkillReferences.GetForSkill(ui.SkillId).Keys.Skip(1))
                Assert.IsFalse(File.Exists(Path.Combine(root, relative)));
        }

        [TestCaseSource(nameof(Platforms))]
        public void UpgradesEntryOnlyInstallationFromPreviousRelease(string platform)
        {
            Apply(platform);
            foreach (var file in Current(platform).Files)
            {
                File.WriteAllText(file.Path, File.ReadAllText(file.Path)
                    .Replace("unity-mcp-workflow@1.0.8", "unity-mcp-workflow@1.0.7")
                    .Replace("unity-ui-composition@1.0.10", "unity-ui-composition@1.0.9"));
                foreach (var relative in ProjectSkillReferences.GetForSkill(file.SkillId).Keys)
                    File.Delete(Path.Combine(Path.GetDirectoryName(file.Path), relative));
            }
            Assert.IsTrue(Current(platform).HasUpdates);
            Apply(platform);
            Assert.IsFalse(Current(platform).HasUpdates);
            Assert.AreEqual(2, ProjectSkillsManager.GetBuiltInSkills().Count);
            foreach (var file in Current(platform).Files.Where(file => file.SkillId != "project"))
                Assert.IsTrue(File.Exists(ReferencePath(file.Path, file.SkillId)));
        }

        private void Apply(string platform)
        {
            ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { platform }, Array.Empty<string>());
        }

        [Test, Combinatorial]
        public void RejectsDirectoryAndAncestorFileCollisionsBeforeCreatingManifest(
            [Values("codex", "claude", "cursor", "opencode", "dsh", "antigravity")] string platform,
            [Values(false, true)] bool ancestorFile)
        {
            Apply(platform);
            var ui = Current(platform).Files.Single(file => file.SkillId == "unity-ui-composition");
            var reference = ReferencePath(ui.Path, ui.SkillId);
            var root = Path.GetDirectoryName(ui.Path);
            ProjectSkillsManager.ApplyConfiguration(projectRoot, Array.Empty<string>(), Array.Empty<string>());
            var manifestPath = ProjectSkillsManager.GetManifestPath(projectRoot);
            File.Delete(manifestPath);
            var collision = ancestorFile ? Path.Combine(root, "references") : reference;
            if (ancestorFile)
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(collision, "User-owned file blocks a generated directory.");
            }
            else
                Directory.CreateDirectory(collision);

            CollectionAssert.Contains(ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, new[] { platform }), collision);
            var error = Assert.Throws<InvalidOperationException>(() => Apply(platform));
            StringAssert.Contains(collision, error.Message);
            Assert.IsFalse(File.Exists(manifestPath));
            Assert.IsFalse(File.Exists(ui.Path));
            Assert.IsTrue(ancestorFile ? File.Exists(collision) : Directory.Exists(collision));
        }

        private ProjectSkillsManager.ProjectSkillsUpgradeStatus Current(string platform)
        {
            return ProjectSkillsManager.GetUpgradeStatus(projectRoot, ProjectSkillsManager.LoadManifest(projectRoot), platform);
        }

        private static string ReferencePath(string entryPath, string skillId)
        {
            return Path.Combine(Path.GetDirectoryName(entryPath), ProjectSkillReferences.GetForSkill(skillId).Keys.First());
        }
    }
}
