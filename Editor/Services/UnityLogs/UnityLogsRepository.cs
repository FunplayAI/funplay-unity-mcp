// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Funplay.Editor.Services.UnityLogs
{
    internal class UnityLogsRepository : IDisposable
    {
        private const int MaxLogs = 200;

        private readonly List<LogEntry> _logs = new List<LogEntry>();
        private readonly object _lock = new object();
        private bool _isListening;

        public void StartListening()
        {
            if (_isListening)
                return;

            _isListening = true;
            Application.logMessageReceived += OnLogReceived;
        }

        public void StopListening()
        {
            if (!_isListening)
                return;

            _isListening = false;
            Application.logMessageReceived -= OnLogReceived;
        }

        public string GetRecentLogs(string logType = "all", int count = 30, int sinceSeconds = 0,
            string filterText = null, bool groupDuplicates = false)
        {
            count = Mathf.Clamp(count, 1, 200);
            var filter = (logType ?? "all").ToLowerInvariant();
            var cutoff = sinceSeconds > 0 ? DateTime.Now.AddSeconds(-sinceSeconds) : (DateTime?)null;

            List<LogEntry> snapshot;
            lock (_lock)
            {
                snapshot = new List<LogEntry>(_logs);
            }

            if (snapshot.Count == 0)
                return null;

            var lines = new List<string>();

            for (int i = snapshot.Count - 1; i >= 0 && lines.Count < count; i--)
            {
                var entry = snapshot[i];
                if (cutoff.HasValue && entry.Timestamp < cutoff.Value)
                    continue;

                if (!MatchesFilter(entry.Type, filter))
                    continue;

                var firstLine = FirstLine(entry.Message);
                if (!MatchesTextFilter(firstLine, filterText))
                    continue;

                lines.Add($"[{ToLabel(entry.Type)}] {TruncateLine(firstLine)}");
            }

            if (lines.Count == 0)
            {
                if (sinceSeconds > 0)
                    return $"No {filter} entries found in cached logs from the last {sinceSeconds} second(s)";

                return $"No {filter} entries found in cached logs";
            }

            var sb = new StringBuilder();
            var uniqueCount = AppendLines(sb, lines, groupDuplicates);

            var timeSuffix = sinceSeconds > 0 ? $", last {sinceSeconds}s" : string.Empty;
            var textSuffix = string.IsNullOrEmpty(filterText) ? string.Empty : $", text: '{filterText}'";
            var groupSuffix = groupDuplicates && uniqueCount < lines.Count
                ? $", {uniqueCount} unique"
                : string.Empty;
            return $"Console logs ({lines.Count} entries{groupSuffix}, filter: {filter}, source: cache{timeSuffix}{textSuffix}):\n{sb}";
        }

        internal static bool MatchesTextFilter(string line, string filterText)
        {
            if (string.IsNullOrEmpty(filterText))
                return true;
            return line != null && line.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A single log line can be enormous (e.g. an entire save-file JSON dumped to the console).
        // Cap emitted lines so one such entry cannot blow up the whole tool response.
        private const int MaxEmittedLineLength = 300;

        internal static string TruncateLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length <= MaxEmittedLineLength)
                return line;
            return line.Substring(0, MaxEmittedLineLength) + $"... (+{line.Length - MaxEmittedLineLength} chars)";
        }

        // Appends lines to the builder; with grouping, identical lines collapse to one
        // "line (xN)" entry in first-seen order. Returns the number of lines written.
        internal static int AppendLines(StringBuilder sb, List<string> lines, bool groupDuplicates)
        {
            if (!groupDuplicates)
            {
                foreach (var line in lines)
                    sb.AppendLine(line);
                return lines.Count;
            }

            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (var line in lines)
            {
                if (counts.TryGetValue(line, out var existing))
                {
                    counts[line] = existing + 1;
                }
                else
                {
                    counts[line] = 1;
                    order.Add(line);
                }
            }

            foreach (var line in order)
            {
                var n = counts[line];
                sb.AppendLine(n > 1 ? $"{line} (x{n})" : line);
            }
            return order.Count;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _logs.Clear();
            }
        }

        private void OnLogReceived(string message, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(message))
                return;

            if (message.StartsWith("[Funplay]", StringComparison.Ordinal) ||
                message.StartsWith("[Funplay MCP Server]", StringComparison.Ordinal) ||
                message.StartsWith("[Funplay]", StringComparison.Ordinal) ||
                message.StartsWith("[Funplay MCP Server]", StringComparison.Ordinal))
            {
                return;
            }

            lock (_lock)
            {
                _logs.Add(new LogEntry
                {
                    Message = message,
                    StackTrace = stackTrace,
                    Type = type,
                    Timestamp = DateTime.Now
                });

                while (_logs.Count > MaxLogs)
                    _logs.RemoveAt(0);
            }
        }

        private static bool MatchesFilter(LogType type, string filter)
        {
            switch (filter)
            {
                case "error":
                    return type == LogType.Error || type == LogType.Assert || type == LogType.Exception;
                case "warning":
                    return type == LogType.Warning;
                case "log":
                    return type == LogType.Log;
                default:
                    return true;
            }
        }

        private static string ToLabel(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return "WARN";
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    return "ERROR";
                default:
                    return "LOG";
            }
        }

        private static string FirstLine(string message)
        {
            return string.IsNullOrEmpty(message) ? string.Empty : message.Split('\n')[0];
        }

        public void Dispose()
        {
            StopListening();
        }

        private class LogEntry
        {
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public LogType Type { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}
