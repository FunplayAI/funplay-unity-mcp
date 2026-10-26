// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using Funplay.Editor.MCP.Server;
using UnityEditor.SceneManagement;

namespace Funplay.Editor.Tests
{
    // Explicit command-line test entry only. Never called on the user's interactive Editor.
    internal static class ExecutionTrustTestBootstrap
    {
        public static void Prepare()
        {
            if (!Array.Exists(Environment.GetCommandLineArgs(), a => a == "-funplayOwnedTestSession"))
                throw new InvalidOperationException("This bootstrap requires an explicitly owned test session.");
            const string scene = "Assets/Scenes/SampleScene.unity";
            if (!File.Exists(scene)) throw new FileNotFoundException("Provide a saved SampleScene in the dedicated test project.");
            EditorSceneManager.OpenScene(scene);
            var export = Environment.GetEnvironmentVariable("FUNPLAY_SKILL_EXPORT_ROOT");
            if (!string.IsNullOrEmpty(export))
            {
                Directory.CreateDirectory(Path.Combine(export, ".git"));
                ProjectSkillsManager.ApplyConfiguration(export, new[] { "codex", "claude", "cursor", "opencode", "dsh", "antigravity" }, Array.Empty<string>());
            }
        }
    }
}
