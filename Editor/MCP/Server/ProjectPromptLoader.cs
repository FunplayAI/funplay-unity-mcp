// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Loads project-authored MCP prompts from `<projectRoot>/mcp-prompts/*.md`, so a project can
    /// register its OWN workflow prompts (activity/scene/build recipes) without forking the package
    /// or writing C#. This is the "project workflows stay in the project, generic ones ship in the
    /// package" extension point.
    ///
    /// File format — a front-matter fence followed by the message body template:
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
    /// values (unknown/absent placeholders are left untouched so the template author can see them).
    /// Parsing is intentionally dependency-free (no YAML library).
    /// </summary>
    internal static class ProjectPromptLoader
    {
        public const string FolderName = "mcp-prompts";

        internal sealed class ProjectPrompt
        {
            public string Name;
            public string Description;
            public List<Dictionary<string, object>> Arguments = new List<Dictionary<string, object>>();
            public string Body;

            // Interpolate {arg} placeholders for each declared argument with the caller's value.
            public string BuildText(Dictionary<string, object> arguments)
            {
                var text = Body ?? string.Empty;
                foreach (var arg in Arguments)
                {
                    var argName = arg.TryGetValue("name", out var n) ? n as string : null;
                    if (string.IsNullOrEmpty(argName))
                        continue;
                    var value = "";
                    if (arguments != null && arguments.TryGetValue(argName, out var v) && v != null)
                        value = v.ToString();
                    // Only substitute when a value was supplied; leave the placeholder otherwise so a
                    // missing required argument is visible rather than silently blanked.
                    if (!string.IsNullOrEmpty(value))
                        text = text.Replace("{" + argName + "}", value);
                }
                return text;
            }
        }

        /// <summary>
        /// Scan the project's mcp-prompts folder. Never throws — a missing folder yields an empty
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
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                try
                {
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
            for (int i = 1; i < close; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line.Substring(0, colon).Trim().ToLowerInvariant();
                var value = line.Substring(colon + 1).Trim();
                switch (key)
                {
                    case "name": prompt.Name = value; break;
                    case "description": prompt.Description = value; break;
                    case "arguments": prompt.Arguments = ParseArguments(value); break;
                }
            }

            if (string.IsNullOrEmpty(prompt.Name))
            {
                error = "front-matter is missing 'name'";
                return null;
            }

            prompt.Body = string.Join("\n", lines.Skip(close + 1)).Trim();
            if (string.IsNullOrEmpty(prompt.Description))
                prompt.Description = prompt.Name;
            return prompt;
        }

        // "activity_key(required), theme_id" -> [{name:activity_key, required:true}, {name:theme_id, required:false}]
        private static List<Dictionary<string, object>> ParseArguments(string spec)
        {
            var result = new List<Dictionary<string, object>>();
            if (string.IsNullOrWhiteSpace(spec))
                return result;

            foreach (var raw in spec.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                bool required = false;
                var paren = token.IndexOf('(');
                if (paren >= 0)
                {
                    var flags = token.Substring(paren + 1).TrimEnd(')').Trim().ToLowerInvariant();
                    required = flags == "required";
                    token = token.Substring(0, paren).Trim();
                }
                if (token.Length == 0) continue;
                result.Add(new Dictionary<string, object>
                {
                    ["name"] = token,
                    ["description"] = required ? "(required)" : "(optional)",
                    ["required"] = required
                });
            }
            return result;
        }
    }
}
