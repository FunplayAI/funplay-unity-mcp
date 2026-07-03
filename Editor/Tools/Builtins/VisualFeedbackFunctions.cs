// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Reflection;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.DI;
using Funplay.Editor.Services.UnityLogs;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Visual")]
    internal static class VisualFeedbackFunctions
    {
        [Description("Select a GameObject in the scene hierarchy and inspector")]
        [ReadOnlyTool]
        public static string SelectObject(
            [ToolParam("Name of the GameObject to select")] string name)
        {
            var go = GameObject.Find(name);
            if (go == null)
                return ToolResultFormatter.Error("GAME_OBJECT_NOT_FOUND", new { name });

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return $"Selected '{name}'";
        }

        [Description("Focus the Scene View camera on a specific GameObject")]
        [ReadOnlyTool]
        public static string FocusOnObject(
            [ToolParam("Name of the GameObject to focus on")] string name)
        {
            var go = GameObject.Find(name);
            if (go == null)
                return ToolResultFormatter.Error("GAME_OBJECT_NOT_FOUND", new { name });

            Selection.activeGameObject = go;
            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.FrameSelected();
            }
            return $"Focused scene view on '{name}'";
        }

        [Description("Ping/highlight an asset in the Project window")]
        [ReadOnlyTool]
        public static string PingAsset(
            [ToolParam("Path to the asset")] string path)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj == null)
                return ToolResultFormatter.Error("ASSET_NOT_FOUND", new { path });

            EditorGUIUtility.PingObject(obj);
            return $"Pinged asset at '{path}'";
        }

        [Description("Log a message to the Unity console")]
        [ReadOnlyTool]
        public static string LogMessage(
            [ToolParam("Message to log")] string message,
            [ToolParam("Log type: info, warning, error", Required = false)] string log_type = "info")
        {
            switch (log_type.ToLowerInvariant())
            {
                case "warning": Debug.LogWarning($"[Funplay] {message}"); break;
                case "error": Debug.LogError($"[Funplay] {message}"); break;
                default: Debug.Log($"[Funplay] {message}"); break;
            }
            return $"Logged {log_type}: {message}";
        }

        [Description("Display a dialog box to the user")]
        [ReadOnlyTool]
        public static string ShowDialog(
            [ToolParam("Dialog title")] string title,
            [ToolParam("Dialog message")] string message)
        {
            EditorUtility.DisplayDialog(title, message, "OK");
            return $"Displayed dialog: {title}";
        }

        [Description("Get recent console log messages from Unity. " +
                     "Returns Debug.Log, Debug.LogWarning, and Debug.LogError output. " +
                     "Useful for checking runtime behavior after play mode actions. " +
                     "Supports reading from the live log cache, clearing the cache, time-based filtering, " +
                     "case-insensitive text filtering, and collapsing repeated identical messages into one " +
                     "'message (xN)' line so spammy warnings don't drown out unique entries.")]
        [ReadOnlyTool]
        public static string GetConsoleLogs(
            [ToolParam("Filter by log type: 'all', 'log', 'warning', 'error'", Required = false)] string log_type = "all",
            [ToolParam("Maximum number of entries to return", Required = false)] int count = 30,
            [ToolParam("Source: 'auto', 'cache', or 'console'", Required = false)] string source = "auto",
            [ToolParam("Clear the cached logs before reading", Required = false)] bool clear_cache = false,
            [ToolParam("Only include cached log entries from the last N seconds (cache/auto only)", Required = false)] int since_seconds = 0,
            [ToolParam("Only include entries whose message contains this text (case-insensitive)", Required = false)] string filter_text = null,
            [ToolParam("Collapse repeated identical messages into one line with a (xN) count", Required = false)] bool group_duplicates = false)
        {
            count = Mathf.Clamp(count, 1, 200);
            since_seconds = Mathf.Clamp(since_seconds, 0, 86400);
            source = string.IsNullOrEmpty(source) ? "auto" : source.ToLowerInvariant();

            var logsRepository = RootScopeServices.Services?.GetService(typeof(UnityLogsRepository)) as UnityLogsRepository;
            logsRepository?.StartListening();

            if (clear_cache)
                logsRepository?.Clear();

            if (source != "auto" && source != "cache" && source != "console")
                return ToolResultFormatter.Error("INVALID_SOURCE", new { source, accepted = new[] { "auto", "cache", "console" } });

            if (source == "cache" || source == "auto")
            {
                var cachedLogs = logsRepository?.GetRecentLogs(log_type, count, since_seconds, filter_text, group_duplicates);
                if (!string.IsNullOrEmpty(cachedLogs))
                    return cachedLogs;

                if (source == "cache")
                    return since_seconds > 0
                        ? $"No {log_type} entries found in cached logs from the last {since_seconds} second(s)"
                        : $"No {log_type} entries found in cached logs";
            }

            if (since_seconds > 0)
                return ToolResultFormatter.Error("INVALID_SINCE_SECONDS", new { since_seconds, hint = "since_seconds is only supported when reading from cache or auto mode with cached results." });

            var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (logEntriesType == null)
                return ToolResultFormatter.Error("UNITY_CONSOLE_UNAVAILABLE", new { message = "LogEntries API not found" });

            var getCountMethod = logEntriesType.GetMethod("GetCount",
                BindingFlags.Public | BindingFlags.Static);
            var startMethod = logEntriesType.GetMethod("StartGettingEntries",
                BindingFlags.Public | BindingFlags.Static);
            var endMethod = logEntriesType.GetMethod("EndGettingEntries",
                BindingFlags.Public | BindingFlags.Static);
            var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal",
                BindingFlags.Public | BindingFlags.Static);

            if (getCountMethod == null || startMethod == null || endMethod == null || getEntryMethod == null)
                return ToolResultFormatter.Error("UNITY_CONSOLE_API_INCOMPATIBLE", new { message = "LogEntries API methods not found" });

            var logEntryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
            if (logEntryType == null) return ToolResultFormatter.Error("UNITY_CONSOLE_API_INCOMPATIBLE", new { message = "LogEntry type not found" });

            var modeField = logEntryType.GetField("mode",
                BindingFlags.Public | BindingFlags.Instance);
            var messageField = logEntryType.GetField("message",
                BindingFlags.Public | BindingFlags.Instance);

            if (modeField == null || messageField == null) return ToolResultFormatter.Error("UNITY_CONSOLE_API_INCOMPATIBLE", new { message = "LogEntry fields not found" });

            int totalCount = (int)getCountMethod.Invoke(null, null);
            if (totalCount == 0) return "Console is empty (no log entries)";

            startMethod.Invoke(null, null);
            try
            {
                var lines = new System.Collections.Generic.List<string>();

                for (int i = totalCount - 1; i >= 0 && lines.Count < count; i--)
                {
                    var entry = Activator.CreateInstance(logEntryType);
                    getEntryMethod.Invoke(null, new object[] { i, entry });

                    int mode = (int)modeField.GetValue(entry);
                    string message = (string)messageField.GetValue(entry);

                    if (message != null &&
                        (message.StartsWith("[Funplay", StringComparison.Ordinal) ||
                         message.StartsWith("[Funplay", StringComparison.Ordinal))) continue;

                    // Classify: ERROR (bits 0,1,4,8,11), WARN (bits 9,12), LOG (others)
                    const int errorMask = 1 | (1 << 1) | (1 << 4) | (1 << 8) | (1 << 11);
                    const int warningMask = (1 << 9) | (1 << 12);

                    bool isError = (mode & errorMask) != 0;
                    bool isWarning = !isError && (mode & warningMask) != 0;

                    string typeLabel;
                    if (isError) typeLabel = "ERROR";
                    else if (isWarning) typeLabel = "WARN";
                    else typeLabel = "LOG";

                    string filterLower = log_type.ToLowerInvariant();
                    if (filterLower == "error" && !isError) continue;
                    if (filterLower == "warning" && !isWarning) continue;
                    if (filterLower == "log" && (isError || isWarning)) continue;

                    var firstLine = string.IsNullOrEmpty(message) ? string.Empty : message.Split('\n')[0];
                    if (!UnityLogsRepository.MatchesTextFilter(firstLine, filter_text))
                        continue;

                    lines.Add($"[{typeLabel}] {UnityLogsRepository.TruncateLine(firstLine)}");
                }

                if (lines.Count == 0)
                    return $"No {log_type} entries found in console";

                var sb = new StringBuilder();
                var uniqueCount = UnityLogsRepository.AppendLines(sb, lines, group_duplicates);

                var textSuffix = string.IsNullOrEmpty(filter_text) ? string.Empty : $", text: '{filter_text}'";
                var groupSuffix = group_duplicates && uniqueCount < lines.Count
                    ? $", {uniqueCount} unique"
                    : string.Empty;
                return $"Console logs ({lines.Count} entries{groupSuffix}, filter: {log_type}, source: console{textSuffix}):\n{sb}";
            }
            finally
            {
                endMethod.Invoke(null, null);
            }
        }
    }
}
