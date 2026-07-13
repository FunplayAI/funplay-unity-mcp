// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Text;
using System.Threading.Tasks;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Scripting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Funplay.Editor.Tests
{
    public sealed class ScriptCompilerPipelineTests
    {
        [Test]
        public void RoslynCompiler_ToolchainResolves()
        {
            var compiler = new RoslynCscScriptCompiler();

            Assert.IsTrue(compiler.TryResolveToolchain(out var compilerHostPath, out var cscPath, out var monoLibRoot, out var error), error);
            Assert.IsTrue(compilerHostPath.EndsWith("mono") || compilerHostPath.EndsWith("mono.exe") ||
                          compilerHostPath.EndsWith("dotnet") || compilerHostPath.EndsWith("dotnet.exe"), compilerHostPath);
            Assert.IsTrue(cscPath.EndsWith("csc.exe") || cscPath.EndsWith("csc.dll"), cscPath);
            StringAssert.Contains("MonoBleedingEdge", monoLibRoot);
        }

        [Test]
        public void CompilerPipeline_FallsBackToCodeDomWhenRoslynUnavailable()
        {
            var result = ScriptCompilerPipeline.Compile(
                "public class FallbackSmoke { public static string Run() { return \"ok\"; } }",
                new IScriptCompiler[]
                {
                    new FakeUnavailableCompiler("Roslyn"),
                    new FakeSuccessCompiler("CodeDom")
                });

            Assert.AreEqual(ScriptCompilationStatus.Success, result.Status);
            Assert.AreEqual("CodeDom", result.CompilerName);
            Assert.NotNull(result.Assembly);
            Assert.AreEqual(2, result.Attempts.Count);
            Assert.AreEqual("Unavailable", result.Attempts[0].status);
            Assert.AreEqual("Success", result.Attempts[1].status);
        }

        [UnityTest]
        public IEnumerator ExecuteCode_TraditionalSyntax_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                "public class TraditionalSyntax { public static string Run() { var value = 1 + 2; return \"legacy:\" + value; } }",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("legacy:3", GetProperty<string>(data, "result"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_SkipRefresh_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                "public class SkipRefreshSyntax { public static string Run() { return \"skip-refresh-ok\"; } }",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("skip-refresh-ok", GetProperty<string>(data, "result"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                },
                skipRefresh: true);
        }

        [UnityTest]
        public IEnumerator ExecuteCode_TargetTypedNew_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                "public class TargetTypedNewSyntax { public static string Run() { System.Text.StringBuilder sb = new(); sb.Append(\"modern\"); return sb.ToString(); } }",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("modern", GetProperty<string>(data, "result"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_SwitchExpression_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                "public class SwitchExpressionSyntax { public static string Run() { var value = 2; return value switch { 2 => \"two\", _ => \"other\" }; } }",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("two", GetProperty<string>(data, "result"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_IFunplayCommand_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                @"using Funplay.Editor.Tools.Scripting;

public class CommandSyntax : IFunplayCommand
{
    public void Execute(Funplay.Editor.Tools.Scripting.ExecutionContext ctx)
    {
        System.Collections.Generic.List<string> values = new() { ""a"", ""b"" };
        ctx.Log(""count="" + values.Count);
        ctx.ReturnValue = values.Count switch { 2 => ""two"", _ => ""other"" };
    }
}",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                    Assert.AreEqual("two", GetProperty<object>(data, "returnValue"));

                    var logs = (IEnumerable)GetProperty<object>(data, "logs");
                    var found = false;
                    foreach (var log in logs)
                    {
                        if (GetField<string>(log, "Message") == "count=2")
                            found = true;
                    }
                    Assert.IsTrue(found, "Expected IFunplayCommand ctx.Log output in response data.");
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_IFunplayCommandMissingUsing_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                @"public class CommandSyntaxWithoutUsing : IFunplayCommand
{
    public void Execute(ExecutionContext ctx)
    {
        ctx.Log(""auto using ok"");
        ctx.ReturnValue = ""ok"";
    }
}",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                    Assert.AreEqual("ok", GetProperty<object>(data, "returnValue"));
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_CompilationError_ReturnsStructuredErrorFormat()
        {
            return ExecuteCodeAndAssert(
                "public class BadSyntax { public static string Run() { return \"oops\" } }",
                result =>
                {
                    AssertError(result, "COMPILATION_FAILED");
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));

                    var errors = (IEnumerable)GetProperty<object>(data, "errors");
                    var sawDiagnostic = false;
                    foreach (var error in errors)
                    {
                        Assert.GreaterOrEqual(GetField<int>(error, "line"), 0);
                        Assert.GreaterOrEqual(GetField<int>(error, "column"), 0);
                        Assert.IsNotEmpty(GetField<string>(error, "text"));
                        StringAssert.StartsWith("CS", GetField<string>(error, "code"));
                        sawDiagnostic = true;
                    }

                    Assert.IsTrue(sawDiagnostic, "Expected at least one Roslyn diagnostic.");
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_RuntimeError_ReturnsStructuredErrorFormat()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Funplay\] Script runtime error: boom from test"));
            return ExecuteCodeAndAssert(
                "public class RuntimeBoom { public static string Run() { throw new System.InvalidOperationException(\"boom from test\"); } }",
                result =>
                {
                    AssertError(result, "RUNTIME_ERROR");
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("boom from test", GetProperty<string>(data, "message"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                });
        }

        private static IEnumerator ExecuteCodeAndAssert(string code, Action<object> assert, bool skipRefresh = false)
        {
            var task = ScriptExecutionFunctions.ExecuteCode(code, false, skipRefresh);
            while (!task.IsCompleted)
                yield return null;

            if (task.Exception != null)
                throw task.Exception;

            assert(task.Result);
        }

        private static void AssertSuccess(object result)
        {
            Assert.IsTrue(GetProperty<bool>(result, "success"), Describe(result));
        }

        private static void AssertError(object result, string expectedCode)
        {
            Assert.IsFalse(GetProperty<bool>(result, "success"));
            Assert.AreEqual(expectedCode, GetProperty<string>(result, "code"));
            Assert.AreEqual(expectedCode, GetProperty<string>(result, "error"));
        }

        private static T GetProperty<T>(object obj, string name)
        {
            var prop = obj.GetType().GetProperty(name);
            Assert.NotNull(prop, $"Missing property {name} on {obj.GetType().FullName}");
            return (T)prop.GetValue(obj);
        }

        private static T GetField<T>(object obj, string name)
        {
            var field = obj.GetType().GetField(name);
            Assert.NotNull(field, $"Missing field {name} on {obj.GetType().FullName}");
            return (T)field.GetValue(obj);
        }

        private static string Describe(object obj, int depth = 0)
        {
            if (obj == null)
                return "null";
            if (depth > 5)
                return "...";
            if (obj is string s)
                return s;
            if (obj is IEnumerable enumerable && !(obj is string))
            {
                var list = new StringBuilder("[");
                var index = 0;
                foreach (var item in enumerable)
                {
                    if (index++ > 0)
                        list.Append(", ");
                    if (index > 8)
                    {
                        list.Append("...");
                        break;
                    }
                    list.Append(Describe(item, depth + 1));
                }
                list.Append("]");
                return list.ToString();
            }

            var type = obj.GetType();
            if (type.IsPrimitive || type.IsEnum)
                return obj.ToString();

            var sb = new StringBuilder(type.Name).Append("{");
            var first = true;
            foreach (var prop in type.GetProperties())
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                sb.Append(prop.Name).Append("=").Append(Describe(prop.GetValue(obj), depth + 1));
            }
            foreach (var field in type.GetFields())
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                sb.Append(field.Name).Append("=").Append(Describe(field.GetValue(obj), depth + 1));
            }
            sb.Append("}");
            return sb.ToString();
        }

        private sealed class FakeUnavailableCompiler : IScriptCompiler
        {
            public FakeUnavailableCompiler(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public ScriptCompilationResult Compile(string code)
            {
                return ScriptCompilationResult.Unavailable(Name, "forced unavailable");
            }
        }

        private sealed class FakeSuccessCompiler : IScriptCompiler
        {
            public FakeSuccessCompiler(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public ScriptCompilationResult Compile(string code)
            {
                return ScriptCompilationResult.Success(Name, typeof(string).Assembly);
            }
        }
    }
}
