// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
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

        [Description("Set one serialized field on a component in a prefab asset without opening Prefab Mode. " +
                     "The prefab path must be a normalized Assets/**/*.prefab path. Exact hierarchy and component matches are " +
                     "required; pass game_object_index or component_index only after an ambiguity response identifies duplicates. " +
                     "This avoids live Prefab Stage layout/TMP/Spine recomputation, but Unity serialization callbacks can still run. " +
                     "The asset is saved with PrefabUtility, synchronously reimported, and the persisted value is read back. " +
                     "Only serialized fields are accepted; public-member reflection fallback is intentionally disabled. " +
                     "For structural edits such as adding, removing, or reparenting objects, use Prefab Mode.")]
        [SceneEditingTool]
        public static object SetPrefabProperty(
            [ToolParam("Path to the .prefab asset (e.g. 'Assets/Prefabs/UIFoo.prefab')")] string prefab_path = null,
            [ToolParam("Component type name (e.g. 'TextMeshProUGUI', 'UnityEngine.UI.Image')")] string component = null,
            [ToolParam("Property/field name to set")] string property = null,
            [ToolParam("New value as JSON literal")] string value = null,
            [ToolParam("Exact '/'-separated child path relative to the prefab root (empty or '/' = root)", Required = false)] string game_object_path = null,
            [ToolParam("Zero-based match index when duplicate hierarchy paths exist; omit to reject ambiguity", Required = false)] int game_object_index = -1,
            [ToolParam("Zero-based match index when the target has duplicate components of this type; omit to reject ambiguity", Required = false)] int component_index = -1)
        {
            if (string.IsNullOrWhiteSpace(property))
                return Response.Error("PROPERTY_REQUIRED");
            if (value == null)
                return Response.Error("VALUE_REQUIRED");

            JToken token;
            try { token = ParseJsonValue(value); }
            catch (Exception ex) { return Response.Error("INVALID_VALUE_JSON", new { message = ex.Message }); }

            var resolved = ResolvePrefabComponent(
                prefab_path, game_object_path, component, game_object_index, component_index);
            if (resolved.Error != null) return resolved.Error;

            var props = new JObject { [property] = token };
            var results = ComponentSerializer.WriteSerializedProperties(resolved.Component, props,
                $"Set {property} on {resolved.Component.GetType().Name} (prefab asset)");

            var first = results.Count > 0 ? results[0] : null;
            if (first == null || !first.Success)
                return Response.Error("PREFAB_PROPERTY_SET_FAILED", new
                {
                    prefabPath = resolved.AssetPath,
                    property,
                    error = first?.Error ?? "unknown"
                });

            var saveError = SaveAndReloadPrefabComponent(resolved, out var verified);
            if (saveError != null)
                return saveError;

            var readBack = ReadBackFields(verified.Component, new[] { property });
            return Response.Success($"Set {verified.Component.GetType().Name}.{property} in {verified.AssetPath}.",
                new
                {
                    prefabPath = verified.AssetPath,
                    gameObjectPath = verified.GameObjectPath,
                    gameObjectIndex = verified.GameObjectIndex,
                    componentType = verified.Component.GetType().FullName,
                    componentIndex = verified.ComponentIndex,
                    property,
                    newValue = readBack.TryGetValue(property, out var nv) ? nv : null
                });
        }

        [Description("Set multiple serialized fields on one component in a prefab asset without opening Prefab Mode. " +
                     "Path validation, ambiguity handling, save, synchronous reimport, and persisted readback match " +
                     "set_prefab_property. Pass properties as a JSON object. Fields are applied independently and the response " +
                     "reports partial failures; if every field fails, the tool returns an error and does not save the asset.")]
        [SceneEditingTool]
        public static object SetPrefabProperties(
            [ToolParam("Path to the .prefab asset")] string prefab_path = null,
            [ToolParam("Component type name")] string component = null,
            [ToolParam("JSON object of property→value pairs")] string properties = null,
            [ToolParam("Exact '/'-separated child path relative to the prefab root (empty or '/' = root)", Required = false)] string game_object_path = null,
            [ToolParam("Zero-based match index when duplicate hierarchy paths exist; omit to reject ambiguity", Required = false)] int game_object_index = -1,
            [ToolParam("Zero-based match index when the target has duplicate components of this type; omit to reject ambiguity", Required = false)] int component_index = -1)
        {
            if (string.IsNullOrWhiteSpace(properties))
                return Response.Error("PROPERTIES_REQUIRED");

            JObject jobj;
            try { jobj = JObject.Parse(properties); }
            catch (Exception ex) { return Response.Error("INVALID_PROPERTIES_JSON", new { message = ex.Message }); }
            if (!jobj.HasValues)
                return Response.Error("PROPERTIES_REQUIRED",
                    new { hint = "The properties JSON object has no fields to set." });

            var resolved = ResolvePrefabComponent(
                prefab_path, game_object_path, component, game_object_index, component_index);
            if (resolved.Error != null) return resolved.Error;

            var results = ComponentSerializer.WriteSerializedProperties(resolved.Component, jobj,
                $"Set properties on {resolved.Component.GetType().Name} (prefab asset)");

            int success = results.Count(r => r.Success);
            int fail = results.Count - success;
            if (success == 0)
            {
                return Response.Error("PREFAB_PROPERTIES_SET_FAILED", new
                {
                    prefabPath = resolved.AssetPath,
                    successCount = success,
                    failCount = fail,
                    fields = results
                });
            }

            var saveError = SaveAndReloadPrefabComponent(resolved, out var verified);
            if (saveError != null)
                return saveError;

            var applied = ReadBackFields(verified.Component, results.Where(r => r.Success).Select(r => r.Field));
            return Response.Success(
                $"Applied {success} of {results.Count} field(s) on {verified.Component.GetType().Name} in {verified.AssetPath}.",
                new
                {
                    prefabPath = verified.AssetPath,
                    gameObjectPath = verified.GameObjectPath,
                    gameObjectIndex = verified.GameObjectIndex,
                    componentType = verified.Component.GetType().FullName,
                    componentIndex = verified.ComponentIndex,
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
            public string AssetPath;
            public string GameObjectPath;
            public int GameObjectIndex;
            public int ComponentIndex;
            public string ComponentName;
            public GameObject Root;
            public Component Component;
            public object Error;
        }

        private static ResolvedPrefabComponent ResolvePrefabComponent(
            string prefabPath,
            string gameObjectPath,
            string componentName,
            int gameObjectIndex,
            int componentIndex)
        {
            if (gameObjectIndex < -1)
            {
                return new ResolvedPrefabComponent { Error = Response.Error("INVALID_PARAM", new
                {
                    param = "game_object_index",
                    provided = gameObjectIndex,
                    expected = "A zero-based index, or omit it."
                }) };
            }
            if (componentIndex < -1)
            {
                return new ResolvedPrefabComponent { Error = Response.Error("INVALID_PARAM", new
                {
                    param = "component_index",
                    provided = componentIndex,
                    expected = "A zero-based index, or omit it."
                }) };
            }

            var pathError = ValidatePrefabAssetPath(prefabPath, out var normalizedAssetPath);
            if (pathError != null)
                return new ResolvedPrefabComponent { Error = pathError };

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(normalizedAssetPath);
            if (root == null)
                return new ResolvedPrefabComponent { Error = Response.Error("PREFAB_NOT_FOUND",
                    new { prefabPath = normalizedAssetPath }) };

            var assetType = PrefabUtility.GetPrefabAssetType(root);
            if (assetType != PrefabAssetType.Regular && assetType != PrefabAssetType.Variant)
            {
                return new ResolvedPrefabComponent { Error = Response.Error("PREFAB_NOT_EDITABLE", new
                {
                    prefabPath = normalizedAssetPath,
                    prefabAssetType = assetType.ToString(),
                    hint = "Only regular and variant .prefab assets can be edited directly."
                }) };
            }

            if (!AssetDatabase.IsOpenForEdit(normalizedAssetPath, StatusQueryOptions.UseCachedIfPossible))
            {
                return new ResolvedPrefabComponent { Error = Response.Error("PREFAB_NOT_EDITABLE", new
                {
                    prefabPath = normalizedAssetPath,
                    hint = "The asset is read-only or locked by version control."
                }) };
            }

            var hierarchyError = ResolvePrefabGameObject(
                root,
                gameObjectPath,
                gameObjectIndex,
                out var gameObject,
                out var normalizedGameObjectPath,
                out var selectedGameObjectIndex);
            if (hierarchyError != null)
            {
                return new ResolvedPrefabComponent
                {
                    AssetPath = normalizedAssetPath,
                    Root = root,
                    Error = hierarchyError
                };
            }

            if (string.IsNullOrWhiteSpace(componentName))
            {
                return new ResolvedPrefabComponent
                {
                    AssetPath = normalizedAssetPath,
                    Root = root,
                    Error = Response.Error("COMPONENT_REQUIRED")
                };
            }

            var attachedComponents = gameObject.GetComponents<Component>()
                .Where(candidate => candidate != null)
                .ToList();
            var matches = attachedComponents
                .Where(candidate =>
                    (string.Equals(candidate.GetType().Name, componentName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(candidate.GetType().FullName, componentName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count == 0)
            {
                var resolvedType = TypeResolver.ResolveComponent(componentName);
                if (resolvedType != null)
                    matches = attachedComponents.Where(candidate => resolvedType.IsAssignableFrom(candidate.GetType())).ToList();
            }

            if (matches.Count == 0)
            {
                var available = attachedComponents.Select(c => c.GetType().Name);
                return new ResolvedPrefabComponent
                {
                    AssetPath = normalizedAssetPath,
                    Root = root,
                    Error = Response.Error("COMPONENT_NOT_FOUND_ON_TARGET", new
                    {
                        gameObjectPath = normalizedGameObjectPath,
                        component = componentName,
                        available = available.ToArray()
                    })
                };
            }

            if (matches.Count > 1 && componentIndex < 0)
            {
                return new ResolvedPrefabComponent
                {
                    AssetPath = normalizedAssetPath,
                    Root = root,
                    Error = Response.Error("AMBIGUOUS_PREFAB_COMPONENT", new
                    {
                        gameObjectPath = normalizedGameObjectPath,
                        component = componentName,
                        matchCount = matches.Count,
                        matches = matches.Select((candidate, index) => new
                        {
                            index,
                            type = candidate.GetType().FullName
                        }).ToArray(),
                        hint = "Retry with component_index from this list."
                    })
                };
            }

            var selectedComponentIndex = componentIndex < 0 ? 0 : componentIndex;
            if (selectedComponentIndex >= matches.Count)
            {
                return new ResolvedPrefabComponent
                {
                    AssetPath = normalizedAssetPath,
                    Root = root,
                    Error = Response.Error("PREFAB_COMPONENT_INDEX_OUT_OF_RANGE", new
                    {
                        gameObjectPath = normalizedGameObjectPath,
                        component = componentName,
                        provided = selectedComponentIndex,
                        matchCount = matches.Count
                    })
                };
            }

            return new ResolvedPrefabComponent
            {
                AssetPath = normalizedAssetPath,
                GameObjectPath = normalizedGameObjectPath,
                GameObjectIndex = selectedGameObjectIndex,
                ComponentIndex = selectedComponentIndex,
                ComponentName = componentName,
                Root = root,
                Component = matches[selectedComponentIndex]
            };
        }

        private static object ValidatePrefabAssetPath(string prefabPath, out string normalizedPath)
        {
            normalizedPath = null;
            if (string.IsNullOrWhiteSpace(prefabPath))
                return Response.Error("PREFAB_PATH_REQUIRED");

            var candidate = prefabPath.Trim();
            if (candidate.Contains("\\") || !candidate.StartsWith("Assets/", StringComparison.Ordinal) ||
                !candidate.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return Response.Error("INVALID_PREFAB_PATH", new
                {
                    prefabPath,
                    hint = "Use a forward-slash Assets/**/*.prefab path."
                });
            }

            var segments = candidate.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
            {
                return Response.Error("INVALID_PREFAB_PATH", new
                {
                    prefabPath,
                    hint = "Path segments must be non-empty and cannot contain '.' or '..'."
                });
            }

            try
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot ?? string.Empty, candidate));
                var assetsRoot = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
#if UNITY_EDITOR_WIN
                var comparison = StringComparison.OrdinalIgnoreCase;
#else
                var comparison = StringComparison.Ordinal;
#endif
                if (!fullPath.StartsWith(assetsRoot, comparison))
                {
                    return Response.Error("INVALID_PREFAB_PATH", new
                    {
                        prefabPath,
                        hint = "Path must resolve inside this project's Assets directory."
                    });
                }

                if (File.Exists(fullPath) && new FileInfo(fullPath).IsReadOnly)
                {
                    return Response.Error("PREFAB_NOT_EDITABLE", new
                    {
                        prefabPath = candidate,
                        hint = "The prefab file is read-only."
                    });
                }
            }
            catch (Exception ex)
            {
                return Response.Error("INVALID_PREFAB_PATH", new { prefabPath, message = ex.Message });
            }

            normalizedPath = candidate;
            return null;
        }

        private static object ResolvePrefabGameObject(
            GameObject root,
            string requestedPath,
            int requestedIndex,
            out GameObject gameObject,
            out string normalizedPath,
            out int selectedIndex)
        {
            gameObject = null;
            normalizedPath = string.Empty;
            selectedIndex = -1;

            if (string.IsNullOrEmpty(requestedPath) || requestedPath == "/")
            {
                if (requestedIndex > 0)
                {
                    return Response.Error("PREFAB_GAMEOBJECT_INDEX_OUT_OF_RANGE", new
                    {
                        gameObjectPath = string.Empty,
                        provided = requestedIndex,
                        matchCount = 1
                    });
                }

                gameObject = root;
                selectedIndex = 0;
                return null;
            }

            if (requestedPath.Contains("\\") || requestedPath.StartsWith("/", StringComparison.Ordinal) ||
                requestedPath.EndsWith("/", StringComparison.Ordinal))
            {
                return Response.Error("INVALID_PREFAB_GAMEOBJECT_PATH", new
                {
                    gameObjectPath = requestedPath,
                    hint = "Use an exact forward-slash child path relative to the prefab root."
                });
            }

            var segments = requestedPath.Split('/');
            if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment == "." || segment == ".."))
            {
                return Response.Error("INVALID_PREFAB_GAMEOBJECT_PATH", new
                {
                    gameObjectPath = requestedPath,
                    hint = "Path segments must be non-empty and cannot contain '.' or '..'."
                });
            }

            var candidates = new List<Transform> { root.transform };
            foreach (var segment in segments)
            {
                var next = new List<Transform>();
                foreach (var parent in candidates)
                {
                    for (var childIndex = 0; childIndex < parent.childCount; childIndex++)
                    {
                        var child = parent.GetChild(childIndex);
                        if (string.Equals(child.name, segment, StringComparison.Ordinal))
                            next.Add(child);
                    }
                }
                candidates = next;
                if (candidates.Count == 0)
                    break;
            }

            normalizedPath = string.Join("/", segments);
            if (candidates.Count == 0)
            {
                return Response.Error("PREFAB_GAMEOBJECT_NOT_FOUND", new
                {
                    gameObjectPath = normalizedPath,
                    prefabRoot = root.name,
                    hint = "Use exact, case-sensitive child names and exclude the prefab root."
                });
            }

            if (candidates.Count > 1 && requestedIndex < 0)
            {
                return Response.Error("AMBIGUOUS_PREFAB_GAMEOBJECT", new
                {
                    gameObjectPath = normalizedPath,
                    matchCount = candidates.Count,
                    matches = candidates.Select((candidate, index) => new
                    {
                        index,
                        name = candidate.name,
                        siblingIndex = candidate.GetSiblingIndex()
                    }).ToArray(),
                    hint = "Retry with game_object_index from this list."
                });
            }

            selectedIndex = requestedIndex < 0 ? 0 : requestedIndex;
            if (selectedIndex >= candidates.Count)
            {
                return Response.Error("PREFAB_GAMEOBJECT_INDEX_OUT_OF_RANGE", new
                {
                    gameObjectPath = normalizedPath,
                    provided = selectedIndex,
                    matchCount = candidates.Count
                });
            }

            gameObject = candidates[selectedIndex].gameObject;
            return null;
        }

        private static object SaveAndReloadPrefabComponent(
            ResolvedPrefabComponent resolved,
            out ResolvedPrefabComponent verified)
        {
            verified = default;
            var saved = PrefabUtility.SavePrefabAsset(resolved.Root, out var success);
            if (!success || saved == null)
            {
                AssetDatabase.ImportAsset(
                    resolved.AssetPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                return Response.Error("PREFAB_SAVE_FAILED", new
                {
                    prefabPath = resolved.AssetPath
                });
            }

            AssetDatabase.ImportAsset(
                resolved.AssetPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            verified = ResolvePrefabComponent(
                resolved.AssetPath,
                resolved.GameObjectPath,
                resolved.ComponentName,
                resolved.GameObjectIndex,
                resolved.ComponentIndex);
            if (verified.Error != null || verified.Component == null)
            {
                return Response.Error("PREFAB_READBACK_FAILED", new
                {
                    prefabPath = resolved.AssetPath,
                    hint = "The prefab was saved but could not be resolved after synchronous reimport."
                });
            }

            return null;
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
