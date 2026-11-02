// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Reflection;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class ExecuteCodeSafetyPolicyTests
    {
        [Test]
        public void BaseSafety_BlocksExistingDangerousPatterns()
        {
            AssertBlocked("File.Delete(\"Assets/test.txt\");", false, "File.Delete");
            AssertBlocked("while (true) { }", false, "while");
        }

        [Test]
        public void StrictSafety_BlocksBroadFilesystemWritesAndPaths()
        {
            AssertBlocked("File.WriteAllText(\"Assets/test.txt\", \"x\");", true, "File write");
            AssertBlocked("Directory.CreateDirectory(\"Assets/generated\");", true, "Directory write");
            AssertBlocked("var path = \"/Users/xyz/.ssh/config\";", true, "Absolute");
            AssertBlocked("var path = \"../ProjectSettings/ProjectSettings.asset\";", true, "Path traversal");
            AssertBlocked("var path = @\"C:\\Users\\Public\\test.txt\";", true, "Absolute");
        }

        [TestCase(@"var normalized = path.Replace(""\\"", ""/"");")]
        [TestCase(@"var normalized = path.Replace(""/"", ""\\"");")]
        [TestCase(@"var separator = ""\\"";")]
        [TestCase(@"var separators = ""\\\\"";")]
        [TestCase(@"var separator = @""\"";")]
        [TestCase(@"var separators = @""\\"";")]
        [TestCase(@"var normalized = path.Replace('\\', '/');")]
        [TestCase(@"var combined = string.Join(""\\"", parts);")]
        public void StrictSafety_AllowsStandaloneSeparatorLiterals(string code)
        {
            AssertAllowed(code, true);
        }

        [TestCase(@"var path = ""C:\\Users\\Public\\test.txt"";", "Absolute")]
        [TestCase(@"var path = @""C:\Users\Public\test.txt"";", "Absolute")]
        [TestCase(@"var path = ""\\\\server\\share\\test.txt"";", "Absolute")]
        [TestCase(@"var path = @""\\server\share\test.txt"";", "Absolute")]
        [TestCase(@"var path = ""\\folder\\test.txt"";", "Absolute")]
        [TestCase(@"var path = @""\\?\C:\folder\test.txt"";", "Absolute")]
        [TestCase(@"var normalized = path.Replace(""\\"", ""/""); var path = ""/Users/xyz/.ssh/config"";", "Absolute")]
        [TestCase(@"var separator = ""\\""; File.WriteAllText(""Assets/test.txt"", separator);", "File write")]
        [TestCase(@"var separator = ""\\""; var path = ""..\\ProjectSettings\\ProjectSettings.asset"";", "Path traversal")]
        [TestCase(@"var separator = ""\\""; var path = ""~\\config"";", "User home")]
        public void StrictSafety_SeparatorLiteralsDoNotHideFilesystemViolations(string code, string expectedReason)
        {
            AssertBlocked(code, true, expectedReason);
        }

        [Test]
        public void NonStrictSafety_DoesNotApplyStrictFilesystemRules()
        {
            AssertAllowed("File.WriteAllText(\"Assets/test.txt\", \"x\");", false);
            AssertAllowed("var path = \"/Users/xyz/.ssh/config\";", false);
        }

        [Test]
        public void ToolResultFormatter_DetectsStructuredErrorResponses()
        {
            var error = ToolResultFormatter.Error("TEST_ERROR");

            Assert.IsTrue(ToolResultFormatter.IsError(error));
            Assert.IsFalse(ToolResultFormatter.IsError("Error: legacy"));
            Assert.IsFalse(ToolResultFormatter.IsError("{\"success\":true,\"message\":\"ok\"}"));
        }

        [Test]
        public void BuildCodeForCompilation_ProjectNamespaceInjectionDisabled_DoesNotInjectProjectNamespaces()
        {
            var fullCode = ScriptExecutionFunctions.BuildCodeForCompilation(
                "public class Smoke { public static string Run() { return \"ok\"; } }",
                "TempScript",
                false,
                out var actualClassName);

            Assert.AreEqual("Smoke", actualClassName);
            Assert.IsFalse(fullCode.Contains("using Funplay.Editor.Tools.Builtins;"), fullCode);
            Assert.IsFalse(fullCode.Contains("using Funplay.Editor.Tests;"), fullCode);
        }

        [Test]
        public void BuildCodeForCompilation_IFunplayCommandMissingUsing_AddsScriptingNamespace()
        {
            var fullCode = ScriptExecutionFunctions.BuildCodeForCompilation(
                "public class CommandScript : IFunplayCommand { public void Execute(ExecutionContext ctx) { ctx.ReturnValue = \"ok\"; } }",
                "TempScript",
                false,
                out var actualClassName);

            Assert.AreEqual("CommandScript", actualClassName);
            StringAssert.StartsWith("using Funplay.Editor.Tools.Scripting;", fullCode);
        }

        [Test]
        public void BuildCodeForCompilation_IFunplayCommandExistingUsing_DoesNotDuplicateScriptingNamespace()
        {
            var fullCode = ScriptExecutionFunctions.BuildCodeForCompilation(
                "using Funplay.Editor.Tools.Scripting;\npublic class CommandScript : IFunplayCommand { public void Execute(ExecutionContext ctx) { ctx.ReturnValue = \"ok\"; } }",
                "TempScript",
                false,
                out _);

            var first = fullCode.IndexOf("using Funplay.Editor.Tools.Scripting;", StringComparison.Ordinal);
            Assert.GreaterOrEqual(first, 0, fullCode);
            Assert.AreEqual(first, fullCode.LastIndexOf("using Funplay.Editor.Tools.Scripting;", StringComparison.Ordinal), fullCode);
        }

        [Test]
        public void ReachableProjectNamespaceUsings_AreDerivedFromLoadedScriptAssemblies()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var usings = ScriptExecutionFunctions.GetReachableProjectNamespaceUsings(
                new[] { typeof(string).Assembly, typeof(ScriptExecutionFunctions).Assembly },
                projectRoot);

            StringAssert.Contains("using Funplay.Editor.Tools.Builtins;", usings);
            Assert.IsFalse(usings.Contains("using System;"), usings);
            Assert.IsFalse(usings.Contains("Funplay.Repro.Unreachable"), usings);
        }

        [Test]
        public void ProjectScriptAssemblyPath_OnlyAllowsLibraryScriptAssembliesUnderProject()
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "Funplay Project");

            Assert.IsTrue(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot, "Library", "ScriptAssemblies", "Game.Editor.dll"),
                projectRoot));
            Assert.IsFalse(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot, "Library", "PackageCache", "com.example", "Example.dll"),
                projectRoot));
            Assert.IsFalse(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot, "Packages", "com.example", "Example.dll"),
                projectRoot));
            Assert.IsFalse(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot + "Other", "Library", "ScriptAssemblies", "Game.Editor.dll"),
                projectRoot));
        }

        [Test]
        public void ExecuteCodeDiagnostics_UnwrapsTargetInvocationException()
        {
            var inner = new InvalidOperationException("real compiler failure");
            var wrapped = new TargetInvocationException(
                "Exception has been thrown by the target of an invocation.",
                new TargetInvocationException(inner));

            Assert.AreSame(inner, ScriptExecutionFunctions.UnwrapTargetInvocationException(wrapped));
        }

        private static void AssertBlocked(string code, bool strict, string expectedReasonPart)
        {
            Assert.IsTrue(ExecuteCodeSafetyPolicy.TryFindViolation(code, strict, out _, out var reason));
            StringAssert.Contains(expectedReasonPart, reason);
        }

        private static void AssertAllowed(string code, bool strict)
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(code, strict, out _, out _));
        }
    }
}
