// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class ProjectSkillsManagerTests
    {
        [Test]
        public void ApplyConfiguration_WritesSkillVersionMarkers()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex", "claude" }, Array.Empty<string>());

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var status = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "codex");
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                var skillPath = GetCodexWorkflowSkillPath(projectRoot);
                var claudePath = ProjectSkillsManager.GetClaudeInstructionsPath(projectRoot);

                Assert.IsFalse(status.HasUpdates);
                Assert.IsTrue(File.Exists(agentsPath));
                Assert.IsTrue(File.Exists(skillPath));
                StringAssert.Contains("unity-mcp-workflow@1.0.3", File.ReadAllText(agentsPath));
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(agentsPath));
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(claudePath));
                var skillContent = File.ReadAllText(skillPath);
                StringAssert.Contains("- Skill version: `1.0.3`", skillContent);
                StringAssert.Contains("<!-- Funplay Unity MCP skill version: unity-mcp-workflow@1.0.3 -->", skillContent);
                StringAssert.Contains(
                    "PORT=24312 # replace with the port shown in the Funplay MCP Server window",
                    skillContent);
                StringAssert.DoesNotContain("PORT=<port shown", skillContent);

                var manifestJson = File.ReadAllText(ProjectSkillsManager.GetManifestPath(projectRoot));
                StringAssert.Contains("\"skillVersions\"", manifestJson);
                StringAssert.Contains("\"id\": \"unity-mcp-workflow\"", manifestJson);
                StringAssert.Contains("\"version\": \"1.0.3\"", manifestJson);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_WritesPrefabStructurePreservationRuleToEveryPlatform()
        {
            const string expectedRule =
                "Unless the user explicitly requests a full rebuild, preserve the existing hierarchy when editing UI or GameObject prefabs and modify only the required objects, components, and serialized fields; do not recreate the entire prefab.";
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(
                    projectRoot,
                    new[] { "codex", "claude", "cursor" },
                    Array.Empty<string>());

                var codexSkillPath = GetCodexWorkflowSkillPath(projectRoot);
                var claudeSkillPath = Path.Combine(
                    ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot),
                    "funplay-unity-mcp-workflow",
                    "SKILL.md");
                var cursorRulePath = Path.Combine(
                    ProjectSkillsManager.GetCursorRulesPath(projectRoot),
                    "funplay-unity-mcp-workflow.mdc");

                StringAssert.Contains(expectedRule, File.ReadAllText(codexSkillPath));
                StringAssert.Contains(expectedRule, File.ReadAllText(claudeSkillPath));
                StringAssert.Contains(expectedRule, File.ReadAllText(cursorRulePath));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_WritesOptionalUnityUiCompositionSkillOnlyWhenSelected()
        {
            const string skillId = "unity-ui-composition";
            var projectRoot = CreateTempProjectPath();
            var codexSkillPath = Path.Combine(
                ProjectSkillsManager.GetCodexSkillsRoot(projectRoot),
                $"funplay-{skillId}",
                "SKILL.md");
            var claudeSkillPath = Path.Combine(
                ProjectSkillsManager.GetClaudeSkillsRoot(projectRoot),
                $"funplay-{skillId}",
                "SKILL.md");
            var cursorRulePath = Path.Combine(
                ProjectSkillsManager.GetCursorRulesPath(projectRoot),
                $"funplay-{skillId}.mdc");

            try
            {
                ProjectSkillsManager.ApplyConfiguration(
                    projectRoot,
                    new[] { "codex", "claude", "cursor" },
                    Array.Empty<string>());

                Assert.IsFalse(File.Exists(codexSkillPath));
                Assert.IsFalse(File.Exists(claudeSkillPath));
                Assert.IsFalse(File.Exists(cursorRulePath));

                ProjectSkillsManager.ApplyConfiguration(
                    projectRoot,
                    new[] { "codex", "claude", "cursor" },
                    new[] { skillId });

                Assert.IsTrue(File.Exists(codexSkillPath));
                Assert.IsTrue(File.Exists(claudeSkillPath));
                Assert.IsTrue(File.Exists(cursorRulePath));

                foreach (var path in new[] { codexSkillPath, claudeSkillPath, cursorRulePath })
                {
                    var content = File.ReadAllText(path);
                    StringAssert.Contains("unity-ui-composition@1.0.0", content);
                    StringAssert.Contains("Screen.safeArea", content);
                    StringAssert.Contains("720 x 1559", content);
                    StringAssert.Contains("1559 x 720", content);
                    StringAssert.Contains("RectTransformUtility.CalculateRelativeRectTransformBounds", content);
                    StringAssert.Contains("Do not recreate an entire UI or GameObject prefab", content);
                    StringAssert.Contains("Official Unity References", content);
                }

                AssertStandardSkillFrontmatter(codexSkillPath);
                AssertStandardSkillFrontmatter(claudeSkillPath);

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                CollectionAssert.Contains(manifest.optionalSkills, skillId);
                Assert.IsTrue(manifest.skillVersions.Any(entry =>
                    entry.id == skillId && entry.version == "1.0.0"));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_AppendsAndUpdatesOnlyManagedBlock()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                File.WriteAllText(agentsPath, "# Team instructions\n\nKeep this before Funplay.\n");

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" }, Array.Empty<string>());
                File.AppendAllText(agentsPath, "\nKeep this after Funplay.\n");
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" }, Array.Empty<string>());

                var content = File.ReadAllText(agentsPath);
                StringAssert.Contains("# Team instructions", content);
                StringAssert.Contains("Keep this before Funplay.", content);
                StringAssert.Contains("Keep this after Funplay.", content);
                Assert.AreEqual(1, CountOccurrences(content, ProjectSkillsManager.ManagedMarker));
                Assert.AreEqual(1, CountOccurrences(content, ProjectSkillsManager.ManagedEndMarker));
                CollectionAssert.DoesNotContain(
                    ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, new[] { "codex" }),
                    agentsPath);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_MigratesExactLegacyGeneratedFile()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var manifest = CreateManifest("codex");
                ProjectSkillsManager.SaveManifest(projectRoot, manifest);
                manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                var legacy = BuildLegacyCodexContent(projectRoot, manifest).Replace("\n", "\r\n");
                File.WriteAllText(agentsPath, legacy);

                var before = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "codex");
                var projectStatus = before.Files.First(file => file.Path == agentsPath);
                Assert.IsTrue(before.HasUpdates);
                Assert.AreEqual("legacy marker", projectStatus.InstalledVersion);

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" }, Array.Empty<string>());

                var migrated = File.ReadAllText(agentsPath);
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, migrated);
                Assert.AreEqual(1, CountOccurrences(migrated, ProjectSkillsManager.ManagedMarker));
                Assert.IsFalse(ProjectSkillsManager.GetUpgradeStatus(
                    projectRoot,
                    ProjectSkillsManager.LoadManifest(projectRoot),
                    "codex").HasUpdates);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void ApplyConfiguration_RejectsEditedLegacyFileWithoutChangingIt()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var manifest = CreateManifest("codex");
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                var editedLegacy = BuildLegacyCodexContent(projectRoot, manifest) + "\n# Hand-authored notes\nKeep me.\n";
                File.WriteAllText(agentsPath, editedLegacy);

                var exception = Assert.Throws<InvalidOperationException>(() =>
                    ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" }, Array.Empty<string>()));

                StringAssert.Contains("No content was changed", exception.Message);
                Assert.AreEqual(editedLegacy, File.ReadAllText(agentsPath));
                StringAssert.DoesNotContain(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(agentsPath));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void DisablePlatform_RemovesOnlyManagedBlockAndDeletesFunplayOnlyFile()
        {
            var sharedRoot = CreateTempProjectPath();
            var funplayOnlyRoot = CreateTempProjectPath();

            try
            {
                var sharedPath = ProjectSkillsManager.GetCodexAgentsPath(sharedRoot);
                File.WriteAllText(sharedPath, "# Team instructions\nKeep me.\n");
                ProjectSkillsManager.ApplyConfiguration(sharedRoot, new[] { "codex" }, Array.Empty<string>());
                ProjectSkillsManager.ApplyConfiguration(sharedRoot, Array.Empty<string>(), Array.Empty<string>());

                var sharedContent = File.ReadAllText(sharedPath);
                StringAssert.Contains("# Team instructions", sharedContent);
                StringAssert.Contains("Keep me.", sharedContent);
                StringAssert.DoesNotContain(ProjectSkillsManager.ManagedMarker, sharedContent);
                StringAssert.DoesNotContain(ProjectSkillsManager.ManagedEndMarker, sharedContent);

                var funplayOnlyPath = ProjectSkillsManager.GetCodexAgentsPath(funplayOnlyRoot);
                ProjectSkillsManager.ApplyConfiguration(funplayOnlyRoot, new[] { "codex" }, Array.Empty<string>());
                Assert.IsTrue(File.Exists(funplayOnlyPath));
                ProjectSkillsManager.ApplyConfiguration(funplayOnlyRoot, Array.Empty<string>(), Array.Empty<string>());
                Assert.IsFalse(File.Exists(funplayOnlyPath));
            }
            finally
            {
                DeleteTempProjectPath(sharedRoot);
                DeleteTempProjectPath(funplayOnlyRoot);
            }
        }

        [Test]
        public void GetUpgradeStatus_DetectsUnversionedManagedSkillFile()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" }, Array.Empty<string>());
                var skillPath = GetCodexWorkflowSkillPath(projectRoot);
                RemoveLinesContaining(skillPath, "Funplay Unity MCP skill version:");
                RemoveLinesContaining(skillPath, "version: 1.0.3");

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var status = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "codex");
                var skillStatus = status.Files.First(file => file.Path == skillPath);

                Assert.IsTrue(status.HasUpdates);
                Assert.IsTrue(skillStatus.RequiresUpgrade);
                Assert.AreEqual("unknown", skillStatus.InstalledVersion);
                Assert.AreEqual("1.0.3", skillStatus.ExpectedVersion);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void GetUpgradeStatus_DetectsMissingGeneratedSkillFile()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" }, Array.Empty<string>());
                var skillPath = GetCodexWorkflowSkillPath(projectRoot);
                File.Delete(skillPath);

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var status = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, "codex");
                var skillStatus = status.Files.First(file => file.Path == skillPath);

                Assert.IsTrue(status.HasUpdates);
                Assert.IsTrue(skillStatus.Missing);
                Assert.AreEqual("missing", skillStatus.InstalledVersion);
                Assert.AreEqual("1.0.3", skillStatus.ExpectedVersion);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        [Test]
        public void GetPlatformConflictPaths_DetectsUnmanagedCursorRule()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var rulesRoot = ProjectSkillsManager.GetCursorRulesPath(projectRoot);
                Directory.CreateDirectory(rulesRoot);
                var path = Path.Combine(rulesRoot, "funplay-unity-mcp-workflow.mdc");
                File.WriteAllText(path, "# User-owned Cursor rule");

                var conflicts = ProjectSkillsManager.GetPlatformConflictPaths(projectRoot, new[] { "cursor" });

                CollectionAssert.Contains(conflicts, path);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        private static string GetCodexWorkflowSkillPath(string projectRoot)
        {
            return Path.Combine(
                ProjectSkillsManager.GetCodexSkillsRoot(projectRoot),
                "funplay-unity-mcp-workflow",
                "SKILL.md");
        }

        private static ProjectSkillsManager.ProjectSkillsManifest CreateManifest(params string[] platforms)
        {
            return new ProjectSkillsManager.ProjectSkillsManifest
            {
                platforms = platforms.ToList(),
                optionalSkills = new System.Collections.Generic.List<string>()
            };
        }

        private static string BuildLegacyCodexContent(
            string projectRoot,
            ProjectSkillsManager.ProjectSkillsManifest manifest)
        {
            var method = typeof(ProjectSkillsManager).GetMethod(
                "BuildLegacyCodexAgentsContent",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (string)method.Invoke(null, new object[] { projectRoot, manifest });
        }

        private static int CountOccurrences(string content, string marker)
        {
            var count = 0;
            var index = 0;
            while ((index = content.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += marker.Length;
            }
            return count;
        }

        private static void AssertStandardSkillFrontmatter(string path)
        {
            var content = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.IsTrue(content.StartsWith("---\n", StringComparison.Ordinal));
            var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            Assert.Greater(end, 0);

            var frontmatter = content.Substring(4, end - 4);
            StringAssert.Contains("name:", frontmatter);
            StringAssert.Contains("description:", frontmatter);
            StringAssert.DoesNotContain("version:", frontmatter);
            StringAssert.DoesNotContain("platform:", frontmatter);
        }

        private static void RemoveLinesContaining(string path, string text)
        {
            var lines = File.ReadAllLines(path)
                .Where(line => !line.Contains(text))
                .ToArray();
            File.WriteAllLines(path, lines);
        }

        private static string CreateTempProjectPath()
        {
            var path = Path.Combine(Path.GetTempPath(), "FunplayProjectSkillsTests_" + Guid.NewGuid().ToString("N"));
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
