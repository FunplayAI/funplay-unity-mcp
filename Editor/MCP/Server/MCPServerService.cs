// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.MCP;
using Funplay.Editor.Services;
using Funplay.Editor.Settings;
using Funplay.Editor.State;
using Funplay.Editor.Threading;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Main MCP server service singleton.
    /// Manages server lifecycle, coordinates transport, handler, exporter, and bridge.
    /// </summary>
    internal class MCPServerService : IDisposable
    {
        private readonly ISettingsController _settings;
        private readonly IEditorThreadHelper _threadHelper;
        private readonly IStateController _stateController;
        private readonly IEditorContextBuilder _contextBuilder;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ICompilationService _compilationService;
        private readonly FunctionInvokerController _invoker;
        private readonly object _lifecycleLock = new object();

        private IMCPTransport _transport;
        private MCPRequestHandler _requestHandler;
        private MCPResourceProvider _resourceProvider;
        private Task<bool> _startTask;
        private CancellationTokenSource _startCts;
        private int _lifecycleVersion;
        private bool _isRunning;
        private bool _disposed;
        private bool _recoveryChecked;
        private bool _restartScheduled;
        private bool _restartInProgress;
        private string _toolExposureSetting;
        private string _transportSetting;

        public bool IsRunning
        {
            get
            {
                lock (_lifecycleLock)
                {
                    return _isRunning;
                }
            }
        }
        public bool IsAttachedToExistingTransport
        {
            get
            {
                lock (_lifecycleLock)
                {
                    return _transport?.IsAttachedToExistingServer == true;
                }
            }
        }
        public int Port { get; private set; }
        public MCPInteractionLog InteractionLog { get; }

        public MCPServerService(
            ISettingsController settings,
            IEditorThreadHelper threadHelper,
            IStateController stateController,
            IEditorContextBuilder contextBuilder,
            IApplicationPaths applicationPaths,
            ICompilationService compilationService,
            FunctionInvokerController invoker)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _threadHelper = threadHelper ?? throw new ArgumentNullException(nameof(threadHelper));
            _stateController = stateController ?? throw new ArgumentNullException(nameof(stateController));
            _contextBuilder = contextBuilder;
            _applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
            _compilationService = compilationService ?? throw new ArgumentNullException(nameof(compilationService));
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));

            Port = _settings.MCPServerPort;
            _toolExposureSetting = BuildToolExposureSetting();
            _transportSetting = BuildTransportSetting();
            InteractionLog = new MCPInteractionLog();
            _settings.OnSettingsChanged += HandleSettingsChanged;
            DomainReloadHandler.Register(_stateController);
        }

        public Task<bool> StartAsync(CancellationToken ct = default)
        {
            if (Application.isBatchMode)
            {
                Debug.LogWarning("[Funplay MCP Server] Skipping server start in Unity batch mode process.");
                return Task.FromResult(false);
            }

            bool cleanupStaleState = false;
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    Debug.LogWarning("[Funplay MCP Server] Cannot start: service is disposed");
                    return Task.FromResult(false);
                }

                if (_isRunning && _transport?.IsRunning == true)
                {
                    PluginDebugLogger.Log("[Funplay MCP Server] Server is already running");
                    return Task.FromResult(true);
                }

                if (_startTask != null)
                {
                    PluginDebugLogger.Log("[Funplay MCP Server] Server start is already in progress");
                    return _startTask;
                }

                cleanupStaleState = _isRunning || _transport != null || _requestHandler != null || _resourceProvider != null;
            }

            if (cleanupStaleState)
            {
                Debug.LogWarning("[Funplay MCP Server] Server lifecycle state was stale; cleaning up before restart.");
                StopSync();
            }

            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    Debug.LogWarning("[Funplay MCP Server] Cannot start: service is disposed");
                    return Task.FromResult(false);
                }

                if (_isRunning && _transport?.IsRunning == true)
                {
                    PluginDebugLogger.Log("[Funplay MCP Server] Server is already running");
                    return Task.FromResult(true);
                }

                if (_startTask != null)
                {
                    PluginDebugLogger.Log("[Funplay MCP Server] Server start is already in progress");
                    return _startTask;
                }

                _lifecycleVersion++;
                _startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var startCts = _startCts;
                var startTask = StartCoreAsync(_lifecycleVersion, startCts);
                _startTask = startTask;
                _ = startTask.ContinueWith(
                    _ => ClearCompletedStartTask(startTask, startCts),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return startTask;
            }
        }

        private async Task<bool> StartCoreAsync(int lifecycleVersion, CancellationTokenSource startCts)
        {
            IMCPTransport transport = null;
            MCPResourceProvider resourceProvider = null;
            var assigned = false;
            try
            {
                var startupPort = ResolveStartupPort();
                var toolExposureSetting = BuildToolExposureSetting();
                PluginDebugLogger.Log("[Funplay MCP Server] Starting server...");

                var serverName = "Funplay MCP Server - " + Application.productName;
                var projectIdentity = FunplayProjectIdentity.FromProjectPath(_applicationPaths.ProjectPath);
                transport = CreateTransport(startupPort, serverName, projectIdentity);
                var toolExporter = new MCPToolExporter(_settings);
                MCPToolListChangeNotifier.CheckForChanges(toolExporter);
                var executionBridge = new MCPExecutionBridge(_threadHelper, _settings, _stateController, _invoker, InteractionLog);
                resourceProvider = new MCPResourceProvider(_contextBuilder, _applicationPaths, InteractionLog);
                var promptProvider = new MCPPromptProvider(Application.productName, _applicationPaths.ProjectPath);
                var requestHandler = new MCPRequestHandler(
                    toolExporter,
                    executionBridge,
                    resourceProvider,
                    promptProvider,
                    serverName,
                    PackageVersionUtility.CurrentVersion,
                    projectIdentity);

                transport.OnRequestReceived += HandleRequestReceived;

                lock (_lifecycleLock)
                {
                    if (!_disposed && lifecycleVersion == _lifecycleVersion)
                    {
                        Port = startupPort;
                        _toolExposureSetting = toolExposureSetting;
                        _transportSetting = BuildTransportSetting();
                        _transport = transport;
                        _resourceProvider = resourceProvider;
                        _requestHandler = requestHandler;
                        assigned = true;
                    }
                }

                if (!assigned)
                {
                    DisposeUnassignedStartState(transport, resourceProvider);
                    return false;
                }

                var started = await transport.StartAsync(startCts.Token);
                if (started)
                {
                    var shouldDisposeStartedTransport = false;
                    lock (_lifecycleLock)
                    {
                        if (_disposed || lifecycleVersion != _lifecycleVersion || !ReferenceEquals(_transport, transport))
                            shouldDisposeStartedTransport = true;
                        else
                            _isRunning = true;
                    }

                    if (shouldDisposeStartedTransport)
                    {
                        CleanupServerState(transport);
                        return false;
                    }

                    if (transport.IsAttachedToExistingServer)
                    {
                        PluginDebugLogger.Log($"[Funplay] MCP Server attached to existing listener on http://127.0.0.1:{Port}/");
                    }
                    else
                    {
                        PluginDebugLogger.Log($"[Funplay] MCP Server started on http://127.0.0.1:{Port}/ If this tool saves you time, please consider giving it a Star on GitHub: https://github.com/FunplayAI/funplay-unity-mcp");
                    }
                    ExternalSyncRecoveryTracker.TryCompletePendingRecovery();
                    CheckForInterruptedExecution();
                    return true;
                }

                CleanupServerState(transport);
                Debug.LogError("[Funplay MCP Server] Failed to start transport");
                return false;
            }
            catch (OperationCanceledException)
            {
                if (assigned)
                    CleanupServerState(transport);
                else
                    DisposeUnassignedStartState(transport, resourceProvider);
                return false;
            }
            catch (Exception ex)
            {
                if (assigned)
                    CleanupServerState(transport);
                else
                    DisposeUnassignedStartState(transport, resourceProvider);
                Debug.LogError($"[Funplay MCP Server] Failed to start: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private void ClearCompletedStartTask(Task<bool> completedTask, CancellationTokenSource startCts)
        {
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_startTask, completedTask))
                    _startTask = null;

                if (ReferenceEquals(_startCts, startCts))
                    _startCts = null;
            }

            startCts.Dispose();
        }

        public Task StopAsync()
        {
            StopSync();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Synchronously stop the server. Required during
        /// <c>AssemblyReloadEvents.beforeAssemblyReload</c> and from <see cref="Dispose"/>:
        /// Unity unloads the AppDomain immediately after these callbacks return and does not
        /// await fire-and-forget tasks, which would leave the transport bound to the port.
        /// </summary>
        public void StopSync()
        {
            CancellationTokenSource startCtsToCancel;
            lock (_lifecycleLock)
            {
                _lifecycleVersion++;
                startCtsToCancel = _startCts;
                _startCts = null;
                _startTask = null;
            }

            startCtsToCancel?.Cancel();

            if (!CleanupServerState())
                return;

            try
            {
                PluginDebugLogger.Log("[Funplay] MCP Server stopped");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error stopping server: {ex.Message}");
            }
        }

        private bool CleanupServerState(IMCPTransport expectedTransport = null)
        {
            IMCPTransport transportToDispose;
            MCPResourceProvider resourceProviderToDispose;
            bool hadState;

            lock (_lifecycleLock)
            {
                if (expectedTransport != null &&
                    _transport != null &&
                    !ReferenceEquals(_transport, expectedTransport))
                {
                    return false;
                }

                transportToDispose = _transport ?? expectedTransport;
                resourceProviderToDispose = _resourceProvider;
                hadState = _isRunning || _transport != null || _requestHandler != null || _resourceProvider != null || expectedTransport != null;

                _transport = null;
                _requestHandler = null;
                _resourceProvider = null;
                _isRunning = false;
            }

            if (transportToDispose != null)
            {
                transportToDispose.OnRequestReceived -= HandleRequestReceived;
                transportToDispose.Stop();
                transportToDispose.Dispose();
            }

            resourceProviderToDispose?.Dispose();
            return hadState;
        }

        private void DisposeUnassignedStartState(IMCPTransport transport, MCPResourceProvider resourceProvider)
        {
            if (transport != null)
            {
                transport.OnRequestReceived -= HandleRequestReceived;
                transport.Stop();
                transport.Dispose();
            }

            resourceProvider?.Dispose();
        }

        private async void HandleRequestReceived(MCPRequest request, Action<MCPResponse> sendResponse)
        {
            try
            {
                MCPRequestHandler requestHandler;
                lock (_lifecycleLock)
                {
                    requestHandler = _requestHandler;
                }

                if (requestHandler == null)
                {
                    sendResponse(new MCPResponse
                    {
                        Id = request?.Id,
                        Error = new MCPError { Code = -32000, Message = "MCP server is stopping or not ready." }
                    });
                    return;
                }

                var response = await _threadHelper.ExecuteAsyncOnEditorThreadAsync(
                    async () =>
                    {
                        var redeliveryResponse = TryCreateBrokerRedeliveryResponse(request);
                        if (redeliveryResponse != null)
                            return redeliveryResponse;

                        return await requestHandler.HandleRequestAsync(request, default);
                    });
                sendResponse(response);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error handling request: {ex.Message}");
                sendResponse(new MCPResponse
                {
                    Id = request?.Id,
                    Error = new MCPError { Code = -32603, Message = $"Internal error: {ex.Message}" }
                });
            }
        }

        private void HandleSettingsChanged()
        {
            if (_disposed) return;

            var portChanged = _settings.MCPServerPort != Port;
            var toolExposureSetting = BuildToolExposureSetting();
            var transportSetting = BuildTransportSetting();
            var toolExposureChanged = !string.Equals(toolExposureSetting, _toolExposureSetting, StringComparison.Ordinal);
            var transportChanged = !string.Equals(transportSetting, _transportSetting, StringComparison.Ordinal);

            if ((portChanged || toolExposureChanged || transportChanged) && _isRunning)
            {
                PluginDebugLogger.Log("[Funplay MCP Server] Server settings changed, restarting MCP transport...");
                Port = _settings.MCPServerPort;
                _toolExposureSetting = toolExposureSetting;
                _transportSetting = transportSetting;
                ScheduleRestart();
            }
        }

        private IMCPTransport CreateTransport(int startupPort, string serverName, string projectIdentity)
        {
            if (_settings.MCPBrokerModeEnabled)
            {
                if (MCPBrokerProcessManager.EnsureRunning(startupPort, _settings.MCPBrokerMonoPath) &&
                    MCPBrokerProcessManager.TryGetConnectionInfo(startupPort, out var broker))
                {
                    return new MCPBrokerClientTransport(startupPort, broker.Token);
                }

                Debug.LogWarning(
                    "[Funplay MCP Server] Broker mode requested but broker could not start (" +
                    (MCPBrokerProcessManager.LastError ?? "unknown error") +
                    "); falling back to in-process HTTP transport.");
            }
            else
            {
                MCPBrokerProcessManager.Stop();
            }

            return new HttpMCPTransport(startupPort, serverName, projectIdentity);
        }

        private string BuildToolExposureSetting()
        {
            return string.Join("|",
                _settings.MCPToolExportProfile ?? string.Empty,
                _settings.MCPCoreToolsConfigured ? "core=custom" : "core=default",
                string.Join(",", _settings.MCPCoreTools ?? Array.Empty<string>()),
                _settings.MCPFullToolsConfigured ? "full=custom" : "full=default",
                string.Join(",", _settings.MCPFullTools ?? Array.Empty<string>()));
        }

        private string BuildTransportSetting()
        {
            return string.Join("|",
                _settings.MCPBrokerModeEnabled ? "broker=on" : "broker=off",
                _settings.MCPBrokerMonoPath ?? string.Empty);
        }

        internal static MCPResponse TryCreateBrokerRedeliveryResponse(MCPRequest request)
        {
            if (request == null ||
                !request.IsBrokerRedelivery ||
                !string.Equals(request.Method, "tools/call", StringComparison.Ordinal))
            {
                return null;
            }

            var toolName = GetToolName(request);
            if (string.Equals(toolName, "get_reload_recovery_status", StringComparison.OrdinalIgnoreCase))
                return null;

            var recovery = DomainReloadHandler.GetLastRecoveryInfo(false);
            string summary;
            bool isError;

            if (recovery != null &&
                (string.IsNullOrEmpty(toolName) ||
                 string.Equals(recovery.ToolName, toolName, StringComparison.OrdinalIgnoreCase)) &&
                (DateTime.Now - recovery.Timestamp).TotalMinutes <= 10)
            {
                summary =
                    "Broker mode recovered a tool call that was interrupted by Unity domain reload.\n" +
                    "Tool: " + recovery.ToolName + "\n" +
                    "Status: " + recovery.Status + "\n" +
                    recovery.Summary;
                isError = string.Equals(recovery.Status, MCPToolCallStatus.Error.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                summary =
                    "Tool '" + (string.IsNullOrEmpty(toolName) ? "unknown" : toolName) +
                    "' was interrupted by Unity domain reload. Broker mode kept the HTTP request alive, " +
                    "but the original Unity AppDomain was unloaded before it could send a response. " +
                    "The tool was not re-run automatically to avoid duplicate side effects. " +
                    "Call get_reload_recovery_status, then retry only if more work is needed.";
                isError = true;
            }

            return new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object>
                {
                    ["content"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = summary
                        }
                    },
                    ["isError"] = isError
                }
            };
        }

        private static string GetToolName(MCPRequest request)
        {
            if (request?.Params == null)
                return string.Empty;

            return request.Params.TryGetValue("name", out var value) ? value?.ToString() ?? string.Empty : string.Empty;
        }

        private int ResolveStartupPort()
        {
            var configuredPort = NormalizePort(_settings.MCPServerPort);
            return configuredPort;
        }

        private static int NormalizePort(int port)
        {
            return port > 0 ? port : 8765;
        }

        private void ScheduleRestart()
        {
            if (_disposed || _restartScheduled)
                return;

            _restartScheduled = true;
            EditorApplication.update -= RestartTransportAfterSettingsChange;
            EditorApplication.delayCall -= RestartTransportAfterSettingsChange;
            EditorApplication.delayCall += RestartTransportAfterSettingsChange;
            EditorApplication.update += RestartTransportAfterSettingsChange;
        }

        private async void RestartTransportAfterSettingsChange()
        {
            EditorApplication.update -= RestartTransportAfterSettingsChange;
            EditorApplication.delayCall -= RestartTransportAfterSettingsChange;
            _restartScheduled = false;

            if (_disposed)
                return;

            if (_restartInProgress)
            {
                ScheduleRestart();
                return;
            }

            _restartInProgress = true;
            try
            {
                await StopAsync();

                if (_disposed)
                    return;

                ScheduleStartAfterSettingsChange();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Failed while restarting after settings change: {ex.Message}");
                _restartInProgress = false;
            }
        }

        private void ScheduleStartAfterSettingsChange()
        {
            EditorApplication.update -= StartTransportAfterSettingsChange;
            EditorApplication.delayCall -= StartTransportAfterSettingsChange;
            EditorApplication.delayCall += StartTransportAfterSettingsChange;
            EditorApplication.update += StartTransportAfterSettingsChange;
        }

        private async void StartTransportAfterSettingsChange()
        {
            EditorApplication.update -= StartTransportAfterSettingsChange;
            EditorApplication.delayCall -= StartTransportAfterSettingsChange;

            try
            {
                if (!_disposed)
                    await StartAsync();
            }
            finally
            {
                _restartInProgress = false;
                if (_restartScheduled && !_disposed)
                    EditorApplication.delayCall += RestartTransportAfterSettingsChange;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settings.OnSettingsChanged -= HandleSettingsChanged;
            StopSync();
        }

        private void CheckForInterruptedExecution()
        {
            if (_recoveryChecked)
                return;

            _recoveryChecked = true;

            var interrupted = DomainReloadHandler.ConsumeInterruptedState();
            if (interrupted == null)
                return;

            if (!DomainReloadHandler.CanAutoResume())
            {
                var summary = interrupted.GetDescription() +
                              " Auto-recovery paused after too many consecutive recompilations. Retry the tool manually.";
                PublishRecoverySummary(interrupted, summary, MCPToolCallStatus.Error);
                DomainReloadHandler.ResetResumeCounter();
                return;
            }

            DomainReloadHandler.RecordAutoResume();
            WaitForCompilationThen(() =>
            {
                _stateController.ClearState();

                var scriptResult = TempScriptRunner.ConsumeResult();
                var summary = interrupted.GetDescription();
                if (IsSyncExternalChanges(interrupted))
                {
                    var compilationSummary = BuildSyncExternalChangesRecoverySummary();
                    summary += "\n" + compilationSummary.Summary;
                    PublishRecoverySummary(interrupted, summary, compilationSummary.Status);
                    return;
                }

                if (!string.IsNullOrEmpty(scriptResult))
                {
                    summary += "\nContinuation result:\n" + scriptResult;
                }
                else
                {
                    summary += " The MCP server recovered after reload. Re-run the tool if more work is needed.";
                }

                var status = DetermineInterruptedToolRecoveryStatus(scriptResult);

                PublishRecoverySummary(interrupted, summary, status);
            });
        }

        private bool IsSyncExternalChanges(DomainReloadHandler.InterruptedState interrupted)
        {
            return string.Equals(
                interrupted?.PendingFunction?.FunctionName,
                "request_recompile",
                StringComparison.OrdinalIgnoreCase);
        }

        private (string Summary, MCPToolCallStatus Status) BuildSyncExternalChangesRecoverySummary()
        {
            var issues = _compilationService.GetCompilationErrors(includeWarnings: true);
            var hasIssues = !string.Equals(issues, "No compilation errors or warnings detected.", StringComparison.Ordinal) &&
                            !string.Equals(issues, "No compilation errors detected.", StringComparison.Ordinal);

            if (hasIssues)
            {
                return ("External changes were imported, but compilation reported issues.\n" + issues, MCPToolCallStatus.Error);
            }

            return ("External changes were imported and script compilation finished successfully after domain reload.", MCPToolCallStatus.Success);
        }

        private void PublishRecoverySummary(
            DomainReloadHandler.InterruptedState interrupted,
            string summary,
            MCPToolCallStatus status)
        {
            var toolName = interrupted.PendingFunction?.FunctionName;
            if (string.IsNullOrEmpty(toolName))
                toolName = "domain_reload";

            DomainReloadHandler.StoreRecoveryInfo(toolName, status.ToString(), summary);
            InteractionLog.Add(toolName, status, summary);

            if (status == MCPToolCallStatus.Success || status == MCPToolCallStatus.Interrupted)
                PluginDebugLogger.Log($"[Funplay MCP Server] Recovery completed for '{toolName}'. {summary}");
            else
                Debug.LogWarning($"[Funplay MCP Server] Recovery detected for '{toolName}'. {summary}");
        }

        private static bool IsErrorResult(string scriptResult)
        {
            if (string.IsNullOrEmpty(scriptResult))
                return false;

            return ToolResultFormatter.IsError(scriptResult);
        }

        internal static MCPToolCallStatus DetermineInterruptedToolRecoveryStatus(string scriptResult)
        {
            if (IsErrorResult(scriptResult))
                return MCPToolCallStatus.Error;

            return string.IsNullOrEmpty(scriptResult)
                ? MCPToolCallStatus.Interrupted
                : MCPToolCallStatus.Success;
        }

        private static void WaitForCompilationThen(Action onReady)
        {
            if (!EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += () => onReady();
                return;
            }

            void CheckCompilation()
            {
                if (EditorApplication.isCompiling)
                    return;

                EditorApplication.update -= CheckCompilation;
                EditorApplication.delayCall += () => onReady();
            }

            EditorApplication.update += CheckCompilation;
        }
    }
}
