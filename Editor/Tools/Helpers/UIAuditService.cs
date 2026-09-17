// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.Tools.Helpers
{
    internal sealed class UIAuditJob
    {
        public string job_id, scope, status = "running", started_at, finished_at, stop_reason;
        public int scanned_objects, skipped_objects, scanned_assets, candidate_assets, suppressed_count;
        public bool complete;
        public List<UIAuditFinding> findings = new List<UIAuditFinding>();
        public List<string> skipped = new List<string>();
    }

    [InitializeOnLoad]
    internal static class UIAuditService
    {
        private const string Key = "Funplay.UIAudit.Latest";
        private static UIAuditJob job;
        private static UIAuditConfiguration config;
        private static Queue<GameObject> objects;
        private static Queue<string> assets;
        private static HashSet<string> visited;
        private static Scene preview;
        private static string currentAsset;
        private static bool live, includeInactive;
        private static int objectLimit, findingLimit;
        private static DateTime deadline;

        static UIAuditService()
        {
            try { job = JsonConvert.DeserializeObject<UIAuditJob>(SessionState.GetString(Key, "null")); }
            catch { job = null; }
            if (job?.status == "running") Finish("interrupted", "domain_reload");
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += () => { if (job?.status == "running") Finish("interrupted", "domain_reload"); };
            EditorApplication.quitting += ClosePreview;
        }

        internal static object Start(string scope, string paths, string roots, string configuration, bool inactive, int maxObjects, int maxFindings, int seconds)
        {
            if (job?.status == "running") return Response.Error("UI_AUDIT_IN_PROGRESS", new { job_id = job.job_id });
            if (maxObjects < 1 || maxObjects > 100000 || maxFindings < 1 || maxFindings > 5000 || seconds < 1 || seconds > 300)
                return Response.Error("INVALID_LIMIT", new { max_objects = "1..100000", max_findings = "1..5000", timeout_seconds = "1..300" });
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return Response.Error("EDITOR_BUSY");
            try
            {
                var nextConfig = LoadConfiguration(configuration);
                var nextObjects = new Queue<GameObject>();
                var nextAssets = new List<string>();
                switch (scope)
                {
                    case "selection": foreach (var go in Selection.gameObjects) nextObjects.Enqueue(go); break;
                    case "scene":
                        if (!string.IsNullOrWhiteSpace(roots))
                        {
                            foreach (var token in ParsePaths(roots))
                            {
                                var go = ObjectsHelper.FindObject(token, "by_id_or_name_or_path");
                                if (go == null || EditorUtility.IsPersistent(go)) throw new ArgumentException("Live scene root not found: " + token);
                                nextObjects.Enqueue(go);
                            }
                        }
                        else for (int i = 0; i < SceneManager.sceneCount; i++)
                        {
                            var scene = SceneManager.GetSceneAt(i);
                            if (scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene))
                                foreach (var root in scene.GetRootGameObjects()) nextObjects.Enqueue(root);
                        }
                        break;
                    case "prefabs": case "scenes": case "assets":
                        foreach (var path in ParsePaths(paths))
                        {
                            ValidateAssetPath(path);
                            if (AssetDatabase.IsValidFolder(path))
                            {
                                if (scope != "scenes") nextAssets.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { path }).Select(AssetDatabase.GUIDToAssetPath));
                                if (scope != "prefabs") nextAssets.AddRange(AssetDatabase.FindAssets("t:Scene", new[] { path }).Select(AssetDatabase.GUIDToAssetPath));
                            }
                            else
                            {
                                if ((scope == "prefabs" && !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) ||
                                    (scope == "scenes" && !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) ||
                                    (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
                                    throw new ArgumentException("Expected a prefab, saved scene or Assets folder: " + path);
                                nextAssets.Add(path);
                            }
                        }
                        break;
                    default: throw new ArgumentException("scope must be selection, scene, prefabs, scenes or assets.");
                }
                nextAssets = nextAssets.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (nextObjects.Count == 0 && nextAssets.Count == 0) return Response.Error("EMPTY_AUDIT_SCOPE");
                if (EditorApplication.isPlaying && nextAssets.Any(x => x.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
                    return Response.Error("SAVED_SCENE_AUDIT_REQUIRES_EDIT_MODE");
                config = nextConfig; objects = nextObjects; assets = new Queue<string>(nextAssets.Take(500));
                visited = new HashSet<string>(); includeInactive = inactive; objectLimit = maxObjects; findingLimit = maxFindings;
                deadline = DateTime.UtcNow.AddSeconds(seconds); currentAsset = null; live = true;
                job = new UIAuditJob { job_id = Guid.NewGuid().ToString("N"), scope = scope, started_at = DateTime.UtcNow.ToString("O"), candidate_assets = nextAssets.Count };
                if (nextAssets.Count > 500) job.skipped.Add("Asset limit: only the first 500 of " + nextAssets.Count + " assets will be inspected. Narrow paths.");
                Save();
                return Read(job.job_id, 0, 100);
            }
            catch (Exception ex) { return Response.Error("INVALID_AUDIT_REQUEST", new { detail = ex.Message }); }
        }

        internal static object Read(string id, int offset, int limit)
        {
            if (job == null || job.job_id != id) return Response.Error("AUDIT_JOB_NOT_FOUND", new { retained = "latest job only" });
            if (offset < 0 || limit < 1 || limit > 500) return Response.Error("INVALID_PAGE");
            return Response.Success("Read-only UI audit; contextual findings require visual/runtime judgement.", new
            {
                job.job_id, job.scope, job.status, job.started_at, job.finished_at, job.complete, job.stop_reason,
                job.scanned_objects, job.skipped_objects, job.scanned_assets, job.candidate_assets, job.suppressed_count,
                job.skipped, total_findings = job.findings.Count, offset, findings = job.findings.Skip(offset).Take(limit).ToArray(),
                next_offset = offset + limit < job.findings.Count ? (int?)(offset + limit) : null,
                runtime_checks = "Text metrics and rectangular clipping use current live layout only. Sprite/stencil-mask shapes, localization, animation, arbitrary business-required references and input behavior need representative Play Mode verification. No Canvas rebuild or assets are saved."
            });
        }
        internal static object Cancel(string id)
        {
            if (job == null || job.job_id != id) return Response.Error("AUDIT_JOB_NOT_FOUND");
            if (job.status == "running") Finish("cancelled", "requested");
            return Read(id, 0, 100);
        }
        internal static UIAuditConfiguration LoadConfiguration(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                var path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "ProjectSettings/FunplayMcp.UIAudit.json");
                if (File.Exists(path)) json = File.ReadAllText(path);
            }
            var result = string.IsNullOrWhiteSpace(json) ? new UIAuditConfiguration() :
                JsonConvert.DeserializeObject<UIAuditConfiguration>(json, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
            if (result == null || result.required_references == null || result.suppressions == null) throw new ArgumentException("Invalid audit configuration.");
            if (result.required_references.Count > 100 || result.suppressions.Count > 200) throw new ArgumentException("At most 100 required-reference rules and 200 suppressions.");
            foreach (var required in result.required_references)
                if (required == null || string.IsNullOrWhiteSpace(required.component_type) || string.IsNullOrWhiteSpace(required.property))
                    throw new ArgumentException("Each required reference needs component_type (full type name) and serialized property path.");
            foreach (var suppression in result.suppressions)
                if (suppression == null || !UIAuditRules.RuleNames.Contains(suppression.rule) || string.IsNullOrWhiteSpace(suppression.reason))
                    throw new ArgumentException("Each suppression needs a known rule and a reason.");
            return result;
        }
        internal static string[] ParsePaths(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("A JSON array of paths/identifiers is required.");
            var paths = JsonConvert.DeserializeObject<string[]>(json);
            if (paths == null || paths.Length == 0 || paths.Length > 500 || paths.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Provide 1..500 non-empty paths/identifiers.");
            return paths;
        }
        internal static void ValidateAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !(path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal)) || path.Contains("..") || path.Contains('\\'))
                throw new ArgumentException("Use a project-relative Assets path without traversal: " + path);
        }
        private static void Tick()
        {
            if (job?.status != "running") return;
            if (DateTime.UtcNow >= deadline) { Finish("incomplete", "timeout"); return; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 8)
                {
                    if (DateTime.UtcNow >= deadline) { Finish("incomplete", "timeout"); return; }
                    if (job.scanned_objects >= objectLimit) { Finish("incomplete", "object_limit"); return; }
                    if (job.findings.Count >= findingLimit) { Finish("incomplete", "finding_limit"); return; }
                    if (objects.Count == 0)
                    {
                        ClosePreview();
                        if (assets.Count == 0) { Finish(job.skipped.Count == 0 ? "completed" : "incomplete", job.skipped.Count == 0 ? null : "skipped_assets"); return; }
                        LoadNextAsset();
                        continue;
                    }
                    var go = objects.Dequeue();
                    if (go == null) { job.skipped_objects++; job.skipped.Add("A live object was destroyed during the audit."); continue; }
                    if (!visited.Add(ObjectIdHelper.GetSerializableId(go))) continue;
                    foreach (Transform child in go.transform) objects.Enqueue(child.gameObject);
                    if (!includeInactive && !go.activeInHierarchy) { job.skipped_objects++; continue; }
                    bool persistent = EditorUtility.IsPersistent(go);
                    string path = currentAsset ?? (persistent ? AssetDatabase.GetAssetPath(go) : go.scene.path ?? "");
                    var findings = UIAuditRules.Inspect(go, path, live && !persistent, config);
                    job.scanned_objects++;
                    var remaining = findingLimit - job.findings.Count;
                    job.findings.AddRange(findings.Take(remaining));
                    job.suppressed_count += findings.Take(remaining).Count(x => x.suppressed);
                    if (findings.Count > remaining) { Finish("incomplete", "finding_limit"); return; }
                }
                Save();
            }
            catch (Exception ex) { job.skipped.Add(ex.GetType().Name + ": " + ex.Message); Finish("failed", "inspection_error"); }
        }
        private static void LoadNextAsset()
        {
            currentAsset = assets.Dequeue(); live = false;
            try
            {
                if (currentAsset.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    var root = AssetDatabase.LoadAssetAtPath<GameObject>(currentAsset);
                    if (root == null) throw new InvalidOperationException("Prefab could not be loaded.");
                    objects.Enqueue(root);
                }
                else
                {
                    // Unity exposes New/ClosePreviewScene publicly but opening a saved scene is
                    // internal on 2022 LTS. Do not fall back to replacing a user's loaded scene.
                    var open = typeof(EditorSceneManager).GetMethod("OpenPreviewScene", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                    if (open == null) throw new NotSupportedException("This Unity version does not expose saved preview-scene loading. Inspect a live scene instead.");
                    preview = (Scene)open.Invoke(null, new object[] { currentAsset });
                    foreach (var root in preview.GetRootGameObjects()) objects.Enqueue(root);
                }
                job.scanned_assets++;
            }
            catch (Exception ex) { job.skipped.Add(currentAsset + ": " + ex.Message); ClosePreview(); }
        }
        private static void Finish(string status, string reason)
        {
            ClosePreview(); job.status = status; job.stop_reason = reason;
            job.complete = status == "completed"; job.finished_at = DateTime.UtcNow.ToString("O"); Save();
        }
        private static void ClosePreview()
        {
            if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
            preview = default;
        }
        private static void Save() => SessionState.SetString(Key, JsonConvert.SerializeObject(job));
    }
}
