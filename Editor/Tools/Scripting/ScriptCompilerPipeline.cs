// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Funplay.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Scripting
{
    internal enum ScriptCompilationStatus
    {
        Success,
        CompilationFailed,
        Unavailable
    }

    internal sealed class ScriptCompilationError
    {
        public int line;
        public int column;
        public string text;
        public string code;
    }

    internal sealed class ScriptCompilerAttempt
    {
        public string compiler;
        public string status;
        public string message;
    }

    internal sealed class ScriptCompilationResult
    {
        public ScriptCompilationStatus Status { get; private set; }
        public string CompilerName { get; private set; }
        public Assembly Assembly { get; private set; }
        public List<ScriptCompilationError> Errors { get; private set; }
        public string Message { get; private set; }
        public List<ScriptCompilerAttempt> Attempts { get; private set; }

        public static ScriptCompilationResult Success(string compilerName, Assembly assembly)
        {
            return new ScriptCompilationResult
            {
                Status = ScriptCompilationStatus.Success,
                CompilerName = compilerName,
                Assembly = assembly,
                Message = "Compiled successfully."
            };
        }

        public static ScriptCompilationResult CompilationFailed(
            string compilerName,
            List<ScriptCompilationError> errors,
            string message = null)
        {
            return new ScriptCompilationResult
            {
                Status = ScriptCompilationStatus.CompilationFailed,
                CompilerName = compilerName,
                Errors = errors ?? new List<ScriptCompilationError>(),
                Message = message ?? "Compilation failed."
            };
        }

        public static ScriptCompilationResult Unavailable(string compilerName, string message)
        {
            return new ScriptCompilationResult
            {
                Status = ScriptCompilationStatus.Unavailable,
                CompilerName = compilerName,
                Message = message ?? "Compiler unavailable."
            };
        }

        public ScriptCompilationResult WithAttempts(List<ScriptCompilerAttempt> attempts)
        {
            Attempts = attempts ?? new List<ScriptCompilerAttempt>();
            return this;
        }
    }

    internal interface IScriptCompiler
    {
        string Name { get; }
        ScriptCompilationResult Compile(string code);
    }

    internal static class ScriptCompilerPipeline
    {
        public static ScriptCompilationResult Compile(string code)
        {
            return Compile(code, new IScriptCompiler[]
            {
                new RoslynCscScriptCompiler(),
                new CodeDomScriptCompiler()
            });
        }

        internal static ScriptCompilationResult Compile(string code, IEnumerable<IScriptCompiler> compilers)
        {
            var attempts = new List<ScriptCompilerAttempt>();
            ScriptCompilationResult lastUnavailable = null;

            foreach (var compiler in compilers ?? Array.Empty<IScriptCompiler>())
            {
                if (compiler == null)
                    continue;

                ScriptCompilationResult result;
                try
                {
                    result = compiler.Compile(code);
                }
                catch (Exception ex)
                {
                    result = ScriptCompilationResult.Unavailable(compiler.Name, ex.Message);
                }

                attempts.Add(new ScriptCompilerAttempt
                {
                    compiler = compiler.Name,
                    status = result.Status.ToString(),
                    message = result.Message
                });

                if (result.Status == ScriptCompilationStatus.Success)
                {
                    PluginDebugLogger.Log($"[Funplay MCP Server] execute_code compiled with {compiler.Name}.");
                    return result.WithAttempts(attempts);
                }

                if (result.Status == ScriptCompilationStatus.CompilationFailed)
                {
                    PluginDebugLogger.Log($"[Funplay MCP Server] execute_code compilation failed with {compiler.Name}.");
                    return result.WithAttempts(attempts);
                }

                lastUnavailable = result;
                PluginDebugLogger.Log($"[Funplay MCP Server] {compiler.Name} unavailable for execute_code: {result.Message}");
            }

            return (lastUnavailable ?? ScriptCompilationResult.Unavailable("none", "No script compiler was configured."))
                .WithAttempts(attempts);
        }
    }

    internal sealed class RoslynCscScriptCompiler : IScriptCompiler
    {
        private const int DefaultTimeoutMilliseconds = 15000;

        private readonly string _compilerHostPathOverride;
        private readonly string _cscPathOverride;
        private readonly string _monoLibRootOverride;
        private readonly int _timeoutMilliseconds;

        public string Name => "Roslyn";

        public RoslynCscScriptCompiler(
            string compilerHostPath = null,
            string cscPath = null,
            string monoLibRoot = null,
            int timeoutMilliseconds = DefaultTimeoutMilliseconds)
        {
            _compilerHostPathOverride = compilerHostPath;
            _cscPathOverride = cscPath;
            _monoLibRootOverride = monoLibRoot;
            _timeoutMilliseconds = timeoutMilliseconds <= 0 ? DefaultTimeoutMilliseconds : timeoutMilliseconds;
        }

        public ScriptCompilationResult Compile(string code)
        {
            if (!TryResolveToolchain(out var compilerHostPath, out var cscPath, out var monoLibRoot, out var toolchainError))
                return ScriptCompilationResult.Unavailable(Name, toolchainError);

            var tempRoot = Path.Combine(Path.GetTempPath(), "FunplayExecuteCode", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                var sourcePath = Path.Combine(tempRoot, "Snippet.cs");
                // csc derives the assembly name from the output file. CoreCLR editors (Unity 7+)
                // refuse to load a second assembly with the same simple name, so keep it unique.
                var outputPath = Path.Combine(tempRoot, "Snippet_" + Path.GetFileName(tempRoot) + ".dll");
                var responsePath = Path.Combine(tempRoot, "csc.rsp");

                File.WriteAllText(sourcePath, code ?? string.Empty, Encoding.UTF8);
                File.WriteAllText(responsePath, BuildResponseFile(sourcePath, outputPath, monoLibRoot), Encoding.UTF8);

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var psi = new ProcessStartInfo
                {
                    FileName = compilerHostPath,
                    Arguments = BuildCompilerArguments(cscPath, responsePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = tempRoot
                };

                using (var process = new Process { StartInfo = psi })
                {
                    process.OutputDataReceived += (_, args) =>
                    {
                        if (args.Data != null)
                            stdout.AppendLine(args.Data);
                    };
                    process.ErrorDataReceived += (_, args) =>
                    {
                        if (args.Data != null)
                            stderr.AppendLine(args.Data);
                    };

                    try
                    {
                        process.Start();
                    }
                    catch (Exception ex)
                    {
                        return ScriptCompilationResult.Unavailable(Name, $"Failed to start Roslyn csc: {ex.Message}");
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMilliseconds);
                    while (!process.HasExited && DateTime.UtcNow < deadline)
                        Thread.Sleep(25);

                    if (!process.HasExited)
                    {
                        try { process.Kill(); }
                        catch { }
                        return ScriptCompilationResult.Unavailable(Name, $"Roslyn csc timed out after {_timeoutMilliseconds} ms.");
                    }

                    Thread.Sleep(25);

                    var compilerOutput = (stdout.ToString() + stderr.ToString()).Trim();
                    if (process.ExitCode != 0)
                    {
                        var errors = ParseDiagnostics(compilerOutput);
                        if (errors.Count == 0)
                        {
                            errors.Add(new ScriptCompilationError
                            {
                                line = 0,
                                column = 0,
                                text = string.IsNullOrEmpty(compilerOutput)
                                    ? $"Roslyn csc exited with code {process.ExitCode}."
                                    : compilerOutput,
                                code = "CS0000"
                            });
                        }

                        return ScriptCompilationResult.CompilationFailed(Name, errors, "Roslyn csc reported compilation errors.");
                    }
                }

                if (!File.Exists(outputPath))
                    return ScriptCompilationResult.Unavailable(Name, "Roslyn csc completed without producing an output assembly.");

                var assemblyBytes = File.ReadAllBytes(outputPath);
                var assembly = Assembly.Load(assemblyBytes);
                return ScriptCompilationResult.Success(Name, assembly);
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); }
                catch { }
            }
        }

        internal bool TryResolveToolchain(
            out string compilerHostPath,
            out string cscPath,
            out string monoLibRoot,
            out string error)
        {
            compilerHostPath = _compilerHostPathOverride;
            cscPath = _cscPathOverride;
            monoLibRoot = _monoLibRootOverride;

            if (string.IsNullOrEmpty(compilerHostPath) || string.IsNullOrEmpty(cscPath))
                TryResolvePreferredCompilerHost(out compilerHostPath, out cscPath);
            if (string.IsNullOrEmpty(monoLibRoot))
                monoLibRoot = FindFirstExisting(GetMonoLibRootCandidates());

            if (string.IsNullOrEmpty(compilerHostPath))
            {
                error = "Unity Roslyn compiler host executable was not found.";
                return false;
            }

            if (string.IsNullOrEmpty(cscPath))
            {
                error = "Unity Roslyn compiler was not found.";
                return false;
            }

            // The Mono profile only backfills references the editor has not loaded itself, so a
            // CoreCLR editor without MonoBleedingEdge still compiles against its loaded assemblies.
            error = null;
            return true;
        }

        private string BuildResponseFile(string sourcePath, string outputPath, string monoLibRoot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-nologo");
            sb.AppendLine("-target:library");
            sb.AppendLine("-langversion:preview");
            sb.AppendLine("-nostdlib");
            sb.AppendLine("-optimize-");
            sb.AppendLine("-debug-");
            sb.AppendLine("-unsafe-");
            sb.AppendLine("-out:" + QuoteResponseFileValue(outputPath));

            foreach (var reference in GetReferencePaths(monoLibRoot))
                sb.AppendLine("-r:" + QuoteResponseFileValue(reference));

            sb.AppendLine(QuoteResponseFileValue(sourcePath));
            return sb.ToString();
        }

        private static string BuildCompilerArguments(string cscPath, string responsePath)
        {
            // csc compiles in-process unless /shared is passed. "/shared:false" is not a toggle: it
            // names a compiler-server pipe called "false", and waiting on that server added ~20 s
            // to every dotnet-hosted compile.
            return $"{QuoteArgument(cscPath)} -noconfig @{QuoteArgument(responsePath)}";
        }

        private static bool TryResolvePreferredCompilerHost(out string compilerHostPath, out string cscPath)
        {
            foreach (var root in GetUnityToolRoots())
            {
                var monoPath = Path.Combine(root, "MonoBleedingEdge", "bin", MonoExecutableName());
                var cscExePath = Path.Combine(root, "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn", "csc.exe");
                if (File.Exists(monoPath) && File.Exists(cscExePath))
                {
                    compilerHostPath = monoPath;
                    cscPath = cscExePath;
                    return true;
                }
            }

            return TryResolveDotnetCompilerHost(out compilerHostPath, out cscPath);
        }

        internal static bool TryResolveDotnetCompilerHost(out string compilerHostPath, out string cscPath)
        {
            compilerHostPath = FindFirstExisting(GetDotnetCandidates());
            cscPath = FindFirstExisting(GetCscDllCandidates());
            return !string.IsNullOrEmpty(compilerHostPath) && !string.IsNullOrEmpty(cscPath);
        }

        private static IEnumerable<string> GetReferencePaths(string monoLibRoot)
        {
            var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                try
                {
                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location) || !File.Exists(location))
                        continue;

                    var identity = assembly.GetName().FullName;
                    if (!string.IsNullOrEmpty(identity) && !references.ContainsKey(identity))
                        references.Add(identity, location);
                }
                catch
                {
                }
            }

            AddProfileReferenceIfMissing(references, monoLibRoot, "mscorlib.dll");
            AddProfileReferenceIfMissing(references, monoLibRoot, "System.dll");
            AddProfileReferenceIfMissing(references, monoLibRoot, "System.Core.dll");

            return references.Values
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void AddProfileReferenceIfMissing(
            IDictionary<string, string> references,
            string monoLibRoot,
            string fileName)
        {
            if (references.Values.Any(path =>
                    string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (string.IsNullOrEmpty(monoLibRoot))
                return;

            var path = Path.Combine(monoLibRoot, fileName);
            if (File.Exists(path))
                references[fileName] = path;
        }

        private static List<ScriptCompilationError> ParseDiagnostics(string output)
        {
            var errors = new List<ScriptCompilationError>();
            if (string.IsNullOrEmpty(output))
                return errors;

            var withLocation = new Regex(
                @"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*(?<severity>error|warning)\s*(?<code>CS\d+):\s*(?<text>.*)$",
                RegexOptions.IgnoreCase);
            var withoutLocation = new Regex(
                @"^(?<severity>error|warning)\s*(?<code>CS\d+):\s*(?<text>.*)$",
                RegexOptions.IgnoreCase);

            foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                var match = withLocation.Match(line);
                if (!match.Success)
                    match = withoutLocation.Match(line);
                if (!match.Success ||
                    !string.Equals(match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int.TryParse(match.Groups["line"].Value, out var lineNumber);
                int.TryParse(match.Groups["column"].Value, out var columnNumber);
                errors.Add(new ScriptCompilationError
                {
                    line = lineNumber,
                    column = columnNumber,
                    code = match.Groups["code"].Value,
                    text = match.Groups["text"].Value
                });
            }

            return errors;
        }

        private static IEnumerable<string> GetDotnetCandidates()
        {
            foreach (var root in GetUnityToolRoots())
            {
                yield return Path.Combine(root, "NetCoreRuntime", DotnetExecutableName());
                // Unity 7 replaced NetCoreRuntime/DotNetSdkRoslyn with a full SDK under DotNetSdk.
                yield return Path.Combine(root, "DotNetSdk", DotnetExecutableName());
            }
        }

        private static IEnumerable<string> GetCscDllCandidates()
        {
            foreach (var root in GetUnityToolRoots())
            {
                yield return Path.Combine(root, "DotNetSdkRoslyn", "csc.dll");

                var sdkRoot = Path.Combine(root, "DotNetSdk", "sdk");
                if (!Directory.Exists(sdkRoot))
                    continue;

                var sdkDirectories = Directory.GetDirectories(sdkRoot)
                    .OrderByDescending(path => ParseSdkVersion(Path.GetFileName(path)));
                foreach (var sdkDirectory in sdkDirectories)
                    yield return Path.Combine(sdkDirectory, "Roslyn", "bincore", "csc.dll");
            }
        }

        private static Version ParseSdkVersion(string directoryName)
        {
            var numericPart = directoryName.Split('-')[0];
            return Version.TryParse(numericPart, out var version) ? version : new Version(0, 0);
        }

        private static IEnumerable<string> GetMonoLibRootCandidates()
        {
            var platformSuffix = GetMonoPlatformSuffix();
            foreach (var root in GetUnityToolRoots())
            {
                var monoRoot = Path.Combine(root, "MonoBleedingEdge", "lib", "mono");
                yield return Path.Combine(monoRoot, "net_4_x-" + platformSuffix);
                yield return Path.Combine(monoRoot, "unityjit-" + platformSuffix);
                yield return Path.Combine(monoRoot, "unity");
                yield return Path.Combine(monoRoot, "4.8-api");
                yield return Path.Combine(monoRoot, "4.7.2-api");
                yield return Path.Combine(monoRoot, "4.7.1-api");
            }
        }

        private static IEnumerable<string> GetUnityToolRoots()
        {
            var roots = new List<string>();
            var applicationContentsPath = EditorApplication.applicationContentsPath;
            AddRoot(roots, applicationContentsPath);

            try
            {
                var directory = new DirectoryInfo(applicationContentsPath);
                if (string.Equals(directory.Name, "Resources", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(directory.Name, "Scripting", StringComparison.OrdinalIgnoreCase))
                {
                    AddRoot(roots, directory.Parent?.FullName);
                    AddRoot(roots, directory.Parent?.Parent?.FullName);
                }
            }
            catch
            {
            }

            var baseRoots = roots.ToArray();
            for (var i = 0; i < baseRoots.Length; i++)
            {
                var root = baseRoots[i];
                AddRoot(roots, Path.Combine(root, "Resources", "Scripting"));
            }

            return roots;
        }

        private static void AddRoot(List<string> roots, string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            string normalized;
            try { normalized = Path.GetFullPath(path); }
            catch { return; }

            if (!roots.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                roots.Add(normalized);
        }

        private static string GetMonoPlatformSuffix()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    return "win32";
                case RuntimePlatform.LinuxEditor:
                    return "linux";
                default:
                    return "macos";
            }
        }

        private static string DotnetExecutableName()
        {
            return Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
        }

        private static string MonoExecutableName()
        {
            return Application.platform == RuntimePlatform.WindowsEditor ? "mono.exe" : "mono";
        }

        private static string FindFirstExisting(IEnumerable<string> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && (File.Exists(candidate) || Directory.Exists(candidate)))
                    return candidate;
            }

            return null;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string QuoteResponseFileValue(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class CodeDomScriptCompiler : IScriptCompiler
    {
        private static Type _providerType;
        private static Type _paramsType;
        private static bool _typesResolved;
        private static string _typeLoadError;

        public string Name => "CodeDom";

        public ScriptCompilationResult Compile(string code)
        {
            if (!EnsureCodeDomTypes())
                return ScriptCompilationResult.Unavailable(Name, _typeLoadError);

            var provider = Activator.CreateInstance(_providerType);
            try
            {
                var parameters = Activator.CreateInstance(_paramsType);
                _paramsType.GetProperty("GenerateInMemory")?.SetValue(parameters, true, null);
                _paramsType.GetProperty("GenerateExecutable")?.SetValue(parameters, false, null);
                _paramsType.GetProperty("TreatWarningsAsErrors")?.SetValue(parameters, false, null);

                var referencedAssembliesProperty = _paramsType.GetProperty("ReferencedAssemblies");
                var referencedAssemblies = referencedAssembliesProperty?.GetValue(parameters, null);
                var addMethod = referencedAssemblies?.GetType().GetMethod("Add", new[] { typeof(string) });

                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic)
                        continue;

                    try
                    {
                        var location = assembly.Location;
                        if (!string.IsNullOrEmpty(location) && File.Exists(location) && referenced.Add(location))
                            addMethod?.Invoke(referencedAssemblies, new object[] { location });
                    }
                    catch
                    {
                    }
                }

                var compileMethod = _providerType.GetMethod("CompileAssemblyFromSource", new[] { _paramsType, typeof(string[]) });
                var results = compileMethod?.Invoke(provider, new object[] { parameters, new[] { code } });
                if (results == null)
                    return ScriptCompilationResult.Unavailable(Name, "CodeDom returned a null compilation result.");

                var resultsType = results.GetType();
                var errors = resultsType.GetProperty("Errors")?.GetValue(results, null);
                var hasErrors = (bool)(errors?.GetType().GetProperty("HasErrors")?.GetValue(errors, null) ?? false);

                if (hasErrors)
                    return ScriptCompilationResult.CompilationFailed(Name, GetCodeDomErrors(errors), "CodeDom reported compilation errors.");

                var compiledAssembly = resultsType.GetProperty("CompiledAssembly")?.GetValue(results, null) as Assembly;
                return compiledAssembly == null
                    ? ScriptCompilationResult.Unavailable(Name, "CodeDom did not return a compiled assembly.")
                    : ScriptCompilationResult.Success(Name, compiledAssembly);
            }
            finally
            {
                if (provider is IDisposable disposable)
                    disposable.Dispose();
            }
        }

        private static List<ScriptCompilationError> GetCodeDomErrors(object errors)
        {
            var errorList = new List<ScriptCompilationError>();
            foreach (var error in (IEnumerable)errors)
            {
                var errorType = error.GetType();
                var isWarning = (bool)(errorType.GetProperty("IsWarning")?.GetValue(error, null) ?? false);
                if (isWarning)
                    continue;

                errorList.Add(new ScriptCompilationError
                {
                    line = (int)(errorType.GetProperty("Line")?.GetValue(error, null) ?? 0),
                    column = (int)(errorType.GetProperty("Column")?.GetValue(error, null) ?? 0),
                    code = errorType.GetProperty("ErrorNumber")?.GetValue(error, null)?.ToString(),
                    text = errorType.GetProperty("ErrorText")?.GetValue(error, null)?.ToString() ?? "Unknown error"
                });
            }

            return errorList;
        }

        private static bool EnsureCodeDomTypes()
        {
            if (_typesResolved)
                return _providerType != null;

            _typesResolved = true;

            try
            {
                _providerType = Type.GetType("Microsoft.CSharp.CSharpCodeProvider, System");
                _paramsType = Type.GetType("System.CodeDom.Compiler.CompilerParameters, System");

                if (_providerType == null || _paramsType == null)
                {
                    try
                    {
                        var codeDomAssembly = Assembly.Load("System.CodeDom");
                        _providerType = _providerType ?? codeDomAssembly.GetType("Microsoft.CSharp.CSharpCodeProvider");
                        _paramsType = _paramsType ?? codeDomAssembly.GetType("System.CodeDom.Compiler.CompilerParameters");
                    }
                    catch
                    {
                    }
                }

                if (_providerType == null || _paramsType == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.IsDynamic)
                            continue;

                        try
                        {
                            _providerType = _providerType ?? assembly.GetType("Microsoft.CSharp.CSharpCodeProvider");
                            _paramsType = _paramsType ?? assembly.GetType("System.CodeDom.Compiler.CompilerParameters");
                            if (_providerType != null && _paramsType != null)
                                break;
                        }
                        catch
                        {
                        }
                    }
                }

                if (_providerType == null || _paramsType == null)
                {
                    _typeLoadError = "CSharpCodeProvider or CompilerParameters types not found in any loaded assembly";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _typeLoadError = ex.Message;
                return false;
            }
        }
    }
}
