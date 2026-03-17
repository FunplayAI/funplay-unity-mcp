// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using GameBooom.Editor.Settings;
using GameBooom.Editor.State;
using GameBooom.Editor.Threading;

namespace GameBooom.Editor.Tools
{
    /// <summary>
    /// Orchestrates function execution, invocation, and result collection.
    /// All functions auto-execute immediately on the editor thread.
    /// </summary>
    internal class FunctionExecutionController
    {
        private readonly FunctionQueueController _queue;
        private readonly FunctionInvokerController _invoker;
        private readonly IStateController _stateController;
        private readonly ISettingsController _settings;
        private readonly IEditorThreadHelper _threadHelper;

        public event Action<FunctionCall> OnFunctionCompleted;
        public event Action<List<FunctionCall>> OnAllFunctionsCompleted;

        private List<FunctionCall> _currentBatch;

        public FunctionExecutionController(
            FunctionQueueController queue,
            FunctionInvokerController invoker,
            IStateController stateController,
            ISettingsController settings,
            IEditorThreadHelper threadHelper)
        {
            _queue = queue;
            _invoker = invoker;
            _stateController = stateController;
            _settings = settings;
            _threadHelper = threadHelper;
        }

        /// <summary>
        /// Process all queued function calls. All functions auto-execute immediately.
        /// </summary>
        public void ProcessQueue()
        {
            _currentBatch = new List<FunctionCall>(_queue.All);
            if (_currentBatch.Count == 0) return;
            _ = ExecuteAllAsync();
        }

        /// <summary>
        /// Execute all queued functions.
        /// All calls are executed as local tool invocations on the editor thread.
        /// Returns the list of completed function calls.
        /// </summary>
        public async Task<List<FunctionCall>> ExecuteAllAsync()
        {
            _stateController.SetState(GameBooomState.ExecutingAllFunctions);

            var results = new List<FunctionCall>();
            var calls = new List<FunctionCall>();

            while (_queue.Count > 0)
                calls.Add(_queue.Dequeue());

            if (calls.Count > 0)
            {
                await _threadHelper.ExecuteAsyncOnEditorThreadAsync(async () =>
                {
                    foreach (var fc in calls)
                    {
                        await ExecuteSingleAsync(fc);
                        results.Add(fc);
                    }

                    return true;
                });
            }

            _stateController.ReturnToPreviousState();
            OnAllFunctionsCompleted?.Invoke(results);
            return results;
        }

        /// <summary>
        /// Execute a single function call.
        /// </summary>
        public void ExecuteSingle(FunctionCall functionCall)
        {
            ExecuteSingleAsync(functionCall).GetAwaiter().GetResult();
        }

        public async Task ExecuteSingleAsync(FunctionCall functionCall)
        {
            functionCall.SetState(FunctionState.Executing);
            _stateController.SetState(GameBooomState.ExecutingFunction);

            try
            {
                var result = await _invoker.InvokeAsync(functionCall);
                functionCall.Result = result;

                if (result != null && result.StartsWith("Error:"))
                {
                    functionCall.Error = result;
                    functionCall.SetState(FunctionState.Failed);
                }
                else
                {
                    functionCall.SetState(FunctionState.Completed);
                }
            }
            catch (Exception ex)
            {
                functionCall.Error = ex.Message;
                functionCall.Result = $"Error: {ex.Message}";
                functionCall.SetState(FunctionState.Failed);
                Debug.LogError($"[GameBooom] Function execution error: {ex.Message}");
            }

            OnFunctionCompleted?.Invoke(functionCall);
        }

        /// <summary>
        /// Cancel all pending functions.
        /// </summary>
        public void CancelAll()
        {
            while (_queue.Count > 0)
            {
                var fc = _queue.Dequeue();
                fc.SetState(FunctionState.Cancelled);
                fc.Result = "Cancelled by user";
            }
            _stateController.ReturnToPreviousState();
            OnAllFunctionsCompleted?.Invoke(new List<FunctionCall>(_currentBatch ?? new List<FunctionCall>()));
        }
    }
}
