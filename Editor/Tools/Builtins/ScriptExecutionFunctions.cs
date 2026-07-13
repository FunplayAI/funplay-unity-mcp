// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Funplay.Editor.DI;
using Funplay.Editor.Settings;
using Funplay.Editor.Tools.Helpers;
using Funplay.Editor.Tools.Scripting;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Script")]
    internal static class ScriptExecutionFunctions
    {
        private const string HistorySessionKey = "Funplay.MCP.ExecuteCode.History";
        private const int HistoryMaxEntries = 50;
        private const string FunplayScriptingNamespace = "Funplay.Editor.Tools.Scripting";

        [Description("Primary high-flexibility execution tool. Compiles a C# snippet with Unity's Roslyn csc first " +
                     "while preserving the in-memory compilation/execution flow, then runs the compiled assembly on the editor thread. " +
                     "Two templates are supported:\n" +
                     "  1) Recommended: implement IFunplayCommand on a class — receives an ExecutionContext (ctx) " +
                     "with RegisterObjectCreation/RegisterObjectModification/DestroyObject (auto-Undo + tracked) and " +
                     "Log/LogWarning/LogError (returned in the response).\n" +
                     "  2) Legacy: any class with `public static string Run()` — return value becomes the response message.\n" +
                     "Before compiling, the editor's AssetDatabase is refreshed and pending compilation is awaited, " +
                     "so external file edits are picked up automatically without a separate request_recompile " +
                     "(pass skip_refresh=true to bypass this for read-only snippets or a live Play Mode session you must not disturb). " +
                     "When a full-class snippet implements IFunplayCommand, the required Funplay.Editor.Tools.Scripting using is added automatically if omitted. " +
                     "safety_checks blocks a small set of obviously dangerous patterns " +
                     "(File.Delete, Process.Start, while(true), Environment.Exit, AssetDatabase.DeleteAsset, etc) " +
                     "and, when strict filesystem safety is enabled, broad System.IO writes plus obvious absolute/system/traversal paths. " +
                     "This is a defensive layer, not a full sandbox. If omitted, the MCP Settings window's default safety-check setting is used " +
                     "(enabled by default); explicitly passing true or false overrides that default. Project namespaces are not auto-injected " +
                     "by default; add `using` directives in the snippet, or enable the ScriptAssemblies-based convenience toggle in the MCP Settings window. " +
                     "Every invocation is appended to a session-scoped history (see get_execute_code_history / replay_execute_code).")]
        [SceneEditingTool]
        public static async Task<object> ExecuteCode(
            [ToolParam("C# code to execute. See description for IFunplayCommand vs legacy Run() templates.")] string code,
            [ToolParam("If true, reject the call before compile when the code contains obviously dangerous patterns. If omitted, uses the MCP Settings window default.", Required = false)] bool? safety_checks = null,
            [ToolParam("If true, skip the pre-compile AssetDatabase.Refresh + wait-for-ready. Use only when the editor is already up to date -- e.g. a read-only inspection snippet, or during a live Play Mode session you must not disturb. The default refresh can trigger an import/domain reload (from your own OR another actor's pending changes in a shared editor) that wipes Play Mode runtime state. When skipped, external file edits made since the last compile are NOT picked up.", Required = false)] bool skip_refresh = false)
        {
            var effectiveSafetyChecks = ResolveSafetyChecks(safety_checks);
            if (effectiveSafetyChecks)
            {
                var strictFilesystemChecks = ResolveStrictFilesystemSafety();
                if (ExecuteCodeSafetyPolicy.TryFindViolation(code, strictFilesystemChecks, out var pattern, out var reason))
                {
                    var blocked = Response.Error("SAFETY_CHECK_BLOCKED",
                        new
                        {
                            pattern,
                            reason,
                            strict_filesystem_checks = strictFilesystemChecks,
                            hint = "Disable the strict filesystem guard in the MCP Settings window or pass safety_checks=false only for trusted local calls."
                        });
                    AppendHistory(code, false, $"Blocked: {reason}");
                    return blocked;
                }
            }

            if (!skip_refresh)
            {
                try
                {
                    await EditorReadyHelper.RefreshAndWaitForReady();
                }
                catch (TimeoutException)
                {
                    AppendHistory(code, false, "EDITOR_BUSY");
                    return Response.Error("EDITOR_BUSY",
                        new { hint = "Unity is still compiling/importing. Retry in a moment, or pass skip_refresh=true if you know the editor is up to date." });
                }
            }

            var className = "TempScript_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var fullCode = BuildCodeForCompilation(
                code,
                className,
                ResolveProjectNamespaceInjection(),
                out var actualClassName);

            try
            {
                var result = CompileAndExecute(fullCode, actualClassName);
                AppendHistory(code, IsSuccess(result), SummarizeResult(result));
                return result;
            }
            catch (Exception ex)
            {
                var root = UnwrapTargetInvocationException(ex);
                Debug.LogError($"[Funplay] ExecuteCode failed: {root.GetType().FullName}: {root.Message}\n{root.StackTrace}");
                AppendHistory(code, false, $"{root.GetType().Name}: {root.Message}");
                return Response.Error("EXECUTE_CODE_FAILED", new
                {
                    message = root.Message,
                    exception_type = root.GetType().FullName,
                    stack = root.StackTrace,
                    outer_exception_type = ReferenceEquals(root, ex) ? null : ex.GetType().FullName,
                    outer_message = ReferenceEquals(root, ex) ? null : ex.Message
                });
            }
        }

        internal static Exception UnwrapTargetInvocationException(Exception exception)
        {
            while (exception is TargetInvocationException invocationException &&
                   invocationException.InnerException != null)
            {
                exception = invocationException.InnerException;
            }

            return exception;
        }

        [Description("Return the most recent execute_code invocations (success or failure) from the current Editor session. " +
                     "Use replay_execute_code to re-run a past entry by index. History survives domain reloads but is cleared " +
                     "when the Editor closes (uses SessionState). Cap is 50 entries; older entries are dropped.")]
        [ReadOnlyTool]
        public static object GetExecuteCodeHistory(
            [ToolParam("Number of most-recent entries to return (1-50). Default 10.", Required = false)] int limit = 10)
        {
            var entries = LoadHistory().entries;
            limit = Mathf.Clamp(limit, 1, HistoryMaxEntries);
            var slice = entries.Skip(Math.Max(0, entries.Count - limit)).ToList();
            // Render newest first, with original index preserved (for replay)
            var view = new List<object>(slice.Count);
            for (int i = slice.Count - 1; i >= 0; i--)
            {
                var entry = slice[i];
                var globalIndex = entries.IndexOf(entry);
                view.Add(new
                {
                    index = globalIndex,
                    timestamp = entry.timestamp,
                    success = entry.success,
                    summary = entry.summary,
                    code_preview = Preview(entry.code, 240),
                    code_length = entry.code?.Length ?? 0
                });
            }
            return Response.Success($"Returned {view.Count} of {entries.Count} history entries.",
                new { total = entries.Count, returned = view.Count, entries = view });
        }

        [Description("Re-run a past execute_code invocation by index (use get_execute_code_history to discover indices). " +
                     "The original code is re-compiled and executed; this also appends a new history entry. " +
                     "Pass safety_checks to override the MCP Settings window default.")]
        [SceneEditingTool]
        public static async Task<object> ReplayExecuteCode(
            [ToolParam("History index to replay (as returned by get_execute_code_history).")] int index,
            [ToolParam("If true, re-evaluate the safety blocklist before re-running. If omitted, uses the MCP Settings window default.", Required = false)] bool? safety_checks = null)
        {
            var entries = LoadHistory().entries;
            if (entries.Count == 0)
                return Response.Error("HISTORY_EMPTY");
            if (index < 0 || index >= entries.Count)
                return Response.Error("HISTORY_INDEX_OUT_OF_RANGE",
                    new { provided = index, valid_range = new { min = 0, max = entries.Count - 1 } });

            var entry = entries[index];
            if (string.IsNullOrEmpty(entry.code))
                return Response.Error("HISTORY_ENTRY_EMPTY", new { index });

            return await ExecuteCode(entry.code, safety_checks);
        }

        [Description("Erase the entire execute_code history for the current Editor session. " +
                     "Useful when you want a clean slate before a fresh experiment or before sharing a session recording.")]
        [SceneEditingTool]
        public static object ClearExecuteCodeHistory()
        {
            var before = LoadHistory().entries.Count;
            SessionState.EraseString(HistorySessionKey);
            return Response.Success($"Cleared {before} history entr{(before == 1 ? "y" : "ies")}.",
                new { cleared = before });
        }

        // ---- History helpers ----------------------------------------------------

        private static bool ResolveSafetyChecks(bool? safetyChecks)
        {
            if (safetyChecks.HasValue)
                return safetyChecks.Value;

            var settings = RootScopeServices.Services?.GetService(typeof(ISettingsController)) as ISettingsController;
            return settings?.ExecuteCodeSafetyChecksEnabled ?? true;
        }

        private static bool ResolveStrictFilesystemSafety()
        {
            var settings = RootScopeServices.Services?.GetService(typeof(ISettingsController)) as ISettingsController;
            return settings?.ExecuteCodeStrictFilesystemSafetyEnabled ?? true;
        }

        private static bool ResolveProjectNamespaceInjection()
        {
            var settings = RootScopeServices.Services?.GetService(typeof(ISettingsController)) as ISettingsController;
            return settings?.ExecuteCodeProjectNamespaceInjectionEnabled ?? false;
        }

        [Serializable]
        private class HistoryEntry
        {
            public string timestamp;
            public string code;
            public bool success;
            public string summary;
        }

        [Serializable]
        private class HistoryBox
        {
            public List<HistoryEntry> entries = new List<HistoryEntry>();
        }

        private static HistoryBox LoadHistory()
        {
            var raw = SessionState.GetString(HistorySessionKey, null);
            if (string.IsNullOrEmpty(raw))
                return new HistoryBox();
            try
            {
                return JsonConvert.DeserializeObject<HistoryBox>(raw) ?? new HistoryBox();
            }
            catch
            {
                return new HistoryBox();
            }
        }

        private static void AppendHistory(string code, bool success, string summary)
        {
            try
            {
                var box = LoadHistory();
                box.entries.Add(new HistoryEntry
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    code = code ?? string.Empty,
                    success = success,
                    summary = Preview(summary, 200)
                });
                if (box.entries.Count > HistoryMaxEntries)
                    box.entries.RemoveRange(0, box.entries.Count - HistoryMaxEntries);
                SessionState.SetString(HistorySessionKey, JsonConvert.SerializeObject(box));
            }
            catch (Exception ex)
            {
                // Swallow — history is best-effort, must never break real execution.
                Debug.LogWarning($"[Funplay] Failed to append execute_code history: {ex.Message}");
            }
        }

        private static bool IsSuccess(object result)
        {
            if (result == null) return false;
            var prop = result.GetType().GetProperty("success");
            return prop?.GetValue(result) is bool b && b;
        }

        private static string SummarizeResult(object result)
        {
            if (result == null) return "null";
            var t = result.GetType();
            var success = t.GetProperty("success")?.GetValue(result) as bool? ?? false;
            if (success)
                return (t.GetProperty("message")?.GetValue(result) as string) ?? "OK";
            var code = t.GetProperty("code")?.GetValue(result) as string;
            var error = t.GetProperty("error")?.GetValue(result) as string;
            return code ?? error ?? "ERROR";
        }

        private static string Preview(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
            return s.Substring(0, max) + "…";
        }

        private static object CompileAndExecute(string code, string className)
        {
            var compilation = ScriptCompilerPipeline.Compile(code);
            if (compilation.Status == ScriptCompilationStatus.CompilationFailed)
            {
                return Response.Error("COMPILATION_FAILED", new
                {
                    compiler = compilation.CompilerName,
                    errors = compilation.Errors,
                    compiler_attempts = compilation.Attempts,
                    hint = "Roslyn is tried first for modern C# syntax while preserving execute_code's in-memory compilation/execution flow."
                });
            }

            if (compilation.Status != ScriptCompilationStatus.Success || compilation.Assembly == null)
            {
                return Response.Error("COMPILATION_BACKEND_UNAVAILABLE", new
                {
                    compiler = compilation.CompilerName,
                    message = compilation.Message,
                    compiler_attempts = compilation.Attempts
                });
            }

            return ExecuteCompiledAssembly(compilation.Assembly, className, compilation.CompilerName, compilation.Attempts);
        }

        private static object ExecuteCompiledAssembly(
            Assembly compiledAssembly,
            string className,
            string compilerName,
            List<ScriptCompilerAttempt> compilerAttempts)
        {
            // Prefer IFunplayCommand path: any class in the compiled assembly that implements it
            Type commandType = null;
            try
            {
                commandType = compiledAssembly.GetTypes()
                    .FirstOrDefault(t => typeof(IFunplayCommand).IsAssignableFrom(t)
                                         && !t.IsInterface && !t.IsAbstract);
            }
            catch (ReflectionTypeLoadException)
            {
                // Fall through to legacy Run() path
            }
            if (commandType != null)
                return ExecuteAsCommand(commandType, compilerName);

            // Legacy path: class with `static Run()`
            var type = compiledAssembly.GetType(className);
            if (type == null)
                return Response.Error("CLASS_NOT_FOUND",
                    new { className, available = GetTypeNames(compiledAssembly), compiler = compilerName, compiler_attempts = compilerAttempts });

            var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                return Response.Error("RUN_METHOD_NOT_FOUND", new { className, compiler = compilerName });

            try
            {
                var result = method.Invoke(null, null);
                return Response.Success("Executed (legacy Run()).", new
                {
                    result = result?.ToString() ?? "OK",
                    compiler = compilerName
                });
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                Debug.LogError($"[Funplay] Script runtime error: {inner.Message}\n{inner.StackTrace}");
                return Response.Error("RUNTIME_ERROR",
                    new { message = inner.Message, stack = inner.StackTrace, compiler = compilerName });
            }
        }

        private static object ExecuteAsCommand(Type commandType, string compilerName)
        {
            IFunplayCommand instance;
            try { instance = (IFunplayCommand)Activator.CreateInstance(commandType); }
            catch (Exception ex)
            {
                return Response.Error("COMMAND_INSTANTIATION_FAILED",
                    new { type = commandType.FullName, error = ex.Message, compiler = compilerName });
            }

            var ctx = new ExecutionContext();
            try
            {
                instance.Execute(ctx);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay] Command runtime error: {ex.Message}\n{ex.StackTrace}");
                return Response.Error("COMMAND_RUNTIME_ERROR", new
                {
                    message = ex.Message,
                    stack = ex.StackTrace,
                    compiler = compilerName,
                    logs = ctx.Logs,
                    created = ctx.CreatedInstanceIds,
                    modified = ctx.ModifiedInstanceIds,
                    destroyed = ctx.DestroyedInstanceIds
                });
            }

            return Response.Success("Command executed.", new
            {
                compiler = compilerName,
                logs = ctx.Logs,
                created = ctx.CreatedInstanceIds,
                modified = ctx.ModifiedInstanceIds,
                destroyed = ctx.DestroyedInstanceIds,
                returnValue = ctx.ReturnValue
            });
        }

        private static string[] GetTypeNames(Assembly assembly)
        {
            try
            {
                return Array.ConvertAll(assembly.GetTypes(), t => t.FullName);
            }
            catch
            {
                return new[] { "(unable to list types)" };
            }
        }

        internal static string BuildCodeForCompilation(
            string code,
            string className,
            bool injectProjectNamespaces,
            out string actualClassName)
        {
            actualClassName = className;
            var projectUsings = injectProjectNamespaces ? GetReachableProjectNamespaceUsings() : string.Empty;

            if (code.Contains("class "))
            {
                var match = Regex.Match(code, @"class\s+(\w+)");
                if (match.Success)
                    actualClassName = match.Groups[1].Value;

                var requiredUsings = GetRequiredSnippetUsings(code);
                return PrependMissingUsings(code, requiredUsings + projectUsings);
            }

            return WrapCode(code, className, projectUsings);
        }

        private static string WrapCode(string code, string className, string projectUsings)
        {
            return $@"using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using {FunplayScriptingNamespace};
{projectUsings}
public static class {className}
{{
    public static string Run()
    {{
        {code}
        return ""OK"";
    }}
}}";
        }

        internal static string GetReachableProjectNamespaceUsings()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return GetReachableProjectNamespaceUsings(AppDomain.CurrentDomain.GetAssemblies(), projectRoot);
        }

        internal static string GetReachableProjectNamespaceUsings(IEnumerable<Assembly> assemblies, string projectRoot)
        {
            if (assemblies == null || string.IsNullOrEmpty(projectRoot))
                return string.Empty;

            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in assemblies)
            {
                if (!IsProjectScriptAssembly(assembly, projectRoot))
                    continue;

                foreach (var type in GetLoadableTypes(assembly))
                {
                    if (!string.IsNullOrEmpty(type.Namespace) && IsValidNamespace(type.Namespace))
                        namespaces.Add(type.Namespace);
                }
            }

            if (namespaces.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var ns in namespaces.OrderBy(value => value, StringComparer.Ordinal))
                sb.AppendLine($"using {ns};");
            return sb.ToString();
        }

        private static bool IsProjectScriptAssembly(Assembly assembly, string projectRoot)
        {
            if (assembly == null || assembly.IsDynamic)
                return false;

            try
            {
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location) || !File.Exists(location))
                    return false;

                return IsProjectScriptAssemblyPath(location, projectRoot);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsProjectScriptAssemblyPath(string location, string projectRoot)
        {
            if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(projectRoot))
                return false;

            var normalizedLocation = NormalizePath(location);
            var normalizedProjectRoot = NormalizePath(projectRoot);
            return normalizedLocation.StartsWith(normalizedProjectRoot + "/", StringComparison.OrdinalIgnoreCase) &&
                   normalizedLocation.IndexOf("/Library/ScriptAssemblies/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static bool IsValidNamespace(string ns)
        {
            return Regex.IsMatch(ns, @"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$");
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }

        private static string PrependMissingUsings(string code, string projectUsings)
        {
            if (string.IsNullOrEmpty(projectUsings))
                return code;

            var existing = new HashSet<string>();
            var matches = Regex.Matches(code, @"^\s*using\s+([\w.]+)\s*;", RegexOptions.Multiline);
            foreach (Match match in matches)
                existing.Add(match.Groups[1].Value);

            var missing = new StringBuilder();
            foreach (var line in projectUsings.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                var nsMatch = Regex.Match(trimmed, @"using\s+([\w.]+)\s*;");
                if (nsMatch.Success && !existing.Contains(nsMatch.Groups[1].Value))
                    missing.AppendLine(trimmed);
            }

            return missing.Length == 0 ? code : missing + code;
        }

        private static string GetRequiredSnippetUsings(string code)
        {
            if (!UsesUnqualifiedIFunplayCommand(code))
                return string.Empty;

            return $"using {FunplayScriptingNamespace};\n";
        }

        internal static bool UsesUnqualifiedIFunplayCommand(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            return Regex.IsMatch(code, @"(?<![\w.])IFunplayCommand(?!\w)");
        }
    }
}
