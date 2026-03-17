// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Reflection;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using GameBooom.Editor.Tools;
using UnityEditor;
using UnityEngine;

namespace GameBooom.Editor.Tools.Builtins
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
                return $"Error: GameObject '{name}' not found";

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
                return $"Error: GameObject '{name}' not found";

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
                return $"Error: Asset not found at '{path}'";

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
                case "warning": Debug.LogWarning($"[GameBooom] {message}"); break;
                case "error": Debug.LogError($"[GameBooom] {message}"); break;
                default: Debug.Log($"[GameBooom] {message}"); break;
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
                     "Useful for checking runtime behavior after play mode actions.")]
        [ReadOnlyTool]
        public static string GetConsoleLogs(
            [ToolParam("Filter by log type: 'all', 'log', 'warning', 'error'", Required = false)] string log_type = "all",
            [ToolParam("Maximum number of entries to return", Required = false)] int count = 30)
        {
            count = Mathf.Clamp(count, 1, 200);

            var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (logEntriesType == null)
                return "Error: Cannot access Unity console (LogEntries API not found)";

            var getCountMethod = logEntriesType.GetMethod("GetCount",
                BindingFlags.Public | BindingFlags.Static);
            var startMethod = logEntriesType.GetMethod("StartGettingEntries",
                BindingFlags.Public | BindingFlags.Static);
            var endMethod = logEntriesType.GetMethod("EndGettingEntries",
                BindingFlags.Public | BindingFlags.Static);
            var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal",
                BindingFlags.Public | BindingFlags.Static);

            if (getCountMethod == null || startMethod == null || endMethod == null || getEntryMethod == null)
                return "Error: LogEntries API methods not found (Unity version incompatible)";

            var logEntryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
            if (logEntryType == null) return "Error: LogEntry type not found";

            var modeField = logEntryType.GetField("mode",
                BindingFlags.Public | BindingFlags.Instance);
            var messageField = logEntryType.GetField("message",
                BindingFlags.Public | BindingFlags.Instance);

            if (modeField == null || messageField == null) return "Error: LogEntry fields not found";

            int totalCount = (int)getCountMethod.Invoke(null, null);
            if (totalCount == 0) return "Console is empty (no log entries)";

            startMethod.Invoke(null, null);
            try
            {
                var sb = new StringBuilder();
                int matchCount = 0;

                for (int i = totalCount - 1; i >= 0 && matchCount < count; i--)
                {
                    var entry = Activator.CreateInstance(logEntryType);
                    getEntryMethod.Invoke(null, new object[] { i, entry });

                    int mode = (int)modeField.GetValue(entry);
                    string message = (string)messageField.GetValue(entry);

                    if (message != null && message.StartsWith("[GameBooom")) continue;

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

                    var firstLine = message.Split('\n')[0];
                    sb.AppendLine($"[{typeLabel}] {firstLine}");
                    matchCount++;
                }

                if (matchCount == 0)
                    return $"No {log_type} entries found in console";

                return $"Console logs ({matchCount} entries, filter: {log_type}):\n{sb}";
            }
            finally
            {
                endMethod.Invoke(null, null);
            }
        }
    }
}
