// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using GameBooom.Editor.DI;
using GameBooom.Editor.Services;
using UnityEditor;
using UnityEngine;

namespace GameBooom.Editor.Tools.Builtins
{
    [ToolProvider("Compilation")]
    internal static class CompilationFunctions
    {
        [Description("Force Unity to refresh and wait until script compilation is complete without blocking the editor thread. " +
                     "Use this after editing scripts to ensure the latest code is active before entering Play Mode. " +
                     "Returns compilation errors if any, or a success message.")]
        [ReadOnlyTool]
        public static async Task<string> WaitForCompilation(
            [ToolParam("Force a reimport/refresh before waiting", Required = false)] bool force_refresh = true,
            [ToolParam("Maximum seconds to wait for compilation", Required = false)] int timeout_seconds = 30)
        {
            try
            {
                timeout_seconds = Mathf.Clamp(timeout_seconds, 5, 120);

                var compilationService = GetCompilationService();
                if (compilationService == null)
                    return "Error: Compilation service is unavailable";

                var startTime = DateTime.UtcNow;
                bool completed = await compilationService
                    .WaitForCompilationAsync(force_refresh, timeout_seconds)
                    .ConfigureAwait(false);

                if (!completed)
                    return $"Error: Compilation timed out after {timeout_seconds}s (still compiling)";

                var issues = compilationService.GetCompilationErrors();
                if (!string.Equals(issues, "No compilation errors detected.", StringComparison.Ordinal))
                    return issues;

                double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                return $"Compilation complete ({elapsed:F1}s). No errors detected.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [Description("Request Unity to recompile scripts asynchronously (non-blocking). " +
                     "Call get_compilation_errors or wait_for_compilation afterwards to inspect the result.")]
        [ReadOnlyTool]
        public static string RequestRecompile()
        {
            try
            {
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                return "Script recompilation requested. Call get_compilation_errors or wait_for_compilation after it finishes.";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [Description("Get the latest Unity script compilation errors from the most recent compilation cycle.")]
        [ReadOnlyTool]
        public static string GetCompilationErrors(
            [ToolParam("Maximum number of issues to return", Required = false)] int max_entries = 50,
            [ToolParam("Include warnings in addition to errors", Required = false)] bool include_warnings = false)
        {
            var compilationService = GetCompilationService();
            if (compilationService == null)
                return "Error: Compilation service is unavailable";

            return compilationService.GetCompilationErrors(max_entries, include_warnings);
        }

        private static ICompilationService GetCompilationService()
        {
            return RootScopeServices.Services?.GetService(typeof(ICompilationService)) as ICompilationService;
        }
    }
}
