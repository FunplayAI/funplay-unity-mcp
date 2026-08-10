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
    internal sealed class MCPServerRestartCompletion
    {
        private readonly object _lock = new object();
        private TaskCompletionSource<bool> _completion;

        public Task<bool> Begin()
        {
            lock (_lock)
            {
                if (_completion == null || _completion.Task.IsCompleted)
                {
                    _completion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return _completion.Task;
            }
        }

        public Task<bool> CurrentOrCompleted(bool settledResult)
        {
            lock (_lock)
                return _completion?.Task ?? Task.FromResult(settledResult);
        }

        public void Complete(bool result)
        {
            lock (_lock)
            {
                var completion = _completion;
                _completion = null;
                completion?.TrySetResult(result);
            }
        }
    }

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
        private readonly MCPServerRestartCompletion _settingsRestartCompletion = new MCPServerRestartCompletion();

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

        /// <summary>
        /// The resolution the last applied lifecycle used. Only consumed by
        /// <see cref="HandleSettingsChanged"/> to decide whether a settings edit changed the port and
        /// the transport must restart -- everything user-facing reads the live computed
        /// <see cref="Port"/>/<see cref="ResolvedPort"/> instead, which cannot go stale.
        /// </summary>
        private int _resolvedStartupPort;

        /// <summary>
        /// Per-project derived port, computed once: the project path cannot change within a domain,
        /// and caching keeps the SHA-256 derivation out of every settings-change and property read.
        /// 0 when the project path could not be resolved.
        /// </summary>
        private readonly int _derivedPort;

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
        /// <summary>
        /// Port clients must use right now: the live transport's port while one exists (which is the
        /// fallback port during a fallback bind), otherwise the current resolution. Computed rather
        /// than stored so it can never go stale -- a stored copy went wrong twice (stale after a
        /// pin change while the server was stopped, and a dead fallback port surviving a stop).
        /// </summary>
        public int Port
        {
            get
            {
                lock (_lifecycleLock)
                {
                    if (_transport != null)
                        return _transport.Port;
                }

                return ResolveStartupPort();
            }
        }

        /// <summary>
        /// Port resolution asks for -- the explicit setting when there is one, otherwise this
        /// project's derived port. Differs from <see cref="Port"/> only while a fallback bind is in
        /// effect. This is the stable identity a client config entry should point at. Never read
        /// <c>ISettingsController.MCPServerPort</c> to answer "which port is this project on": that
        /// field is only the stored override and is meaningless when nothing was pinned.
        /// </summary>
        public int ResolvedPort => ResolveStartupPort();
        public MCPInteractionLog InteractionLog { get; }

        internal Task<bool> WaitForSettingsRestartAsync()
        {
            return _settingsRestartCompletion.CurrentOrCompleted(IsRunning);
        }

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

            int derivedPort;
            _derivedPort = FunplayProjectIdentity.TryDerivePortFromProjectPath(
                _applicationPaths.ProjectPath, out derivedPort)
                ? derivedPort
                : 0;

            lock (_lifecycleLock)
                _resolvedStartupPort = ResolveStartupPort();
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
                // Pass the resource provider so prompts can embed live read-only resources
                // (current compile errors, selection, scene) into their workflow messages.
                var promptProvider = new MCPPromptProvider(Application.productName, _applicationPaths.ProjectPath, resourceProvider);
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
                        _resolvedStartupPort = startupPort;
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

                    // Awaits in this method resume on the editor sync context, so this is the main
                    // thread SessionState requires. A fallback bind is remembered for the next
                    // (post-reload) start; landing on the requested port clears the memory.
                    FunplayPortFallbackMemory.RecordStartOutcome(startupPort, transport.Port);

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
            catch (Exception ex) when (IsShutdownCancellation(ex))
            {
                // Routine, not a failure: the editor-thread pump was disposed while this request
                // was in flight, which happens on every domain reload (script compile, Play Mode
                // transition) and on server stop. Logging it as an error made every reload that
                // caught a request mid-flight print "Error handling request: A task was canceled."
                // Nothing here is ever cancelled per-request -- HandleRequestAsync is called with
                // a default token and no tool raises OperationCanceledException -- so a
                // cancellation can only mean the server is going away.
                PluginDebugLogger.Log(
                    "[Funplay MCP Server] Request cancelled because the server is stopping or the domain is reloading.");
                sendResponse(CreateBackendUnavailableResponse(request?.Id));
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

        /// <summary>
        /// True when an exception raised while handling a request is the editor-thread pump being
        /// disposed (server stop / domain reload) rather than a real failure.
        /// <see cref="System.Threading.Tasks.TaskCanceledException"/> derives from
        /// <see cref="OperationCanceledException"/>, so both are covered.
        /// </summary>
        internal static bool IsShutdownCancellation(Exception ex)
        {
            return ex is OperationCanceledException;
        }

        /// <summary>
        /// Retryable "backend is going away" response. Mirrors the broker's own
        /// backend-unavailable payload (code, message and <c>retryable</c>/<c>reason</c> data) so a
        /// client sees the same shape whether the broker or the in-process server answers.
        /// Keep in sync with <c>BackendUnavailableCode</c>/<c>BackendUnavailableMessage</c> in
        /// <c>keepalive-broker.cs.txt</c> (the broker compiles standalone and cannot share them).
        /// </summary>
        internal static MCPResponse CreateBackendUnavailableResponse(object id)
        {
            return new MCPResponse
            {
                Id = id,
                Error = new MCPError
                {
                    Code = -32001,
                    Message = "Unity MCP backend is reloading or reconnecting. Retry shortly.",
                    Data = new Dictionary<string, object>
                    {
                        ["retryable"] = true,
                        ["reason"] = "unity_backend_reloading"
                    }
                }
            };
        }

        private void HandleSettingsChanged()
        {
            if (_disposed) return;

            // Compare against the port resolution asked for, not the active one: a fallback bind (the
            // resolved port was occupied) leaves Port different from the resolved port, and comparing
            // those would report a port change on every unrelated settings edit and restart the server.
            var resolvedStartupPort = ResolveStartupPort();
            var toolExposureSetting = BuildToolExposureSetting();
            var transportSetting = BuildTransportSetting();
            var toolExposureChanged = !string.Equals(toolExposureSetting, _toolExposureSetting, StringComparison.Ordinal);
            var transportChanged = !string.Equals(transportSetting, _transportSetting, StringComparison.Ordinal);
            bool hasActiveLifecycle;
            bool portChanged;
            lock (_lifecycleLock)
            {
                // Read the previous resolution under the same lock a start writes it under, so a
                // settings change racing an in-flight start cannot compare against a torn value.
                portChanged = resolvedStartupPort != _resolvedStartupPort;
                hasActiveLifecycle = _isRunning || _startTask != null || _restartScheduled || _restartInProgress;
            }

            if ((portChanged || toolExposureChanged || transportChanged) && hasActiveLifecycle)
            {
                PluginDebugLogger.Log("[Funplay MCP Server] Server settings changed, restarting MCP transport...");
                lock (_lifecycleLock)
                    _resolvedStartupPort = resolvedStartupPort;
                _toolExposureSetting = toolExposureSetting;
                _transportSetting = transportSetting;
                ScheduleRestart();
            }
        }

        private IMCPTransport CreateTransport(int startupPort, string serverName, string projectIdentity)
        {
            if (_settings.MCPBrokerModeEnabled)
            {
                int brokerPort;
                if (MCPBrokerProcessManager.EnsureRunningWithPortFallback(
                        startupPort, _settings.MCPBrokerMonoPath, out brokerPort) &&
                    MCPBrokerProcessManager.TryGetConnectionInfo(brokerPort, out var broker))
                {
                    return new MCPBrokerClientTransport(brokerPort, broker.Token);
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

            // Read on the main thread (CreateTransport runs before the first await in
            // StartCoreAsync); the transport itself binds on pool threads where SessionState is off
            // limits, so the hints travel in by value.
            var fallbackHints = FunplayPortFallbackMemory.ReadHints(startupPort);
            return new HttpMCPTransport(startupPort, serverName, projectIdentity, fallbackHints);
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

        /// <summary>
        /// The port this project wants to bind: a port the user picked always wins, otherwise it is
        /// derived from the project identity so two editors on different projects do not both aim at
        /// one shared default. Derivation is a pure function of the project path, so the resolved
        /// port is stable across restarts and a client config entry stays valid; if a derived port is
        /// occupied by something else, the transport falls back to a free one at bind time.
        /// </summary>
        private int ResolveStartupPort()
        {
            if (_settings.MCPServerPortConfigured)
                return NormalizePort(_settings.MCPServerPort);

            return _derivedPort > 0 ? _derivedPort : NormalizePort(_settings.MCPServerPort);
        }

        private static int NormalizePort(int port)
        {
            // The upper bound matters: an out-of-range pin reaching TcpListener throws
            // ArgumentOutOfRangeException, which the start path classifies as a hard failure with no
            // port fallback -- the server would just stay down.
            return port > 0 && port <= 65535 ? port : 8765;
        }

        private void ScheduleRestart()
        {
            if (_disposed)
                return;

            _settingsRestartCompletion.Begin();
            if (_restartScheduled)
                return;

            _restartScheduled = true;
            if (_restartInProgress)
                return;

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
            {
                _settingsRestartCompletion.Complete(false);
                return;
            }

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
                {
                    FinishSettingsRestartAttempt(false);
                    return;
                }

                ScheduleStartAfterSettingsChange();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Failed while restarting after settings change: {ex.Message}");
                FinishSettingsRestartAttempt(false);
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

            var started = false;
            try
            {
                if (!_disposed && _settings.MCPServerEnabled)
                    started = await StartAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Failed while starting after settings change: {ex.Message}");
            }
            finally
            {
                FinishSettingsRestartAttempt(started);
            }
        }

        private void FinishSettingsRestartAttempt(bool started)
        {
            _restartInProgress = false;
            if (_restartScheduled && !_disposed)
            {
                EditorApplication.delayCall -= RestartTransportAfterSettingsChange;
                EditorApplication.delayCall += RestartTransportAfterSettingsChange;
                return;
            }

            _settingsRestartCompletion.Complete(started && !_disposed);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settings.OnSettingsChanged -= HandleSettingsChanged;
            EditorApplication.update -= RestartTransportAfterSettingsChange;
            EditorApplication.delayCall -= RestartTransportAfterSettingsChange;
            EditorApplication.update -= StartTransportAfterSettingsChange;
            EditorApplication.delayCall -= StartTransportAfterSettingsChange;
            _restartScheduled = false;
            _restartInProgress = false;
            _settingsRestartCompletion.Complete(false);
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
