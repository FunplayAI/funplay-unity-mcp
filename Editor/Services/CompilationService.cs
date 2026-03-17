// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;

namespace GameBooom.Editor.Services
{
    internal class CompilationService : ICompilationService, IDisposable
    {
        private readonly object _lock = new object();
        private readonly List<CompilerMessage> _latestMessages = new List<CompilerMessage>();
        private TaskCompletionSource<bool> _compilationFinishedTcs;

        public bool IsCompiling => EditorApplication.isCompiling;
        public event Action OnCompilationFinished;

        public CompilationService()
        {
            CompilationPipeline.compilationStarted += HandleCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += HandleAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += HandleCompilationFinished;
        }

        public async Task<bool> WaitForCompilationAsync(bool forceRefresh, int timeoutSeconds)
        {
            if (forceRefresh)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }

            if (!EditorApplication.isCompiling)
            {
                return true;
            }

            TaskCompletionSource<bool> waitSource;
            lock (_lock)
            {
                if (_compilationFinishedTcs == null || _compilationFinishedTcs.Task.IsCompleted)
                {
                    _compilationFinishedTcs = CreateCompletionSource();
                }

                waitSource = _compilationFinishedTcs;
            }

            var completedTask = await Task.WhenAny(
                waitSource.Task,
                Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);

            return completedTask == waitSource.Task && waitSource.Task.IsCompletedSuccessfully;
        }

        public string GetCompilationErrors(int maxEntries = 50, bool includeWarnings = false)
        {
            maxEntries = Math.Max(1, maxEntries);

            List<CompilerMessage> messages;
            lock (_lock)
            {
                messages = _latestMessages.ToList();
            }

            var filtered = messages
                .Where(message => message.type == CompilerMessageType.Error ||
                                  (includeWarnings && message.type == CompilerMessageType.Warning))
                .Take(maxEntries)
                .ToList();

            if (filtered.Count == 0)
            {
                return includeWarnings
                    ? "No compilation errors or warnings detected."
                    : "No compilation errors detected.";
            }

            var lines = filtered.Select(message =>
            {
                var location = string.IsNullOrEmpty(message.file)
                    ? string.Empty
                    : $" ({message.file}:{message.line})";
                return $"- [{message.type}] {message.message}{location}";
            });

            return "Compilation issues:\n" + string.Join("\n", lines);
        }

        private void HandleCompilationStarted(object context)
        {
            lock (_lock)
            {
                _latestMessages.Clear();
                _compilationFinishedTcs = CreateCompletionSource();
            }
        }

        private void HandleAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return;
            }

            lock (_lock)
            {
                _latestMessages.AddRange(messages);
            }
        }

        private void HandleCompilationFinished(object obj)
        {
            TaskCompletionSource<bool> waitSource = null;
            lock (_lock)
            {
                waitSource = _compilationFinishedTcs;
            }

            waitSource?.TrySetResult(true);
            OnCompilationFinished?.Invoke();
        }

        private static TaskCompletionSource<bool> CreateCompletionSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Dispose()
        {
            CompilationPipeline.compilationStarted -= HandleCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= HandleAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= HandleCompilationFinished;
        }
    }
}
