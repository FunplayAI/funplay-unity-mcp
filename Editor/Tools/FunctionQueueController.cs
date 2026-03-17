// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Collections.Generic;

namespace GameBooom.Editor.Tools
{
    /// <summary>
    /// FIFO queue for pending function calls.
    /// </summary>
    internal class FunctionQueueController
    {
        private readonly Queue<FunctionCall> _queue = new Queue<FunctionCall>();
        private readonly List<FunctionCall> _all = new List<FunctionCall>();

        public event Action<FunctionCall> OnEnqueued;
        public event Action<FunctionCall> OnDequeued;
        public event Action OnCleared;

        public int Count => _queue.Count;
        public IReadOnlyList<FunctionCall> All => _all;

        public void Enqueue(FunctionCall functionCall)
        {
            _queue.Enqueue(functionCall);
            _all.Add(functionCall);
            OnEnqueued?.Invoke(functionCall);
        }

        public void EnqueueRange(IEnumerable<FunctionCall> calls)
        {
            foreach (var call in calls)
            {
                Enqueue(call);
            }
        }

        public FunctionCall Dequeue()
        {
            if (_queue.Count == 0) return null;
            var call = _queue.Dequeue();
            OnDequeued?.Invoke(call);
            return call;
        }

        public FunctionCall Peek()
        {
            return _queue.Count > 0 ? _queue.Peek() : null;
        }

        public void Clear()
        {
            _queue.Clear();
            _all.Clear();
            OnCleared?.Invoke();
        }
    }
}
