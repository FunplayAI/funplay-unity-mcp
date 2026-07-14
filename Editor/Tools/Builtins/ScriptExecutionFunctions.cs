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
                     "The snippet is compiled into a SEPARATE assembly, so funplay package internal types (e.g. ToolRegistry) cannot be referenced directly from the snippet -- reach them via reflection. " +
                     "safety_checks blocks a small set of obviously dangerous patterns " +
                     "(File.Delete, Process.Start, while(true), Environment.Exit, AssetDatabase.DeleteAsset, etc) " +
                     "and, when strict filesystem safety is enabled, broad System.IO writes plus obvious absolute/system/traversal paths. " +
                     "This is a defensive layer, not a full sandbox. If omitted, the MCP Settings window's default safety-check setting is used " +
                     "(enabled by default); explicitly passing true or false overrides that default. Project namespaces are not auto-injected " +
                     "by default; add `using` directives in the snippet, or enable the ScriptAssemblies-based convenience toggle in the MCP Settings window. " +
                     "If a compile fails with CS0104 (a type name ambiguous between two imported namespaces, e.g. System.Random vs UnityEngine.Random) or CS0433 (a type provided by multiple loaded assemblies) the error carries a structured `ambiguous` list; for CS0104 pass preferred_namespaces (comma-separated) to auto-insert a using-alias binding the name to that namespace and recompile once. CS0433 (same fully-qualified type in two assemblies) can't be fixed by an alias — use extern alias / drop a reference / reflection. " +
                     "Every invocation is appended to a session-scoped history (see get_execute_code_history / replay_execute_code).")]
        [SceneEditingTool]
        public static async Task<object> ExecuteCode(
            [ToolParam("C# code to execute. See description for IFunplayCommand vs legacy Run() templates.")] string code,
            [ToolParam("If true, reject the call before compile when the code contains obviously dangerous patterns. If omitted, uses the MCP Settings window default.", Required = false)] bool? safety_checks = null,
            [ToolParam("If true, skip the pre-compile AssetDatabase.Refresh + wait-for-ready. Use only when the editor is already up to date -- e.g. a read-only inspection snippet, or during a live Play Mode session you must not disturb. The default refresh can trigger an import/domain reload (from your own OR another actor's pending changes in a shared editor) that wipes Play Mode runtime state. When skipped, external file edits made since the last compile are NOT picked up.", Required = false)] bool skip_refresh = false,
            [ToolParam("Optional comma-separated namespaces used to auto-resolve CS0104 ambiguity: when an unqualified type name is ambiguous between two imported namespaces, a using-alias binding that name to the candidate in the listed (winning) namespace is inserted and the snippet is recompiled once. Does NOT fix CS0433 (same fully-qualified type in two assemblies). If it stays ambiguous, a structured COMPILATION_FAILED error with an `ambiguous` array is returned.", Required = false)] string preferred_namespaces = null)
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
                var result = CompileAndExecute(fullCode, actualClassName, preferred_namespaces);
                AppendHistory(code, IsSuccess(result), SummarizeResult(result), preferred_namespaces);
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

            // Forward the stored preferred_namespaces so a snippet that only compiled via CS0104
            // alias auto-resolution replays faithfully instead of failing with the original ambiguity.
            return await ExecuteCode(entry.code, safety_checks, preferred_namespaces: entry.preferredNamespaces);
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
            public string preferredNamespaces;
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

        private static void AppendHistory(string code, bool success, string summary, string preferredNamespaces = null)
        {
            try
            {
                var box = LoadHistory();
                box.entries.Add(new HistoryEntry
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    code = code ?? string.Empty,
                    preferredNamespaces = preferredNamespaces,
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

        private static object CompileAndExecute(string code, string className, string preferredNamespaces)
        {
            var compilation = ScriptCompilerPipeline.Compile(code);

            if (compilation.Status == ScriptCompilationStatus.CompilationFailed)
            {
                // CS0433: the same type name is provided by multiple loaded assemblies.
                // Surface a structured disambiguation payload and, when preferred_namespaces is
                // supplied, attempt a single alias-based recompile before giving up.
                if (TryGetTypeAmbiguities(compilation.Errors, out var ambiguities))
                {
                    var preferred = ParsePreferredNamespaces(preferredNamespaces);
                    if (preferred.Count > 0)
                    {
                        var aliasBlock = BuildDisambiguationAliases(ambiguities, preferred, code);
                        if (!string.IsNullOrEmpty(aliasBlock))
                        {
                            var retry = ScriptCompilerPipeline.Compile(aliasBlock + code);
                            if (retry.Status == ScriptCompilationStatus.Success && retry.Assembly != null)
                                return ExecuteCompiledAssembly(retry.Assembly, className, retry.CompilerName, retry.Attempts);

                            if (retry.Status == ScriptCompilationStatus.CompilationFailed)
                            {
                                compilation = retry;
                                // The alias resolved the CS0104/CS0433, but the retry surfaced OTHER
                                // (non-ambiguity) errors. Report them plainly instead of a misleading
                                // "ambiguity" payload with an empty candidates list.
                                if (!TryGetTypeAmbiguities(compilation.Errors, out ambiguities) || ambiguities.Count == 0)
                                    return Response.Error("COMPILATION_FAILED", new
                                    {
                                        compiler = compilation.CompilerName,
                                        errors = compilation.Errors,
                                        compiler_attempts = compilation.Attempts,
                                        hint = "A using-alias for the ambiguous name was applied and recompiled, but other compilation errors remain. Error line numbers are offset by the injected alias line(s) prepended to your snippet."
                                    });
                            }
                        }
                    }

                    return BuildAmbiguousCompilationError(compilation, ambiguities);
                }

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

        private sealed class AmbiguousType
        {
            public string Kind;      // "CS0433" (same FQN in multiple assemblies) or "CS0104" (name ambiguous across namespaces)
            public string Type;      // CS0433: the full type name; CS0104: the ambiguous simple name as written
            public List<string> Candidates = new List<string>(); // CS0433: assembly short names; CS0104: candidate full type names
        }

        // Parse both ambiguity diagnostics into a structured list:
        //  - CS0433 "the type 'X' exists in both 'AsmA' and 'AsmB'" → Type=X (full), Candidates=assembly short names.
        //  - CS0104 "'Name' is an ambiguous reference between 'Ns1.Name' and 'Ns2.Name'" → Type=Name (simple), Candidates=full type names.
        // Robust to localized compiler output by extracting the single-quoted tokens (first = identifier, rest = candidates).
        private static bool TryGetTypeAmbiguities(List<ScriptCompilationError> errors, out List<AmbiguousType> ambiguities)
        {
            ambiguities = new List<AmbiguousType>();
            if (errors == null)
                return false;

            foreach (var error in errors)
            {
                if (error == null)
                    continue;
                bool is433 = string.Equals(error.code, "CS0433", StringComparison.OrdinalIgnoreCase);
                bool is104 = string.Equals(error.code, "CS0104", StringComparison.OrdinalIgnoreCase);
                if (!is433 && !is104)
                    continue;

                var quoted = Regex.Matches(error.text ?? string.Empty, "'([^']+)'");
                if (quoted.Count < 2)
                    continue;

                var kind = is433 ? "CS0433" : "CS0104";
                var type = quoted[0].Groups[1].Value;
                var existing = ambiguities.FirstOrDefault(a =>
                    a.Kind == kind && string.Equals(a.Type, type, StringComparison.Ordinal));
                if (existing == null)
                {
                    existing = new AmbiguousType { Kind = kind, Type = type };
                    ambiguities.Add(existing);
                }

                for (int i = 1; i < quoted.Count; i++)
                {
                    // CS0433 candidate token is an assembly identity ("Name, Version=..."); keep the short name.
                    // CS0104 candidate token is a full type name ("Namespace.Type"); keep it verbatim.
                    var raw = quoted[i].Groups[1].Value;
                    var candidate = is433 ? raw.Split(',')[0].Trim() : raw.Trim();
                    if (!string.IsNullOrEmpty(candidate) && !existing.Candidates.Contains(candidate))
                        existing.Candidates.Add(candidate);
                }
            }

            return ambiguities.Count > 0;
        }

        private static List<string> ParsePreferredNamespaces(string preferredNamespaces)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(preferredNamespaces))
                return result;

            foreach (var raw in preferredNamespaces.Split(','))
            {
                var ns = raw.Trim();
                if (ns.Length > 0 && !result.Contains(ns))
                    result.Add(ns);
            }

            return result;
        }

        // Build `using <simple> = <fullType>;` alias directives that resolve CS0104 ("ambiguous
        // reference between two namespaces") by binding the ambiguous simple name to the candidate
        // whose namespace the caller listed in preferred_namespaces. CS0433 (the SAME fully-qualified
        // type provided by two assemblies) is intentionally skipped: both candidates are the identical
        // name, so a using-alias cannot disambiguate it — that needs extern alias / dropping a reference.
        private static string BuildDisambiguationAliases(
            List<AmbiguousType> ambiguities,
            List<string> preferred,
            string code)
        {
            var sb = new StringBuilder();
            var added = new HashSet<string>(StringComparer.Ordinal);

            foreach (var ambiguity in ambiguities)
            {
                if (!string.Equals(ambiguity.Kind, "CS0104", StringComparison.Ordinal))
                    continue;

                var simple = ambiguity.Type; // for CS0104 this is the ambiguous simple name as written
                if (string.IsNullOrEmpty(simple) || !IsValidNamespace(simple) || added.Contains(simple))
                    continue;
                if (Regex.IsMatch(code, $@"using\s+{Regex.Escape(simple)}\s*="))
                    continue;

                // Pick the candidate full type whose namespace the caller preferred.
                string target = null;
                foreach (var candidate in ambiguity.Candidates)
                {
                    var candNs = NamespaceOfType(candidate);
                    if (preferred.Any(p => string.Equals(p, candNs, StringComparison.Ordinal)))
                    {
                        target = candidate;
                        break;
                    }
                }

                if (target == null)
                    continue; // no preferred namespace matches a candidate → leave it for the structured error

                sb.AppendLine($"using {simple} = {target};");
                added.Add(simple);
            }

            return sb.ToString();
        }

        private static object BuildAmbiguousCompilationError(ScriptCompilationResult compilation, List<AmbiguousType> ambiguities)
        {
            var list = ambiguities ?? new List<AmbiguousType>();
            var ambiguousView = list
                .Select(a => new
                {
                    kind = a.Kind,
                    type = a.Type,
                    // CS0433 candidates are assemblies → present as assembly-qualified names;
                    // CS0104 candidates are already full type names → present verbatim.
                    candidates = string.Equals(a.Kind, "CS0433", StringComparison.Ordinal)
                        ? a.Candidates.Select(asm => $"{a.Type}, {asm}").ToArray()
                        : a.Candidates.ToArray()
                })
                .ToArray();

            bool has104 = list.Any(a => string.Equals(a.Kind, "CS0104", StringComparison.Ordinal));
            bool has433 = list.Any(a => string.Equals(a.Kind, "CS0433", StringComparison.Ordinal));
            string hint;
            if (has104 && !has433)
                hint = "CS0104: an unqualified type name is ambiguous between two imported namespaces (e.g. System.Random vs UnityEngine.Random). Pass preferred_namespaces=<namespace> to bind the name to that namespace's type and recompile once, or fully-qualify the name in the snippet.";
            else if (has433 && !has104)
                hint = "CS0433: the SAME fully-qualified type is provided by multiple loaded assemblies (e.g. a HybridCLR hot-update assembly vs the editor assembly). A using-alias or fully-qualified name CANNOT disambiguate this — use `extern alias`, drop one assembly reference, or reach the type via reflection. funplay package internal types are not directly referenceable from the compiled snippet assembly.";
            else
                hint = "Mixed ambiguity. CS0104 (name ambiguous across namespaces) is resolvable via preferred_namespaces or fully-qualifying the name; CS0433 (same fully-qualified type in two assemblies) needs `extern alias`, dropping a reference, or reflection.";

            return Response.Error("COMPILATION_FAILED", new
            {
                compiler = compilation.CompilerName,
                errors = compilation.Errors,
                compiler_attempts = compilation.Attempts,
                ambiguous = ambiguousView,
                hint
            });
        }

        private static string NamespaceOfType(string type)
        {
            if (string.IsNullOrEmpty(type))
                return string.Empty;
            var idx = type.LastIndexOf('.');
            return idx > 0 ? type.Substring(0, idx) : string.Empty;
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
