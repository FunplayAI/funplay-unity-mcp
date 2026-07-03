// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Profiler")]
    internal static class ProfilerFunctions
    {
        // 高信号计数器默认列表：(category, statName) —— 已用 execute_code 在 Unity 6000.3.13f1 上核实存在。
        private static readonly (string Category, string Name)[] DefaultCounters =
        {
            ("Render", "Draw Calls Count"),
            ("Render", "Batches Count"),
            ("Render", "SetPass Calls Count"),
            ("Render", "Triangles Count"),
            ("Render", "Vertices Count"),
            ("Memory", "GC Allocated In Frame"),
            ("Memory", "GC Used Memory"),
            ("Memory", "Gfx Used Memory"),
            ("Memory", "Total Used Memory"),
        };

        private static Dictionary<string, ProfilerRecorder> _recorders;
        private static bool _initializedReloadHook;

        [Description("Start a Profiler capture session (enables UnityEngine.Profiling.Profiler and starts a set of " +
                     "persistent ProfilerRecorder counters used by get_counters/get_frame_timing). Safe to call again while already running.")]
        public static string ProfilerStart()
        {
            try
            {
                EnsureReloadHook();
                Profiler.enabled = true;
                StartRecorders();
                return "Profiler started. Recorders running: " + _recorders.Count +
                       ". Call get_frame_timing / get_counters after a few frames have elapsed.";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Stop the current Profiler capture session and dispose all persistent ProfilerRecorder counters.")]
        public static string ProfilerStop()
        {
            try
            {
                StopRecorders();
                Profiler.enabled = false;
                return "Profiler stopped.";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Report whether a Profiler capture session is currently running and how many counters are active.")]
        [ReadOnlyTool]
        public static string ProfilerStatus()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Profiler Status");
                sb.AppendLine($"- Profiler.enabled: {Profiler.enabled}");
                sb.AppendLine($"- Active recorders: {(_recorders?.Count ?? 0)}");
                if (_recorders != null)
                {
                    foreach (var kv in _recorders)
                        sb.AppendLine($"  - {kv.Key}: running={kv.Value.IsRunning}, valid={kv.Value.Valid}");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Get CPU/GPU frame timing for the most recently completed frame(s). " +
                     "Does not require profiler_start. Uses FrameTimingManager.")]
        [ReadOnlyTool]
        public static string GetFrameTiming(
            [ToolParam("Number of recent frames to sample and average (1-30)", Required = false)] int sample_frames = 1)
        {
            try
            {
                sample_frames = Mathf.Clamp(sample_frames, 1, 30);
                FrameTimingManager.CaptureFrameTimings();
                var timings = new FrameTiming[sample_frames];
                uint timingCount = FrameTimingManager.GetLatestTimings((uint)sample_frames, timings);

                if (timingCount == 0)
                {
                    var fallbackMs = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime * 1000.0 : 0.0;
                    if (fallbackMs <= 0.0)
                        return "Frame timing not available yet. Try again after the editor has rendered a few frames.";
                    return $"CPU Frame (fallback from Time.unscaledDeltaTime): {fallbackMs:F2} ms, Approx FPS: {1000.0 / fallbackMs:F1}";
                }

                double cpuSum = 0.0, gpuSum = 0.0;
                int gpuSamples = 0;
                for (int i = 0; i < timingCount; i++)
                {
                    cpuSum += timings[i].cpuFrameTime;
                    if (timings[i].gpuFrameTime > 0.0)
                    {
                        gpuSum += timings[i].gpuFrameTime;
                        gpuSamples++;
                    }
                }

                var avgCpu = cpuSum / timingCount;
                var sb = new StringBuilder();
                sb.AppendLine($"Sampled {timingCount} frame(s)");
                sb.AppendLine($"- Avg CPU Frame: {avgCpu:F2} ms");
                sb.AppendLine(gpuSamples > 0
                    ? $"- Avg GPU Frame: {gpuSum / gpuSamples:F2} ms"
                    : "- GPU Frame: unavailable");
                sb.AppendLine($"- Approx FPS (from avg CPU frame): {(avgCpu > 0.0 ? 1000.0 / avgCpu : 0.0):F1}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Read current values of high-signal rendering/memory counters (Draw Calls, Batches, Triangles, " +
                     "SetPass Calls, GC/Gfx memory, etc). Requires profiler_start to have been called at least a few frames earlier; " +
                     "if not running, starts recorders automatically and returns zero/stale values with a warning. " +
                     "KNOWN LIMITATION: Render-category counters (Draw Calls/Batches/SetPass Calls/Triangles/Vertices Count) " +
                     "have been observed to read 0 in the Unity Editor even during confirmed active rendering (verified via " +
                     "frame_debugger_get_events showing real events at the same time); Memory-category counters are unaffected. " +
                     "Not verified whether this limitation also applies to a Standalone Player build.")]
        [ReadOnlyTool]
        public static string GetCounters(
            [ToolParam("Comma-separated list of specific counter names to query (e.g. 'Draw Calls Count,Triangles Count'). Omit for the default set.", Required = false)] string names = null)
        {
            try
            {
                bool wasRunning = _recorders != null;
                if (!wasRunning)
                {
                    EnsureReloadHook();
                    StartRecorders();
                }

                var sb = new StringBuilder();
                if (!wasRunning)
                    sb.AppendLine("WARNING: profiler_start was not called; recorders were just started now, values below may be 0/stale until a few more frames elapse.");

                var keysToShow = new List<string>();
                if (string.IsNullOrEmpty(names))
                {
                    keysToShow.AddRange(_recorders.Keys);
                }
                else
                {
                    var rawNames = names.Split(',');
                    var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rawName in rawNames)
                        wanted.Add(rawName.Trim());
                    foreach (var kv in _recorders)
                    {
                        var shortName = kv.Key.Substring(kv.Key.IndexOf('/') + 1);
                        if (wanted.Contains(shortName))
                            keysToShow.Add(kv.Key);
                    }
                }

                foreach (var key in keysToShow)
                {
                    var recorder = _recorders[key];
                    sb.AppendLine($"{key}: last={recorder.LastValueAsDouble:F0}, current={recorder.CurrentValueAsDouble:F0}, unit={recorder.UnitType}");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        private static void StartRecorders()
        {
            if (_recorders != null)
                return; // already running, idempotent

            _recorders = new Dictionary<string, ProfilerRecorder>();
            foreach (var (category, name) in DefaultCounters)
            {
                var key = category + "/" + name;
                try
                {
                    var recorder = new ProfilerRecorder(category, name, 15);
                    _recorders[key] = recorder;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Funplay.Profiler] Failed to start recorder '{key}': {ex.Message}");
                }
            }
        }

        private static void StopRecorders()
        {
            if (_recorders == null)
                return;

            foreach (var kv in _recorders)
                kv.Value.Dispose();
            _recorders = null;
        }

        private static void EnsureReloadHook()
        {
            if (_initializedReloadHook)
                return;
            _initializedReloadHook = true;
            // ProfilerRecorder wraps a native handle -- must dispose before domain reload
            // to avoid leaking it, same class of bug as the atlas handle leak we already hit elsewhere.
            AssemblyReloadEvents.beforeAssemblyReload += StopRecorders;
        }

        [Description("Get the runtime memory footprint of a specific asset (by project path) or scene GameObject " +
                     "(by hierarchy path, e.g. 'Canvas/Panel/Icon'). Useful for diagnosing texture/atlas memory leaks.")]
        [ReadOnlyTool]
        public static string GetObjectMemory(
            [ToolParam("Asset path (e.g. 'Assets/Art/atlas.png') or scene GameObject hierarchy path")] string target)
        {
            try
            {
                if (string.IsNullOrEmpty(target))
                    return "target is required.";

                UnityEngine.Object obj = null;

                if (target.StartsWith("Assets/"))
                {
                    obj = AssetDatabase.LoadMainAssetAtPath(target);
                    if (obj == null)
                        return $"No asset found at path: {target}";
                }
                else
                {
                    var go = GameObject.Find(target);
                    if (go == null)
                        return $"No active GameObject found at hierarchy path: {target}";
                    obj = go;
                }

                var size = Profiler.GetRuntimeMemorySizeLong(obj);
                var sb = new StringBuilder();
                sb.AppendLine($"Target: {target}");
                sb.AppendLine($"Type: {obj.GetType().Name}");
                sb.AppendLine($"Runtime Memory: {FormatBytes(size)}");

                if (obj is GameObject rootGo)
                {
                    long total = size;
                    foreach (var component in rootGo.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null) continue;
                        total += Profiler.GetRuntimeMemorySizeLong(component);
                    }
                    sb.AppendLine($"Total (incl. all child components): {FormatBytes(total)}");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        // Known high-signal asset types for get_top_memory_objects. Order defines the 'All' summary order.
        private static readonly (string Name, Type Type)[] KnownMemoryTypes =
        {
            ("Texture2D", typeof(Texture2D)),
            ("RenderTexture", typeof(RenderTexture)),
            ("Mesh", typeof(Mesh)),
            ("AudioClip", typeof(AudioClip)),
            ("Material", typeof(Material)),
            ("AnimationClip", typeof(AnimationClip)),
            ("Shader", typeof(Shader)),
            ("Sprite", typeof(Sprite)),
        };

        [Description("List the top N memory-consuming loaded objects of a given type (Texture2D, RenderTexture, Mesh, AudioClip, " +
                     "Material, AnimationClip, Shader, Sprite, or any UnityEngine.Object-derived type by name), enumerated via " +
                     "Resources.FindObjectsOfTypeAll and measured with Profiler.GetRuntimeMemorySizeLong. " +
                     "Complements memory_take_snapshot/memory_compare_snapshots: after a snapshot diff shows growth, use this " +
                     "to find WHICH objects are consuming the memory. Pass type_name='All' for a per-type total summary instead " +
                     "of individual objects. Note: in the Editor this also enumerates editor-owned objects; a non-empty flags " +
                     "column (e.g. HideAndDontSave) usually indicates editor internals or runtime-created objects.")]
        [ReadOnlyTool]
        public static string GetTopMemoryObjects(
            [ToolParam("Type to enumerate (e.g. 'Texture2D'), or 'All' for a per-type summary", Required = false)] string type_name = "Texture2D",
            [ToolParam("Number of top objects to return (1-100)", Required = false)] int top_n = 20)
        {
            try
            {
                if (string.IsNullOrEmpty(type_name))
                    type_name = "Texture2D";
                top_n = Mathf.Clamp(top_n, 1, 100);

                if (type_name.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
                    return BuildPerTypeMemorySummary();

                var type = ResolveUnityObjectType(type_name.Trim());
                if (type == null)
                    return $"Type not found (or not a UnityEngine.Object): {type_name}. " +
                           "Try one of: " + string.Join(", ", GetKnownTypeNames()) + ", or 'All'.";

                var objects = UnityEngine.Resources.FindObjectsOfTypeAll(type);
                var entries = new List<(UnityEngine.Object Obj, long Size)>(objects.Length);
                long totalSize = 0;
                foreach (var obj in objects)
                {
                    if (obj == null) continue;
                    var size = Profiler.GetRuntimeMemorySizeLong(obj);
                    totalSize += size;
                    entries.Add((obj, size));
                }

                entries.Sort((a, b) => b.Size.CompareTo(a.Size));

                var sb = new StringBuilder();
                sb.AppendLine($"Top memory objects: {type.Name}");
                sb.AppendLine($"Total: {entries.Count} object(s), {FormatBytes(totalSize)}");
                var count = Mathf.Min(top_n, entries.Count);
                for (int i = 0; i < count; i++)
                {
                    var (obj, size) = entries[i];
                    sb.Append($"[{i}] {FormatBytes(size)}  {obj.name}");
                    AppendObjectDetail(sb, obj);
                    sb.AppendLine();
                }
                if (entries.Count > count)
                    sb.AppendLine($"... {entries.Count - count} more object(s) not shown (raise top_n to see more).");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        private static string BuildPerTypeMemorySummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Loaded object memory by type:");
            foreach (var (name, type) in KnownMemoryTypes)
            {
                var objects = UnityEngine.Resources.FindObjectsOfTypeAll(type);
                long totalSize = 0;
                foreach (var obj in objects)
                {
                    if (obj == null) continue;
                    totalSize += Profiler.GetRuntimeMemorySizeLong(obj);
                }
                sb.AppendLine($"- {name}: {objects.Length} object(s), {FormatBytes(totalSize)}");
            }
            sb.AppendLine("Call again with a specific type_name to list the top objects of that type.");
            return sb.ToString();
        }

        private static void AppendObjectDetail(StringBuilder sb, UnityEngine.Object obj)
        {
            switch (obj)
            {
                case Texture2D t2d:
                    sb.Append($"  ({t2d.width}x{t2d.height} {t2d.format})");
                    break;
                case RenderTexture rt:
                    sb.Append($"  ({rt.width}x{rt.height} {rt.format})");
                    break;
                case Mesh mesh:
                    sb.Append($"  ({mesh.vertexCount} verts)");
                    break;
                case AudioClip clip:
                    sb.Append($"  ({clip.length:F1}s {clip.frequency}Hz)");
                    break;
            }
            if (obj.hideFlags != HideFlags.None)
                sb.Append($"  [flags: {obj.hideFlags}]");
        }

        private static string[] GetKnownTypeNames()
        {
            var names = new string[KnownMemoryTypes.Length];
            for (int i = 0; i < KnownMemoryTypes.Length; i++)
                names[i] = KnownMemoryTypes[i].Name;
            return names;
        }

        private static Type ResolveUnityObjectType(string typeName)
        {
            foreach (var (name, type) in KnownMemoryTypes)
            {
                if (name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    return type;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) &&
                        typeof(UnityEngine.Object).IsAssignableFrom(t))
                        return t;
                }
            }
            return null;
        }

        private static string FormatBytes(long bytes)
        {
            const double kilo = 1024.0;
            const double mega = kilo * 1024.0;
            const double giga = mega * 1024.0;

            if (bytes >= giga) return $"{bytes / giga:F2} GB";
            if (bytes >= mega) return $"{bytes / mega:F2} MB";
            if (bytes >= kilo) return $"{bytes / kilo:F2} KB";
            return $"{bytes} B";
        }

        private const string SnapshotDirRelative = "MemoryCaptures/mcp-snapshots";

        [Serializable]
        private class MemorySnapshotData
        {
            public string name;
            public string timestamp;
            public long totalAllocated;
            public long totalReserved;
            public long monoUsed;
            public long monoHeap;
            public long gcUsedMemory;
            public long gfxUsedMemory;
            public long systemUsedMemory;
        }

        [Description("Take a lightweight memory snapshot (aggregate numbers only, NOT a real Unity .snap file openable " +
                     "in the Memory Profiler window) and save it as JSON under MemoryCaptures/mcp-snapshots/. " +
                     "Use memory_compare_snapshots to diff two of these.")]
        public static string MemoryTakeSnapshot(
            [ToolParam("Optional name for the snapshot file (without extension)", Required = false)] string name = null)
        {
            try
            {
                var dir = System.IO.Path.Combine(Application.dataPath, "..", SnapshotDirRelative);
                System.IO.Directory.CreateDirectory(dir);

                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var fileName = string.IsNullOrEmpty(name) ? stamp : $"{name}_{stamp}";
                fileName = string.Join("_", fileName.Split(System.IO.Path.GetInvalidFileNameChars()));
                var path = System.IO.Path.Combine(dir, fileName + ".json");

                var data = new MemorySnapshotData
                {
                    name = fileName,
                    timestamp = stamp,
                    totalAllocated = Profiler.GetTotalAllocatedMemoryLong(),
                    totalReserved = Profiler.GetTotalReservedMemoryLong(),
                    monoUsed = Profiler.GetMonoUsedSizeLong(),
                    monoHeap = Profiler.GetMonoHeapSizeLong(),
                    gcUsedMemory = ReadCounterOrZero("Memory", "GC Used Memory"),
                    gfxUsedMemory = ReadCounterOrZero("Memory", "Gfx Used Memory"),
                    systemUsedMemory = ReadCounterOrZero("Memory", "System Used Memory"),
                };

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(path, json);

                return $"Snapshot saved: {path}\nTotal Allocated: {FormatBytes(data.totalAllocated)}, Mono Used: {FormatBytes(data.monoUsed)}";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("List previously saved lightweight memory snapshots (see memory_take_snapshot).")]
        [ReadOnlyTool]
        public static string MemoryListSnapshots()
        {
            try
            {
                var dir = System.IO.Path.Combine(Application.dataPath, "..", SnapshotDirRelative);
                if (!System.IO.Directory.Exists(dir))
                    return "No snapshots directory yet. Call memory_take_snapshot first.";

                var files = System.IO.Directory.GetFiles(dir, "*.json");
                if (files.Length == 0)
                    return "No snapshots found.";

                var sb = new StringBuilder();
                foreach (var f in files)
                    sb.AppendLine(System.IO.Path.GetFileName(f));
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Compare two lightweight memory snapshots (by file name, with or without .json extension) and " +
                     "report the delta for each aggregate category. Best-effort: not a full per-type/per-object diff.")]
        [ReadOnlyTool]
        public static string MemoryCompareSnapshots(
            [ToolParam("First snapshot file name (from memory_list_snapshots)")] string path_a,
            [ToolParam("Second snapshot file name (from memory_list_snapshots)")] string path_b)
        {
            try
            {
                var dir = System.IO.Path.Combine(Application.dataPath, "..", SnapshotDirRelative);
                var a = LoadSnapshot(dir, path_a);
                var b = LoadSnapshot(dir, path_b);
                if (a == null) return $"Snapshot not found: {path_a}";
                if (b == null) return $"Snapshot not found: {path_b}";

                var sb = new StringBuilder();
                sb.AppendLine($"Comparing {a.name} -> {b.name}");
                AppendDelta(sb, "Total Allocated", a.totalAllocated, b.totalAllocated);
                AppendDelta(sb, "Total Reserved", a.totalReserved, b.totalReserved);
                AppendDelta(sb, "Mono Used", a.monoUsed, b.monoUsed);
                AppendDelta(sb, "Mono Heap", a.monoHeap, b.monoHeap);
                AppendDelta(sb, "GC Used Memory", a.gcUsedMemory, b.gcUsedMemory);
                AppendDelta(sb, "Gfx Used Memory", a.gfxUsedMemory, b.gfxUsedMemory);
                AppendDelta(sb, "System Used Memory", a.systemUsedMemory, b.systemUsedMemory);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        private static MemorySnapshotData LoadSnapshot(string dir, string fileNameOrPath)
        {
            var safeName = System.IO.Path.GetFileName(fileNameOrPath);
            var fileName = safeName.EndsWith(".json") ? safeName : safeName + ".json";
            var path = System.IO.Path.Combine(dir, fileName);
            if (!System.IO.File.Exists(path))
                return null;
            var json = System.IO.File.ReadAllText(path);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<MemorySnapshotData>(json);
        }

        private static void AppendDelta(StringBuilder sb, string label, long before, long after)
        {
            var delta = after - before;
            sb.AppendLine($"- {label}: {FormatBytes(before)} -> {FormatBytes(after)} ({FormatSignedBytes(delta)})");
        }

        private static string FormatSignedBytes(long bytes)
        {
            return bytes >= 0 ? "+" + FormatBytes(bytes) : "-" + FormatBytes(-bytes);
        }

        private static long ReadCounterOrZero(string category, string statName)
        {
            var key = category + "/" + statName;
            if (_recorders != null && _recorders.TryGetValue(key, out var recorder))
                return recorder.CurrentValue;

            try
            {
                using (var temp = new ProfilerRecorder(category, statName))
                {
                    return temp.CurrentValue;
                }
            }
            catch
            {
                return 0;
            }
        }

        private static System.Reflection.MethodInfo _fdGetFrameEvents;
        private static System.Reflection.MethodInfo _fdGetFrameEventInfoName;
        private static System.Reflection.MethodInfo _fdGetFrameEventObject;
        private static System.Type _fdWindowType;
        private static System.Reflection.MethodInfo _fdWindowOpen;
        private static System.Reflection.MethodInfo _fdWindowEnable;
        private static System.Reflection.MethodInfo _fdWindowDisable;
        private static System.Reflection.MethodInfo _fdWindowClose;
        private static System.Reflection.MethodInfo _fdWindowRepaint;
        private static bool _fdReflectionResolved;

        private static bool ResolveFrameDebuggerReflection(out string error)
        {
            error = null;
            if (_fdReflectionResolved)
                return _fdGetFrameEvents != null && _fdWindowOpen != null;

            System.Type fdUtilType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = asm.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
                if (found != null) { fdUtilType = found; break; }
            }
            if (fdUtilType == null)
            {
                error = "FrameDebuggerUtility type not found in this Unity version.";
                return false;
            }

            const System.Reflection.BindingFlags staticPublic = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            _fdGetFrameEvents = fdUtilType.GetMethod("GetFrameEvents", staticPublic);
            _fdGetFrameEventInfoName = fdUtilType.GetMethod("GetFrameEventInfoName", staticPublic);
            _fdGetFrameEventObject = fdUtilType.GetMethod("GetFrameEventObject", staticPublic);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = asm.GetType("UnityEditor.FrameDebuggerWindow");
                if (found != null) { _fdWindowType = found; break; }
            }
            if (_fdWindowType == null)
            {
                error = "FrameDebuggerWindow type not found in this Unity version.";
                return false;
            }

            _fdWindowOpen = _fdWindowType.GetMethod("OpenWindow", staticPublic);
            _fdWindowEnable = _fdWindowType.GetMethod("EnableFrameDebugger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            _fdWindowDisable = _fdWindowType.GetMethod("DisableFrameDebugger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            _fdWindowClose = _fdWindowType.GetMethod("Close", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            _fdWindowRepaint = _fdWindowType.GetMethod("Repaint", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (_fdGetFrameEvents == null || _fdGetFrameEventInfoName == null || _fdWindowOpen == null || _fdWindowEnable == null || _fdWindowDisable == null)
            {
                error = "One or more expected FrameDebugger reflection targets were not found (Unity API surface may have changed).";
                return false;
            }

            _fdReflectionResolved = true;
            return true;
        }

        [Description("Enable the Frame Debugger by opening a Frame Debugger window and driving its internal capture loop " +
                     "(a bare static SetEnabled call does not populate events without a live window instance). " +
                     "Follow with frame_debugger_get_events after a moment, then frame_debugger_disable when done.")]
        public static string FrameDebuggerEnable()
        {
            try
            {
                if (!ResolveFrameDebuggerReflection(out var error))
                    return ToolResultFormatter.Exception(new NotSupportedException(error));

                var win = _fdWindowOpen.Invoke(null, null);
                _fdWindowEnable.Invoke(win, null);
                _fdWindowRepaint?.Invoke(win, null);
                return "Frame Debugger enabled (window opened and driven). Wait a moment, then call frame_debugger_get_events.";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Disable the Frame Debugger and close its window. IMPORTANT: always call this after frame_debugger_enable " +
                     "when done inspecting, otherwise a Frame Debugger window is left open in the editor layout. " +
                     "Note: this closes ALL currently open Frame Debugger windows, including any a developer opened manually " +
                     "(not just the one this tool opened) — mention this to the user if they may have one open.")]
        public static string FrameDebuggerDisable()
        {
            try
            {
                if (!ResolveFrameDebuggerReflection(out var error))
                    return ToolResultFormatter.Exception(new NotSupportedException(error));

                var instances = UnityEngine.Resources.FindObjectsOfTypeAll(_fdWindowType);
                if (instances.Length == 0)
                    return "No open Frame Debugger window found (already disabled?).";

                foreach (var win in instances)
                {
                    _fdWindowDisable.Invoke(win, null);
                    _fdWindowClose?.Invoke(win, null);
                }
                return "Frame Debugger disabled and window closed.";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("List frame debugger events for the currently captured frame (requires frame_debugger_enable first, " +
                     "and a moment for the editor to actually render/capture a frame). " +
                     "Best-effort: returns event name and associated object per event, not full per-draw-call shader parameters.")]
        [ReadOnlyTool]
        public static string FrameDebuggerGetEvents(
            [ToolParam("Maximum number of events to return", Required = false)] int max_events = 50)
        {
            try
            {
                if (!ResolveFrameDebuggerReflection(out var error))
                    return ToolResultFormatter.Exception(new NotSupportedException(error));

                var events = _fdGetFrameEvents.Invoke(null, null) as Array;
                if (events == null || events.Length == 0)
                    return "No frame debugger events captured yet. Make sure frame_debugger_enable was called, then wait a moment for a frame to render before calling this again.";

                max_events = Mathf.Clamp(max_events, 1, 500);
                var count = Mathf.Min(max_events, events.Length);
                var sb = new StringBuilder();
                sb.AppendLine($"Total events: {events.Length} (showing {count})");
                for (int i = 0; i < count; i++)
                {
                    var infoName = (string)_fdGetFrameEventInfoName.Invoke(null, new object[] { i });
                    string objName = "<none>";
                    if (_fdGetFrameEventObject != null)
                    {
                        var obj = _fdGetFrameEventObject.Invoke(null, new object[] { i }) as UnityEngine.Object;
                        if (obj != null) objName = obj.name;
                    }
                    sb.AppendLine($"[{i}] {infoName}  (object: {objName})");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }
    }
}
