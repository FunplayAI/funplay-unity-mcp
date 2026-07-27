// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Loads project-authored MCP prompts from `<projectRoot>/mcp-prompts/*.md`, so a project can
    /// register its OWN workflow prompts (activity/scene/build recipes) without forking the package
    /// or writing C#. This is the "project workflows stay in the project, generic ones ship in the
    /// package" extension point.
    ///
    /// File format - a front-matter fence followed by the message body template:
    /// <code>
    /// ---
    /// name: force_open_activity
    /// description: Force-open a time-windowed activity in the editor for testing.
    /// arguments: activity_key(required), theme_id
    /// ---
    /// Goal: force-open activity {activity_key} (theme {theme_id}).
    /// 1. ...
    /// </code>
    /// `arguments` is a comma-separated list; a `(required)` suffix marks a required argument.
    /// The body may reference declared arguments as `{arg_name}`; GetPrompt substitutes provided
    /// values. Declared optional arguments that are omitted resolve to an empty string.
    /// Parsing is intentionally dependency-free (no YAML library).
    /// </summary>
    internal static class ProjectPromptLoader
    {
        public const string FolderName = "mcp-prompts";
        internal const int MaxPromptFiles = 100;
        internal const long MaxPromptFileBytes = 256 * 1024;
        private static readonly Regex ArgumentPlaceholderPattern = new Regex(
            @"\{([a-z][a-z0-9_-]{0,63})\}",
            RegexOptions.CultureInvariant);

        internal sealed class ProjectPrompt
        {
            public string Name;
            public string Description;
            public List<Dictionary<string, object>> Arguments = new List<Dictionary<string, object>>();
            public string Body;

            // Interpolate {arg} placeholders for each declared argument with the caller's value.
            public string BuildText(Dictionary<string, object> arguments)
            {
                var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var arg in Arguments)
                {
                    var argName = arg.TryGetValue("name", out var n) ? n as string : null;
                    if (string.IsNullOrEmpty(argName))
                        continue;
                    var value = string.Empty;
                    if (arguments != null && arguments.TryGetValue(argName, out var v) && v != null)
                        value = v.ToString();
                    replacements[argName] = value;
                }

                return ArgumentPlaceholderPattern.Replace(Body ?? string.Empty, match =>
                    replacements.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
            }
        }

        /// <summary>
        /// Scan the project's mcp-prompts folder. Never throws - a missing folder yields an empty
        /// list, and a malformed file is skipped (its parse error surfaced via <paramref name="warnings"/>).
        /// </summary>
        public static List<ProjectPrompt> Load(string projectRoot, out List<string> warnings)
        {
            warnings = new List<string>();
            var prompts = new List<ProjectPrompt>();
            if (string.IsNullOrEmpty(projectRoot))
                return prompts;

            string dir;
            try { dir = Path.Combine(projectRoot, FolderName); }
            catch { return prompts; }

            if (!Directory.Exists(dir))
                return prompts;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) { warnings.Add($"Could not list {FolderName}: {ex.Message}"); return prompts; }

            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length > MaxPromptFiles)
                warnings.Add($"Found {files.Length} prompt files; only the first {MaxPromptFiles} are loaded.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files.Take(MaxPromptFiles))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.Length > MaxPromptFileBytes)
                    {
                        warnings.Add($"{Path.GetFileName(file)}: file is {fileInfo.Length} bytes; maximum is {MaxPromptFileBytes}.");
                        continue;
                    }

                    var prompt = Parse(File.ReadAllText(file), out var parseError);
                    if (prompt == null)
                    {
                        warnings.Add($"{Path.GetFileName(file)}: {parseError}");
                        continue;
                    }
                    if (!seen.Add(prompt.Name))
                    {
                        warnings.Add($"{Path.GetFileName(file)}: duplicate prompt name '{prompt.Name}' ignored.");
                        continue;
                    }
                    prompts.Add(prompt);
                }
                catch (Exception ex)
                {
                    warnings.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            return prompts;
        }

        internal static ProjectPrompt Parse(string content, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(content))
            {
                error = "empty file";
                return null;
            }

            // Split the leading `---` front-matter fence from the body.
            var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "---")
            {
                error = "missing front-matter (file must start with a '---' line)";
                return null;
            }

            int close = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---") { close = i; break; }
            }
            if (close < 0)
            {
                error = "front-matter not closed with a second '---' line";
                return null;
            }

            var prompt = new ProjectPrompt();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < close; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line.Substring(0, colon).Trim().ToLowerInvariant();
                var value = line.Substring(colon + 1).Trim();
                if ((key == "name" || key == "description" || key == "arguments") && !seenKeys.Add(key))
                {
                    error = $"front-matter contains duplicate '{key}'";
                    return null;
                }
                switch (key)
                {
                    case "name": prompt.Name = value; break;
                    case "description": prompt.Description = value; break;
                    case "arguments":
                        prompt.Arguments = ParseArguments(value, out var argumentError);
                        if (argumentError != null)
                        {
                            error = argumentError;
                            return null;
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(prompt.Name))
            {
                error = "front-matter is missing 'name'";
                return null;
            }
            if (!IsValidIdentifier(prompt.Name))
            {
                error = "prompt name must match [a-z][a-z0-9_-]{0,63}";
                return null;
            }
            if (!string.IsNullOrEmpty(prompt.Description) && prompt.Description.Length > 512)
            {
                error = "description exceeds 512 characters";
                return null;
            }

            prompt.Body = string.Join("\n", lines.Skip(close + 1)).Trim();
            if (string.IsNullOrWhiteSpace(prompt.Body))
            {
                error = "prompt body is empty";
                return null;
            }
            if (string.IsNullOrEmpty(prompt.Description))
                prompt.Description = prompt.Name;
            return prompt;
        }

        // "activity_key(required), theme_id" -> [{name:activity_key, required:true}, {name:theme_id, required:false}]
        private static List<Dictionary<string, object>> ParseArguments(string spec, out string error)
        {
            error = null;
            var result = new List<Dictionary<string, object>>();
            if (string.IsNullOrWhiteSpace(spec))
                return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in spec.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0)
                {
                    error = "arguments contains an empty entry";
                    return null;
                }
                bool required = false;
                var paren = token.IndexOf('(');
                if (paren >= 0)
                {
                    if (!token.EndsWith(")", StringComparison.Ordinal))
                    {
                        error = $"argument '{token}' has malformed flags";
                        return null;
                    }
                    var flags = token.Substring(paren + 1).TrimEnd(')').Trim().ToLowerInvariant();
                    if (flags != "required")
                    {
                        error = $"argument '{token}' has unsupported flag '{flags}'";
                        return null;
                    }
                    required = true;
                    token = token.Substring(0, paren).Trim();
                }
                if (!IsValidIdentifier(token))
                {
                    error = $"argument name '{token}' must match [a-z][a-z0-9_-]{{0,63}}";
                    return null;
                }
                if (!seen.Add(token))
                {
                    error = $"duplicate argument name '{token}'";
                    return null;
                }
                result.Add(new Dictionary<string, object>
                {
                    ["name"] = token,
                    ["description"] = required ? "(required)" : "(optional)",
                    ["required"] = required
                });
            }
            return result;
        }

        private static bool IsValidIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64 || value[0] < 'a' || value[0] > 'z')
                return false;

            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if ((character < 'a' || character > 'z') &&
                    (character < '0' || character > '9') &&
                    character != '_' && character != '-')
                    return false;
            }
            return true;
        }
    }
}
