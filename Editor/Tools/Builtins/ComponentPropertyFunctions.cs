// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    /// <summary>
    /// Read and write component fields through SerializedObject — picks up
    /// <c>[SerializeField] private</c>, supports Object references via
    /// <c>{"fileID": instanceId}</c>, returns per-field success so agents can
    /// recover from partial writes.
    /// </summary>
    [ToolProvider("ComponentProperty")]
    internal static class ComponentPropertyFunctions
    {
        [Description("List components on a GameObject. Each entry includes its instanceId so subsequent " +
                     "set_component_property calls can disambiguate when a GameObject has multiple components of the same type.")]
        [ReadOnlyTool]
        public static object ListComponents(
            [ToolParam("GameObject identifier (instance id, name, path, tag…)")] string target,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method, searchInactive: true);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            var items = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => new { instanceId = ObjectIdHelper.GetSerializableId(c), type = c.GetType().Name, fullType = c.GetType().FullName })
                .ToList();

            return Response.Success($"{items.Count} component(s) on '{go.name}'.", new
            {
                gameObject = new { instanceId = ObjectIdHelper.GetSerializableId(go), name = go.name },
                components = items
            });
        }

        [Description("Get all serialized properties on a component, including [SerializeField] private fields. " +
                     "Component can be addressed by type name on a GameObject, or directly by component instanceId.")]
        [ReadOnlyTool]
        public static object GetComponentProperties(
            [ToolParam("GameObject identifier (omit if using component_instance_id)", Required = false)] string target = null,
            [ToolParam("Component type name (e.g. 'Rigidbody'). Omit if using component_instance_id.", Required = false)] string component = null,
            [ToolParam("Component instanceId (alternative to target+component)", Required = false)] string component_instance_id = null,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var resolved = ResolveComponent(target, component, component_instance_id, find_method);
            if (resolved.Error != null) return resolved.Error;

            var props = ComponentSerializer.ReadProperties(resolved.Component);
            return Response.Success(
                $"{props.Count} properties on {resolved.Component.GetType().Name}.",
                new
                {
                    componentInstanceId = ObjectIdHelper.GetSerializableId(resolved.Component),
                    type = resolved.Component.GetType().Name,
                    gameObject = new { instanceId = ObjectIdHelper.GetSerializableId(resolved.Component.gameObject), name = resolved.Component.gameObject.name },
                    properties = props
                });
        }

        [Description("Set a single property or field on a component. " +
                     "Use simple JSON for value (e.g. '5', 'true', '\"text\"', '[1,2,3]'). " +
                     "For Object references pass {\"fileID\": <instanceId>} or {\"assetPath\": \"Assets/...\"}. " +
                     "The success response echoes the post-write serialized value ('newValue'). " +
                     "Pass `targets` (comma-separated identifiers, or a find spec resolving to at most 100 objects) with a " +
                     "`component` type name to apply the SAME field to many objects (returns per-target results).")]
        [SceneEditingTool]
        public static object SetComponentProperty(
            [ToolParam("GameObject identifier (omit if using component_instance_id or targets)", Required = false)] string target = null,
            [ToolParam("Component type name (omit if using component_instance_id)", Required = false)] string component = null,
            [ToolParam("Property/field name to set")] string property = null,
            [ToolParam("New value as JSON literal")] string value = null,
            [ToolParam("Component instanceId (alternative to target+component)", Required = false)] string component_instance_id = null,
            [ToolParam("How to resolve target(s)", Required = false)] string find_method = null,
            [ToolParam("Comma-separated identifiers, or a find spec resolving to at most 100 objects (batch; requires `component`)", Required = false)] string targets = null)
        {
            if (string.IsNullOrWhiteSpace(property))
                return Response.Error("PROPERTY_REQUIRED");
            if (value == null)
                return Response.Error("VALUE_REQUIRED");

            var selectorError = ValidateComponentTargetSelectors(target, component_instance_id, targets);
            if (selectorError != null)
                return selectorError;

            JToken token;
            try { token = ParseJsonValue(value); }
            catch (Exception ex) { return Response.Error("INVALID_VALUE_JSON", new { message = ex.Message }); }

            var props = new JObject { [property] = token };

            // Multi-target batch path
            if (!string.IsNullOrWhiteSpace(targets))
                return ApplyPropertiesToMany(targets, component, props, find_method, property);

            // Single-target path
            var resolved = ResolveComponent(target, component, component_instance_id, find_method);
            if (resolved.Error != null) return resolved.Error;

            var results = ComponentSerializer.WriteProperties(resolved.Component, props,
                $"Set {property} on {resolved.Component.GetType().Name}");

            var first = results.Count > 0 ? results[0] : null;
            if (first == null || !first.Success)
            {
                return Response.Error("PROPERTY_SET_FAILED",
                    new { property, error = first?.Error ?? "unknown" });
            }

            var readBack = ReadBackFields(resolved.Component, new[] { property });
            return Response.Success($"Set {resolved.Component.GetType().Name}.{property}.",
                new
                {
                    componentInstanceId = ObjectIdHelper.GetSerializableId(resolved.Component),
                    property,
                    newValue = readBack.TryGetValue(property, out var nv) ? nv : null
                });
        }

        [Description("Set multiple properties on a component in one call. " +
                     "Pass `properties` as a JSON object: {\"mass\": 5, \"isKinematic\": true, \"material\": {\"fileID\": 12345}}. " +
                     "Returns per-field success so partial failures are diagnosable, plus the post-write serialized " +
                     "values of the fields that applied ('applied'). " +
                     "Pass `targets` (comma-separated identifiers, or a find spec resolving to at most 100 objects) with a " +
                     "`component` type name to apply the SAME properties to many objects (returns per-target results).")]
        [SceneEditingTool]
        public static object SetComponentProperties(
            [ToolParam("GameObject identifier (omit if using component_instance_id or targets)", Required = false)] string target = null,
            [ToolParam("Component type name (omit if using component_instance_id)", Required = false)] string component = null,
            [ToolParam("JSON object of property→value pairs")] string properties = null,
            [ToolParam("Component instanceId (alternative to target+component)", Required = false)] string component_instance_id = null,
            [ToolParam("How to resolve target(s)", Required = false)] string find_method = null,
            [ToolParam("Comma-separated identifiers, or a find spec resolving to at most 100 objects (batch; requires `component`)", Required = false)] string targets = null)
        {
            if (string.IsNullOrWhiteSpace(properties))
                return Response.Error("PROPERTIES_REQUIRED");

            var selectorError = ValidateComponentTargetSelectors(target, component_instance_id, targets);
            if (selectorError != null)
                return selectorError;

            JObject jobj;
            try { jobj = JObject.Parse(properties); }
            catch (Exception ex) { return Response.Error("INVALID_PROPERTIES_JSON", new { message = ex.Message }); }
            if (!jobj.HasValues)
                return Response.Error("PROPERTIES_REQUIRED",
                    new { hint = "The properties JSON object has no fields to set." });

            // Multi-target batch path
            if (!string.IsNullOrWhiteSpace(targets))
                return ApplyPropertiesToMany(targets, component, jobj, find_method);

            // Single-target path
            var resolved = ResolveComponent(target, component, component_instance_id, find_method);
            if (resolved.Error != null) return resolved.Error;

            var results = ComponentSerializer.WriteProperties(resolved.Component, jobj,
                $"Set properties on {resolved.Component.GetType().Name}");

            int success = results.Count(r => r.Success);
            int fail = results.Count - success;
            var applied = ReadBackFields(resolved.Component, results.Where(r => r.Success).Select(r => r.Field));
            return Response.Success(
                $"Applied {success} of {results.Count} field(s) on {resolved.Component.GetType().Name}.",
                new
                {
                    componentInstanceId = ObjectIdHelper.GetSerializableId(resolved.Component),
                    successCount = success,
                    failCount = fail,
                    fields = results,
                    applied
                });
        }

        [Description("Set ONE serialized field on a component inside a .prefab ASSET directly, WITHOUT opening a prefab stage. " +
                     "SAFE alternative to the open_prefab_stage -> set_component_property -> save_prefab_stage three-piece for " +
                     "single-field edits: no live preview scene/Canvas is created, so layout groups / ContentSizeFitter / TMP " +
                     "auto-size / Spine SkeletonGraphic never recompute and mutate the graph. The three-piece's wholesale " +
                     "SaveAsPrefabAsset re-serializes those transient recomputes -- freezing RectTransform/font values and even " +
                     "zeroing Spine skeletonDataAsset references; this path changes ONLY the field you set. Value format matches " +
                     "set_component_property (JSON literal; Object references via {\"fileID\":<instanceId>} or " +
                     "{\"assetPath\":\"Assets/...\"}). Echoes the post-write serialized value ('newValue'). For structural edits " +
                     "(add/reparent components) use the prefab stage. Note: any save round-trips Unity serialization, so a prefab " +
                     "authored by an older Unity/TMP version may pick up harmless serialization-format upgrades.")]
        public static object SetPrefabProperty(
            [ToolParam("Path to the .prefab asset (e.g. 'Assets/Prefabs/UIFoo.prefab')")] string prefab_path = null,
            [ToolParam("Component type name (e.g. 'TextMeshProUGUI', 'UnityEngine.UI.Image')")] string component = null,
            [ToolParam("Property/field name to set")] string property = null,
            [ToolParam("New value as JSON literal")] string value = null,
            [ToolParam("Hierarchy path of the target GameObject inside the prefab, '/'-separated, relative to and EXCLUDING the root (empty = the prefab root)", Required = false)] string game_object_path = null)
        {
            if (string.IsNullOrWhiteSpace(property))
                return Response.Error("PROPERTY_REQUIRED");
            if (value == null)
                return Response.Error("VALUE_REQUIRED");

            JToken token;
            try { token = ParseJsonValue(value); }
            catch (Exception ex) { return Response.Error("INVALID_VALUE_JSON", new { message = ex.Message }); }

            var resolved = ResolvePrefabComponent(prefab_path, game_object_path, component);
            if (resolved.Error != null) return resolved.Error;

            var props = new JObject { [property] = token };
            var results = ComponentSerializer.WriteProperties(resolved.Component, props,
                $"Set {property} on {resolved.Component.GetType().Name} (prefab asset)");

            var first = results.Count > 0 ? results[0] : null;
            if (first == null || !first.Success)
                return Response.Error("PROPERTY_SET_FAILED", new { property, error = first?.Error ?? "unknown" });

            // Persist ONLY this prefab asset (not the whole project) — the edit was made on the
            // loaded asset sub-object in memory; nothing recomputed it, so only the intended field changed.
            AssetDatabase.SaveAssetIfDirty(resolved.Root);

            var readBack = ReadBackFields(resolved.Component, new[] { property });
            return Response.Success($"Set {resolved.Component.GetType().Name}.{property} in {prefab_path}.",
                new
                {
                    prefab_path,
                    gameObject = resolved.Component.gameObject.name,
                    componentType = resolved.Component.GetType().Name,
                    property,
                    newValue = readBack.TryGetValue(property, out var nv) ? nv : null
                });
        }

        [Description("Set MULTIPLE serialized fields on one component inside a .prefab ASSET directly, WITHOUT opening a prefab " +
                     "stage. Same safety as set_prefab_property (no live scene => no layout/TMP/Spine recompute corruption). Pass " +
                     "`properties` as a JSON object, e.g. {\"m_LocalizationID\": 42, \"m_Color\": {\"a\": 0.5}}. Returns per-field " +
                     "success plus the post-write serialized values ('applied'). For structural edits use the prefab stage.")]
        public static object SetPrefabProperties(
            [ToolParam("Path to the .prefab asset")] string prefab_path = null,
            [ToolParam("Component type name")] string component = null,
            [ToolParam("JSON object of property→value pairs")] string properties = null,
            [ToolParam("Hierarchy path of the target GameObject inside the prefab ('/'-separated, excluding root; empty = root)", Required = false)] string game_object_path = null)
        {
            if (string.IsNullOrWhiteSpace(properties))
                return Response.Error("PROPERTIES_REQUIRED");

            JObject jobj;
            try { jobj = JObject.Parse(properties); }
            catch (Exception ex) { return Response.Error("INVALID_PROPERTIES_JSON", new { message = ex.Message }); }
            if (!jobj.HasValues)
                return Response.Error("PROPERTIES_REQUIRED",
                    new { hint = "The properties JSON object has no fields to set." });

            var resolved = ResolvePrefabComponent(prefab_path, game_object_path, component);
            if (resolved.Error != null) return resolved.Error;

            var results = ComponentSerializer.WriteProperties(resolved.Component, jobj,
                $"Set properties on {resolved.Component.GetType().Name} (prefab asset)");

            int success = results.Count(r => r.Success);
            int fail = results.Count - success;
            if (success > 0)
                AssetDatabase.SaveAssetIfDirty(resolved.Root);

            var applied = ReadBackFields(resolved.Component, results.Where(r => r.Success).Select(r => r.Field));
            return Response.Success(
                $"Applied {success} of {results.Count} field(s) on {resolved.Component.GetType().Name} in {prefab_path}.",
                new
                {
                    prefab_path,
                    gameObject = resolved.Component.gameObject.name,
                    componentType = resolved.Component.GetType().Name,
                    successCount = success,
                    failCount = fail,
                    fields = results,
                    applied
                });
        }

        // -------- Helpers --------

        /// <summary>
        /// Apply a property set to the same-named component on MANY GameObjects. Addressing by a
        /// The batch form requires a `component` type name so it can be located on each resolved
        /// target. When singlePropertyName is set each per-target result includes newValue;
        /// otherwise it includes an applied map for all successfully written fields.
        /// </summary>
        private static object ApplyPropertiesToMany(string targets, string componentName, JObject props,
            string findMethod, string singlePropertyName = null)
        {
            if (string.IsNullOrWhiteSpace(componentName))
                return Response.Error("COMPONENT_REQUIRED",
                    new { hint = "Provide `component` type name so it can be located on each target." });
            // An empty properties object would write 0 fields to every target yet count each as a
            // success (failCount==0), falsely reporting "Applied properties on N of N target(s)".
            if (props == null || !props.HasValues)
                return Response.Error("PROPERTIES_REQUIRED",
                    new { hint = "The properties JSON object has no fields to set." });

            var gos = ObjectsHelper.ResolveMany(
                targets,
                findMethod,
                searchInactive: true,
                maxResults: ObjectsHelper.DefaultBatchTargetLimit,
                limitExceeded: out var limitExceeded);
            if (limitExceeded)
            {
                return Response.Error("TOO_MANY_TARGETS", new
                {
                    find_method = findMethod,
                    maxTargets = ObjectsHelper.DefaultBatchTargetLimit,
                    resolvedAtLeast = gos.Count,
                    hint = "Narrow the selector or split the operation into smaller batches."
                });
            }
            if (gos.Count == 0)
                return Response.Error("NO_TARGETS_RESOLVED", new { targets, find_method = findMethod });

            var perTarget = new List<object>();
            int okTargets = 0;
            foreach (var g in gos)
            {
                var comp = ObjectsHelper.ResolveComponentOnGo(g, componentName);
                if (comp == null)
                {
                    if (!string.IsNullOrEmpty(singlePropertyName))
                    {
                        perTarget.Add(new
                        {
                            target = g.name,
                            instanceId = ObjectIdHelper.GetSerializableId(g),
                            ok = false,
                            error = "COMPONENT_NOT_ON_TARGET",
                            newValue = (object)null
                        });
                    }
                    else
                    {
                        perTarget.Add(new
                        {
                            target = g.name,
                            instanceId = ObjectIdHelper.GetSerializableId(g),
                            ok = false,
                            error = "COMPONENT_NOT_ON_TARGET",
                            applied = new Dictionary<string, object>()
                        });
                    }
                    continue;
                }

                var res = ComponentSerializer.WriteProperties(comp, props, $"Set properties on {comp.GetType().Name}");
                int s = res.Count(r => r.Success);
                int f = res.Count - s;
                if (f == 0) okTargets++;
                var applied = ReadBackFields(comp, res.Where(r => r.Success).Select(r => r.Field));
                if (!string.IsNullOrEmpty(singlePropertyName))
                {
                    applied.TryGetValue(singlePropertyName, out var newValue);
                    perTarget.Add(new
                    {
                        target = g.name,
                        instanceId = ObjectIdHelper.GetSerializableId(g),
                        componentInstanceId = ObjectIdHelper.GetSerializableId(comp),
                        ok = f == 0,
                        successCount = s,
                        failCount = f,
                        fields = res,
                        newValue
                    });
                }
                else
                {
                    perTarget.Add(new
                    {
                        target = g.name,
                        instanceId = ObjectIdHelper.GetSerializableId(g),
                        componentInstanceId = ObjectIdHelper.GetSerializableId(comp),
                        ok = f == 0,
                        successCount = s,
                        failCount = f,
                        fields = res,
                        applied
                    });
                }
            }

            return Response.Success(
                $"Applied properties on {okTargets} of {gos.Count} target(s).",
                new { successCount = okTargets, failCount = gos.Count - okTargets, results = perTarget });
        }

        /// <summary>
        /// Re-read the given field names off a component after a write and return {name → {type,value}}.
        /// Serialized names are normalized so public names such as isKinematic match m_IsKinematic.
        /// Truly non-serialized public members are read through the same safe reflection boundary used
        /// by ComponentSerializer's write fallback.
        /// </summary>
        private static Dictionary<string, object> ReadBackFields(Component comp, IEnumerable<string> fieldNames)
        {
            var result = new Dictionary<string, object>();
            var names = fieldNames as IList<string> ?? fieldNames?.ToList();
            if (names == null || names.Count == 0)
                return result; // nothing written (e.g. all fields failed) — skip the full-component snapshot
            var snapshots = ComponentSerializer.ReadProperties(comp);
            foreach (var name in names)
            {
                if (result.ContainsKey(name)) continue;
                // Prefer an exact serialized-Name match (the authoritative write key via FindProperty),
                // then exact DisplayName, then case-insensitive on both. WriteProperties' reflection
                // fallback resolves member names case-insensitively (e.g. 'mass' -> Rigidbody.mass), so an
                // exact-case miss must not report newValue:null for a write that actually landed.
                var snap = snapshots.FirstOrDefault(sp => sp.Name == name);
                if (snap == null)
                    snap = snapshots.FirstOrDefault(sp => sp.DisplayName == name);
                if (snap == null)
                    snap = snapshots.FirstOrDefault(sp => string.Equals(sp.Name, name, StringComparison.OrdinalIgnoreCase));
                if (snap == null)
                    snap = snapshots.FirstOrDefault(sp => string.Equals(sp.DisplayName, name, StringComparison.OrdinalIgnoreCase));
                if (snap == null)
                {
                    var normalizedName = NormalizePropertyName(name);
                    if (normalizedName.Length > 0)
                    {
                        snap = snapshots.FirstOrDefault(sp =>
                            NormalizePropertyName(sp.Name) == normalizedName ||
                            NormalizePropertyName(sp.DisplayName) == normalizedName);
                    }
                }

                if (snap != null)
                {
                    result[name] = new { type = snap.Type, value = snap.Value };
                }
                else if (ComponentSerializer.TryReadPublicMember(comp, name, out var reflected))
                {
                    result[name] = new { type = reflected.Type, value = reflected.Value };
                }
                else
                {
                    result[name] = null;
                }
            }
            return result;
        }

        private static string NormalizePropertyName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var normalized = value.Trim();
            if (normalized.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(2);
            return new string(normalized
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        private static object ValidateComponentTargetSelectors(
            string target, string componentInstanceId, string targets)
        {
            var supplied = new List<string>();
            if (!string.IsNullOrWhiteSpace(target))
                supplied.Add("target");
            if (!string.IsNullOrWhiteSpace(componentInstanceId))
                supplied.Add("component_instance_id");
            if (!string.IsNullOrWhiteSpace(targets))
                supplied.Add("targets");

            if (supplied.Count <= 1)
                return null;

            return Response.Error("INVALID_PARAM", new
            {
                param = "target/component_instance_id/targets",
                supplied = supplied.ToArray(),
                expected = "Pass exactly one target selector."
            });
        }

        private struct ResolvedComponent
        {
            public Component Component;
            public object Error;
        }

        private static ResolvedComponent ResolveComponent(string target, string componentName, string componentInstanceId, string findMethod)
        {
            // Direct component instanceId path (preferred when GameObject has multiple of same type)
            if (!string.IsNullOrWhiteSpace(componentInstanceId))
            {
                var c = ObjectsHelper.FindComponentById(componentInstanceId);
                if (c == null)
                    return new ResolvedComponent { Error = Response.Error("COMPONENT_NOT_FOUND",
                        new { component_instance_id = componentInstanceId }) };
                return new ResolvedComponent { Component = c };
            }

            if (string.IsNullOrWhiteSpace(target))
                return new ResolvedComponent { Error = Response.Error("TARGET_REQUIRED",
                    new { hint = "Pass either target+component or component_instance_id." }) };

            var go = ObjectsHelper.FindObject(target, findMethod, searchInactive: true);
            if (go == null)
                return new ResolvedComponent { Error = Response.Error("TARGET_NOT_FOUND",
                    new { target, find_method = findMethod }) };

            if (string.IsNullOrWhiteSpace(componentName))
                return new ResolvedComponent { Error = Response.Error("COMPONENT_REQUIRED") };

            // Try TypeResolver-driven exact lookup first (handles full names, namespaced types)
            var type = TypeResolver.ResolveComponent(componentName);
            if (type != null)
            {
                var c = go.GetComponent(type);
                if (c != null) return new ResolvedComponent { Component = c };
            }

            // Fallback: case-insensitive name match across attached components
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                if (string.Equals(c.GetType().Name, componentName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.GetType().FullName, componentName, StringComparison.OrdinalIgnoreCase))
                    return new ResolvedComponent { Component = c };
            }

            var available = string.Join(", ", go.GetComponents<Component>()
                .Where(c => c != null).Select(c => c.GetType().Name));
            return new ResolvedComponent
            {
                Error = Response.Error("COMPONENT_NOT_FOUND_ON_TARGET",
                    new { target = go.name, component = componentName, available })
            };
        }

        private struct ResolvedPrefabComponent
        {
            public GameObject Root;      // the prefab asset root (for SaveAssetIfDirty)
            public Component Component;
            public object Error;
        }

        // Resolve a component inside a .prefab ASSET (loaded, not instantiated into a scene) by
        // hierarchy path + type name. No PrefabStage / preview scene is opened, so nothing recomputes
        // the graph — the caller's SerializedObject write is the only mutation.
        private static ResolvedPrefabComponent ResolvePrefabComponent(
            string prefabPath, string gameObjectPath, string componentName)
        {
            if (string.IsNullOrWhiteSpace(prefabPath))
                return new ResolvedPrefabComponent { Error = Response.Error("PREFAB_PATH_REQUIRED") };

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (root == null)
                return new ResolvedPrefabComponent { Error = Response.Error("PREFAB_NOT_FOUND",
                    new { prefab_path = prefabPath, hint = "Path must point to an existing .prefab asset." }) };

            GameObject go;
            if (string.IsNullOrEmpty(gameObjectPath) || gameObjectPath == "/")
            {
                go = root;
            }
            else
            {
                var t = root.transform.Find(gameObjectPath.Trim('/'));
                if (t == null)
                    return new ResolvedPrefabComponent { Root = root, Error = Response.Error("PREFAB_GAMEOBJECT_NOT_FOUND",
                        new { game_object_path = gameObjectPath, prefab_root = root.name,
                              hint = "'/'-separated child names relative to (and excluding) the prefab root." }) };
                go = t.gameObject;
            }

            if (string.IsNullOrWhiteSpace(componentName))
                return new ResolvedPrefabComponent { Root = root, Error = Response.Error("COMPONENT_REQUIRED") };

            var comp = ObjectsHelper.ResolveComponentOnGo(go, componentName);
            if (comp == null)
            {
                var available = string.Join(", ", go.GetComponents<Component>()
                    .Where(c => c != null).Select(c => c.GetType().Name));
                return new ResolvedPrefabComponent { Root = root, Error = Response.Error("COMPONENT_NOT_FOUND_ON_TARGET",
                    new { game_object = go.name, component = componentName, available }) };
            }

            return new ResolvedPrefabComponent { Root = root, Component = comp };
        }

        // Accept loose values: bare numbers/booleans, quoted strings, JSON objects/arrays.
        private static JToken ParseJsonValue(string raw)
        {
            raw = raw.Trim();
            if (raw.Length == 0) return JValue.CreateString(string.Empty);
            if (raw.StartsWith("{") || raw.StartsWith("[") || raw.StartsWith("\""))
                return JToken.Parse(raw);
            if (bool.TryParse(raw, out var b)) return new JValue(b);
            if (long.TryParse(raw, out var l)) return new JValue(l);
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                return new JValue(d);
            return new JValue(raw); // treat as string
        }
    }
}
