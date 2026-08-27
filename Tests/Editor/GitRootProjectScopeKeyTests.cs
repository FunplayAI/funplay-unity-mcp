// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor
{
    /// <summary>
    /// Covers <see cref="FunplayMCPClientConfigPanel.FindGitRootOrSelf"/>: the walk that finds the key
    /// Claude Code's <c>projects["&lt;path&gt;"]</c> config scope is actually keyed by. A Unity project
    /// living in a subdirectory of a larger git repository (a monorepo layout) has a git root above its
    /// own directory; using the Unity project's own path as the scope key instead silently writes to a
    /// key Claude Code never reads for that session, even though the config and the server both look
    /// correct.
    /// </summary>
    public sealed class GitRootProjectScopeKeyTests
    {
        private string _sandbox;

        [SetUp]
        public void CreateSandbox()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "FunplayGitRootScopeKeyTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);
        }

        [TearDown]
        public void DeleteSandbox()
        {
            if (Directory.Exists(_sandbox))
                Directory.Delete(_sandbox, recursive: true);
        }

        [Test]
        public void ReturnsTheStartDirectoryWhenItIsItselfTheGitRoot()
        {
            var unityProject = CreateDir("Repo");
            Directory.CreateDirectory(Path.Combine(unityProject, ".git"));

            Assert.AreEqual(unityProject, FunplayMCPClientConfigPanel.FindGitRootOrSelf(unityProject));
        }

        [Test]
        public void WalksUpToAnAncestorDirectoryThatHasDotGit()
        {
            // The monorepo layout this fix targets: the Unity project is a subdirectory of the git repo,
            // not the repo root itself (e.g. Repo/UnityProject with Repo/.git).
            var gitRoot = CreateDir("Repo");
            Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
            var unityProject = CreateDir("Repo/UnityProject");

            Assert.AreEqual(gitRoot, FunplayMCPClientConfigPanel.FindGitRootOrSelf(unityProject));
        }

        [Test]
        public void TreatsADotGitFileTheSameAsADotGitDirectory()
        {
            // A submodule or worktree records its git dir in a ".git" *file*, not a directory.
            var gitRoot = CreateDir("Repo");
            File.WriteAllText(Path.Combine(gitRoot, ".git"), "gitdir: ../.git/modules/Repo\n");
            var unityProject = CreateDir("Repo/UnityProject");

            Assert.AreEqual(gitRoot, FunplayMCPClientConfigPanel.FindGitRootOrSelf(unityProject));
        }

        [Test]
        public void WalksThroughMultipleAncestorsToFindTheGitRoot()
        {
            var gitRoot = CreateDir("Repo");
            Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
            var unityProject = CreateDir("Repo/Games/UnityProject");

            Assert.AreEqual(gitRoot, FunplayMCPClientConfigPanel.FindGitRootOrSelf(unityProject));
        }

        [Test]
        public void FallsBackToTheStartDirectoryWhenNoAncestorWithinTheSandboxHasDotGit()
        {
            // No ".git" anywhere under the sandbox root: the walk exits the sandbox and keeps climbing
            // real ancestors (e.g. the OS temp directory) that this test cannot control. What must hold
            // regardless of what it finds out there is that a project with none of its *own* ancestors
            // under git never throws and never returns something empty.
            var unityProject = CreateDir("NoGitAnywhereNearHere/UnityProject");

            var result = FunplayMCPClientConfigPanel.FindGitRootOrSelf(unityProject);

            Assert.IsFalse(string.IsNullOrEmpty(result));
        }

        [Test]
        public void EmptyStartDirectoryReturnsItselfWithoutThrowing()
        {
            Assert.AreEqual(string.Empty, FunplayMCPClientConfigPanel.FindGitRootOrSelf(string.Empty));
        }

        private string CreateDir(string relativePath)
        {
            var full = Path.Combine(_sandbox, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(full);
            return full;
        }
    }
}
