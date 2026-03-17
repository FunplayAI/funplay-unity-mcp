// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Generic;

namespace GameBooom.Editor.MCP.Server
{
    internal enum MCPToolCallStatus
    {
        Success,
        Error
    }

    internal struct MCPLogEntry
    {
        public DateTime Timestamp;
        public string ToolName;
        public MCPToolCallStatus Status;
        public string ResultSummary;
    }

    internal class MCPInteractionLog
    {
        private readonly MCPLogEntry[] _buffer;
        private int _head;
        private int _count;
        private readonly object _lock = new object();

        public event Action<MCPLogEntry> OnEntryAdded;

        public MCPInteractionLog(int capacity = 200)
        {
            _buffer = new MCPLogEntry[capacity];
        }

        public void Add(string toolName, MCPToolCallStatus status, string resultSummary)
        {
            var entry = new MCPLogEntry
            {
                Timestamp = DateTime.Now,
                ToolName = toolName,
                Status = status,
                ResultSummary = resultSummary != null && resultSummary.Length > 200
                    ? resultSummary.Substring(0, 197) + "..."
                    : resultSummary ?? ""
            };

            lock (_lock)
            {
                _buffer[_head] = entry;
                _head = (_head + 1) % _buffer.Length;
                if (_count < _buffer.Length) _count++;
            }

            OnEntryAdded?.Invoke(entry);
        }

        public List<MCPLogEntry> GetEntries()
        {
            lock (_lock)
            {
                var result = new List<MCPLogEntry>(_count);
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - 1 - i + _buffer.Length) % _buffer.Length;
                    result.Add(_buffer[idx]);
                }
                return result;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _count = 0;
            }
        }
    }
}
