// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.Tools.Helpers
{
    internal sealed class ObjectQueryFilter
    {
        public string component, assembly;
        public List<ObjectQueryPredicate> where = new List<ObjectQueryPredicate>();
        public string[] select = Array.Empty<string>();
    }
    internal sealed class ObjectQueryPredicate { public string property, op = "eq"; public JToken value; }
    internal static class StructuredObjectQuery
    {
        private sealed class Snapshot
        {
            internal string id = Guid.NewGuid().ToString("N"), stop;
            internal DateTime created = DateTime.UtcNow;
            internal bool complete = true;
            internal int scanned;
            internal readonly List<object> items = new List<object>();
            internal readonly List<object> errors = new List<object>();
        }
        private static readonly List<Snapshot> Snapshots = new List<Snapshot>();
        private static readonly string[] Operators = { "eq", "ne", "gt", "gte", "lt", "lte", "contains", "starts_with", "is_null", "not_null", "missing" };

        internal static object Execute(string query, string method, bool inactive, string parent, int limit,
            string filterJson, string scope, string pathsJson, int offset, int scanLimit, string snapshotId)
        {
            if (limit < 1 || limit > 500 || offset < 0 || scanLimit < 1 || scanLimit > 50000) return Response.Error("INVALID_QUERY_LIMIT");
            Snapshots.RemoveAll(x => (DateTime.UtcNow - x.created).TotalSeconds > 120);
            if (!string.IsNullOrEmpty(snapshotId))
            {
                var saved = Snapshots.FirstOrDefault(x => x.id == snapshotId);
                return saved == null ? Response.Error("QUERY_SNAPSHOT_EXPIRED") : Page(saved, offset, limit);
            }
            try
            {
                var filter = Parse(filterJson);
                Type componentType = null;
                if (!string.IsNullOrEmpty(filter.component))
                {
                    var candidates = ProjectTypeCatalog.Candidates(filter.component, filter.assembly, true);
                    if (candidates.Length != 1) return Response.Error(candidates.Length == 0 ? "COMPONENT_TYPE_NOT_FOUND" : "AMBIGUOUS_COMPONENT_TYPE",
                        new { component = filter.component, candidates = candidates.Select(ProjectTypeCatalog.Describe).ToArray() });
                    componentType = candidates[0];
                }
                if ((filter.where.Count > 0 || filter.select.Length > 0) && componentType == null) return Response.Error("QUERY_COMPONENT_REQUIRED");
                var pending = ResolveRoots(scope ?? "scene", pathsJson, parent, inactive);
                var result = new Snapshot();
                var seen = new HashSet<string>(); var timer = Stopwatch.StartNew();
                while (pending.Count > 0)
                {
                    if (result.scanned >= scanLimit || result.items.Count >= 2000 || timer.ElapsedMilliseconds > 2000)
                    {
                        result.complete = false; result.stop = result.scanned >= scanLimit ? "scan_limit" : result.items.Count >= 2000 ? "match_limit" : "time_limit"; break;
                    }
                    var go = pending.Dequeue();
                    if (go == null || !seen.Add(ObjectIdHelper.GetSerializableId(go))) continue;
                    foreach (Transform child in go.transform) pending.Enqueue(child.gameObject);
                    if (!inactive && !go.activeInHierarchy) continue;
                    result.scanned++;
                    if (!MatchesObject(go, query, method)) continue;
                    var matching = new List<object>();
                    if (componentType != null)
                    {
                        foreach (var component in go.GetComponents(componentType).Cast<Component>())
                        {
                            try
                            {
                                using (var serialized = new SerializedObject(component))
                                {
                                    bool match = true;
                                    foreach (var predicate in filter.where)
                                    {
                                        var property = serialized.FindProperty(predicate.property);
                                        if (property == null && predicate.op != "missing")
                                        {
                                            result.errors.Add(new { instance_id = ObjectIdHelper.GetSerializableId(component), property = predicate.property, code = "PROPERTY_NOT_FOUND" });
                                            match = false; break;
                                        }
                                        if (!MatchesProperty(property, predicate)) { match = false; break; }
                                    }
                                    if (!match) continue;
                                    var projection = new Dictionary<string, object>();
                                    foreach (var field in filter.select)
                                    {
                                        var property = serialized.FindProperty(field);
                                        projection[field] = property == null ? (object)new { exists = false } : new { exists = true, type = property.propertyType.ToString(), value = ComponentSerializer.ReadPropertyValue(property) };
                                    }
                                    matching.Add(new { instance_id = ObjectIdHelper.GetSerializableId(component), type = component.GetType().FullName, properties = projection });
                                }
                            }
                            catch (Exception ex) { result.errors.Add(new { instance_id = ObjectIdHelper.GetSerializableId(component), code = "PROPERTY_QUERY_FAILED", detail = ex.Message }); }
                            if (result.errors.Count >= 100) break;
                        }
                        if (matching.Count == 0)
                        {
                            if (result.errors.Count >= 100) { result.complete = false; result.stop = "error_limit"; break; }
                            continue;
                        }
                    }
                    result.items.Add(new
                    {
                        instanceId = ObjectIdHelper.GetSerializableId(go), name = go.name, path = ObjectsHelper.GetGameObjectPath(go),
                        global_object_id = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                        asset_path = EditorUtility.IsPersistent(go) ? AssetDatabase.GetAssetPath(go) : go.scene.path,
                        value_source = EditorUtility.IsPersistent(go) ? "loaded_asset" : "live_scene", persisted = false,
                        active = go.activeInHierarchy, components = matching.ToArray()
                    });
                    if (result.errors.Count >= 100) { result.complete = false; result.stop = "error_limit"; break; }
                }
                if (result.errors.Count > 0) result.complete = false;
                Snapshots.Add(result); while (Snapshots.Count > 8) Snapshots.RemoveAt(0);
                return Page(result, offset, limit);
            }
            catch (Exception ex) { return Response.Error("INVALID_OBJECT_QUERY", new { detail = ex.Message }); }
        }
        private static object Page(Snapshot s, int offset, int limit) => Response.Success("Bounded structured object query snapshot.", new
        {
            snapshot_id = s.id, captured_at = s.created.ToString("O"), expires_at = s.created.AddSeconds(120).ToString("O"),
            complete = s.complete, scanned_objects = s.scanned, stop_reason = s.stop, total_matches = s.items.Count,
            errors = s.errors, offset, items = s.items.Skip(offset).Take(limit).ToArray(),
            next_offset = offset + limit < s.items.Count ? (int?)(offset + limit) : null,
            note = "Snapshot values are read-only observations, not fresh mutation targets. Re-resolve/re-read before writing, especially after reload. Limits apply to the scan, not only this page."
        });
        internal static ObjectQueryFilter Parse(string json)
        {
            var filter = string.IsNullOrWhiteSpace(json) ? new ObjectQueryFilter() : JsonConvert.DeserializeObject<ObjectQueryFilter>(json, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
            if (filter == null || filter.where == null || filter.select == null || filter.where.Count > 20 || filter.select.Length > 30)
                throw new ArgumentException("A filter supports up to 20 predicates and 30 projected serialized fields.");
            foreach (var p in filter.where)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.property) || !Operators.Contains(p.op)) throw new ArgumentException("Invalid predicate property/op.");
                if (p.value == null && p.op != "is_null" && p.op != "not_null" && p.op != "missing") throw new ArgumentException("Predicate value is required (use JSON null explicitly).");
            }
            if (filter.select.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Projection paths cannot be empty.");
            return filter;
        }
        internal static bool MatchesProperty(SerializedProperty property, ObjectQueryPredicate predicate)
        {
            if (predicate.op == "missing") return property == null;
            if (property == null) return false;
            object raw = ComponentSerializer.ReadPropertyValue(property);
            var value = raw == null ? JValue.CreateNull() : JToken.FromObject(raw);
            if (predicate.op == "is_null") return value.Type == JTokenType.Null;
            if (predicate.op == "not_null") return value.Type != JTokenType.Null;
            // Reference selectors may use asset identity instead of an editor-session instance id.
            if (property.propertyType == SerializedPropertyType.ObjectReference && predicate.value is JObject reference)
            {
                var obj = property.objectReferenceValue;
                bool equal = obj != null;
                if (reference["asset_path"] != null) equal &= obj != null && AssetDatabase.GetAssetPath(obj) == (string)reference["asset_path"];
                if (reference["fileID"] != null) equal &= obj != null && ObjectIdHelper.GetSerializableId(obj) == reference["fileID"].ToString();
                if (reference["asset_path"] == null && reference["fileID"] == null) throw new ArgumentException("Reference predicates require asset_path or fileID.");
                if (predicate.op != "eq" && predicate.op != "ne") throw new ArgumentException("Reference comparison supports eq/ne/null predicates.");
                return predicate.op == "eq" ? equal : !equal;
            }
            if (predicate.op == "eq" || predicate.op == "ne")
            {
                bool equal = IsNumber(value) && IsNumber(predicate.value) ? (double)value == (double)predicate.value : JToken.DeepEquals(value, predicate.value);
                return predicate.op == "eq" ? equal : !equal;
            }
            if (predicate.op == "contains" || predicate.op == "starts_with")
            {
                if (value.Type != JTokenType.String || predicate.value.Type != JTokenType.String) throw new ArgumentException("String operators require string values.");
                return predicate.op == "contains" ? ((string)value).Contains((string)predicate.value) : ((string)value).StartsWith((string)predicate.value, StringComparison.Ordinal);
            }
            if (!IsNumber(value) || !IsNumber(predicate.value)) throw new ArgumentException("Ordered comparisons require numbers.");
            double left = (double)value, right = (double)predicate.value;
            switch (predicate.op) { case "gt": return left > right; case "gte": return left >= right; case "lt": return left < right; case "lte": return left <= right; default: return false; }
        }
        private static bool IsNumber(JToken value) => value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float);
        private static bool MatchesObject(GameObject go, string query, string method)
        {
            if (string.IsNullOrEmpty(query)) return true;
            switch (method ?? "by_name")
            {
                case "by_name": return go.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                case "by_path": return ObjectsHelper.GetGameObjectPath(go) == query;
                case "by_id": return ObjectIdHelper.GetSerializableId(go).ToString() == query;
                case "by_tag": return go.tag == query;
                case "by_layer": return go.layer.ToString() == query || LayerMask.LayerToName(go.layer) == query;
                case "by_component":
                    var candidates = ProjectTypeCatalog.Candidates(query, null, true);
                    if (candidates.Length != 1) throw new ArgumentException("Component query is missing or ambiguous; use a full/assembly-qualified name.");
                    return go.GetComponent(candidates[0]) != null;
                default: throw new ArgumentException("Unsupported find_method.");
            }
        }
        private static Queue<GameObject> ResolveRoots(string scope, string paths, string parent, bool inactive)
        {
            var result = new Queue<GameObject>();
            if (!string.IsNullOrEmpty(parent))
            {
                if (scope != "scene") throw new ArgumentException("in_parent requires scope=scene.");
                var matches = ObjectsHelper.FindObjects(parent, null, true, searchInactive: inactive);
                if (matches.Count != 1) throw new ArgumentException("Parent must resolve to exactly one live object; prefer its ID.");
                result.Enqueue(matches[0]); return result;
            }
            switch (scope)
            {
                case "scene":
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var scene = SceneManager.GetSceneAt(i);
                        if (scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene)) foreach (var go in scene.GetRootGameObjects()) result.Enqueue(go);
                    }
                    break;
                case "selection": foreach (var go in Selection.gameObjects) result.Enqueue(go); break;
                case "prefabs":
                    var prefabPaths = new HashSet<string>();
                    foreach (var path in UIAuditService.ParsePaths(paths))
                    {
                        UIAuditService.ValidateAssetPath(path);
                        if (AssetDatabase.IsValidFolder(path)) foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { path })) prefabPaths.Add(AssetDatabase.GUIDToAssetPath(guid));
                        else if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) prefabPaths.Add(path);
                        else throw new ArgumentException("Expected prefab asset/folder: " + path);
                    }
                    if (prefabPaths.Count > 200) throw new ArgumentException("More than 200 prefabs; narrow asset_paths.");
                    foreach (var path in prefabPaths.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab == null) throw new ArgumentException("Cannot load prefab: " + path);
                        result.Enqueue(prefab);
                    }
                    break;
                default: throw new ArgumentException("scope must be scene, selection or prefabs.");
            }
            return result;
        }
    }
}
