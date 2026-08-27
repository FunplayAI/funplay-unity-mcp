// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor
{
    public sealed class KimiConfigPathTests
    {
        private string _sandbox;
        private string _home;
        private string _projectRoot;

        [SetUp]
        public void CreateSandbox()
        {
            _sandbox = Path.Combine(
                Path.GetTempPath(),
                "FunplayKimiConfigPathTests_" + Guid.NewGuid().ToString("N"));
            _home = Path.Combine(_sandbox, "home");
            _projectRoot = Path.Combine(_sandbox, "UnityProject");
            Directory.CreateDirectory(_home);
            Directory.CreateDirectory(_projectRoot);
        }

        [TearDown]
        public void DeleteSandbox()
        {
            if (Directory.Exists(_sandbox))
                Directory.Delete(_sandbox, recursive: true);
        }

        [Test]
        public void ModernKimiUsesTheCurrentProjectsMcpFile()
        {
            Directory.CreateDirectory(Path.Combine(_home, ".kimi-code"));
            Directory.CreateDirectory(Path.Combine(_home, ".kimi"));

            var result = FunplayMCPClientConfigPanel.GetKimiConfigPath(
                _home, _projectRoot, kimiCodeHomeOverride: null);

            Assert.AreEqual(Path.Combine(_projectRoot, ".kimi-code", "mcp.json"), result);
        }

        [Test]
        public void FreshInstallDefaultsToTheCurrentProjectFormat()
        {
            var result = FunplayMCPClientConfigPanel.GetKimiConfigPath(
                _home, _projectRoot, kimiCodeHomeOverride: null);

            Assert.AreEqual(Path.Combine(_projectRoot, ".kimi-code", "mcp.json"), result);
        }

        [Test]
        public void LegacyOnlyInstallUsesTheLegacyUserConfig()
        {
            Directory.CreateDirectory(Path.Combine(_home, ".kimi"));

            var result = FunplayMCPClientConfigPanel.GetKimiConfigPath(
                _home, _projectRoot, kimiCodeHomeOverride: null);

            Assert.AreEqual(Path.Combine(_home, ".kimi", "mcp.json"), result);
        }

        [Test]
        public void KimiCodeHomeOverrideSelectsModernProjectConfigEvenWithLegacyData()
        {
            Directory.CreateDirectory(Path.Combine(_home, ".kimi"));
            var customModernHome = Path.Combine(_sandbox, "custom-kimi-home");

            var result = FunplayMCPClientConfigPanel.GetKimiConfigPath(
                _home, _projectRoot, customModernHome);

            Assert.AreEqual(Path.Combine(_projectRoot, ".kimi-code", "mcp.json"), result);
        }

        [Test]
        public void MissingProjectRootFallsBackToTheModernUserConfig()
        {
            var customModernHome = Path.Combine(_sandbox, "custom-kimi-home");

            var result = FunplayMCPClientConfigPanel.GetKimiConfigPath(
                _home, string.Empty, customModernHome);

            Assert.AreEqual(Path.Combine(customModernHome, "mcp.json"), result);
        }
    }
}
