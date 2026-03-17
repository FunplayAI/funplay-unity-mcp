// Copyright (C) GameBooom. Licensed under GPLv3.
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using GameBooom.Editor.Tools;
using UnityEditor;
using UnityEditor.PackageManager;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("Package")]
    internal static class PackageFunctions
    {
        [Description("Install a Unity package by name")]
        [SceneEditingTool]
        public static string InstallPackage(
            [ToolParam("Package identifier (e.g. 'com.unity.textmeshpro', 'com.unity.cinemachine')")] string package_id)
        {
            var request = Client.Add(package_id);
            // Note: Package installation is async in Unity, we just kick it off
            return $"Package installation initiated for '{package_id}'. Check Package Manager for status.";
        }

        [Description("Remove a Unity package")]
        [SceneEditingTool]
        public static string RemovePackage(
            [ToolParam("Package name to remove")] string package_name)
        {
            var request = Client.Remove(package_name);
            return $"Package removal initiated for '{package_name}'. Check Package Manager for status.";
        }

        [Description("List all installed packages")]
        [ReadOnlyTool]
        public static string ListPackages()
        {
            var request = Client.List(true);
            // Since this is a synchronous context, read the manifest directly
            var manifestPath = "Packages/manifest.json";
            if (System.IO.File.Exists(manifestPath))
            {
                var content = System.IO.File.ReadAllText(manifestPath);
                if (content.Length > 5000)
                    content = content.Substring(0, 5000) + "\n... (truncated)";
                return $"Package manifest:\n{content}";
            }
            return "Package listing initiated. Check Package Manager window.";
        }
    }
}
