// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.Services;
using Funplay.Editor.Settings;
using NUnit.Framework;

namespace Funplay.Editor
{
    public sealed class SettingsControllerTests
    {
        [Test]
        public void NewSettings_EnableExecuteCodeSafetyChecksByDefault()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));

                Assert.IsTrue(controller.ExecuteCodeSafetyChecksEnabled);
                Assert.IsTrue(controller.ExecuteCodeStrictFilesystemSafetyEnabled);
                Assert.IsFalse(controller.ExecuteCodeProjectNamespaceInjectionEnabled);
                Assert.IsFalse(controller.PluginDebugLoggingEnabled);
                Assert.IsFalse(controller.MCPBrokerModeEnabled);
                Assert.AreEqual(string.Empty, controller.MCPBrokerMonoPath);
                StringAssert.Contains("\"executeCodeSafetyChecksEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeSafetyChecksConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerModeEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerMonoPath\": \"\"", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void ExistingSettingsWithoutSafetyField_MigrateToEnabledDefault()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var settingsDirectory = Path.Combine(projectPath, "UserSettings");
                Directory.CreateDirectory(settingsDirectory);
                File.WriteAllText(
                    Path.Combine(settingsDirectory, "FunplayMcpSettings.json"),
                    "{\"enabled\":false,\"port\":8765,\"toolExportProfile\":\"core\"}");

                var controller = new SettingsController(new TestApplicationPaths(projectPath));

                Assert.IsTrue(controller.ExecuteCodeSafetyChecksEnabled);
                Assert.IsTrue(controller.ExecuteCodeStrictFilesystemSafetyEnabled);
                Assert.IsFalse(controller.ExecuteCodeProjectNamespaceInjectionEnabled);
                Assert.IsFalse(controller.PluginDebugLoggingEnabled);
                Assert.IsFalse(controller.MCPBrokerModeEnabled);
                Assert.AreEqual(string.Empty, controller.MCPBrokerMonoPath);
                StringAssert.Contains("\"executeCodeSafetyChecksEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeSafetyChecksConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerModeEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerMonoPath\": \"\"", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void ExecuteCodeStrictFilesystemSafetySetting_PersistsFalseValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                controller.ExecuteCodeStrictFilesystemSafetyEnabled = false;

                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));

                Assert.IsFalse(reloaded.ExecuteCodeStrictFilesystemSafetyEnabled);
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyConfigured\": true", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void ExecuteCodeSafetyChecksSetting_PersistsFalseValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                controller.ExecuteCodeSafetyChecksEnabled = false;

                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));

                Assert.IsFalse(reloaded.ExecuteCodeSafetyChecksEnabled);
                StringAssert.Contains("\"executeCodeSafetyChecksEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeSafetyChecksConfigured\": true", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void ExecuteCodeProjectNamespaceInjectionSetting_PersistsTrueValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                controller.ExecuteCodeProjectNamespaceInjectionEnabled = true;

                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));

                Assert.IsTrue(reloaded.ExecuteCodeProjectNamespaceInjectionEnabled);
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionConfigured\": true", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void PluginDebugLoggingSetting_PersistsTrueValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                controller.PluginDebugLoggingEnabled = true;

                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));

                Assert.IsTrue(reloaded.PluginDebugLoggingEnabled);
                StringAssert.Contains("\"pluginDebugLoggingEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingConfigured\": true", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void BrokerSettings_PersistValues()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                controller.MCPBrokerModeEnabled = true;
                controller.MCPBrokerMonoPath = "  /tmp/unity-mono  ";

                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));

                Assert.IsTrue(reloaded.MCPBrokerModeEnabled);
                Assert.AreEqual("/tmp/unity-mono", reloaded.MCPBrokerMonoPath);
                StringAssert.Contains("\"mcpBrokerModeEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerMonoPath\": \"/tmp/unity-mono\"", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void NewSettings_DoNotRecordAPortChoice()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));

                // A project with no settings file yet is new: no recorded choice, so the server derives
                // a per-project port. This is the case the derived default exists for -- an upgraded
                // project keeps its old port instead (see LegacySettings_KeepTheirPortPinned...).
                Assert.IsFalse(controller.MCPServerPortConfigured);
                StringAssert.Contains("\"portConfigured\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"settingsVersion\": 1", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void MigratedPin_CanBeReleasedAndStaysReleasedAcrossReloads()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                WriteLegacySettings(projectPath, port: 8765);
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                Assert.IsTrue(controller.MCPServerPortConfigured, "Upgrade pins the existing port.");

                // The opt-in path for an upgraded project: "Use Per-Project Port". It must survive
                // reloads, or the one-shot migration would silently re-pin it on the next start.
                controller.ClearMCPServerPortOverride();

                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));
                Assert.IsFalse(reloaded.MCPServerPortConfigured);
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void LegacySettings_KeepTheirPortPinnedSoAnUpgradeMovesNothing()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                WriteLegacySettings(projectPath, port: 8765);

                var controller = new SettingsController(new TestApplicationPaths(projectPath));

                // An existing install is already serving on a port its clients are configured against,
                // so upgrading must not move it. Per-project derivation is opt-in for these projects
                // ("Use Per-Project Port"), and the default only for new ones.
                Assert.IsTrue(controller.MCPServerPortConfigured);
                Assert.AreEqual(8765, controller.MCPServerPort);
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void LegacySettingsWithCustomPort_StayPinned()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                WriteLegacySettings(projectPath, port: 9123);

                var controller = new SettingsController(new TestApplicationPaths(projectPath));

                Assert.IsTrue(controller.MCPServerPortConfigured);
                Assert.AreEqual(9123, controller.MCPServerPort);

                // Releasing the pin has to stick: the migration must not re-derive it from the port
                // value on the next load, which is what the persisted schema version guarantees.
                controller.ClearMCPServerPortOverride();
                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));
                Assert.IsFalse(reloaded.MCPServerPortConfigured);
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void WritingAPort_PinsItEvenWhenItEqualsTheOldDefault()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                controller.MCPServerPort = 8765;

                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));

                Assert.IsTrue(reloaded.MCPServerPortConfigured, "An explicitly typed port must survive as a pin.");
                Assert.AreEqual(8765, reloaded.MCPServerPort);

                reloaded.ClearMCPServerPortOverride();
                var afterClear = new SettingsController(new TestApplicationPaths(projectPath));
                Assert.IsFalse(afterClear.MCPServerPortConfigured, "Clearing the override must not be re-migrated back to a pin.");
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void ClearingThePortField_ReleasesThePinInsteadOfPinningTheOldDefault()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                controller.MCPServerPort = 9123;
                Assert.IsTrue(controller.MCPServerPortConfigured);

                // An emptied IntegerField commits 0; pinning DefaultPort there would put this project
                // back on the old shared port and collide with every other project again.
                controller.MCPServerPort = 0;

                Assert.IsFalse(controller.MCPServerPortConfigured);
                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));
                Assert.IsFalse(reloaded.MCPServerPortConfigured);
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void LastClientConfigKey_IsRecordedPerTargetSoOneRenameCleansUpEveryClient()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                Assert.AreEqual(string.Empty, controller.GetLastClientConfigKey("Claude Code"));

                // A single shared slot broke this: re-configuring Claude Code after a rename
                // overwrote the record Codex still needed to retire its own old entry.
                controller.SetLastClientConfigKey("Claude Code", "  funplay-old-name  ");
                controller.SetLastClientConfigKey("Codex", "funplay-old-name");
                controller.SetLastClientConfigKey("Claude Code", "funplay-new-name");

                var reloaded = new SettingsController(new TestApplicationPaths(projectPath));
                Assert.AreEqual("funplay-new-name", reloaded.GetLastClientConfigKey("Claude Code"));
                Assert.AreEqual("funplay-old-name", reloaded.GetLastClientConfigKey("Codex"));
                Assert.AreEqual(string.Empty, reloaded.GetLastClientConfigKey("Cursor"));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void OutOfRangePort_ReleasesThePinInsteadOfStoringAnUnbindableValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(new TestApplicationPaths(projectPath));
                controller.MCPServerPort = 9123;
                Assert.IsTrue(controller.MCPServerPortConfigured);

                // A pin past 65535 reaches TcpListener as ArgumentOutOfRangeException, classified as
                // a hard failure with no fallback -- the server would just stay down.
                controller.MCPServerPort = 80000;

                Assert.IsFalse(controller.MCPServerPortConfigured);
                Assert.AreEqual(8765, controller.MCPServerPort);
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        private static void WriteLegacySettings(string projectPath, int port)
        {
            var settingsDirectory = Path.Combine(projectPath, "UserSettings");
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(
                Path.Combine(settingsDirectory, "FunplayMcpSettings.json"),
                "{\"enabled\":true,\"port\":" + port + ",\"toolExportProfile\":\"core\"}");
        }

        private static string ReadSettingsJson(string projectPath)
        {
            return File.ReadAllText(Path.Combine(projectPath, "UserSettings", "FunplayMcpSettings.json"));
        }

        private static string CreateTempProjectPath()
        {
            var path = Path.Combine(Path.GetTempPath(), "FunplaySettingsTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempProjectPath(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private sealed class TestApplicationPaths : IApplicationPaths
        {
            public TestApplicationPaths(string projectPath)
            {
                ProjectPath = projectPath;
                AssetsPath = Path.Combine(projectPath, "Assets");
                TempPath = Path.Combine(projectPath, "Temp", "Funplay");
                DataPath = AssetsPath;
                PersistentDataPath = Path.Combine(projectPath, "PersistentData");
            }

            public string ProjectPath { get; }
            public string AssetsPath { get; }
            public string TempPath { get; }
            public string DataPath { get; }
            public string PersistentDataPath { get; }
        }
    }
}
