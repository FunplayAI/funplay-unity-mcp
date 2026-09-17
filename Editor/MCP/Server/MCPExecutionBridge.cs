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
        }

        public async Task<string> ExecuteToolAsync(
            string toolName,
            Dictionary<string, object> arguments,
            CancellationToken ct)
        {
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
                        functionCall.Parameters[kvp.Key] = ConvertArgumentToString(kvp.Value);

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

                    if (tracksPending)
                    {
                        DomainReloadHandler.ResetResumeCounter();
                        _stateController.SetState(FunplayState.ExecutingFunction);
                        DomainReloadHandler.SavePendingFunction(functionCall);
                    }

                    PluginDebugLogger.Log($"[Funplay MCP Server] Executing tool: {toolName}");
                    var result = await _invoker.InvokeAsync(functionCall);
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
            // Logging must not re-block an already timed-out status response on the same busy
            // Editor queue. It remains one final activity entry, delivered when the UI can run.
            var logging = _threadHelper.ExecuteOnEditorThreadAsync(() => _interactionLog?.Add(toolName,
                ToolResultFormatter.IsError(finalResult) ? MCPToolCallStatus.Error : MCPToolCallStatus.Success,
                VisualCoordinates.WithoutInlineImage(finalResult)));
            _ = logging.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return finalResult;
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
