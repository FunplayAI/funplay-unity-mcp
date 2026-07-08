// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("ProjectSettings")]
    internal static class ProjectSettingsFunctions
    {
        [Description("Report a flat snapshot of project settings: QualitySettings (level names + current level index/name), " +
                     "PlayerSettings (applicationIdentifier, productName, companyName, Android/Standalone scripting backend, " +
                     "Android target/min SDK versions), and the active build target. Each PlayerSettings read is guarded " +
                     "individually so one unavailable field (e.g. Android module not installed) does not fail the whole call — " +
                     "unreadable fields are omitted from the data and listed in the 'failures' array.")]
        [ReadOnlyTool]
        public static object GetProjectSettings()
        {
            var result = new Dictionary<string, object>();
            var failures = new List<string>();

            // --- QualitySettings ---
            try
            {
                var names = QualitySettings.names;
                var level = QualitySettings.GetQualityLevel();
                result["qualityLevelNames"] = names;
                result["currentQualityLevel"] = level;
                result["currentQualityName"] = (names != null && level >= 0 && level < names.Length) ? names[level] : null;
            }
            catch (Exception ex) { failures.Add("qualitySettings: " + ex.Message); }

            // --- PlayerSettings (each read guarded so one missing field doesn't sink the call) ---
            TryRead(result, failures, "applicationIdentifier", () => PlayerSettings.applicationIdentifier);
            TryRead(result, failures, "productName", () => PlayerSettings.productName);
            TryRead(result, failures, "companyName", () => PlayerSettings.companyName);
            TryRead(result, failures, "scriptingBackendAndroid", () => ReadScriptingBackend("Android"));
            TryRead(result, failures, "scriptingBackendStandalone", () => ReadScriptingBackend("Standalone"));
            TryRead(result, failures, "androidTargetSdkVersion", () => PlayerSettings.Android.targetSdkVersion.ToString());
            TryRead(result, failures, "androidMinSdkVersion", () => PlayerSettings.Android.minSdkVersion.ToString());

            // --- Active build target ---
            TryRead(result, failures, "activeBuildTarget", () => EditorUserBuildSettings.activeBuildTarget.ToString());

            result["failures"] = failures;

            return Response.Success(
                failures.Count == 0
                    ? "Project settings read."
                    : $"Project settings read ({failures.Count} field(s) unavailable, see 'failures').",
                result);
        }

        [Description("Read a single EditorPrefs value by key. type must be one of string|int|bool|float (default 'string'). " +
                     "Returns EDITORPREF_NOT_FOUND if the key is absent (guarded with EditorPrefs.HasKey). CAVEAT: EditorPrefs " +
                     "is untyped and exposes no way to query a key's stored type; reading a key under the WRONG type returns " +
                     "that type's default (e.g. a string key read as int returns 0) indistinguishable from a genuinely stored " +
                     "default. Pass the type the key was written with.")]
        [ReadOnlyTool]
        public static object GetEditorPref(
            [ToolParam("EditorPrefs key to read")] string key,
            [ToolParam("Value type to read the key as: string|int|bool|float", Required = false)] string type = "string")
        {
            if (string.IsNullOrEmpty(key))
                return Response.Error("INVALID_PARAM", new { param = "key", hint = "Provide a non-empty EditorPrefs key." });

            if (!EditorPrefs.HasKey(key))
                return Response.Error("EDITORPREF_NOT_FOUND", new { key, hint = "No EditorPrefs entry with this key exists." });

            var t = (type ?? "string").Trim().ToLowerInvariant();
            object value;
            switch (t)
            {
                case "string": value = EditorPrefs.GetString(key); break;
                case "int": value = EditorPrefs.GetInt(key); break;
                case "bool": value = EditorPrefs.GetBool(key); break;
                case "float": value = EditorPrefs.GetFloat(key); break;
                default:
                    return Response.Error("INVALID_PARAM",
                        new { param = "type", provided = type, expected = "string|int|bool|float" });
            }

            return Response.Success($"EditorPrefs['{key}'] read as {t}.", new { key, type = t, value });
        }

        // --- Helpers ---

        // Guard a single PlayerSettings/BuildSettings read: on success store under `key`, on failure record the message.
        private static void TryRead(Dictionary<string, object> result, List<string> failures, string key, Func<object> read)
        {
            try { result[key] = read(); }
            catch (Exception ex) { failures.Add(key + ": " + ex.Message); }
        }

        // Read the scripting backend for a platform using the modern NamedBuildTarget overload,
        // falling back (via reflection, to avoid a compile-time obsolete warning) to the legacy
        // BuildTargetGroup overload if the modern one throws. Throws on total failure so the caller
        // records it as a failed field.
        private static string ReadScriptingBackend(string platform)
        {
            try
            {
                var named = platform == "Android" ? NamedBuildTarget.Android : NamedBuildTarget.Standalone;
                return PlayerSettings.GetScriptingBackend(named).ToString();
            }
            catch
            {
                var group = platform == "Android" ? BuildTargetGroup.Android : BuildTargetGroup.Standalone;
                var mi = typeof(PlayerSettings).GetMethod("GetScriptingBackend", new[] { typeof(BuildTargetGroup) });
                if (mi == null)
                    throw new InvalidOperationException("No GetScriptingBackend(BuildTargetGroup) overload available in this Unity version.");
                return mi.Invoke(null, new object[] { group }).ToString();
            }
        }
    }
}
