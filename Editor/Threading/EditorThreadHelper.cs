// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using GameBooom.Editor.Services;

namespace GameBooom.Editor.Threading
{
    internal class EditorThreadHelper : IEditorThreadHelper
    {
        private readonly ConcurrentQueue<(Action action, TaskCompletionSource<bool> tcs)> _actionQueue
            = new ConcurrentQueue<(Action, TaskCompletionSource<bool>)>();

        private readonly ConcurrentQueue<(Func<object> func, TaskCompletionSource<object> tcs)> _funcQueue
            = new ConcurrentQueue<(Func<object>, TaskCompletionSource<object>)>();

        private readonly int _mainThreadId;
        private readonly IEditorStateService _editorStateService;
        private bool _disposed;

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public EditorThreadHelper(IEditorStateService editorStateService)
        {
            _editorStateService = editorStateService;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update += ProcessQueues;
        }

        public Task ExecuteOnEditorThreadAsync(Action action)
        {
            if (IsMainThread && !_editorStateService.IsPlayingOrWillChangePlaymode)
            {
                try
                {
                    action();
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
            }

            var tcs = new TaskCompletionSource<bool>();
            _actionQueue.Enqueue((action, tcs));
            return tcs.Task;
        }

        public Task<T> ExecuteOnEditorThreadAsync<T>(Func<T> func)
        {
            if (IsMainThread && !_editorStateService.IsPlayingOrWillChangePlaymode)
            {
                try
                {
                    return Task.FromResult(func());
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            var tcs = new TaskCompletionSource<object>();
            _funcQueue.Enqueue((() => func(), tcs));
            return tcs.Task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    throw t.Exception?.InnerException ?? t.Exception ?? new Exception("Unknown error");
                return (T)t.Result;
            });
        }

        public Task<T> ExecuteAsyncOnEditorThreadAsync<T>(Func<Task<T>> asyncFunc)
        {
            if (IsMainThread && !_editorStateService.IsPlayingOrWillChangePlaymode)
            {
                return asyncFunc();
            }

            var outerTcs = new TaskCompletionSource<T>();
            var tcs = new TaskCompletionSource<object>();
            _funcQueue.Enqueue((() =>
            {
                asyncFunc().ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        outerTcs.SetException(task.Exception?.InnerException ?? task.Exception ?? new Exception("Unknown error"));
                    else if (task.IsCanceled)
                        outerTcs.SetCanceled();
                    else
                        outerTcs.SetResult(task.Result);
                });
                return (object)null;
            }, tcs));

            return outerTcs.Task;
        }

        private void ProcessQueues()
        {
            if (_disposed) return;

            int processedCount = 0;
            const int maxPerFrame = 10;

            while (processedCount < maxPerFrame && _actionQueue.TryDequeue(out var item))
            {
                try
                {
                    item.action();
                    item.tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    item.tcs.TrySetException(ex);
                }
                processedCount++;
            }

            while (processedCount < maxPerFrame && _funcQueue.TryDequeue(out var item))
            {
                try
                {
                    var result = item.func();
                    item.tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    item.tcs.TrySetException(ex);
                }
                processedCount++;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorApplication.update -= ProcessQueues;

            while (_actionQueue.TryDequeue(out var item))
                item.tcs.TrySetCanceled();
            while (_funcQueue.TryDequeue(out var item))
                item.tcs.TrySetCanceled();
        }
    }
}
