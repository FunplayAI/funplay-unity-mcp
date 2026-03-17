// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using UnityEditor;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("Code")]
    internal static class CodeFunctions
    {
        [Description("Create a new C# script with the specified content")]
        [SceneEditingTool]
        public static string CreateScript(
            [ToolParam("Script file name (without .cs)")]
            string name,
            [ToolParam("C# source code content")] string content,
            [ToolParam("Path to save (e.g. 'Assets/Scripts/')", Required = false)]
            string save_path = "Assets/Scripts/")
        {
            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

            var fullPath = Path.Combine(save_path, $"{name}.cs");
            File.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();

            return $"Created script '{name}.cs' at {fullPath}";
        }

        [Description("Edit/replace the contents of an existing script")]
        [SceneEditingTool]
        public static string EditScript(
            [ToolParam("Path to the script file")] string path,
            [ToolParam("New full content for the script")]
            string content)
        {
            var fullPath = ResolveProjectPath(path);
            if (!File.Exists(fullPath))
                return $"Error: Script not found at '{path}'";

            File.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();

            return $"Updated script at {path}";
        }

        [Description("Patch a script by finding and replacing specific text. " +
                     "Safer than edit_script for small changes since it doesn't require sending the entire file content. " +
                     "The old_text must match exactly (including whitespace and indentation).")]
        [SceneEditingTool]
        public static string PatchScript(
            [ToolParam("Path to the script file")] string path,
            [ToolParam("Exact text to find in the file")] string old_text,
            [ToolParam("Replacement text")] string new_text,
            [ToolParam("Replace all occurrences (default: false, only first)", Required = false)]
            bool replace_all = false)
        {
            var fullPath = ResolveProjectPath(path);
            if (!File.Exists(fullPath))
                return $"Error: Script not found at '{path}'";

            var content = File.ReadAllText(fullPath);

            if (!content.Contains(old_text))
                return $"Error: Could not find the specified text in '{path}'. " +
                       "Make sure the text matches exactly, including whitespace and indentation.";

            int occurrences = 0;
            int index = 0;
            while ((index = content.IndexOf(old_text, index, StringComparison.Ordinal)) >= 0)
            {
                occurrences++;
                index += old_text.Length;
            }

            string newContent;
            if (replace_all)
            {
                newContent = content.Replace(old_text, new_text);
            }
            else
            {
                int firstIndex = content.IndexOf(old_text, StringComparison.Ordinal);
                newContent = content.Substring(0, firstIndex) +
                             new_text +
                             content.Substring(firstIndex + old_text.Length);
            }

            File.WriteAllText(fullPath, newContent);
            AssetDatabase.Refresh();

            string replacedInfo = replace_all
                ? $"Replaced all {occurrences} occurrence(s)"
                : $"Replaced first occurrence (of {occurrences} total)";

            return $"Patched script at {path}. {replacedInfo}.";
        }

        private static string ResolveProjectPath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            return Path.Combine(projectRoot, path);
        }
    }
}
