// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Performance")]
    internal static class PerformanceFunctions
    {
        [Description("Get a lightweight performance snapshot of the current Unity session. " +
                     "Returns frame timing, memory usage, scene scale, and a few high-signal rendering counts. " +
                     "Useful as a first pass before deeper profiling.")]
        [ReadOnlyTool]
        public static string GetPerformanceSnapshot(
            [ToolParam("Include scene object and renderer counts", Required = false)] bool include_scene_counts = true)
        {
            try
            {
                SceneCounters counters = null;
                if (include_scene_counts)
                    counters = CollectSceneCounters(includeInactive: true, topN: 0);

                var sb = new StringBuilder();
                sb.AppendLine("Performance Snapshot");
                sb.AppendLine($"Mode: {(EditorApplication.isPlaying ? "Play Mode" : "Edit Mode")}");
                sb.AppendLine($"Scene(s): {GetLoadedScenesSummary()}");
                sb.AppendLine($"Target Frame Rate: {Application.targetFrameRate}");
                sb.AppendLine($"VSync Count: {QualitySettings.vSyncCount}");
                sb.AppendLine($"Time Scale: {Time.timeScale:F2}");

                AppendFrameTiming(sb);
                AppendMemoryStats(sb);

                if (include_scene_counts)
                {
                    sb.AppendLine("Scene Counts:");
                    sb.AppendLine($"- GameObjects: {counters.GameObjectCount} total, {counters.ActiveGameObjectCount} active");
                    sb.AppendLine($"- Components: {counters.ComponentCount}");
                    sb.AppendLine($"- Renderers: {counters.RendererCount} ({counters.SkinnedMeshRendererCount} skinned)");
                    sb.AppendLine($"- Triangles: {counters.TriangleCount:N0}");
                    sb.AppendLine($"- Material Slots: {counters.MaterialSlotCount}");
                    sb.AppendLine($"- Canvases: {counters.CanvasCount}, UI Graphics: {counters.GraphicCount}");
                    sb.AppendLine($"- Colliders: {counters.ColliderCount}, Rigidbodies: {counters.RigidbodyCount}");
                    sb.AppendLine($"- Lights: {counters.LightCount}, Particle Systems: {counters.ParticleSystemCount}, Animators: {counters.AnimatorCount}");
                    sb.AppendLine($"- Audio Sources: {counters.AudioSourceCount}, Cameras: {counters.CameraCount}, Scripts: {counters.MonoBehaviourCount}");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Analyze the current scene for common performance hotspots. " +
                     "Summarizes scene complexity and lists the heaviest objects by triangle count, material count, and component count.")]
        [ReadOnlyTool]
        public static string AnalyzeSceneComplexity(
            [ToolParam("Number of heaviest objects to return", Required = false)] int top_n = 10,
            [ToolParam("Include inactive objects in the analysis", Required = false)] bool include_inactive = true)
        {
            try
            {
                top_n = Mathf.Clamp(top_n, 1, 25);
                var counters = CollectSceneCounters(include_inactive, top_n);

                var sb = new StringBuilder();
                sb.AppendLine("Scene Complexity Analysis");
                sb.AppendLine($"Scene(s): {GetLoadedScenesSummary()}");
                sb.AppendLine($"Objects: {counters.GameObjectCount} total, {counters.ActiveGameObjectCount} active");
                sb.AppendLine($"Components: {counters.ComponentCount}, Scripts: {counters.MonoBehaviourCount}");
                sb.AppendLine($"Renderers: {counters.RendererCount}, Triangles: {counters.TriangleCount:N0}, Material Slots: {counters.MaterialSlotCount}");
                sb.AppendLine($"UI: {counters.CanvasCount} canvases, {counters.GraphicCount} graphics");
                sb.AppendLine($"Physics: {counters.ColliderCount} colliders, {counters.RigidbodyCount} rigidbodies");
                sb.AppendLine($"Animation/FX: {counters.AnimatorCount} animators, {counters.ParticleSystemCount} particle systems");

                AppendComplexityWarnings(sb, counters);

                if (counters.TopEntries.Count > 0)
                {
                    sb.AppendLine("Heavy Objects:");
                    for (int i = 0; i < counters.TopEntries.Count; i++)
                    {
                        var entry = counters.TopEntries[i];
                        sb.AppendLine($"- {entry.Path}: triangles={entry.Triangles:N0}, materials={entry.MaterialSlots}, components={entry.ComponentCount}");
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        private static void AppendFrameTiming(StringBuilder sb)
        {
            sb.AppendLine("Frame Timing:");

            var fallbackFrameMs = Time.unscaledDeltaTime > 0f
                ? Time.unscaledDeltaTime * 1000.0
                : 0.0;
            var fallbackFps = fallbackFrameMs > 0.0
                ? 1000.0 / fallbackFrameMs
                : 0.0;

            try
            {
                FrameTimingManager.CaptureFrameTimings();
                var timings = new FrameTiming[1];
                uint timingCount = FrameTimingManager.GetLatestTimings(1, timings);
                if (timingCount > 0)
                {
                    var timing = timings[0];
                    if (timing.cpuFrameTime > 0.0 || timing.gpuFrameTime > 0.0)
                    {
                        var cpuMs = timing.cpuFrameTime > 0.0 ? timing.cpuFrameTime : fallbackFrameMs;
                        var gpuMs = timing.gpuFrameTime;
                        var fps = cpuMs > 0.0 ? 1000.0 / cpuMs : fallbackFps;

                        sb.AppendLine($"- CPU Frame: {cpuMs:F2} ms");
                        if (gpuMs > 0.0)
                            sb.AppendLine($"- GPU Frame: {gpuMs:F2} ms");
                        else
                            sb.AppendLine("- GPU Frame: unavailable");
                        sb.AppendLine($"- Approx FPS: {fps:F1}");
                        sb.AppendLine($"- Delta Time: {Time.deltaTime * 1000.0:F2} ms, Smooth Delta: {Time.smoothDeltaTime * 1000.0:F2} ms");
                        return;
                    }
                }
            }
            catch
            {
            }

            if (fallbackFrameMs > 0.0)
            {
                sb.AppendLine($"- CPU Frame: {fallbackFrameMs:F2} ms (from Time.unscaledDeltaTime)");
                sb.AppendLine("- GPU Frame: unavailable");
                sb.AppendLine($"- Approx FPS: {fallbackFps:F1}");
            }
            else
            {
                sb.AppendLine("- Frame timing is not available yet");
            }

            sb.AppendLine($"- Delta Time: {Time.deltaTime * 1000.0:F2} ms, Smooth Delta: {Time.smoothDeltaTime * 1000.0:F2} ms");
        }

        private static void AppendMemoryStats(StringBuilder sb)
        {
            var totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            var totalReserved = Profiler.GetTotalReservedMemoryLong();
            var totalUnusedReserved = Profiler.GetTotalUnusedReservedMemoryLong();
            var monoUsed = Profiler.GetMonoUsedSizeLong();
            var monoHeap = Profiler.GetMonoHeapSizeLong();
            var gcTotal = GC.GetTotalMemory(false);

            sb.AppendLine("Memory:");
            sb.AppendLine($"- Total Allocated: {FormatBytes(totalAllocated)}");
            sb.AppendLine($"- Total Reserved: {FormatBytes(totalReserved)}");
            sb.AppendLine($"- Unused Reserved: {FormatBytes(totalUnusedReserved)}");
            sb.AppendLine($"- Mono Used: {FormatBytes(monoUsed)} / Heap: {FormatBytes(monoHeap)}");
            sb.AppendLine($"- GC Managed Estimate: {FormatBytes(gcTotal)}");
        }

        private static SceneCounters CollectSceneCounters(bool includeInactive, int topN)
        {
            var roots = GetAllLoadedSceneRootGameObjects();
            var counters = new SceneCounters();
            var stack = new Stack<Transform>(roots.Count);

            for (int i = roots.Count - 1; i >= 0; i--)
            {
                if (roots[i] != null)
                    stack.Push(roots[i].transform);
            }

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null)
                    continue;

                var go = current.gameObject;
                bool isActive = go.activeInHierarchy;
                if (!includeInactive && !isActive)
                    continue;

                counters.GameObjectCount++;
                if (isActive)
                    counters.ActiveGameObjectCount++;

                var components = go.GetComponents<Component>();
                counters.ComponentCount += components.Length;

                int localRendererCount = 0;
                int localTriangles = 0;
                int localMaterialSlots = 0;

                for (int i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    if (component == null)
                        continue;

                    if (component is MonoBehaviour &&
                        !(component is Graphic) &&
                        !(component is Canvas))
                    {
                        counters.MonoBehaviourCount++;
                    }

                    switch (component)
                    {
                        case Renderer renderer:
                            counters.RendererCount++;
                            localRendererCount++;
                            localMaterialSlots += renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0;
                            counters.MaterialSlotCount += renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0;

                            if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
                            {
                                counters.SkinnedMeshRendererCount++;
                                localTriangles += GetTriangleCount(skinnedMeshRenderer.sharedMesh);
                            }
                            break;
                        case MeshFilter meshFilter:
                            localTriangles += GetTriangleCount(meshFilter.sharedMesh);
                            break;
                        case Collider _:
                            counters.ColliderCount++;
                            break;
                        case Rigidbody _:
                            counters.RigidbodyCount++;
                            break;
                        case Animator _:
                            counters.AnimatorCount++;
                            break;
                        case Light _:
                            counters.LightCount++;
                            break;
                        case ParticleSystem _:
                            counters.ParticleSystemCount++;
                            break;
                        case AudioSource _:
                            counters.AudioSourceCount++;
                            break;
                        case Camera _:
                            counters.CameraCount++;
                            break;
                        case Canvas _:
                            counters.CanvasCount++;
                            break;
                        case Graphic _:
                            counters.GraphicCount++;
                            break;
                    }
                }

                counters.TriangleCount += localTriangles;

                if (topN > 0 && (localTriangles > 0 || localMaterialSlots > 0 || components.Length > 8))
                {
                    counters.TopEntries.Add(new ComplexityEntry(
                        GetTransformPath(current),
                        localTriangles,
                        localMaterialSlots,
                        components.Length,
                        CalculateEntryScore(localTriangles, localMaterialSlots, components.Length, localRendererCount)));
                }

                for (int childIndex = current.childCount - 1; childIndex >= 0; childIndex--)
                {
                    stack.Push(current.GetChild(childIndex));
                }
            }

            if (topN > 0 && counters.TopEntries.Count > 1)
            {
                counters.TopEntries.Sort((left, right) => right.Score.CompareTo(left.Score));
                if (counters.TopEntries.Count > topN)
                    counters.TopEntries.RemoveRange(topN, counters.TopEntries.Count - topN);
            }

            return counters;
        }

        private static List<GameObject> GetAllLoadedSceneRootGameObjects()
        {
            // Multi-scene projects commonly load additional content additively (e.g. a decoration/UI
            // scene stacked on top of a bootstrap scene). SceneManager.GetActiveScene() only reflects
            // one of those scenes, so counting just its roots silently misses everything else that's
            // actually loaded and rendering. Walk every loaded scene instead.
            var roots = new List<GameObject>();
            var sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    roots.AddRange(scene.GetRootGameObjects());
            }
            return roots;
        }

        private static string GetLoadedScenesSummary()
        {
            var names = new List<string>();
            var sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    names.Add(scene.name);
            }
            return names.Count > 0 ? string.Join(", ", names) : "<none>";
        }

        private static void AppendComplexityWarnings(StringBuilder sb, SceneCounters counters)
        {
            var warnings = new List<string>();

            if (counters.TriangleCount > 1_000_000)
                warnings.Add($"High triangle count: {counters.TriangleCount:N0}");
            if (counters.MaterialSlotCount > 500)
                warnings.Add($"High material slot count: {counters.MaterialSlotCount}");
            if (counters.CanvasCount > 5)
                warnings.Add($"Many canvases: {counters.CanvasCount}");
            if (counters.GraphicCount > 200)
                warnings.Add($"Many UI graphics: {counters.GraphicCount}");
            if (counters.ParticleSystemCount > 30)
                warnings.Add($"Many particle systems: {counters.ParticleSystemCount}");
            if (counters.AnimatorCount > 50)
                warnings.Add($"Many animators: {counters.AnimatorCount}");
            if (counters.MonoBehaviourCount > 500)
                warnings.Add($"Many scripts: {counters.MonoBehaviourCount}");

            sb.AppendLine("Potential Hotspots:");
            if (warnings.Count == 0)
            {
                sb.AppendLine("- No obvious complexity spikes from static scene counts");
                return;
            }

            for (int i = 0; i < warnings.Count; i++)
                sb.AppendLine($"- {warnings[i]}");
        }

        private static int GetTriangleCount(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            try
            {
                return mesh.triangles != null ? mesh.triangles.Length / 3 : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int CalculateEntryScore(int triangles, int materialSlots, int componentCount, int rendererCount)
        {
            return triangles + (materialSlots * 500) + (componentCount * 50) + (rendererCount * 250);
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            var names = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names.ToArray());
        }

        private static string FormatBytes(long bytes)
        {
            const double kilo = 1024.0;
            const double mega = kilo * 1024.0;
            const double giga = mega * 1024.0;

            if (bytes >= giga)
                return $"{bytes / giga:F2} GB";
            if (bytes >= mega)
                return $"{bytes / mega:F2} MB";
            if (bytes >= kilo)
                return $"{bytes / kilo:F2} KB";
            return $"{bytes} B";
        }

        private sealed class SceneCounters
        {
            public int GameObjectCount;
            public int ActiveGameObjectCount;
            public int ComponentCount;
            public int MonoBehaviourCount;
            public int RendererCount;
            public int SkinnedMeshRendererCount;
            public int MaterialSlotCount;
            public int TriangleCount;
            public int ColliderCount;
            public int RigidbodyCount;
            public int AnimatorCount;
            public int LightCount;
            public int ParticleSystemCount;
            public int AudioSourceCount;
            public int CameraCount;
            public int CanvasCount;
            public int GraphicCount;
            public readonly List<ComplexityEntry> TopEntries = new List<ComplexityEntry>();
        }

        private readonly struct ComplexityEntry
        {
            public readonly string Path;
            public readonly int Triangles;
            public readonly int MaterialSlots;
            public readonly int ComponentCount;
            public readonly int Score;

            public ComplexityEntry(string path, int triangles, int materialSlots, int componentCount, int score)
            {
                Path = path;
                Triangles = triangles;
                MaterialSlots = materialSlots;
                ComponentCount = componentCount;
                Score = score;
            }
        }
    }
}
