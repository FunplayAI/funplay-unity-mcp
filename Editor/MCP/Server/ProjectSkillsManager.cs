// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Funplay.Editor.DI;
using Funplay.Editor.Services;
using Funplay.Editor.Settings;
using Funplay.Editor.Tools;

namespace Funplay.Editor.MCP.Server
{
    internal static class ProjectSkillsManager
    {
        internal const string ManagedMarker = "<!-- Funplay Unity MCP managed project skills -->";

        // For shared root instructions files (CLAUDE.md / AGENTS.md) Funplay manages ONLY the
        // region between ManagedMarker (begin) and ManagedEndMarker (end); everything outside the
        // block is the user's and is never modified. ManagedMarker doubles as the begin marker so
        // IsManagedFile / version-status detection keep working unchanged.
        internal const string ManagedEndMarker = "<!-- /Funplay Unity MCP managed project skills -->";

        private const string ManifestDirectory = ".funplay/skills";
        private const string ManifestFileName = "manifest.json";
        private const string ProjectSkillVersionsMarkerPrefix = "<!-- Funplay Unity MCP project skill versions: ";
        private const string SkillVersionMarkerPrefix = "<!-- Funplay Unity MCP skill version: ";
        private const string CodexManagedNotice = "This section is managed by Funplay MCP for Unity. Everything between the begin and end markers is regenerated on each sync; edit outside this block.";
        private const string ClaudeManagedNotice = "This section is managed by Funplay MCP for Unity for Claude Code. Everything between the begin and end markers is regenerated on each sync; edit outside this block.";

        private static readonly string[] SupportedPlatforms = { "codex", "claude", "cursor" };

        private static readonly SkillDefinition[] SkillCatalog =
        {
            new SkillDefinition(
                "unity-mcp-workflow",
                "1.0.1",
                "Unity MCP Workflow",
                "Efficient workflow for using Unity MCP to edit, import, compile, inspect, and test Unity projects.",
                true,
                "Use this skill when Codex or another AI agent is working in a Unity project and needs to verify code, prefabs, UI, Play Mode behavior, screenshots, scene hierarchy, console logs, domain reloads, or MCP connection issues.",
                new[]
                {
                    "Use Unity MCP as the source of truth for Editor state, scene hierarchy, prefab references, runtime objects, compilation status, and Play Mode behavior.",
                    "Locate the real Unity project root and active scene before editing.",
                    "Inspect hierarchy, prefab paths, selected objects, and relevant component references through MCP before changing user-named objects. Treat user-provided object names as hints, not paths.",
                    "Tool returns are structured JSON: `{success, message, data}` for success and `{success: false, code, error, data}` for errors. Parse `data` and check `code` (UPPERCASE_SNAKE_CASE) for branching — do not pattern-match free-form text.",
                    "Prefer `instanceId` returned by tools for follow-up calls and pass it back with explicit `find_method=by_id`. This is more reliable than re-resolving by `name` when scenes contain duplicates.",
                    "Use the `find_method` parameter on GameObject/Component tools to choose how a target is resolved: `by_id`, `by_name`, `by_path`, `by_tag`, `by_layer`, `by_component`, or `by_id_or_name_or_path`. Default auto-detect routes integers through `by_id_or_name_or_path` (ID first, then exact name), slashed strings through `by_path`, and other strings through `by_name`. Pass explicit `by_id` when a stale or invalid ID must fail instead of falling back.",
                    "When a GameObject has multiple components of the same type, target a specific one with `component_instance_id` instead of the type name to avoid hitting the wrong component.",
                    "Set component fields with `set_component_property(ies)`: it now writes through SerializedObject, so `[SerializeField] private` fields are reachable. Pass Object references as JSON `{\"fileID\": <instanceId>}` (preferred) or `{\"assetPath\": \"Assets/...\"}`. The response reports per-field success/failure.",
                    "Inspect editor-level state through dedicated tools: `get_selection`, `set_selection`, `get_prefab_stage`, `get_active_tool`, `get_windows`, `get_tags`, `get_layers`, `get_build_settings`. Do not write `execute_code` snippets just to read this.",
                    "When no specialized MCP tool covers an editor action, try `execute_menu_item` (e.g. 'GameObject/2D Object/Sprite', 'Window/Layouts/Default', 'Edit/Project Settings...') before falling back to `execute_code`.",
                    "When Tool Exposure uses the default `core` profile, rely on the focused workflow tools: `execute_code`, recompilation, Play Mode control, hierarchy, console logs, screenshots, input simulation, and performance inspection.",
                    "When Tool Exposure uses the default `full` profile, all registered MCP tools are available. Prefer specific tools for simple scene, asset, GameObject, component, prefab, camera, UI, package, animation, file, or visual-feedback operations.",
                    "If Tool Exposure has been customized and a named tool is unavailable, adapt to the exposed tool list and report which expected tool is missing.",
                    "Never edit Unity serialized files (`.unity`, `.prefab`, `.asset`) with shell text tools or patches. Use Unity MCP or Editor APIs for scenes, prefabs, and ScriptableObject assets; shell tools may only inspect or locate these files.",
                    "Choose the correct edit surface: source files with normal repo tools, scene objects through Unity APIs and saved scenes, prefab assets through `PrefabUtility.LoadPrefabContents` and `SaveAsPrefabAsset`.",
                    "For `execute_code`, prefer the IFunplayCommand template over the legacy `static string Run()`: include `using Funplay.Editor.Tools.Scripting;`, implement `IFunplayCommand`, and use `ctx.RegisterObjectCreation`, `ctx.RegisterObjectModification`, `ctx.DestroyObject` so created/modified objects participate in editor Undo automatically. Use `ctx.Log` / `ctx.LogWarning` / `ctx.LogError` for traceable output that comes back in the response (without polluting the Unity console).",
                    "Batch related Unity-side changes in one guarded `execute_code` snippet. Null-guard every lookup, return explicit missing path/object/component messages, and include concise before/after values.",
                    "`execute_code` now refreshes the asset database and waits for compilation to finish before compiling the snippet, so external file edits are picked up automatically. For other tools that depend on the latest assemblies (e.g. `get_compilation_errors`), still call `request_recompile` after external file edits.",
                    "After code or resource edits, exit Play Mode if needed, call `request_recompile`, call `wait_for_compilation`, then read compilation errors or console errors before claiming success.",
                    "Call `wait_for_compilation` before Play Mode, screenshots, or conclusions when a previous edit has not yet been confirmed.",
                    "After `enter_play_mode`, the MCP HTTP server briefly drops while Unity reloads the domain. Before the next tool call, poll a cheap tool such as `tools/list` or `get_reload_recovery_status` until the server responds again — do not assume the connection is immediately ready.",
                    "`request_recompile` is rejected while Unity is in Play Mode — Unity does not process script compilation or domain reloads while playing. Call `exit_play_mode` first, then retry `request_recompile`.",
                    "If a request is interrupted by script recompilation or domain reload, treat the result as unknown until `get_reload_recovery_status`, compilation checks, and MCP readback confirm the final state.",
                    "Read back exact values from Unity after changes, not only success messages.",
                    "Test actual behavior in Unity through hierarchy, console logs, Play Mode, UI interactions, screenshots, or targeted `execute_code` checks.",
                    "When Unity readback and text files disagree for serialized scene or prefab state, trust Unity readback and investigate the asset path.",
                    "Do not run self-healing fallback loops. If a reference, path, tool, or package is missing, report one clear error and stop or skip that item instead of guessing new paths or silently creating replacements.",
                    "For `UnityEngine.Object` references, never use `??=` for lazy rebinding. Use explicit `if (field == null) field = Resolve();` checks so Unity fake-null references are handled correctly.",
                    "If Play Mode is entered, exit Play Mode before finishing unless the user explicitly wants it left running."
                }),
        };

        internal static IReadOnlyList<SkillDefinition> GetBuiltInSkills()
        {
            return SkillCatalog.Where(skill => skill.IsBuiltIn).ToArray();
        }

        internal static IReadOnlyList<SkillDefinition> GetOptionalSkills()
        {
            return SkillCatalog.Where(skill => !skill.IsBuiltIn).ToArray();
        }

        internal static IReadOnlyList<string> GetSupportedPlatforms()
        {
            return SupportedPlatforms;
        }

        internal static ProjectSkillsManifest LoadManifest(string projectRoot)
        {
            var manifestPath = GetManifestPath(projectRoot);
            try
            {
                if (File.Exists(manifestPath))
                {
                    var json = File.ReadAllText(manifestPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonUtility.FromJson<ProjectSkillsManifest>(json);
                        if (loaded != null)
                            return NormalizeManifest(loaded);
                    }
                }
            }
            catch
            {
            }

            return CreateDefaultManifest();
        }

        internal static void SaveManifest(string projectRoot, ProjectSkillsManifest manifest)
        {
            var normalized = NormalizeManifest(manifest);
            normalized.skillVersions = BuildCurrentSkillVersionEntries(normalized);
            var manifestPath = GetManifestPath(projectRoot);
            var directory = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(manifestPath, JsonUtility.ToJson(normalized, true));
        }

        internal static string GetManifestPath(string projectRoot)
        {
            return Path.Combine(projectRoot, ManifestDirectory, ManifestFileName);
        }

        internal static string GetCodexAgentsPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "AGENTS.md");
        }

        internal static string GetClaudeInstructionsPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "CLAUDE.md");
        }

        internal static string GetCursorRulesPath(string projectRoot)
        {
            return Path.Combine(projectRoot, ".cursor", "rules");
        }

        internal static string GetCodexSkillsRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, ".codex", "skills");
        }

        internal static string GetClaudeSkillsRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, ".claude", "skills");
        }

        internal static void ApplyConfiguration(string projectRoot, IEnumerable<string> selectedPlatforms, IEnumerable<string> selectedOptionalSkills)
        {
            var manifest = new ProjectSkillsManifest
            {
                platforms = selectedPlatforms?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
                optionalSkills = selectedOptionalSkills?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>()
            };

            SaveManifest(projectRoot, manifest);

            var normalized = LoadManifest(projectRoot);
            SyncCodex(projectRoot, normalized);
            SyncClaude(projectRoot, normalized);
            SyncCursor(projectRoot, normalized);
        }

        internal static bool IsPlatformConfigured(string projectRoot, string platformId)
        {
            var manifest = LoadManifest(projectRoot);
            return manifest.platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase);
        }

        internal static IReadOnlyList<SkillDefinition> GetInstalledSkills(ProjectSkillsManifest manifest)
        {
            var installedIds = new HashSet<string>(
                GetBuiltInSkills().Select(skill => skill.Id),
                StringComparer.OrdinalIgnoreCase);

            if (manifest?.optionalSkills != null)
            {
                foreach (var id in manifest.optionalSkills)
                    installedIds.Add(id);
            }

            return SkillCatalog.Where(skill => installedIds.Contains(skill.Id)).ToArray();
        }

        internal static ProjectSkillsUpgradeStatus GetUpgradeStatus(
            string projectRoot,
            ProjectSkillsManifest manifest,
            string platformId)
        {
            var normalized = NormalizeManifest(manifest);
            if (string.IsNullOrEmpty(projectRoot) ||
                string.IsNullOrEmpty(platformId) ||
                !normalized.platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase))
            {
                return new ProjectSkillsUpgradeStatus(Array.Empty<SkillFileVersionStatus>());
            }

            var expectedFiles = GetExpectedVersionedFilesForPlatform(projectRoot, normalized, platformId);
            var entries = new List<SkillFileVersionStatus>(expectedFiles.Count);
            foreach (var expected in expectedFiles)
            {
                entries.Add(InspectVersionedFile(expected));
            }

            return new ProjectSkillsUpgradeStatus(entries);
        }

        internal static bool IsManagedFile(string path)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).Contains(ManagedMarker, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        internal static string[] GetPlatformConflictPaths(string projectRoot, IEnumerable<string> selectedPlatforms)
        {
            var conflicts = new List<string>();
            var platforms = new HashSet<string>(selectedPlatforms ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            // AGENTS.md and CLAUDE.md are shared files. Their Funplay block is appended when no
            // managed marker exists, so user-owned content there is no longer an overwrite conflict.
            // Cursor rules remain Funplay-namespaced whole files and still need conflict handling.
            if (platforms.Contains("cursor"))
            {
                var rulesRoot = GetCursorRulesPath(projectRoot);
                foreach (var skill in SkillCatalog)
                {
                    var path = Path.Combine(rulesRoot, $"funplay-{skill.Id}.mdc");
                    if (File.Exists(path) && !IsManagedFile(path))
                        conflicts.Add(path);
                }
            }

            return conflicts.ToArray();
        }

        internal static IReadOnlyList<string> GetGeneratedPathsForPlatform(string projectRoot, ProjectSkillsManifest manifest, string platformId)
        {
            var enabled = manifest != null && manifest.platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase);
            if (!enabled)
                return Array.Empty<string>();

            var paths = new List<string> { GetManifestPath(projectRoot) };

            switch (platformId?.Trim().ToLowerInvariant())
            {
                case "codex":
                    paths.Add(GetCodexAgentsPath(projectRoot));
                    paths.Add(GetCodexSkillsRoot(projectRoot));
                    break;
                case "claude":
                    paths.Add(GetClaudeInstructionsPath(projectRoot));
                    paths.Add(GetClaudeSkillsRoot(projectRoot));
                    break;
                case "cursor":
                    paths.Add(GetCursorRulesPath(projectRoot));
                    break;
            }

            return paths;
        }

        private static void SyncCodex(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("codex", StringComparer.OrdinalIgnoreCase);
            var agentsPath = GetCodexAgentsPath(projectRoot);
            var skillsRoot = GetCodexSkillsRoot(projectRoot);

            if (!enabled)
            {
                RemoveManagedBlock(agentsPath, "# AGENTS.md", BuildLegacyCodexAgentsContent(projectRoot, manifest));
                DeleteManagedSkillDirectories(skillsRoot);
                return;
            }

            Directory.CreateDirectory(skillsRoot);

            // Manage only the delimited begin..end block; hand-authored content elsewhere in
            // AGENTS.md is preserved (created/appended if the file has no block yet).
            WriteManagedBlock(
                agentsPath,
                "# AGENTS.md",
                BuildCodexManagedBlock(projectRoot, manifest),
                BuildLegacyCodexAgentsContent(projectRoot, manifest));

            WriteManagedSkillDirectories(skillsRoot, manifest, SkillPlatform.Codex);
        }

        private static void SyncClaude(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("claude", StringComparer.OrdinalIgnoreCase);
            var claudePath = GetClaudeInstructionsPath(projectRoot);
            var skillsRoot = GetClaudeSkillsRoot(projectRoot);

            if (!enabled)
            {
                RemoveManagedBlock(claudePath, "# CLAUDE.md", BuildLegacyClaudeInstructionsContent(projectRoot, manifest));
                DeleteManagedSkillDirectories(skillsRoot);
                return;
            }

            Directory.CreateDirectory(skillsRoot);

            // Manage only the delimited begin..end block; hand-authored content elsewhere in
            // CLAUDE.md is preserved (created/appended if the file has no block yet).
            WriteManagedBlock(
                claudePath,
                "# CLAUDE.md",
                BuildClaudeManagedBlock(projectRoot, manifest),
                BuildLegacyClaudeInstructionsContent(projectRoot, manifest));

            WriteManagedSkillDirectories(skillsRoot, manifest, SkillPlatform.Claude);
        }

        private static void SyncCursor(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("cursor", StringComparer.OrdinalIgnoreCase);
            var rulesRoot = GetCursorRulesPath(projectRoot);

            if (!enabled)
            {
                DeleteManagedCursorRules(rulesRoot);
                return;
            }

            Directory.CreateDirectory(rulesRoot);
            WriteManagedCursorRules(rulesRoot, manifest);
        }

        private static void WriteManagedSkillDirectories(string skillsRoot, ProjectSkillsManifest manifest, SkillPlatform platform)
        {
            DeleteManagedSkillDirectories(skillsRoot);

            foreach (var skill in GetInstalledSkills(manifest))
            {
                var directory = Path.Combine(skillsRoot, $"funplay-{skill.Id}");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "SKILL.md"), BuildSkillDocument(skill, platform));
            }
        }

        private static void WriteManagedCursorRules(string rulesRoot, ProjectSkillsManifest manifest)
        {
            DeleteManagedCursorRules(rulesRoot);

            foreach (var skill in GetInstalledSkills(manifest))
            {
                var path = Path.Combine(rulesRoot, $"funplay-{skill.Id}.mdc");
                File.WriteAllText(path, BuildCursorRuleContent(skill));
            }
        }

        private static void DeleteManagedSkillDirectories(string skillsRoot)
        {
            if (!Directory.Exists(skillsRoot))
                return;

            foreach (var directory in Directory.GetDirectories(skillsRoot, "funplay-*", SearchOption.TopDirectoryOnly))
            {
                var skillPath = Path.Combine(directory, "SKILL.md");
                if (IsManagedFile(skillPath))
                    Directory.Delete(directory, true);
            }
        }

        private static void DeleteManagedCursorRules(string rulesRoot)
        {
            if (!Directory.Exists(rulesRoot))
                return;

            foreach (var file in Directory.GetFiles(rulesRoot, "funplay-*.mdc", SearchOption.TopDirectoryOnly))
            {
                if (IsManagedFile(file))
                    File.Delete(file);
            }
        }

        // Write `block` (which begins with ManagedMarker and ends with ManagedEndMarker) into a
        // shared root instructions file, managing ONLY the delimited region and preserving all
        // user content outside it:
        //   - file absent/empty        -> create `defaultTitle` + block
        //   - complete begin..end block -> replace just the block region in place
        //   - begin marker, no end      -> migrate an exact legacy generated file; otherwise stop
        //                                  with a manual-migration error rather than clobber content
        //   - no begin marker           -> append the block below the existing user content
        private static void WriteManagedBlock(string path, string defaultTitle, string block, string legacyContent)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, defaultTitle + "\n\n" + block + "\n");
                return;
            }

            var content = File.ReadAllText(path);
            var beginCount = CountOccurrences(content, ManagedMarker);
            var endCount = CountOccurrences(content, ManagedEndMarker);
            var begin = content.IndexOf(ManagedMarker, StringComparison.Ordinal);
            var end = begin >= 0
                ? content.IndexOf(ManagedEndMarker, begin + ManagedMarker.Length, StringComparison.Ordinal)
                : -1;

            if (beginCount == 1 && endCount == 1 && end > begin)
            {
                var prefix = content.Substring(0, begin);
                var suffix = content.Substring(end + ManagedEndMarker.Length);
                File.WriteAllText(path, prefix + block + suffix);
                return;
            }

            if (beginCount == 1 && endCount == 0)
            {
                if (ManagedTextEquals(content, legacyContent))
                {
                    File.WriteAllText(path, defaultTitle + "\n\n" + block + "\n");
                    return;
                }

                throw new InvalidOperationException(
                    $"'{path}' contains a legacy Funplay begin marker without an end marker and no longer exactly matches the known generated file. No content was changed. Preserve any hand-authored text, then either remove the legacy begin marker so Funplay can append a new block, or add '{ManagedEndMarker}' immediately after the Funplay-managed section.");
            }

            if (beginCount != 0 || endCount != 0)
                throw new InvalidOperationException(
                    $"'{path}' contains duplicate, unmatched, or out-of-order Funplay managed markers. No content was changed. Keep exactly one '{ManagedMarker}' followed by one '{ManagedEndMarker}'.");

            var trimmed = content.TrimEnd('\n', '\r', ' ', '\t');
            var userContent = trimmed.Length == 0 ? defaultTitle : trimmed;
            File.WriteAllText(path, userContent + "\n\n" + block + "\n");
        }

        // Inverse of WriteManagedBlock, used when a platform is disabled: strip ONLY the managed
        // block and keep the user's content. If nothing but a bare title (or whitespace) remains,
        // the file was Funplay-only, so delete it. Exact legacy generated files can also be safely
        // deleted; edited legacy files are left untouched and reported for manual cleanup.
        private static void RemoveManagedBlock(string path, string defaultTitle, string legacyContent)
        {
            if (!File.Exists(path))
                return;

            var content = File.ReadAllText(path);
            var beginCount = CountOccurrences(content, ManagedMarker);
            var endCount = CountOccurrences(content, ManagedEndMarker);
            var begin = content.IndexOf(ManagedMarker, StringComparison.Ordinal);
            var end = begin >= 0
                ? content.IndexOf(ManagedEndMarker, begin + ManagedMarker.Length, StringComparison.Ordinal)
                : -1;

            if (beginCount == 0 && endCount == 0)
                return;

            if (beginCount == 1 && endCount == 0)
            {
                if (ManagedTextEquals(content, legacyContent))
                {
                    File.Delete(path);
                    return;
                }

                throw new InvalidOperationException(
                    $"'{path}' contains an edited legacy Funplay file with no end marker. It was left untouched while disabling Project Skills. Preserve any hand-authored text and remove the stale Funplay section manually.");
            }

            if (beginCount != 1 || endCount != 1 || end <= begin)
                throw new InvalidOperationException(
                    $"'{path}' contains duplicate, unmatched, or out-of-order Funplay managed markers. It was left untouched while disabling Project Skills.");

            var prefix = content.Substring(0, begin);
            var suffix = content.Substring(end + ManagedEndMarker.Length);
            var remaining = (prefix + suffix).Trim();

            if (remaining.Length == 0 || remaining == defaultTitle.Trim())
            {
                File.Delete(path);
                return;
            }

            File.WriteAllText(path, prefix.TrimEnd() + "\n" + suffix.TrimStart('\n', '\r'));
        }

        private static int CountOccurrences(string content, string marker)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(marker))
                return 0;

            var count = 0;
            var index = 0;
            while ((index = content.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += marker.Length;
            }
            return count;
        }

        private static bool ManagedTextEquals(string left, string right)
        {
            return string.Equals(NormalizeManagedText(left), NormalizeManagedText(right), StringComparison.Ordinal);
        }

        private static string NormalizeManagedText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .TrimEnd('\n', ' ', '\t');
        }

        private static List<SkillVersionEntry> BuildCurrentSkillVersionEntries(ProjectSkillsManifest manifest)
        {
            return GetInstalledSkills(manifest)
                .OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                .Select(skill => new SkillVersionEntry { id = skill.Id, version = skill.Version })
                .ToList();
        }

        private static string BuildSkillVersionSignature(SkillDefinition skill)
        {
            return $"{skill.Id}@{skill.Version}";
        }

        private static string BuildSkillVersionMarker(SkillDefinition skill)
        {
            return $"{SkillVersionMarkerPrefix}{BuildSkillVersionSignature(skill)} -->";
        }

        private static string BuildProjectSkillVersionsSummary(IEnumerable<SkillDefinition> skills)
        {
            return string.Join(
                ", ",
                (skills ?? Array.Empty<SkillDefinition>())
                    .OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(BuildSkillVersionSignature));
        }

        private static string BuildProjectSkillVersionsMarker(IEnumerable<SkillDefinition> skills)
        {
            return $"{ProjectSkillVersionsMarkerPrefix}{BuildProjectSkillVersionsSummary(skills)} -->";
        }

        private static List<ExpectedSkillVersionFile> GetExpectedVersionedFilesForPlatform(
            string projectRoot,
            ProjectSkillsManifest manifest,
            string platformId)
        {
            var skills = GetInstalledSkills(manifest).ToArray();
            var result = new List<ExpectedSkillVersionFile>();

            switch (platformId?.Trim().ToLowerInvariant())
            {
                case "codex":
                    AddProjectVersionFile(result, GetCodexAgentsPath(projectRoot), skills);
                    foreach (var skill in skills)
                    {
                        result.Add(new ExpectedSkillVersionFile(
                            Path.Combine(GetCodexSkillsRoot(projectRoot), $"funplay-{skill.Id}", "SKILL.md"),
                            skill.Id,
                            skill.Version,
                            BuildSkillVersionMarker(skill)));
                    }
                    break;
                case "claude":
                    AddProjectVersionFile(result, GetClaudeInstructionsPath(projectRoot), skills);
                    foreach (var skill in skills)
                    {
                        result.Add(new ExpectedSkillVersionFile(
                            Path.Combine(GetClaudeSkillsRoot(projectRoot), $"funplay-{skill.Id}", "SKILL.md"),
                            skill.Id,
                            skill.Version,
                            BuildSkillVersionMarker(skill)));
                    }
                    break;
                case "cursor":
                    foreach (var skill in skills)
                    {
                        result.Add(new ExpectedSkillVersionFile(
                            Path.Combine(GetCursorRulesPath(projectRoot), $"funplay-{skill.Id}.mdc"),
                            skill.Id,
                            skill.Version,
                            BuildSkillVersionMarker(skill)));
                    }
                    break;
            }

            return result;
        }

        private static void AddProjectVersionFile(
            List<ExpectedSkillVersionFile> result,
            string path,
            IReadOnlyList<SkillDefinition> skills)
        {
            result.Add(new ExpectedSkillVersionFile(
                path,
                "project",
                BuildProjectSkillVersionsSummary(skills),
                BuildProjectSkillVersionsMarker(skills)));
        }

        private static SkillFileVersionStatus InspectVersionedFile(ExpectedSkillVersionFile expected)
        {
            if (!File.Exists(expected.Path))
            {
                return new SkillFileVersionStatus(
                    expected.Path,
                    expected.SkillId,
                    expected.ExpectedVersion,
                    "missing",
                    true,
                    false,
                    true);
            }

            string content;
            try
            {
                content = File.ReadAllText(expected.Path);
            }
            catch
            {
                return new SkillFileVersionStatus(
                    expected.Path,
                    expected.SkillId,
                    expected.ExpectedVersion,
                    "unreadable",
                    false,
                    true,
                    true);
            }

            var managed = content.Contains(ManagedMarker, StringComparison.Ordinal);
            if (!managed)
            {
                return new SkillFileVersionStatus(
                    expected.Path,
                    expected.SkillId,
                    expected.ExpectedVersion,
                    "unmanaged",
                    false,
                    true,
                    true);
            }

            if (expected.SkillId == "project")
            {
                var beginCount = CountOccurrences(content, ManagedMarker);
                var endCount = CountOccurrences(content, ManagedEndMarker);
                var begin = content.IndexOf(ManagedMarker, StringComparison.Ordinal);
                var end = begin >= 0
                    ? content.IndexOf(ManagedEndMarker, begin + ManagedMarker.Length, StringComparison.Ordinal)
                    : -1;
                if (beginCount != 1 || endCount != 1 || end <= begin)
                {
                    return new SkillFileVersionStatus(
                        expected.Path,
                        expected.SkillId,
                        expected.ExpectedVersion,
                        beginCount == 1 && endCount == 0 ? "legacy marker" : "invalid markers",
                        false,
                        false,
                        true);
                }
            }

            if (content.Contains(expected.ExpectedMarker, StringComparison.Ordinal))
            {
                return new SkillFileVersionStatus(
                    expected.Path,
                    expected.SkillId,
                    expected.ExpectedVersion,
                    expected.ExpectedVersion,
                    false,
                    false,
                    false);
            }

            var installedVersion = expected.SkillId == "project"
                ? ExtractMarkerValue(content, ProjectSkillVersionsMarkerPrefix)
                : ExtractSkillVersion(content, expected.SkillId);
            if (string.IsNullOrEmpty(installedVersion))
                installedVersion = "unknown";

            return new SkillFileVersionStatus(
                expected.Path,
                expected.SkillId,
                expected.ExpectedVersion,
                installedVersion,
                false,
                false,
                true);
        }

        private static string ExtractSkillVersion(string content, string skillId)
        {
            var markerValue = ExtractMarkerValue(content, SkillVersionMarkerPrefix);
            if (string.IsNullOrEmpty(markerValue))
                return null;

            var prefix = skillId + "@";
            return markerValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? markerValue.Substring(prefix.Length)
                : markerValue;
        }

        private static string ExtractMarkerValue(string content, string markerPrefix)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(markerPrefix))
                return null;

            var start = content.IndexOf(markerPrefix, StringComparison.Ordinal);
            if (start < 0)
                return null;

            start += markerPrefix.Length;
            var end = content.IndexOf(" -->", start, StringComparison.Ordinal);
            if (end < 0)
                return null;

            return content.Substring(start, end - start).Trim();
        }

        private static string BuildCodexManagedBlock(string projectRoot, ProjectSkillsManifest manifest)
        {
            var installed = GetInstalledSkills(manifest);
            return
$@"{ManagedMarker}
{BuildProjectSkillVersionsMarker(installed)}

# Funplay Unity MCP Project Guidance

{CodexManagedNotice}

## Installed project skills

{string.Join("\n", installed.Select(skill => $"- `funplay-{skill.Id}` v{skill.Version} - {skill.Description}"))}

## Codex workflow rules

- Prefer project-local Funplay skills under `.codex/skills/`.
- Use `execute_code` as the primary Unity automation tool. For new snippets, include `using Funplay.Editor.Tools.Scripting;`, implement `IFunplayCommand`, and use `ctx.RegisterObjectCreation` / `RegisterObjectModification` / `DestroyObject` so changes participate in Undo automatically.
- Confirm the Unity project root, active scene, and real object/prefab/asset path before edits. Treat user-provided object names as hints, not paths.
- Inspect Unity objects through MCP before changing user-named scene or prefab targets. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- Tool returns are structured JSON (`{{success, message, data}}` / `{{success: false, code, error, data}}`). Branch on `code`, not free-form text.
- Set component fields with `set_component_property(ies)` — it picks up `[SerializeField] private` fields and accepts Object references as `{{""fileID"": <instanceId>}}` or `{{""assetPath"": ""Assets/...""}}`.
- Read editor state through dedicated tools (`get_selection`, `get_prefab_stage`, `get_tags`, `get_layers`, `get_build_settings`); use `execute_menu_item` before falling back to ad-hoc `execute_code`.
- Never edit `.unity`, `.prefab`, or `.asset` files with shell text tools or patches; use Unity MCP / Editor APIs for scenes, prefabs, and ScriptableObject assets.
- Save only the scene or prefab assets intentionally modified, then read back exact values.
- With default `core` exposure, use the focused workflow tools. With default `full` exposure, prefer specific MCP tools for simple editor operations.
- `execute_code` refreshes the asset database and waits for compilation before running. For other tools that depend on freshly compiled code, still call `request_recompile` after external script edits.
- In `execute_code`, null-guard every lookup and return explicit missing path/object/component messages; do not run self-healing fallback loops.
- For Unity object references, do not use `??=` for lazy rebinding; use explicit `if (field == null) field = Resolve();`.
- After code or resource edits, exit Play Mode if needed, call `request_recompile`, `wait_for_compilation`, then read compilation or console errors.
- `request_recompile` is rejected while Unity is in Play Mode. Call `exit_play_mode` first, then retry.
- After `enter_play_mode`, the HTTP server briefly drops while Unity reloads the domain. Poll `tools/list` or `get_reload_recovery_status` until it responds again before issuing the next tool call.
- If recompilation triggers a domain reload or interrupts a request, treat the result as unknown until `get_reload_recovery_status`, compilation checks, and MCP readback confirm it.
- Avoid changing `Library/`, `Temp/`, `Logs/`, or `obj/`.

## Project

- Project root: `{projectRoot}`
- Product name: `{Application.productName}`

## Notes

- Re-run `Funplay > Project Skills` after changing selected skills or platforms.
{ManagedEndMarker}";
        }

        private static string BuildClaudeManagedBlock(string projectRoot, ProjectSkillsManifest manifest)
        {
            var installed = GetInstalledSkills(manifest);
            return
$@"{ManagedMarker}
{BuildProjectSkillVersionsMarker(installed)}

# Funplay Unity MCP Project Guidance

{ClaudeManagedNotice}

## Installed skills

{string.Join("\n", installed.Select(skill => $"- `{skill.Id}` v{skill.Version} - {skill.Description}"))}

## Preferred workflow

- Use Funplay MCP tools for Unity editor state and automation.
- Use `execute_code` for non-trivial Unity orchestration. For new snippets, include `using Funplay.Editor.Tools.Scripting;`, implement `IFunplayCommand`, and use `ctx.RegisterObjectCreation` / `RegisterObjectModification` / `DestroyObject` so changes participate in Undo and `ctx.Log` for traceable output.
- Confirm the Unity project root, active scene, and real object/prefab/asset path before edits. Treat user-provided object names as hints, not paths.
- Inspect Unity objects through MCP before changing user-named scene or prefab targets. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- Tool returns are structured JSON (`{{success, message, data}}` / `{{success: false, code, error, data}}`). Branch on `code`, not free-form text.
- Set component fields with `set_component_property(ies)` — it picks up `[SerializeField] private` fields and accepts Object references as `{{""fileID"": <instanceId>}}` or `{{""assetPath"": ""Assets/...""}}`.
- Read editor state through `get_selection`, `get_prefab_stage`, `get_tags`, `get_layers`, `get_build_settings`; try `execute_menu_item` before writing ad-hoc `execute_code`.
- Never edit `.unity`, `.prefab`, or `.asset` files with shell text tools or patches; use Unity MCP / Editor APIs for scenes, prefabs, and ScriptableObject assets.
- Save only the scene or prefab assets intentionally modified, then read back exact values.
- With default `core` exposure, use the focused workflow tools. With default `full` exposure, prefer specific MCP tools for simple editor operations.
- `execute_code` refreshes assets and waits for compilation before running. For other tools that depend on freshly compiled code, still call `request_recompile` after external script edits.
- In `execute_code`, null-guard every lookup and return explicit missing path/object/component messages; do not run self-healing fallback loops.
- For Unity object references, do not use `??=` for lazy rebinding; use explicit `if (field == null) field = Resolve();`.
- After code or resource edits, exit Play Mode if needed, call `request_recompile`, `wait_for_compilation`, then read compilation or console errors.
- `request_recompile` is rejected while Unity is in Play Mode. Call `exit_play_mode` first, then retry.
- After `enter_play_mode`, the HTTP server briefly drops while Unity reloads the domain. Poll `tools/list` or `get_reload_recovery_status` until it responds again before issuing the next tool call.
- If domain reload interrupts a request, treat the result as unknown until `get_reload_recovery_status`, compilation checks, and MCP readback confirm it.
- Additional installed skills are available under `.claude/skills/`.

## Project

- Project root: `{projectRoot}`
- Product name: `{Application.productName}`
{ManagedEndMarker}";
        }

        private static string BuildLegacyCodexAgentsContent(string projectRoot, ProjectSkillsManifest manifest)
        {
            var block = RemoveManagedEndMarker(BuildCodexManagedBlock(projectRoot, manifest))
                .Replace(CodexManagedNotice, "This file is managed by Funplay MCP for Unity.");
            return "# AGENTS.md\n" + block + "\n";
        }

        private static string BuildLegacyClaudeInstructionsContent(string projectRoot, ProjectSkillsManifest manifest)
        {
            var block = RemoveManagedEndMarker(BuildClaudeManagedBlock(projectRoot, manifest))
                .Replace(ClaudeManagedNotice, "This file is managed by Funplay MCP for Unity for Claude Code.");
            return "# CLAUDE.md\n" + block + "\n";
        }

        private static string RemoveManagedEndMarker(string block)
        {
            var suffix = "\n" + ManagedEndMarker;
            return block != null && block.EndsWith(suffix, StringComparison.Ordinal)
                ? block.Substring(0, block.Length - suffix.Length)
                : block;
        }

        private static string BuildCursorRuleContent(SkillDefinition skill)
        {
            var alwaysApply = skill.IsBuiltIn ? "true" : "false";
            return
$@"---
description: {skill.Description}
alwaysApply: {alwaysApply}
version: {skill.Version}
---
{ManagedMarker}
{BuildSkillVersionMarker(skill)}

# {skill.Title}

{skill.WhenToUse}

## Rules

{string.Join("\n", skill.Rules.Select(rule => $"- {rule}"))}

## Metadata

- Skill id: `{skill.Id}`
- Skill version: `{skill.Version}`
- Built-in: `{skill.IsBuiltIn}`
- Source: `https://github.com/FunplayAI/funplay-unity-mcp`
";
        }

        private static string BuildSkillDocument(SkillDefinition skill, SkillPlatform platform)
        {
            if (string.Equals(skill.Id, "unity-mcp-workflow", StringComparison.OrdinalIgnoreCase))
                return BuildUnityMcpWorkflowSkillDocument(skill, platform);

            return
$@"---
name: funplay-{skill.Id}
description: {skill.Description}
version: {skill.Version}
platform: {platform.ToString().ToLowerInvariant()}
---
{ManagedMarker}
{BuildSkillVersionMarker(skill)}

# {skill.Title}

{skill.WhenToUse}

## Rules

{string.Join("\n", skill.Rules.Select(rule => $"- {rule}"))}

## Metadata

- Original skill id: `{skill.Id}`
- Skill version: `{skill.Version}`
- Source repository: `https://github.com/FunplayAI/funplay-unity-mcp`
";
        }

        private static string BuildUnityMcpWorkflowSkillDocument(SkillDefinition skill, SkillPlatform platform)
        {
            var header =
$@"---
name: funplay-{skill.Id}
description: {skill.Description}
version: {skill.Version}
platform: {platform.ToString().ToLowerInvariant()}
---
{ManagedMarker}
{BuildSkillVersionMarker(skill)}

# {skill.Title}

{skill.WhenToUse}
";

            var body =
@"
## Operating Loop

1. Establish context.
   - Confirm the Unity project root and active scene.
   - Check that Unity MCP is reachable before assuming Editor state.
   - Inspect hierarchy, prefab paths, selected objects, and relevant component references through MCP.
   - If the user names an object, treat the name as a hint and verify the real Unity object path before editing.
2. Choose the edit surface.
   - Edit source files with normal repo tools, then trigger Unity recompilation.
   - Edit scene objects through Unity APIs, mark the scene dirty, and save the scene.
   - Edit prefab assets with `PrefabUtility.LoadPrefabContents`, `PrefabUtility.SaveAsPrefabAsset`, and `PrefabUtility.UnloadPrefabContents`.
   - Edit ScriptableObject assets through `SerializedObject`, `EditorUtility.SetDirty`, and `AssetDatabase.SaveAssetIfDirty` / `SaveAssets`.
   - Never patch `.unity`, `.prefab`, or `.asset` YAML with shell text tools.
   - If the user is looking at an open scene instance, update the visible scene instance as well as the prefab asset when appropriate.
3. Execute changes.
   - Prefer one well-guarded `execute_code` batch over many fragile UI clicks.
   - Use null guards for every object, component, asset, and path lookup.
   - Return explicit missing-path/object/component messages that include the expected path and the scene or prefab searched.
   - Return concise before/after values from snippets.
   - Save only the assets or scenes intentionally modified.
   - Do not run self-healing fallback loops; if a reference, path, package, or tool is missing, report it once and stop or skip that item.
4. Validate.
   - Read back the changed objects through MCP.
   - For code or resource edits, exit Play Mode if needed, call `request_recompile`, call `wait_for_compilation`, then inspect compilation errors and console errors.
   - For runtime behavior, enter Play Mode or inspect live objects when needed.
   - If MCP is unreachable, do not claim scene, prefab, asset, or runtime verification.
   - Report exactly what was verified and what still requires device, store, network, or manual validation.

## Unity Serialized Asset Safety

- Do not use shell text tools, scripts, or patches to modify `.unity`, `.prefab`, or `.asset` files. These are Unity-owned serialized assets; changing them outside Unity can corrupt file IDs, prefab overrides, references, import state, or scene dirtiness.
- Shell tools may inspect or locate serialized Unity assets, but scene, prefab, and ScriptableObject modifications must go through Unity MCP tools or Editor APIs.
- For scenes, modify live objects through Unity APIs, mark only the touched scene dirty, and save that scene.
- For prefabs, use Prefab Mode tools or `PrefabUtility.LoadPrefabContents` / `SaveAsPrefabAsset` / `UnloadPrefabContents`.
- For ScriptableObjects or other `.asset` files, load the asset with `AssetDatabase`, modify serialized properties through `SerializedObject` when possible, mark that asset dirty, and save only that asset.
- If Unity readback and raw file text disagree, trust Unity readback and investigate the asset path instead of hand-editing YAML.

## Tool Exposure

- With the default `core` profile, rely on the focused workflow tools: `execute_code`, recompilation, Play Mode control, hierarchy, console logs, screenshots, input simulation, and performance inspection.
- With the default `full` profile, prefer specific MCP tools for simple scene, asset, GameObject, component, prefab, camera, UI, package, animation, file, or visual-feedback operations.
- If Tool Exposure is customized and a named tool is unavailable, adapt to the exposed tool list and report which expected tool is missing.

## MCP Call Pattern

If native MCP tools are not directly available, probe the local HTTP endpoint:

```bash
curl -sS -m 1 -X POST http://127.0.0.1:8765/mcp \
  -H 'Content-Type: application/json' \
  -d '{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/list""}'
```

For multi-line `execute_code` calls over curl, generate JSON with a real encoder instead of hand-escaping C#:

```bash
node - <<'NODE'
const code = String.raw`
using UnityEngine;

public class InspectSomething
{
    public static string Run()
    {
        var obj = GameObject.Find(""PracticeInGameUiRoot"");
        return obj != null ? obj.name : ""not found"";
    }
}
`;
const payload = {
  jsonrpc: ""2.0"",
  id: 1,
  method: ""tools/call"",
  params: { name: ""execute_code"", arguments: { code } }
};
process.stdout.write(JSON.stringify(payload));
NODE
```

## Recommended `execute_code` Template

For non-trivial snippets, prefer `IFunplayCommand` over the legacy `public static string Run()` template. `execute_code` auto-adds `using Funplay.Editor.Tools.Scripting;` when `IFunplayCommand` is used, but include it explicitly in generated snippets for readability:

```csharp
using Funplay.Editor.Tools.Scripting;
using UnityEngine;

public class CommandScript : IFunplayCommand
{
    public void Execute(ExecutionContext ctx)
    {
        var root = GameObject.Find(""PracticeInGameUiRoot"");
        if (root == null)
        {
            ctx.LogWarning(""PracticeInGameUiRoot not found"");
            ctx.ReturnValue = ""missing root"";
            return;
        }

        ctx.RegisterObjectModification(root);
        ctx.Log(""Found {0}, active={1}"", root.name, root.activeInHierarchy);
        ctx.ReturnValue = new
        {
            name = root.name,
            active = root.activeInHierarchy
        };
    }
}
```

Use `ctx.RegisterObjectCreation(obj)`, `ctx.RegisterObjectModification(obj)`, and `ctx.DestroyObject(obj)` instead of direct Undo calls when possible. Use `ctx.Log`, `ctx.LogWarning`, and `ctx.LogError` for output returned in the MCP response without polluting the Unity Console.

## Unity C# Patterns

Add explicit `using` directives or use fully qualified types for project code. `execute_code` does not auto-inject project namespaces by default:

```csharp
var root = UnityEngine.GameObject.Find(""PracticeInGameUiRoot"");
var rect = root.GetComponent<UnityEngine.RectTransform>();
```

Use Unity null semantics for `UnityEngine.Object` references:

```csharp
if (image == null)
{
    return ""Image missing"";
}
```

Do not use `??=` to lazily resolve or rebind `UnityEngine.Object` references. Unity's destroyed or unbound serialized references can be fake-null: `field == null` returns true through Unity's overloaded operator, while C# `??=` can still treat the managed wrapper as non-null and skip the fallback assignment. Use an explicit Unity-null check instead:

```csharp
if (_hud == null)
{
    _hud = GetComponentInChildren<MyHud>(true);
}
```

For prefab edits:

```csharp
var path = ""Assets/MyGame/UI/Prefabs/PF_PracticeInGameUiRoot.prefab"";
var prefab = UnityEditor.PrefabUtility.LoadPrefabContents(path);
try
{
    var target = prefab.transform.Find(""SafeArea/SwingCancelZone"");
    if (target == null)
    {
        return ""SwingCancelZone not found in prefab"";
    }

    var rect = target.GetComponent<UnityEngine.RectTransform>();
    var before = rect.anchoredPosition;
    rect.anchoredPosition = new UnityEngine.Vector2(-76f, 448f);

    UnityEditor.EditorUtility.SetDirty(rect);
    UnityEditor.PrefabUtility.SaveAsPrefabAsset(prefab, path);
    UnityEditor.AssetDatabase.SaveAssets();
    return ""Prefab saved: pos "" + before + "" -> "" + rect.anchoredPosition;
}
finally
{
    UnityEditor.PrefabUtility.UnloadPrefabContents(prefab);
}
```

For scene edits:

```csharp
var obj = UnityEngine.GameObject.Find(""PracticeInGameUiRoot/SafeArea/SwingCancelZone"");
if (obj == null)
{
    return ""Scene object not found"";
}

var rect = obj.GetComponent<UnityEngine.RectTransform>();
var before = rect.sizeDelta;
UnityEditor.Undo.RecordObject(rect, ""Update cancel zone"");
rect.sizeDelta = new UnityEngine.Vector2(220f, 116f);
UnityEditor.EditorUtility.SetDirty(rect);
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(obj.scene);
UnityEditor.SceneManagement.EditorSceneManager.SaveScene(obj.scene);
return ""Scene saved: size "" + before + "" -> "" + rect.sizeDelta;
```

## Recompile And Reload

After external C# or asset file edits:

1. If Unity is in Play Mode, call `exit_play_mode` first — `request_recompile` is rejected during play because Unity does not run script compilation or domain reloads while playing.
2. Call `request_recompile`.
3. Call `wait_for_compilation`.
4. Read `get_compilation_errors` and `get_console_logs` errors before continuing.
5. If a domain reload drops or interrupts the request, call `get_reload_recovery_status` when available, re-scan the MCP endpoint if needed, then continue from `wait_for_compilation`.

Do not treat a disconnected, interrupted, or domain-reload-recovered request as a successful compile or edit. It only means the state is unknown until compilation checks and MCP readback confirm the final values.

After `enter_play_mode`, the HTTP server is briefly unreachable while Unity reloads the domain. Before issuing the next tool call, poll a cheap endpoint such as `tools/list` (or `get_reload_recovery_status` if exposed) until you get a response — do not assume the connection survives the Play Mode transition.

## Verification Checklist

Use readback snippets that print exact values, not only `success`:

```csharp
var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Transform>();
UnityEngine.Transform target = null;
for (int i = 0; i < all.Length; i++)
{
    if (all[i].name == ""SwingCancelZone"")
    {
        target = all[i];
        break;
    }
}

if (target == null)
{
    return ""SwingCancelZone not found"";
}

var rect = target.GetComponent<UnityEngine.RectTransform>();
return ""path="" + target.name + ""; pos="" + rect.anchoredPosition + ""; size="" + rect.sizeDelta;
```

For UI work, verify prefab or scene hierarchy, sprite references, anchors, sorting order, active state, text fit, and button listeners. A populated `Content` hierarchy does not prove the user can see the UI.

For gameplay or network work, verify object identity, ownership, live instance existence, transform values, animation state, visibility, and whether client-side filters are discarding valid data.

## Failure Handling

- If MCP is unreachable, say so and fall back only to safe filesystem inspection or code edits. Do not claim scene, prefab, or runtime verification without Unity readback.
- If an object lookup fails, inspect hierarchy and prefab contents instead of inventing a path.
- If multiple matching objects exist, print their paths and choose the one matching the user-visible UI or current scene.
- If a reference, package, tool, or path is missing, return one clear error and stop or skip that item. Do not loop through guessed fallback paths, create replacement objects silently, or report success after a best-effort fallback.
- If compile errors appear after a change, fix them before Play Mode validation.
- When Unity and text files disagree for serialized scene or prefab state, trust Unity readback and inspect the asset path.
";

            var footer =
$@"
## Metadata

- Original skill id: `{skill.Id}`
- Skill version: `{skill.Version}`
- Source repository: `https://github.com/FunplayAI/funplay-unity-mcp`
";

            return header + body + footer;
        }

        private static ProjectSkillsManifest CreateDefaultManifest()
        {
            return new ProjectSkillsManifest
            {
                platforms = new List<string>(),
                optionalSkills = new List<string>()
            };
        }

        private static ProjectSkillsManifest NormalizeManifest(ProjectSkillsManifest manifest)
        {
            manifest ??= CreateDefaultManifest();
            manifest.platforms ??= new List<string>();
            manifest.optionalSkills ??= new List<string>();
            manifest.skillVersions ??= new List<SkillVersionEntry>();

            manifest.platforms = manifest.platforms
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(value => SupportedPlatforms.Contains(value, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var optionalIds = new HashSet<string>(
                GetOptionalSkills().Select(skill => skill.Id),
                StringComparer.OrdinalIgnoreCase);

            manifest.optionalSkills = manifest.optionalSkills
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Where(value => optionalIds.Contains(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var installedIds = new HashSet<string>(
                GetInstalledSkills(manifest).Select(skill => skill.Id),
                StringComparer.OrdinalIgnoreCase);

            manifest.skillVersions = manifest.skillVersions
                .Where(entry => entry != null &&
                                !string.IsNullOrWhiteSpace(entry.id) &&
                                !string.IsNullOrWhiteSpace(entry.version) &&
                                installedIds.Contains(entry.id))
                .GroupBy(entry => entry.id.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var first = group.First();
                    return new SkillVersionEntry { id = group.Key, version = first.version.Trim() };
                })
                .OrderBy(entry => entry.id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return manifest;
        }

        internal enum SkillPlatform
        {
            Codex,
            Claude,
            Cursor
        }

        [Serializable]
        internal sealed class ProjectSkillsManifest
        {
            public List<string> platforms = new List<string>();
            public List<string> optionalSkills = new List<string>();
            public List<SkillVersionEntry> skillVersions = new List<SkillVersionEntry>();
        }

        [Serializable]
        internal sealed class SkillVersionEntry
        {
            public string id;
            public string version;
        }

        internal sealed class SkillDefinition
        {
            public SkillDefinition(string id, string version, string title, string description, bool isBuiltIn, string whenToUse, IReadOnlyList<string> rules)
            {
                Id = id;
                Version = version;
                Title = title;
                Description = description;
                IsBuiltIn = isBuiltIn;
                WhenToUse = whenToUse;
                Rules = rules ?? Array.Empty<string>();
            }

            public string Id { get; }
            public string Version { get; }
            public string Title { get; }
            public string Description { get; }
            public bool IsBuiltIn { get; }
            public string WhenToUse { get; }
            public IReadOnlyList<string> Rules { get; }
        }

        private sealed class ExpectedSkillVersionFile
        {
            public ExpectedSkillVersionFile(string path, string skillId, string expectedVersion, string expectedMarker)
            {
                Path = path;
                SkillId = skillId;
                ExpectedVersion = expectedVersion;
                ExpectedMarker = expectedMarker;
            }

            public string Path { get; }
            public string SkillId { get; }
            public string ExpectedVersion { get; }
            public string ExpectedMarker { get; }
        }

        internal sealed class ProjectSkillsUpgradeStatus
        {
            public ProjectSkillsUpgradeStatus(IReadOnlyList<SkillFileVersionStatus> files)
            {
                Files = files ?? Array.Empty<SkillFileVersionStatus>();
                HasUpdates = Files.Any(file => file.RequiresUpgrade);
            }

            public IReadOnlyList<SkillFileVersionStatus> Files { get; }
            public bool HasUpdates { get; }
        }

        internal sealed class SkillFileVersionStatus
        {
            public SkillFileVersionStatus(
                string path,
                string skillId,
                string expectedVersion,
                string installedVersion,
                bool missing,
                bool unmanaged,
                bool requiresUpgrade)
            {
                Path = path;
                SkillId = skillId;
                ExpectedVersion = expectedVersion;
                InstalledVersion = installedVersion;
                Missing = missing;
                Unmanaged = unmanaged;
                RequiresUpgrade = requiresUpgrade;
            }

            public string Path { get; }
            public string SkillId { get; }
            public string ExpectedVersion { get; }
            public string InstalledVersion { get; }
            public bool Missing { get; }
            public bool Unmanaged { get; }
            public bool RequiresUpgrade { get; }
        }
    }
}
