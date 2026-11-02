// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.Settings;
using Funplay.Editor.State;
using Funplay.Editor.Threading;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Bridges MCP tool calls to Funplay's FunctionInvokerController.
    /// Handles thread marshalling and approval workflow.
    /// </summary>
    internal class MCPExecutionBridge
    {
        private readonly IEditorThreadHelper _threadHelper;
        private readonly ISettingsController _settings;
        private readonly IStateController _stateController;
        private readonly FunctionInvokerController _invoker;
        private readonly MCPInteractionLog _interactionLog;

        public MCPExecutionBridge(
            IEditorThreadHelper threadHelper,
            ISettingsController settings,
            IStateController stateController,
            FunctionInvokerController invoker,
            MCPInteractionLog interactionLog)
        {
            _threadHelper = threadHelper ?? throw new ArgumentNullException(nameof(threadHelper));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _stateController = stateController ?? throw new ArgumentNullException(nameof(stateController));
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            _interactionLog = interactionLog;
            ExecutionJobService.Initialize(); // Warm Unity-owned paths/context on the constructing Editor thread.
        }

        public async Task<string> ExecuteToolAsync(
            string toolName,
            Dictionary<string, object> arguments,
            CancellationToken ct,
            string routeProjectId = null)
        {
            if (string.Equals(toolName, "execute_code", StringComparison.OrdinalIgnoreCase) && !ToolRegistry.HasManualOverride("execute_code"))
                return await ExecuteDurableAsync(arguments, ct, routeProjectId).ConfigureAwait(false);
            if (string.Equals(toolName, "get_task", StringComparison.OrdinalIgnoreCase) && ExecutionJobService.IsExecutionRead(arguments) && !ToolRegistry.HasManualOverride("get_task"))
                return await ReadExecutionAsync(arguments, ct, routeProjectId).ConfigureAwait(false);
            bool tracksPending = !string.Equals(toolName, "get_task", StringComparison.OrdinalIgnoreCase);
            var queryClock = System.Diagnostics.Stopwatch.StartNew();
            var invocation = _threadHelper.ExecuteAsyncOnEditorThreadAsync(async () =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var functionCall = new FunctionCall
                    {
                        Id = Guid.NewGuid().ToString(),
                        FunctionName = toolName
                    };

                    foreach (var kvp in arguments)
                    {
                        if (kvp.Key == MCPProjectContext.ExpectedArgument) continue;
                        functionCall.Parameters[kvp.Key] = ConvertArgumentToString(kvp.Value);
                    }

                    ToolRegistry.ManualTools.TryGetValue(toolName, out var manualTool);
                    var method = ToolRegistry.GetMethod(toolName);
                    if (method == null && manualTool == null)
                    {
                        var error = ToolResultFormatter.Error("UNKNOWN_TOOL", new { tool = toolName });
                        return error;
                    }
                    if (!ToolRegistry.IsEnabled(toolName))
                        return ToolResultFormatter.Error("TOOL_DISABLED", new { tool = toolName });

                    var profile = MCPToolExportPolicy.Parse(_settings.MCPToolExportProfile);
                    if (!MCPToolExportPolicy.IsToolAllowed(
                            toolName,
                            profile,
                            _settings.MCPCoreToolsConfigured,
                            _settings.MCPCoreTools,
                            _settings.MCPFullToolsConfigured,
                            _settings.MCPFullTools))
                    {
                        var error = ToolResultFormatter.Error("TOOL_NOT_EXPOSED", new
                        {
                            tool = toolName,
                            profile = MCPToolExportPolicy.ToSettingValue(profile)
                        });
                        return error;
                    }

                    functionCall.IsReadOnly = method != null &&
                        method.GetCustomAttribute<ReadOnlyToolAttribute>() != null;
                    arguments.TryGetValue(MCPProjectContext.ExpectedArgument, out var expectedContext);
                    if (expectedContext != null && !(expectedContext is string))
                        return ToolResultFormatter.Error("INVALID_EXPECTED_CONTEXT");
                    var contextError = MCPProjectContext.Validate(MCPProjectContext.Current, routeProjectId,
                        expectedContext as string, !functionCall.IsReadOnly);
                    if (contextError != null)
                        return ToolResultFormatter.Error(contextError, new { context = MCPProjectContext.Current,
                            hint = "Verify the intended project independently. Reconfigure its project-scoped URL, or pass expected_context from that verified Editor. No tool was executed." });

                    if (tracksPending)
                    {
                        DomainReloadHandler.ResetResumeCounter();
                        _stateController.SetState(FunplayState.ExecutingFunction);
                        DomainReloadHandler.SavePendingFunction(functionCall);
                    }

                    PluginDebugLogger.Log($"[Funplay MCP Server] Executing tool: {toolName}");
                    string result;
                    var previousAccess = ExecutionJobService.InvocationAccess.Value;
                    try
                    {
                        if (string.Equals(toolName, "replay_execute_code", StringComparison.OrdinalIgnoreCase))
                            ExecutionJobService.InvocationAccess.Value = () => AllowedCached("replay_execute_code");
                        result = await _invoker.InvokeAsync(functionCall);
                    }
                    finally { ExecutionJobService.InvocationAccess.Value = previousAccess; }
                    if (tracksPending) DomainReloadHandler.CompletePendingFunction(_stateController);

                    if (!string.IsNullOrEmpty(functionCall.Error))
                    {
                        var errMsg = ToolResultFormatter.Error("TOOL_ERROR",
                            new { tool = toolName, message = functionCall.Error });
                        return errMsg;
                    }

                    var resultText = result ?? "Completed successfully";
                    return resultText;
                }
                catch (Exception ex)
                {
                    if (tracksPending)
                    {
                        DomainReloadHandler.ClearPendingFunction();
                        _stateController.ClearState();
                    }
                    var exError = ToolResultFormatter.Error("TOOL_EXCEPTION",
                        new { tool = toolName, message = ex.Message });
                    Debug.LogError($"[Funplay MCP Server] Error executing tool '{toolName}': {ex.Message}\n{ex.StackTrace}");
                    return exError;
                }
            });
            double? remainingWaitSeconds = null;
            string initialResult;
            if (!tracksPending)
            {
                int seconds = MCPTaskWaiter.QueryBudget(arguments);
                initialResult = await MCPTaskWaiter.InitialReadAsync(invocation, Math.Max(1000, seconds * 1000), ct).ConfigureAwait(false);
                remainingWaitSeconds = Math.Max(0, seconds - queryClock.Elapsed.TotalSeconds);
            }
            else initialResult = await invocation.ConfigureAwait(false);
            // The mutating invocation is already complete. Waiting only re-reads its receipt;
            // it must not occupy the pending-function slot or block Editor update processing.
            var finalResult = await MCPTaskWaiter.CompleteAsync(toolName, arguments, initialResult, _threadHelper, ct, remainingWaitSeconds).ConfigureAwait(false);
            finalResult = MCPProjectContext.Stamp(finalResult);
            // Logging must not re-block an already timed-out status response on the same busy
            // Editor queue. It remains one final activity entry, delivered when the UI can run.
            var logging = _threadHelper.ExecuteOnEditorThreadAsync(() => _interactionLog?.Add(toolName,
                ToolResultFormatter.IsError(finalResult) ? MCPToolCallStatus.Error : MCPToolCallStatus.Success,
                VisualCoordinates.WithoutInlineImage(finalResult)));
            _ = logging.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return finalResult;
        }

        private bool AllowedCached(string tool) => ToolRegistry.IsEnabled(tool) && MCPToolExportPolicy.IsToolAllowed(tool,
            MCPToolExportPolicy.Parse(_settings.MCPToolExportProfile), _settings.MCPCoreToolsConfigured, _settings.MCPCoreTools,
            _settings.MCPFullToolsConfigured, _settings.MCPFullTools);

        private string GuardCached(string tool, Dictionary<string, object> arguments, string route, bool write)
        {
            if (!AllowedCached(tool)) return ToolResultFormatter.Error("TOOL_NOT_EXPOSED", new { tool });
            arguments.TryGetValue(MCPProjectContext.ExpectedArgument, out var expected);
            if (expected != null && !(expected is string)) return ToolResultFormatter.Error("INVALID_EXPECTED_CONTEXT");
            var error = MCPProjectContext.Validate(MCPProjectContext.Current, route, expected as string, write);
            return error == null ? null : ToolResultFormatter.Error(error, new { context = MCPProjectContext.Current, executed = false });
        }
        private T Argument<T>(Dictionary<string, object> arguments, string name, T fallback = default)
        {
            if (!arguments.TryGetValue(name, out var value)) return fallback;
            if (value == null && (!typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) != null)) return default;
            if (value is T typed) return typed;
            if (typeof(T) == typeof(string))
            { if (value is string text) return (T)(object)text; throw new ArgumentException(name); }
            try { return (T)_invoker.ConvertValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), typeof(T)); }
            catch { throw new ArgumentException(name); }
        }
        private async Task<string> ExecuteDurableAsync(Dictionary<string, object> arguments, CancellationToken ct, string route)
        {
            string guarded = GuardCached("execute_code", arguments, route, true);
            if (guarded != null) return guarded;
            object result;
            try
            {
                ct.ThrowIfCancellationRequested();
                string action = Argument(arguments, "action", "start")?.Trim().ToLowerInvariant();
                int wait = Argument(arguments, "wait_seconds", 2);
                if (TaskStatusService.ValidateWait(wait) is object badWait) return FunctionInvokerController.SerializeResult(badWait);
                string code = Argument<string>(arguments, "code"), id = Argument<string>(arguments, "task_id");
                bool confirm = Argument(arguments, "confirm_forget", false);
                if (action != "start")
                {
                    if (code != null || arguments.ContainsKey("request_key")) return ToolResultFormatter.Error("INVALID_EXECUTION_CONTROL_ARGUMENTS");
                    if (action == "forget" && MCPToolExportPolicy.Parse(_settings.MCPToolExportProfile) != MCPToolExportProfile.Full)
                        return ToolResultFormatter.Error("EXECUTION_FORGET_REQUIRES_FULL", new { executed = false });
                    result = ExecutionJobService.Control(action, id, confirm, AllowedCached);
                }
                else
                {
                    if (id != null || confirm) return ToolResultFormatter.Error("INVALID_EXECUTION_START_ARGUMENTS");
                    string key = Argument<string>(arguments, "request_key");
                    var options = new ExecutionOptions { Code = code, Key = key,
                        PreferredNamespaces = Argument<string>(arguments, "preferred_namespaces"),
                        Safety = Argument<bool?>(arguments, "safety_checks") ?? _settings.ExecuteCodeSafetyChecksEnabled,
                        Strict = _settings.ExecuteCodeStrictFilesystemSafetyEnabled,
                        InjectNamespaces = _settings.ExecuteCodeProjectNamespaceInjectionEnabled,
                        SkipRefresh = Argument(arguments, "skip_refresh", false), TimeoutSeconds = Argument(arguments, "timeout_seconds", 300),
                        // Unkeyed starts deliver their durable receipt before waiting across reloads.
                        WaitSeconds = key == null ? 0 : wait, StillAllowed = () => AllowedCached("execute_code") };
                    result = await ExecutionJobService.StartAsync(options, ct).ConfigureAwait(false);
                    if (key == null && result is Newtonsoft.Json.Linq.JObject json && json["data"]?["task"] != null)
                        json["data"]["task"]["wait_reason"] = "receipt_first";
                }
            }
            catch (ArgumentException ex) { return ToolResultFormatter.Error("INVALID_PARAM", new { param = ex.Message, executed = false }); }
            return LogCached("execute_code", FunctionInvokerController.SerializeResult(result, "execute_code"));
        }

        private async Task<string> ReadExecutionAsync(Dictionary<string, object> arguments, CancellationToken ct, string route)
        {
            string guarded = GuardCached("get_task", arguments, route, false); if (guarded != null) return guarded;
            try
            {
                int seconds = Argument(arguments, "wait_seconds", 20);
                var invalid = TaskStatusService.ValidateWait(seconds); if (invalid != null) return FunctionInvokerController.SerializeResult(invalid);
                string id = Argument<string>(arguments, "task_id"), kind = Argument<string>(arguments, "kind"), key = Argument<string>(arguments, "request_key");
                var error = TaskStatusService.Resolve(ref id, ref kind, key); if (error != null) return FunctionInvokerController.SerializeResult(error);
                string after = Argument<string>(arguments, "after_revision"), format = Argument(arguments, "result_format", "summary"), path = Argument<string>(arguments, "result_path");
                int offset = Argument(arguments, "offset", 0), limit = Argument(arguments, "limit", 100);
                bool diagnostics = Argument(arguments, "include_diagnostics", false);
                Newtonsoft.Json.Linq.JObject Read() => ExecutionJobService.Query(id, key, after, offset, limit, format, path, diagnostics, AllowedCached);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                // Slow/contended local reads must not consume an unbounded response wait.
                // These workers inspect stored DTOs only, never Unity objects or user code.
                var initialText = await MCPTaskWaiter.InitialReadAsync(Task.Run(() => Read().ToString(Newtonsoft.Json.Formatting.None)), Math.Max(1000, seconds * 1000), ct, "execution_store").ConfigureAwait(false);
                var initial = Newtonsoft.Json.Linq.JObject.Parse(initialText);
                var result = await MCPTaskWaiter.WaitAsync(initial, Math.Max(0, seconds - watch.Elapsed.TotalSeconds), after, () => Task.Run(Read),
                    () => watch.Elapsed.TotalSeconds, (ms, cancellation) => Task.Delay(ms, cancellation), ct).ConfigureAwait(false);
                return LogCached("get_task", result.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (ArgumentException ex) { return ToolResultFormatter.Error("INVALID_PARAM", new { param = ex.Message }); }
        }

        private string LogCached(string tool, string result)
        {
            result = MCPProjectContext.Stamp(result);
            var text = result;
            var logging = _threadHelper.ExecuteOnEditorThreadAsync(() => _interactionLog?.Add(tool,
                ToolResultFormatter.IsError(text) ? MCPToolCallStatus.Error : MCPToolCallStatus.Success,
                VisualCoordinates.WithoutInlineImage(text)));
            _ = logging.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return text;
        }

        private string ConvertArgumentToString(object value)
        {
            if (value == null) return string.Empty;
            if (value is string strValue) return UnescapeXmlEntities(strValue);
            if (value is bool boolValue) return boolValue ? "true" : "false";
            if (value is int || value is long || value is float || value is double) return value.ToString();
            if (value is Dictionary<string, object> dict) return SimpleJsonHelper.Serialize(dict);
            if (value is System.Collections.IList list)
            {
                var items = new List<object>();
                foreach (var item in list) items.Add(item);
                return SimpleJsonHelper.Serialize(items);
            }
            return value.ToString();
        }

        private string UnescapeXmlEntities(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            return str
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'");
        }
    }
}
