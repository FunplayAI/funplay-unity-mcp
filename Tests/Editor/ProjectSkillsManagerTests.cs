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
                var agentsContent = File.ReadAllText(agentsPath);
                StringAssert.Contains("unity-mcp-workflow@1.0.3", agentsContent);
                StringAssert.Contains("unity-ui-composition@1.0.3", agentsContent);
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
                StringAssert.Contains("\"id\": \"unity-ui-composition\"", manifestJson);
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
        public void ApplyConfiguration_WritesBuiltInUnityUiCompositionSkillForEveryPlatform()
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

                Assert.IsTrue(File.Exists(codexSkillPath));
                Assert.IsTrue(File.Exists(claudeSkillPath));
                Assert.IsTrue(File.Exists(cursorRulePath));

                foreach (var path in new[] { codexSkillPath, claudeSkillPath, cursorRulePath })
                {
                    var content = File.ReadAllText(path);
                    StringAssert.Contains("unity-ui-composition@1.0.3", content);
                    StringAssert.Contains("Screen.safeArea", content);
                    StringAssert.Contains("720 x 1559", content);
                    StringAssert.Contains("1559 x 720", content);
                    StringAssert.Contains("RectTransformUtility.CalculateRelativeRectTransformBounds", content);
                    StringAssert.Contains("Do not recreate an entire UI or GameObject prefab", content);
                    StringAssert.Contains("default to `TextMeshProUGUI`", content);
                    StringAssert.Contains("text component that is most common", content);
                    StringAssert.Contains("Do not add `Outline`, `Shadow`, or another `BaseMeshEffect`", content);
                    StringAssert.Contains("Use `outlineColor` and `outlineWidth` for a simple outline", content);
                    StringAssert.Contains("Do not modify a shared `fontSharedMaterial`", content);
                    StringAssert.Contains("underlay or shadow, glow, face dilation, softness", content);
                    StringAssert.Contains("Author reusable user-facing screens, panels, and controls as prefabs", content);
                    StringAssert.Contains("Only when procedural construction is explicitly required", content);
                    StringAssert.Contains("disable and re-enable the field to run initialization again", content);
                    StringAssert.Contains("Official Unity References", content);
                }

                AssertStandardSkillFrontmatter(codexSkillPath);
                AssertStandardSkillFrontmatter(claudeSkillPath);
                StringAssert.Contains("alwaysApply: true", File.ReadAllText(cursorRulePath));

                Assert.IsTrue(ProjectSkillsManager.GetBuiltInSkills().Any(skill => skill.Id == skillId));
                Assert.IsFalse(ProjectSkillsManager.GetOptionalSkills().Any(skill => skill.Id == skillId));

                // A manifest written by 0.6.1 may still submit this former optional id. It must be
                // normalized away while the skill remains installed as a built-in.
                ProjectSkillsManager.ApplyConfiguration(
                    projectRoot,
                    new[] { "codex", "claude", "cursor" },
                    new[] { skillId });

                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                CollectionAssert.DoesNotContain(manifest.optionalSkills, skillId);
                Assert.IsTrue(manifest.skillVersions.Any(entry =>
                    entry.id == skillId && entry.version == "1.0.3"));
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

        /// <summary>
        /// OpenCode only scans <c>.opencode/skills/</c> (plural) -- "It loads any matching
        /// `skills/*/SKILL.md` in `.opencode/`" per its own docs. A singular <c>skill</c> directory is
        /// never read and the miss is completely silent, so the literal path is asserted here rather
        /// than derived from the same helper the writer uses.
        /// </summary>
        [Test]
        public void ApplyConfiguration_WritesOpenCodeSkillsToThePluralDirectory()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "opencode" }, Array.Empty<string>());

                var expected = Path.Combine(
                    projectRoot, ".opencode", "skills", "funplay-unity-mcp-workflow", "SKILL.md");

                Assert.IsTrue(File.Exists(expected), expected);
                Assert.IsFalse(
                    Directory.Exists(Path.Combine(projectRoot, ".opencode", "skill")),
                    "OpenCode never scans a singular '.opencode/skill' directory.");
                Assert.AreEqual(
                    Path.Combine(projectRoot, ".opencode", "skills"),
                    ProjectSkillsManager.GetOpenCodeSkillsRoot(projectRoot));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        /// <summary>
        /// AGENTS.md is read natively by both Codex and OpenCode, so its single managed block has to
        /// outlive either platform being switched off on its own, and go away only when both are. The
        /// per-platform skill directories still have to track their own platform.
        /// </summary>
        [Test]
        public void ApplyConfiguration_KeepsSharedAgentsBlockUntilBothAgentPlatformsAreDisabled()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                var codexSkill = GetCodexWorkflowSkillPath(projectRoot);
                var openCodeSkill = Path.Combine(
                    ProjectSkillsManager.GetOpenCodeSkillsRoot(projectRoot),
                    "funplay-unity-mcp-workflow",
                    "SKILL.md");

                ProjectSkillsManager.ApplyConfiguration(
                    projectRoot, new[] { "codex", "opencode" }, Array.Empty<string>());
                Assert.IsTrue(File.Exists(agentsPath));
                Assert.IsTrue(File.Exists(codexSkill));
                Assert.IsTrue(File.Exists(openCodeSkill));

                // OpenCode off, Codex still on: shared block stays, OpenCode's own skills go.
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" }, Array.Empty<string>());
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(agentsPath));
                Assert.IsTrue(File.Exists(codexSkill));
                Assert.IsFalse(File.Exists(openCodeSkill));

                // Codex off, OpenCode on: same in the other direction.
                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "opencode" }, Array.Empty<string>());
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(agentsPath));
                Assert.IsFalse(File.Exists(codexSkill));
                Assert.IsTrue(File.Exists(openCodeSkill));

                // Both off: the Funplay-only AGENTS.md is removed.
                ProjectSkillsManager.ApplyConfiguration(projectRoot, Array.Empty<string>(), Array.Empty<string>());
                Assert.IsFalse(File.Exists(agentsPath));
                Assert.IsFalse(File.Exists(openCodeSkill));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        /// <summary>
        /// A legacy (begin-marker-only) AGENTS.md is matched against generated text, so every wording
        /// a released version produced has to stay recognised. This pins the pre-OpenCode Codex-only
        /// wording, which the managed block no longer emits.
        /// </summary>
        [Test]
        public void ApplyConfiguration_MigratesLegacyFileWrittenWithTheOlderCodexOnlyWording()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var manifest = CreateManifest("codex");
                ProjectSkillsManager.SaveManifest(projectRoot, manifest);
                manifest = ProjectSkillsManager.LoadManifest(projectRoot);

                var variants = BuildLegacyCodexContentVariants(projectRoot, manifest);
                Assert.Greater(variants.Length, 1, "The pre-OpenCode wording must still be accepted.");

                var codexOnly = variants.Last();
                StringAssert.Contains("## Codex workflow rules", codexOnly);
                StringAssert.DoesNotContain("## Agent workflow rules", codexOnly);

                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(projectRoot);
                File.WriteAllText(agentsPath, codexOnly);

                ProjectSkillsManager.ApplyConfiguration(projectRoot, new[] { "codex" }, Array.Empty<string>());

                var migrated = File.ReadAllText(agentsPath);
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, migrated);
                StringAssert.Contains("## Agent workflow rules", migrated);
                Assert.AreEqual(1, CountOccurrences(migrated, ProjectSkillsManager.ManagedMarker));
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        /// <summary>
        /// DeepSeek Harness resolves ITS project root as the nearest ancestor containing a
        /// <c>.git</c> entry and scans <c>&lt;that&gt;/.dsh/skills</c> -- so in a monorepo (git root
        /// above the Unity project folder) the skills must land at the repository root, never inside
        /// this Unity project's own directory, where DSH would silently never find them.
        /// </summary>
        [Test]
        public void ApplyConfiguration_WritesDshSkillsToTheGitRootDshDirectory()
        {
            var tempRoot = CreateTempProjectPath();
            // Anchor the walk deterministically: without a .git anywhere below, FindGitRootOrSelf
            // could escape into whatever ancestor of the temp directory happens to be a repository.
            var repoRoot = Path.Combine(tempRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            var unityProject = Path.Combine(repoRoot, "UnityProject");
            Directory.CreateDirectory(unityProject);

            try
            {
                ProjectSkillsManager.ApplyConfiguration(unityProject, new[] { "dsh" }, Array.Empty<string>());

                var expected = Path.Combine(
                    repoRoot, ".dsh", "skills", "funplay-unity-mcp-workflow", "SKILL.md");
                Assert.IsTrue(File.Exists(expected), expected);
                AssertStandardSkillFrontmatter(expected);

                Assert.IsFalse(Directory.Exists(Path.Combine(unityProject, ".dsh")),
                    "Skills belong at the git root DSH scans, not the Unity project folder.");

                // Instructions keep following the same convention as every other platform: written
                // next to the project root the panel passes. DSH's instruction loader reads
                // AGENTS.md in each directory between its project root and the session cwd, so a
                // session opened inside the Unity project folder picks it up there.
                StringAssert.Contains(
                    ProjectSkillsManager.ManagedEndMarker,
                    File.ReadAllText(ProjectSkillsManager.GetCodexAgentsPath(unityProject)),
                    "The shared AGENTS.md block is written beside the other platforms' instruction files.");
            }
            finally
            {
                DeleteTempProjectPath(tempRoot);
            }
        }

        /// <summary>
        /// AGENTS.md is shared by Codex, OpenCode and DeepSeek Harness; the managed block must stay
        /// while any one of them remains enabled, and each platform's own skill directory tracks its
        /// own toggle.
        /// </summary>
        [Test]
        public void ApplyConfiguration_DshSharesTheAgentsBlockAndTracksItsOwnSkillDirectory()
        {
            var tempRoot = CreateTempProjectPath();
            var repoRoot = Path.Combine(tempRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

            try
            {
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(repoRoot);
                var dshSkill = Path.Combine(
                    ProjectSkillsManager.GetDshSkillsRoot(repoRoot),
                    "funplay-unity-mcp-workflow",
                    "SKILL.md");
                var codexSkill = GetCodexWorkflowSkillPath(repoRoot);

                ProjectSkillsManager.ApplyConfiguration(repoRoot, new[] { "codex", "dsh" }, Array.Empty<string>());
                Assert.IsTrue(File.Exists(agentsPath));
                Assert.IsTrue(File.Exists(dshSkill));
                Assert.IsTrue(File.Exists(codexSkill));

                // Codex off, DSH still on: shared block stays, Codex's own skills go.
                ProjectSkillsManager.ApplyConfiguration(repoRoot, new[] { "dsh" }, Array.Empty<string>());
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(agentsPath));
                Assert.IsFalse(File.Exists(codexSkill));
                Assert.IsTrue(File.Exists(dshSkill));

                // All agent platforms off: the Funplay-only AGENTS.md is removed.
                ProjectSkillsManager.ApplyConfiguration(repoRoot, Array.Empty<string>(), Array.Empty<string>());
                Assert.IsFalse(File.Exists(agentsPath));
                Assert.IsFalse(File.Exists(dshSkill));
            }
            finally
            {
                DeleteTempProjectPath(tempRoot);
            }
        }

        /// <summary>
        /// The AGENTS.md wording gains a bullet every time another client joins the shared block, and
        /// each older rendering must stay recognizable or files carrying it stop migrating. Newest
        /// first: current (with Antigravity), pre-Antigravity, pre-DSH.
        /// </summary>
        [Test]
        public void LegacyVariants_StillRecognizeThePreDshWording()
        {
            var projectRoot = CreateTempProjectPath();

            try
            {
                var manifest = CreateManifest("codex", "opencode", "dsh", "antigravity");
                var variants = BuildLegacyCodexContentVariants(projectRoot, manifest);

                Assert.GreaterOrEqual(variants.Length, 4, "The pre-DSH wording must still be accepted.");

                var current = variants[0];
                StringAssert.Contains("`.dsh/skills/`", current);
                StringAssert.Contains("`.agents/skills/`", current);

                var preAntigravity = variants[1];
                StringAssert.Contains("`.dsh/skills/`", preAntigravity);
                StringAssert.DoesNotContain("Antigravity", preAntigravity);

                var preDsh = variants[2];
                StringAssert.Contains("`.opencode/skills/`", preDsh);
                StringAssert.DoesNotContain("DeepSeek Harness", preDsh);
            }
            finally
            {
                DeleteTempProjectPath(projectRoot);
            }
        }

        /// <summary>
        /// Antigravity discovers workspace customizations by walking from the session's working
        /// directory up to the repository root, so <c>.agents/skills</c> must land at the git root --
        /// in a monorepo (git root above the Unity project folder) a copy inside the Unity folder is
        /// only visible to sessions started at or below it. Same reasoning as the DSH case above.
        /// </summary>
        [Test]
        public void ApplyConfiguration_WritesAntigravitySkillsToTheGitRootAgentsDirectory()
        {
            var tempRoot = CreateTempProjectPath();
            var repoRoot = Path.Combine(tempRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            var unityProject = Path.Combine(repoRoot, "UnityProject");
            Directory.CreateDirectory(unityProject);

            try
            {
                ProjectSkillsManager.ApplyConfiguration(unityProject, new[] { "antigravity" }, Array.Empty<string>());

                var expected = Path.Combine(
                    repoRoot, ".agents", "skills", "funplay-unity-mcp-workflow", "SKILL.md");
                Assert.IsTrue(File.Exists(expected), expected);
                AssertStandardSkillFrontmatter(expected);

                Assert.IsFalse(Directory.Exists(Path.Combine(unityProject, ".agents")),
                    "Skills belong at the git root Antigravity walks up to, not the Unity project folder.");

                StringAssert.Contains(
                    ProjectSkillsManager.ManagedEndMarker,
                    File.ReadAllText(ProjectSkillsManager.GetAntigravityAgentsPath(unityProject)),
                    "Antigravity instructions must be visible beside its workspace config and skills.");
                Assert.IsFalse(File.Exists(ProjectSkillsManager.GetCodexAgentsPath(unityProject)));
                var manifest = ProjectSkillsManager.LoadManifest(unityProject);
                CollectionAssert.Contains(
                    ProjectSkillsManager.GetGeneratedPathsForPlatform(unityProject, manifest, "antigravity"),
                    Path.Combine(repoRoot, "AGENTS.md"));
                Assert.IsFalse(ProjectSkillsManager.GetUpgradeStatus(unityProject, manifest, "antigravity").HasUpdates);
            }
            finally
            {
                DeleteTempProjectPath(tempRoot);
            }
        }

        /// <summary>
        /// Antigravity shares AGENTS.md with Codex/OpenCode/DSH: the block stays while any of them is
        /// enabled, while its own skill directory follows only its own toggle.
        /// </summary>
        [Test]
        public void ApplyConfiguration_AntigravitySharesTheAgentsBlockAndTracksItsOwnSkillDirectory()
        {
            var tempRoot = CreateTempProjectPath();
            var repoRoot = Path.Combine(tempRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

            try
            {
                var agentsPath = ProjectSkillsManager.GetCodexAgentsPath(repoRoot);
                var antigravitySkill = Path.Combine(
                    ProjectSkillsManager.GetAntigravitySkillsRoot(repoRoot),
                    "funplay-unity-mcp-workflow",
                    "SKILL.md");
                var codexSkill = GetCodexWorkflowSkillPath(repoRoot);

                ProjectSkillsManager.ApplyConfiguration(
                    repoRoot, new[] { "codex", "antigravity" }, Array.Empty<string>());
                Assert.IsTrue(File.Exists(agentsPath));
                Assert.IsTrue(File.Exists(antigravitySkill));
                Assert.IsTrue(File.Exists(codexSkill));

                ProjectSkillsManager.ApplyConfiguration(repoRoot, new[] { "antigravity" }, Array.Empty<string>());
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(agentsPath));
                Assert.IsFalse(File.Exists(codexSkill));
                Assert.IsTrue(File.Exists(antigravitySkill));

                ProjectSkillsManager.ApplyConfiguration(repoRoot, Array.Empty<string>(), Array.Empty<string>());
                Assert.IsFalse(File.Exists(agentsPath));
                Assert.IsFalse(File.Exists(antigravitySkill));
            }
            finally
            {
                DeleteTempProjectPath(tempRoot);
            }
        }

        [Test]
        public void NestedAntigravityTogglePreservesUserInstructionsAndOtherPlatforms()
        {
            var tempRoot = CreateTempProjectPath();
            var repoRoot = Path.Combine(tempRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            var unityProject = Path.Combine(repoRoot, "UnityProject");
            Directory.CreateDirectory(unityProject);
            var workspaceAgents = Path.Combine(repoRoot, "AGENTS.md");
            var localAgents = ProjectSkillsManager.GetCodexAgentsPath(unityProject);
            File.WriteAllText(workspaceAgents, "# Workspace rules\nKeep this workspace guidance.\n");
            File.WriteAllText(localAgents, "# Unity rules\nKeep this Unity guidance.\n");

            try
            {
                ProjectSkillsManager.ApplyConfiguration(unityProject, new[] { "codex", "antigravity" }, Array.Empty<string>());
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(workspaceAgents));
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(localAgents));

                ProjectSkillsManager.ApplyConfiguration(unityProject, new[] { "codex" }, Array.Empty<string>());
                StringAssert.Contains("Keep this workspace guidance.", File.ReadAllText(workspaceAgents));
                StringAssert.DoesNotContain(ProjectSkillsManager.ManagedMarker, File.ReadAllText(workspaceAgents));
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(localAgents));
                Assert.IsTrue(File.Exists(GetCodexWorkflowSkillPath(unityProject)));
                Assert.IsFalse(File.Exists(Path.Combine(ProjectSkillsManager.GetAntigravitySkillsRoot(unityProject),
                    "funplay-unity-mcp-workflow", "SKILL.md")));

                ProjectSkillsManager.ApplyConfiguration(unityProject, new[] { "antigravity" }, Array.Empty<string>());
                StringAssert.Contains(ProjectSkillsManager.ManagedEndMarker, File.ReadAllText(workspaceAgents));
                StringAssert.Contains("Keep this Unity guidance.", File.ReadAllText(localAgents));
                StringAssert.DoesNotContain(ProjectSkillsManager.ManagedMarker, File.ReadAllText(localAgents));
                Assert.IsFalse(File.Exists(GetCodexWorkflowSkillPath(unityProject)));
            }
            finally
            {
                DeleteTempProjectPath(tempRoot);
            }
        }

        [Test]
        public void ConfiguringAnotherNestedProjectDoesNotRemoveWorkspaceAntigravitySkills()
        {
            var tempRoot = CreateTempProjectPath();
            var repoRoot = Path.Combine(tempRoot, "repo");
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            var first = Path.Combine(repoRoot, "First");
            var second = Path.Combine(repoRoot, "Second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            try
            {
                ProjectSkillsManager.ApplyConfiguration(first, new[] { "antigravity" }, Array.Empty<string>());
                var agentsPath = ProjectSkillsManager.GetAntigravityAgentsPath(first);
                var before = File.ReadAllText(agentsPath);
                var skillPath = Path.Combine(ProjectSkillsManager.GetAntigravitySkillsRoot(first),
                    "funplay-unity-mcp-workflow", "SKILL.md");

                ProjectSkillsManager.ApplyConfiguration(second, new[] { "codex" }, Array.Empty<string>());
                Assert.AreEqual(before, File.ReadAllText(agentsPath));
                Assert.IsTrue(File.Exists(skillPath));
                Assert.Throws<InvalidOperationException>(() =>
                    ProjectSkillsManager.ApplyConfiguration(second, new[] { "antigravity" }, Array.Empty<string>()));
                Assert.AreEqual(before, File.ReadAllText(agentsPath));
                CollectionAssert.AreEqual(new[] { "codex" }, ProjectSkillsManager.LoadManifest(second).platforms);
            }
            finally
            {
                DeleteTempProjectPath(tempRoot);
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

        private static string[] BuildLegacyCodexContentVariants(
            string projectRoot,
            ProjectSkillsManager.ProjectSkillsManifest manifest)
        {
            var method = typeof(ProjectSkillsManager).GetMethod(
                "BuildLegacyCodexAgentsContentVariants",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (string[])method.Invoke(null, new object[] { projectRoot, manifest });
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
