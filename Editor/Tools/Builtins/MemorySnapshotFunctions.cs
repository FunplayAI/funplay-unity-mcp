// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Unity.Profiling.Memory;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    /// <summary>
    /// REAL Unity memory snapshots (.snap) -- the file format the Memory Profiler
    /// package opens for object-level reference-chain analysis. Complements the
    /// lightweight aggregate-counter snapshots in <see cref="ProfilerFunctions"/>
    /// (memory_take_snapshot / memory_compare_snapshots): use those to detect THAT
    /// memory grew, and these to find out WHAT is holding it and WHO references it.
    /// Capture uses <c>Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot</c>
    /// (core engine API, Unity 2022.2+); opening in the Memory Profiler window uses
    /// reflection into the optional com.unity.memoryprofiler package.
    /// </summary>
    [ToolProvider("MemorySnapshot")]
    internal static class MemorySnapshotFunctions
    {
        private const string DefaultStorageDirName = "MemoryCaptures";
        private const string PackageWindowTypeName = "Unity.MemoryProfiler.Editor.MemoryProfilerWindow";
        private const string PackageSettingsTypeName = "Unity.MemoryProfiler.Editor.MemoryProfilerSettings";
        private const string PackageEditorAssemblyName = "Unity.MemoryProfiler.Editor";

        [Description("Capture a REAL Unity memory snapshot (.snap) -- the full-detail file the Memory Profiler package " +
                     "opens for object-level reference-chain analysis ('who is holding this texture alive'). " +
                     "Complements memory_take_snapshot, which only records lightweight aggregate counters: use that to " +
                     "detect THAT memory grew, and this to find out WHAT is holding it. The file is written into the " +
                     "Memory Profiler package's snapshot folder (default 'MemoryCaptures/' at the project root) so it " +
                     "shows up in the window's snapshot list. WARNING: in-editor captures include editor-owned memory " +
                     "and can be hundreds of MB to several GB on large projects; the capture stalls the editor for a " +
                     "few seconds while it runs.")]
        [ReadOnlyTool]
        public static async Task<string> MemoryTakeFullSnapshot(
            [ToolParam("Base file name (timestamp and .snap extension are appended). Default 'mcp'.", Required = false)] string name = null,
            [ToolParam("Comma-separated capture flags: ManagedObjects, NativeObjects, NativeAllocations, " +
                       "NativeAllocationSites, NativeStackTraces. Default 'ManagedObjects,NativeObjects' " +
                       "(what reference-chain analysis needs; the NativeAllocation* flags add a lot of size).", Required = false)] string capture_flags = null,
            [ToolParam("Maximum seconds to wait for the capture to finish. Default 180.", Required = false)] int timeout_seconds = 180,
            [ToolParam("Open the finished snapshot in the Memory Profiler window (requires the com.unity.memoryprofiler package).", Required = false)] bool open_in_profiler = false)
        {
            if (!TryParseCaptureFlags(capture_flags, out var flags, out var flagsError))
                return ToolResultFormatter.Error("INVALID_CAPTURE_FLAGS", flagsError);

            timeout_seconds = Mathf.Clamp(timeout_seconds, 10, 900);

            string path;
            try
            {
                var dir = ResolveStorageDirectory();
                Directory.CreateDirectory(dir);
                var baseName = SanitizeFileNameFragment(string.IsNullOrWhiteSpace(name) ? "mcp" : name);
                path = Path.Combine(dir, baseName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".snap");
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SNAPSHOT_PATH_FAILED", new { message = ex.Message });
            }

            var completion = new TaskCompletionSource<bool>();
            var startedAt = EditorApplication.timeSinceStartup;

            try
            {
                MemoryProfiler.TakeSnapshot(path, (finalPath, success) => completion.TrySetResult(success), flags);
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SNAPSHOT_CAPTURE_FAILED", new { message = ex.Message });
            }

            // The snapshot is written at the end of a player-loop frame; a paused Edit Mode
            // editor may never tick one on its own, so keep pumping until the callback fires.
            while (!completion.Task.IsCompleted)
            {
                if (EditorApplication.timeSinceStartup - startedAt > timeout_seconds)
                {
                    return ToolResultFormatter.Error("SNAPSHOT_TIMEOUT", new
                    {
                        timeout_seconds,
                        path,
                        hint = "The capture callback did not fire in time. Very large heaps can take minutes; retry with a bigger timeout_seconds."
                    });
                }

                EditorApplication.QueuePlayerLoopUpdate();
                await Task.Delay(250);
            }

            if (!completion.Task.Result)
                return ToolResultFormatter.Error("SNAPSHOT_CAPTURE_FAILED", new { path, hint = "Unity reported the capture as failed." });

            long sizeBytes = 0;
            try { sizeBytes = new FileInfo(path).Length; } catch { }

            string openError = null;
            var opened = false;
            if (open_in_profiler)
                opened = TryOpenSnapshotInProfiler(path, out openError);

            return JsonConvert.SerializeObject(Response.Success(
                "Memory snapshot captured.",
                new
                {
                    path,
                    size_bytes = sizeBytes,
                    size = FormatBytes(sizeBytes),
                    flags = flags.ToString(),
                    duration_seconds = Math.Round(EditorApplication.timeSinceStartup - startedAt, 1),
                    opened_in_profiler = opened,
                    open_error = openError,
                    hint = "Open it with memory_open_snapshot_in_profiler, then use capture_editor_window('Memory Profiler') to inspect the analysis visually."
                }));
        }

        [Description("List the REAL .snap memory snapshots in the Memory Profiler package's snapshot folder " +
                     "(the ones memory_take_full_snapshot writes and the Memory Profiler window lists). " +
                     "For the lightweight aggregate JSON snapshots, use memory_list_snapshots instead.")]
        [ReadOnlyTool]
        public static string MemoryListFullSnapshots()
        {
            string dir;
            try
            {
                dir = ResolveStorageDirectory();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SNAPSHOT_DIR_FAILED", new { message = ex.Message });
            }

            if (!Directory.Exists(dir))
                return JsonConvert.SerializeObject(Response.Success("No snapshot folder yet.", new { directory = dir, snapshots = Array.Empty<object>() }));

            var snapshots = Directory.GetFiles(dir, "*.snap")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new
                {
                    file = f.Name,
                    path = f.FullName,
                    size_bytes = f.Length,
                    size = FormatBytes(f.Length),
                    modified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                })
                .ToArray();

            return JsonConvert.SerializeObject(Response.Success(
                $"{snapshots.Length} snapshot(s) in {dir}.",
                new { directory = dir, snapshots }));
        }

        [Description("Open a REAL .snap memory snapshot in the Memory Profiler package window for object-level " +
                     "reference-chain analysis. Accepts an absolute path or a file name from the snapshot folder " +
                     "(see memory_list_full_snapshots). Requires the com.unity.memoryprofiler package. " +
                     "Combine with capture_editor_window('Memory Profiler') to inspect the loaded analysis visually.")]
        [ReadOnlyTool]
        public static string MemoryOpenSnapshotInProfiler(
            [ToolParam("Absolute .snap path, or a file name inside the snapshot folder (with or without the .snap extension).")] string snapshot)
        {
            string path;
            try
            {
                if (!TryResolveSnapshotPath(snapshot, ResolveStorageDirectory(), out path, out var pathError))
                    return ToolResultFormatter.Error("INVALID_SNAPSHOT", pathError);
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("SNAPSHOT_DIR_FAILED", new { message = ex.Message });
            }

            if (path == null)
                return ToolResultFormatter.Error("SNAPSHOT_NOT_FOUND", new { requested = snapshot, hint = "Use memory_list_full_snapshots to see what exists." });

            if (!TryOpenSnapshotInProfiler(path, out var error))
                return ToolResultFormatter.Error("OPEN_IN_PROFILER_FAILED", new { path, message = error });

            return JsonConvert.SerializeObject(Response.Success(
                "Snapshot loaded in the Memory Profiler window.",
                new { path, hint = "Use capture_editor_window('Memory Profiler') to inspect the analysis visually." }));
        }

        [Description("Query a REAL .snap memory snapshot for the largest native objects (Texture2D, Mesh, RenderTexture, " +
                     "AudioClip, etc.) -- structured JSON, no window or screenshot needed. This is the 'WHAT is holding " +
                     "the memory' half of the workflow started by memory_take_full_snapshot. Loads headlessly via the " +
                     "Memory Profiler package's crawler (no window opened); requires the com.unity.memoryprofiler package.")]
        [ReadOnlyTool]
        public static string MemoryQueryTopObjects(
            [ToolParam("Absolute .snap path, or a file name inside the snapshot folder (with or without the .snap extension).")] string snapshot,
            [ToolParam("Only include objects whose native type name contains this text (case-insensitive), e.g. 'Texture2D'.", Required = false)] string type_filter = null,
            [ToolParam("Number of objects to return (1-500). Default 20.", Required = false)] int top_n = 20)
        {
            string path;
            try
            {
                if (!TryResolveSnapshotPath(snapshot, ResolveStorageDirectory(), out path, out var pathError))
                    return ToolResultFormatter.Error("INVALID_SNAPSHOT", pathError);
            }
            catch (Exception ex) { return ToolResultFormatter.Error("SNAPSHOT_DIR_FAILED", new { message = ex.Message }); }
            if (path == null)
                return ToolResultFormatter.Error("SNAPSHOT_NOT_FOUND", new { requested = snapshot, hint = "Use memory_list_full_snapshots to see what exists." });

            top_n = Mathf.Clamp(top_n, 1, 500);

            using (var accessor = SnapshotAccessor.Load(path, out var loadError))
            {
                if (accessor == null)
                    return ToolResultFormatter.Error("SNAPSHOT_QUERY_FAILED", new { path, message = loadError });

                try
                {
                    var rows = accessor.EnumerateNativeObjects();
                    if (!string.IsNullOrWhiteSpace(type_filter))
                        rows = rows.Where(r => r.TypeName.IndexOf(type_filter, StringComparison.OrdinalIgnoreCase) >= 0);

                    var top = rows.OrderByDescending(r => r.Size)
                        .Take(top_n)
                        .Select(r => new
                        {
                            native_object_index = r.Index,
                            name = r.Name,
                            type = r.TypeName,
                            size_bytes = r.Size,
                            size = FormatBytes((long)r.Size)
                        })
                        .ToArray();

                    return JsonConvert.SerializeObject(Response.Success(
                        $"{top.Length} native object(s) (out of {accessor.NativeObjectCount} total in the snapshot).",
                        new { path, type_filter, objects = top }));
                }
                catch (Exception ex)
                {
                    return ToolResultFormatter.Error("SNAPSHOT_QUERY_FAILED", new { path, message = ex.Message });
                }
            }
        }

        [Description("Query a REAL .snap memory snapshot for what references a specific native object, or what it " +
                     "references -- structured JSON reference-chain data, no window or screenshot needed. Answers " +
                     "'who is keeping this texture alive'. Pass the object name or native_object_index from " +
                     "memory_query_top_objects. Loads headlessly via the Memory Profiler package's crawler; requires " +
                     "the com.unity.memoryprofiler package. Only native objects can be looked up directly; the returned " +
                     "reference lists may include managed objects and GC handles as informational entries.")]
        [ReadOnlyTool]
        public static string MemoryQueryReferences(
            [ToolParam("Absolute .snap path, or a file name inside the snapshot folder (with or without the .snap extension).")] string snapshot,
            [ToolParam("Native object name (exact match preferred, falls back to a case-insensitive substring match) or the native_object_index from memory_query_top_objects.")] string target,
            [ToolParam("'referenced_by' (default, what keeps this object alive) or 'references_to' (what this object points to).", Required = false)] string direction = "referenced_by",
            [ToolParam("Maximum number of references to return (1-200). Default 30.", Required = false)] int max_results = 30)
        {
            string path;
            try
            {
                if (!TryResolveSnapshotPath(snapshot, ResolveStorageDirectory(), out path, out var pathError))
                    return ToolResultFormatter.Error("INVALID_SNAPSHOT", pathError);
            }
            catch (Exception ex) { return ToolResultFormatter.Error("SNAPSHOT_DIR_FAILED", new { message = ex.Message }); }
            if (path == null)
                return ToolResultFormatter.Error("SNAPSHOT_NOT_FOUND", new { requested = snapshot, hint = "Use memory_list_full_snapshots to see what exists." });

            var directionNormalized = (direction ?? "referenced_by").Trim().ToLowerInvariant();
            if (directionNormalized != "referenced_by" && directionNormalized != "references_to")
                return ToolResultFormatter.Error("INVALID_DIRECTION", new { direction, accepted = new[] { "referenced_by", "references_to" } });

            max_results = Mathf.Clamp(max_results, 1, 200);

            using (var accessor = SnapshotAccessor.Load(path, out var loadError))
            {
                if (accessor == null)
                    return ToolResultFormatter.Error("SNAPSHOT_QUERY_FAILED", new { path, message = loadError });

                try
                {
                    if (!accessor.TryResolveNativeObject(target, out var resolved, out var candidates))
                    {
                        return ToolResultFormatter.Error("TARGET_NOT_FOUND", new
                        {
                            requested = target,
                            candidates,
                            hint = candidates.Length > 0
                                ? "Multiple objects matched; pass the exact name or native_object_index to disambiguate."
                                : "No native object matched. Use memory_query_top_objects to find the exact name/index."
                        });
                    }

                    var references = accessor.GetReferences(resolved.Index, referencedBy: directionNormalized == "referenced_by")
                        .Take(max_results)
                        .ToArray();

                    return JsonConvert.SerializeObject(Response.Success(
                        $"{references.Length} {directionNormalized} reference(s) for '{resolved.Name}'.",
                        new
                        {
                            path,
                            target = new { native_object_index = resolved.Index, name = resolved.Name, type = resolved.TypeName, size_bytes = resolved.Size, size = FormatBytes((long)resolved.Size) },
                            direction = directionNormalized,
                            references
                        }));
                }
                catch (Exception ex)
                {
                    return ToolResultFormatter.Error("SNAPSHOT_QUERY_FAILED", new { path, message = ex.Message });
                }
            }
        }

        // --- Helpers ---

        internal static bool TryParseCaptureFlags(string raw, out CaptureFlags flags, out object error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                flags = CaptureFlags.ManagedObjects | CaptureFlags.NativeObjects;
                return true;
            }

            flags = 0;
            foreach (var token in raw.Split(','))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0)
                    continue;
                if (!Enum.TryParse<CaptureFlags>(trimmed, ignoreCase: true, out var parsed))
                {
                    error = new { provided = trimmed, accepted = Enum.GetNames(typeof(CaptureFlags)) };
                    return false;
                }
                flags |= parsed;
            }

            if (flags == 0)
            {
                error = new { provided = raw, hint = "At least one capture flag is required." };
                return false;
            }
            return true;
        }

        /// <summary>
        /// The Memory Profiler package's configured snapshot folder when the package is
        /// installed (so captures show up in its window), else MemoryCaptures/ at the
        /// project root -- which is also that setting's default.
        /// </summary>
        private static string ResolveStorageDirectory()
        {
            try
            {
                var settingsType = ResolvePackageType(PackageSettingsTypeName);
                var pathProperty = settingsType?.GetProperty("AbsoluteMemorySnapshotStoragePath",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (pathProperty?.GetValue(null) is string configured && !string.IsNullOrEmpty(configured))
                    return configured;
            }
            catch
            {
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            return Path.Combine(projectRoot, DefaultStorageDirName);
        }

        internal static bool TryResolveSnapshotPath(string requested, string storageDirectory, out string path, out object error)
        {
            path = null;
            error = null;

            if (string.IsNullOrWhiteSpace(requested))
            {
                error = new { hint = "Provide a .snap path or file name." };
                return false;
            }

            if (string.IsNullOrWhiteSpace(storageDirectory))
            {
                error = new { hint = "Memory snapshot storage directory could not be resolved." };
                return false;
            }

            var trimmed = requested.Trim();
            var isRooted = Path.IsPathRooted(trimmed);
            var hasSnapExtension = trimmed.EndsWith(".snap", StringComparison.OrdinalIgnoreCase);
            if (isRooted && !hasSnapExtension)
            {
                error = new { requested, hint = "Absolute snapshot paths must end with .snap." };
                return false;
            }

            if (isRooted)
            {
                var fullPath = Path.GetFullPath(trimmed);
                path = File.Exists(fullPath) ? fullPath : null;
                return true;
            }

            var normalizedDirectory = Path.GetFullPath(storageDirectory);
            var fileName = hasSnapExtension ? trimmed : trimmed + ".snap";
            var candidate = Path.GetFullPath(Path.Combine(normalizedDirectory, fileName));
            if (!IsPathInsideDirectory(candidate, normalizedDirectory))
            {
                error = new
                {
                    requested,
                    snapshot_directory = normalizedDirectory,
                    hint = "Relative snapshot names must resolve inside the Memory Profiler snapshot directory."
                };
                return false;
            }

            path = File.Exists(candidate) ? candidate : null;
            return true;
        }

        private static bool IsPathInsideDirectory(string path, string directory)
        {
            var comparison = StringComparison.OrdinalIgnoreCase;
            var normalizedPath = Path.GetFullPath(path);
            var normalizedDirectory = EnsureTrailingSeparator(Path.GetFullPath(directory));
            return normalizedPath.StartsWith(normalizedDirectory, comparison);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            var last = path[path.Length - 1];
            if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
                return path;

            return path + Path.DirectorySeparatorChar;
        }

        private static bool TryOpenSnapshotInProfiler(string path, out string error)
        {
            error = null;
            try
            {
                var windowType = ResolvePackageType(PackageWindowTypeName);
                if (windowType == null)
                {
                    error = "The com.unity.memoryprofiler package is not installed. Install it via the Package Manager to analyze snapshots.";
                    return false;
                }

                var window = EditorWindow.GetWindow(windowType);
                window.Show();
                window.Focus();

                var serviceField = windowType.GetField("m_SnapshotDataService", BindingFlags.NonPublic | BindingFlags.Instance);
                var service = serviceField?.GetValue(window);
                var loadMethod = service?.GetType().GetMethod("Load",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);
                if (loadMethod == null)
                {
                    error = "The Memory Profiler window opened, but its snapshot-loading internals were not found in this package version. Load the snapshot manually from the window's list.";
                    return false;
                }

                // Load() looks the file up in the service's session collection, which is
                // only populated by a folder scan -- a freshly captured file (or a freshly
                // opened window) hasn't been scanned yet and Load() throws a dictionary
                // key miss. Sync the folder first.
                var syncMethod = service.GetType().GetMethod("SyncSnapshotsFolder",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                syncMethod?.Invoke(service, null);

                loadMethod.Invoke(service, new object[] { path });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        private static Type ResolvePackageType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.GetName().Name.Equals(PackageEditorAssemblyName, StringComparison.Ordinal))
                    continue;
                return assembly.GetType(fullName);
            }
            return null;
        }

        private static string SanitizeFileNameFragment(string value)
        {
            var chars = value.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray();
            var sanitized = new string(chars).Trim('-');
            return string.IsNullOrEmpty(sanitized) ? "mcp" : sanitized;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F2} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F2} MB";
            if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F2} KB";
            return $"{bytes} B";
        }

        /// <summary>
        /// Reflection wrapper around the Memory Profiler package's crawled snapshot
        /// (<c>CachedSnapshot</c>), loaded headlessly via <c>SnapshotDataService.
        /// LoadWithoutLoadingToUI</c> -- no window is ever opened. Exposes the two
        /// pieces of the package's internal data model that answer "what is the
        /// biggest thing" and "who references it": the native object table
        /// (struct-of-arrays: <c>ObjectName</c>/<c>Size</c>/<c>NativeTypeArrayIndex</c>,
        /// parallel by index) and the connection graph
        /// (<c>Connections.ReferencedBy</c>/<c>ReferenceTo</c>, a
        /// <c>Dictionary&lt;SourceIndex, List&lt;SourceIndex&gt;&gt;</c>).
        /// <c>DynamicArray&lt;T&gt;</c>'s indexer returns <c>ref T</c>, which plain
        /// reflection cannot invoke -- values are read by walking the array's
        /// non-generic <see cref="IEnumerable"/> implementation instead, whose
        /// <c>Current</c> returns by value.
        /// </summary>
        private sealed class SnapshotAccessor : IDisposable
        {
            public struct NativeObjectRow
            {
                public long Index;
                public string Name;
                public string TypeName;
                public ulong Size;
            }

            private readonly object _cachedSnapshot;
            private readonly Type _cachedSnapshotType;
            private readonly List<NativeObjectRow> _rows;

            public long NativeObjectCount => _rows.Count;

            private SnapshotAccessor(object cachedSnapshot, Type cachedSnapshotType, List<NativeObjectRow> rows)
            {
                _cachedSnapshot = cachedSnapshot;
                _cachedSnapshotType = cachedSnapshotType;
                _rows = rows;
            }

            public static SnapshotAccessor Load(string path, out string error)
            {
                error = null;
                try
                {
                    var serviceType = ResolvePackageType("Unity.MemoryProfiler.Editor.SnapshotDataService");
                    if (serviceType == null)
                    {
                        error = "The com.unity.memoryprofiler package is not installed. Install it via the Package Manager to analyze snapshots.";
                        return null;
                    }

                    var service = Activator.CreateInstance(serviceType, nonPublic: true);
                    var loadMethod = serviceType.GetMethod("LoadWithoutLoadingToUI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var cachedSnapshot = loadMethod?.Invoke(service, new object[] { path, true });
                    if (cachedSnapshot == null)
                    {
                        error = "The Memory Profiler package could not crawl this file (LoadWithoutLoadingToUI returned null).";
                        return null;
                    }

                    var csType = cachedSnapshot.GetType();
                    var rows = BuildNativeObjectRows(cachedSnapshot, csType);
                    return new SnapshotAccessor(cachedSnapshot, csType, rows);
                }
                catch (Exception ex)
                {
                    error = ex.InnerException?.Message ?? ex.Message;
                    return null;
                }
            }

            private static List<NativeObjectRow> BuildNativeObjectRows(object cachedSnapshot, Type csType)
            {
                var nativeObjects = csType.GetField("NativeObjects", BindingFlags.Public | BindingFlags.Instance).GetValue(cachedSnapshot);
                var noType = nativeObjects.GetType();

                var count = (long)noType.GetField("Count").GetValue(nativeObjects);
                var objectNames = (string[])noType.GetField("ObjectName").GetValue(nativeObjects);
                var sizes = EnumerateDynamicArray(noType.GetField("Size").GetValue(nativeObjects));
                var typeIndices = EnumerateDynamicArray(noType.GetField("NativeTypeArrayIndex").GetValue(nativeObjects));

                var nativeTypes = csType.GetField("NativeTypes", BindingFlags.Public | BindingFlags.Instance).GetValue(cachedSnapshot);
                var typeNames = (string[])nativeTypes.GetType().GetField("TypeName").GetValue(nativeTypes);

                var rows = new List<NativeObjectRow>((int)Math.Min(count, int.MaxValue));
                for (long i = 0; i < count; i++)
                {
                    var typeIndex = Convert.ToInt32(typeIndices[(int)i]);
                    var typeName = (typeIndex >= 0 && typeIndex < typeNames.Length) ? typeNames[typeIndex] : "<unknown type>";
                    rows.Add(new NativeObjectRow
                    {
                        Index = i,
                        Name = objectNames[i],
                        TypeName = typeName,
                        Size = Convert.ToUInt64(sizes[(int)i])
                    });
                }
                return rows;
            }

            /// <summary>Walks a package DynamicArray&lt;T&gt; via its non-generic IEnumerable (Current returns by value).</summary>
            private static List<object> EnumerateDynamicArray(object dynamicArray)
            {
                var values = new List<object>();
                var getEnumerator = dynamicArray.GetType().GetInterfaceMap(typeof(IEnumerable)).TargetMethods[0];
                var enumerator = (IEnumerator)getEnumerator.Invoke(dynamicArray, null);
                while (enumerator.MoveNext())
                    values.Add(enumerator.Current);
                return values;
            }

            public IEnumerable<NativeObjectRow> EnumerateNativeObjects() => _rows;

            public bool TryResolveNativeObject(string target, out NativeObjectRow resolved, out string[] candidates)
            {
                resolved = default;
                candidates = Array.Empty<string>();

                if (string.IsNullOrWhiteSpace(target))
                    return false;

                target = target.Trim();

                if (long.TryParse(target, out var index) && index >= 0 && index < _rows.Count)
                {
                    resolved = _rows[(int)index];
                    return true;
                }

                var exact = _rows.Where(r => string.Equals(r.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 1)
                {
                    resolved = exact[0];
                    return true;
                }

                var partial = (exact.Count > 1 ? exact : _rows.Where(r => r.Name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                if (partial.Count == 1)
                {
                    resolved = partial[0];
                    return true;
                }

                candidates = partial.Take(20).Select(r => $"#{r.Index} '{r.Name}' ({r.TypeName}, {r.Size} bytes)").ToArray();
                return false;
            }

            /// <summary>Resolves the referrers/referents of a native object via Connections.ReferencedBy/ReferenceTo.</summary>
            public IEnumerable<object> GetReferences(long nativeObjectIndex, bool referencedBy)
            {
                var sourceIndexType = _cachedSnapshotType.GetNestedType("SourceIndex", BindingFlags.Public | BindingFlags.NonPublic);
                var sourceIdType = sourceIndexType.GetNestedType("SourceId", BindingFlags.Public | BindingFlags.NonPublic);
                var nativeObjectId = Enum.Parse(sourceIdType, "NativeObject");
                var getNameMethod = sourceIndexType.GetMethod("GetName", new[] { _cachedSnapshotType });
                var idProperty = sourceIndexType.GetProperty("Id");
                var indexProperty = sourceIndexType.GetProperty("Index");

                var connections = _cachedSnapshotType.GetField("Connections", BindingFlags.Public | BindingFlags.Instance).GetValue(_cachedSnapshot);
                var dictProperty = connections.GetType().GetProperty(referencedBy ? "ReferencedBy" : "ReferenceTo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var dict = (IDictionary)dictProperty.GetValue(connections);

                var key = Activator.CreateInstance(sourceIndexType, nativeObjectId, nativeObjectIndex);
                if (!dict.Contains(key))
                    yield break;

                foreach (var entry in (IEnumerable)dict[key])
                {
                    var kind = idProperty.GetValue(entry).ToString();
                    var entryIndex = (long)indexProperty.GetValue(entry);
                    var name = (string)getNameMethod.Invoke(entry, new object[] { _cachedSnapshot });

                    object sizeBytes = null;
                    if (kind == "NativeObject" && entryIndex >= 0 && entryIndex < _rows.Count)
                        sizeBytes = _rows[(int)entryIndex].Size;

                    yield return new { kind, index = entryIndex, name, size_bytes = sizeBytes };
                }
            }

            public void Dispose()
            {
                (_cachedSnapshot as IDisposable)?.Dispose();
            }
        }
    }
}
