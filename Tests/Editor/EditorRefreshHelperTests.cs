// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class EditorRefreshHelperTests
    {
        [Test]
        public void AnalyzeScriptChangeState_SourceNewerThanAssembly_IsPending()
        {
            var temp = CreateTempDirectory();
            try
            {
                var output = Path.Combine(temp, "Assembly-CSharp.dll");
                var source = Path.Combine(temp, "Example.cs");
                File.WriteAllText(output, "compiled");
                File.WriteAllText(source, "class Example {}");
                File.SetLastWriteTimeUtc(output, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));

                var state = EditorRefreshHelper.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(output, new[] { source }) },
                    Array.Empty<string>(),
                    TimeSpan.FromSeconds(1));

                Assert.IsTrue(state.HasPendingScriptChanges);
                Assert.AreEqual(1, state.OutOfDateSourceCount);
                Assert.AreEqual(0, state.UnknownProjectScriptCount);
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        [Test]
        public void AnalyzeScriptChangeState_UnknownProjectScriptNewerThanAssembly_IsPending()
        {
            var temp = CreateTempDirectory();
            try
            {
                var output = Path.Combine(temp, "Assembly-CSharp.dll");
                var knownSource = Path.Combine(temp, "Known.cs");
                var newSource = Path.Combine(temp, "NewBehaviour.cs");
                File.WriteAllText(output, "compiled");
                File.WriteAllText(knownSource, "class Known {}");
                File.WriteAllText(newSource, "class NewBehaviour {}");
                File.SetLastWriteTimeUtc(output, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(knownSource, new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(newSource, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));

                var state = EditorRefreshHelper.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(output, new[] { knownSource }) },
                    new[] { knownSource, newSource },
                    TimeSpan.FromSeconds(1));

                Assert.IsTrue(state.HasPendingScriptChanges);
                Assert.AreEqual(0, state.OutOfDateSourceCount);
                Assert.AreEqual(1, state.UnknownProjectScriptCount);
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        [Test]
        public void AnalyzeScriptChangeState_UpToDateSources_AreNotPending()
        {
            var temp = CreateTempDirectory();
            try
            {
                var output = Path.Combine(temp, "Assembly-CSharp.dll");
                var source = Path.Combine(temp, "Example.cs");
                var oldUnknownSource = Path.Combine(temp, "IgnoredOldScript.cs");
                File.WriteAllText(output, "compiled");
                File.WriteAllText(source, "class Example {}");
                File.WriteAllText(oldUnknownSource, "class IgnoredOldScript {}");
                File.SetLastWriteTimeUtc(output, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(oldUnknownSource, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                var state = EditorRefreshHelper.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(output, new[] { source }) },
                    new[] { source, oldUnknownSource },
                    TimeSpan.FromSeconds(1));

                Assert.IsFalse(state.HasPendingScriptChanges);
                Assert.AreEqual(0, state.OutOfDateSourceCount);
                Assert.AreEqual(0, state.UnknownProjectScriptCount);
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        [Test]
        public void AnalyzeScriptChangeState_ResolvedOutputNewerThanSource_IsNotPending()
        {
            var temp = CreateTempDirectory();
            try
            {
                var staleOutput = Path.Combine(temp, "ScriptAssemblies", "Funplay.Editor.dll");
                var source = Path.Combine(temp, "MCPServerService.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(staleOutput));
                File.WriteAllText(staleOutput, "old compiled copy");
                File.WriteAllText(source, "class MCPServerService {}");
                File.SetLastWriteTimeUtc(staleOutput, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));

                var resolvedOutputTime = new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc);
                var state = EditorRefreshHelper.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(staleOutput, new[] { source }, resolvedOutputTime) },
                    Array.Empty<string>(),
                    TimeSpan.FromSeconds(1));

                Assert.IsFalse(state.HasPendingScriptChanges);
                Assert.AreEqual(0, state.OutOfDateSourceCount);
                Assert.AreEqual(0, state.UnknownProjectScriptCount);
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "FunplayEditorRefreshTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempDirectory(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
