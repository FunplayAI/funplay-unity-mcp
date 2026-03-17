// Copyright (C) GameBooom. Licensed under GPLv3.
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using UnityEditor;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("File")]
    internal static class FileFunctions
    {
        [Description("Read the contents of a file")]
        [ReadOnlyTool]
        public static string ReadFile(
            [ToolParam("Path to the file (relative to project root or absolute)")] string path)
        {
            var fullPath = ResolveProjectPath(path);
            if (!File.Exists(fullPath))
                return $"Error: File not found: {path}";

            var content = File.ReadAllText(fullPath);
            if (content.Length > 10000)
                content = content.Substring(0, 10000) + "\n... (truncated, file is " + content.Length + " chars)";

            return content;
        }

        [Description("Write content to a file (creates or overwrites)")]
        [SceneEditingTool]
        public static string WriteFile(
            [ToolParam("Path to the file")] string path,
            [ToolParam("Content to write")] string content)
        {
            var fullPath = ResolveProjectPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();
            return $"Written {content.Length} chars to {path}";
        }

        [Description("Search for files by name pattern in the project")]
        [ReadOnlyTool]
        public static string SearchFiles(
            [ToolParam("Search pattern (e.g. '*.cs', 'Player*', '*.prefab')")] string pattern,
            [ToolParam("Directory to search in", Required = false)] string directory = "Assets")
        {
            var fullPath = ResolveProjectPath(directory);
            if (!Directory.Exists(fullPath))
                return $"Error: Directory not found: {directory}";

            var files = Directory.GetFiles(fullPath, pattern, SearchOption.AllDirectories);
            if (files.Length == 0)
                return $"No files matching '{pattern}' in {directory}";

            var results = new List<string>();
            int count = 0;
            foreach (var file in files)
            {
                var relative = file.Replace(Path.GetDirectoryName(UnityEngine.Application.dataPath) + "/", "")
                    .Replace('\\', '/');
                results.Add($"  - {relative}");
                count++;
                if (count >= 100) break;
            }

            return $"Found {files.Length} files:\n{string.Join("\n", results)}" +
                   (files.Length > 100 ? $"\n... and {files.Length - 100} more" : "");
        }

        [Description("List files and directories in a directory")]
        [ReadOnlyTool]
        public static string ListDirectory(
            [ToolParam("Path to directory")] string path,
            [ToolParam("Include subdirectories", Required = false)] string recursive = "false")
        {
            var fullPath = ResolveProjectPath(path);
            if (!Directory.Exists(fullPath))
                return $"Error: Directory not found: {path}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Contents of {path}:");

            var dirs = Directory.GetDirectories(fullPath);
            foreach (var dir in dirs)
            {
                sb.AppendLine($"  [DIR] {Path.GetFileName(dir)}/");
            }

            var files = Directory.GetFiles(fullPath);
            int count = 0;
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(".")) continue;
                if (name.EndsWith(".meta")) continue;
                sb.AppendLine($"  {name}");
                count++;
                if (count >= 200) { sb.AppendLine("  ... (truncated)"); break; }
            }

            return sb.ToString();
        }

        [Description("Check if a file or directory exists")]
        [ReadOnlyTool]
        public static string Exists(
            [ToolParam("Path to check")] string path)
        {
            var fullPath = ResolveProjectPath(path);
            bool fileExists = File.Exists(fullPath);
            bool dirExists = Directory.Exists(fullPath);
            return fileExists ? "File exists" :
                   dirExists ? "Directory exists" :
                   "Does not exist";
        }

        private static string ResolveProjectPath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            return Path.Combine(projectRoot, path);
        }
    }
}
