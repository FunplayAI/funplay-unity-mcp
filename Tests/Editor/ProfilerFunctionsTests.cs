// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Linq;
using Funplay.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace Funplay.Editor.Tests
{
    public sealed class ProfilerFunctionsTests
    {
        [Test]
        public void ProfilerTools_ReturnUsableSessionTimingAndMemoryResults()
        {
            var originalProfilerEnabled = Profiler.enabled;
            var objectName = "FunplayProfilerObject_" + Guid.NewGuid().ToString("N");
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = objectName;

            try
            {
                Assert.That(ProfilerFunctions.ProfilerStart(), Does.Contain("Profiler started"));
                Assert.That(ProfilerFunctions.ProfilerStatus(), Does.Contain("Profiler.enabled"));

                var frameTiming = ProfilerFunctions.GetFrameTiming(sample_frames: 1);
                Assert.That(frameTiming, Does.Contain("Frame"));

                var counters = ProfilerFunctions.GetCounters();
                Assert.That(counters, Is.Not.Null);

                var objectMemory = ProfilerFunctions.GetObjectMemory(objectName);
                Assert.That(objectMemory, Does.Contain("Type: GameObject"));
                Assert.That(objectMemory, Does.Contain("Runtime Memory:"));

                var topTextures = ProfilerFunctions.GetTopMemoryObjects(type_name: "Texture2D", top_n: 1);
                Assert.That(topTextures, Does.Contain("Top memory objects: Texture2D"));

                var topAll = ProfilerFunctions.GetTopMemoryObjects(type_name: "All", top_n: 1);
                Assert.That(topAll, Does.Contain("Loaded object memory by type"));

                var badType = ProfilerFunctions.GetTopMemoryObjects(type_name: "DefinitelyNotAUnityObjectType", top_n: 1);
                Assert.That(badType, Does.Contain("Type not found"));

                var frameDebuggerDisable = ProfilerFunctions.FrameDebuggerDisable();
                Assert.That(frameDebuggerDisable, Does.Contain("Frame Debugger"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                ProfilerFunctions.ProfilerStop();
                Profiler.enabled = originalProfilerEnabled;
            }
        }

        [Test]
        public void MemorySnapshotTools_CreateListCompareAndCleanUpJsonSnapshots()
        {
            var prefix = "FunplayProfilerSnapshot_" + Guid.NewGuid().ToString("N");
            var snapshotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "MemoryCaptures/mcp-snapshots"));

            try
            {
                Assert.That(ProfilerFunctions.MemoryTakeSnapshot(prefix + "_a"), Does.Contain("Snapshot saved:"));
                Assert.That(ProfilerFunctions.MemoryTakeSnapshot(prefix + "_b"), Does.Contain("Snapshot saved:"));

                var files = Directory.GetFiles(snapshotDir, prefix + "*.json")
                    .Select(Path.GetFileName)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                Assert.That(files.Length, Is.EqualTo(2));

                var list = ProfilerFunctions.MemoryListSnapshots();
                Assert.That(list, Does.Contain(files[0]));
                Assert.That(list, Does.Contain(files[1]));

                var compare = ProfilerFunctions.MemoryCompareSnapshots(files[0], files[1]);
                Assert.That(compare, Does.Contain("Comparing"));
                Assert.That(compare, Does.Contain("Total Allocated"));

                var missing = ProfilerFunctions.MemoryCompareSnapshots(prefix + "_missing", files[1]);
                Assert.That(missing, Does.Contain("Snapshot not found"));
            }
            finally
            {
                if (Directory.Exists(snapshotDir))
                {
                    foreach (var file in Directory.GetFiles(snapshotDir, prefix + "*.json"))
                        File.Delete(file);
                }
            }
        }
    }
}
