// Copyright (C) Funplay. Licensed under MIT.
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("GameObject")]
    internal static class GameObjectFunctions
    {
        [Description("Create a new empty GameObject in the scene. Returns the new instanceId so it can be targeted by_id in follow-up calls.")]
        [SceneEditingTool]
        public static object CreateGameObject(
            [ToolParam("Name of the new GameObject")] string name,
            [ToolParam("Parent GameObject identifier (instance id, name, or path)", Required = false)] string parent = null,
            [ToolParam("How to resolve parent (by_id, by_name, by_path, by_id_or_name_or_path)", Required = false)] string find_method = null)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

            if (!string.IsNullOrEmpty(parent))
            {
                var parentGo = ObjectsHelper.FindObject(parent, find_method);
                if (parentGo == null)
                    return Response.Error("PARENT_NOT_FOUND", new { parent, find_method });
                Undo.SetTransformParent(go.transform, parentGo.transform, $"Set parent of {name}");
            }

            Selection.activeGameObject = go;
            return Response.Success($"Created GameObject '{name}'.", GameObjectSerializer.Describe(go, includeComponents: false));
        }

        [Description("Create a primitive GameObject (Cube, Sphere, Capsule, Cylinder, Plane, Quad).")]
        [SceneEditingTool]
        public static object CreatePrimitive(
            [ToolParam("Name of the new object")] string name,
            [ToolParam("Primitive type: Cube, Sphere, Capsule, Cylinder, Plane, Quad")] string primitive_type,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = "0,0,0",
            [ToolParam("Scale as 'x,y,z'", Required = false)] string scale = "1,1,1")
        {
            PrimitiveType type;
            switch (primitive_type.ToLowerInvariant())
            {
                case "cube": type = PrimitiveType.Cube; break;
                case "sphere": type = PrimitiveType.Sphere; break;
                case "capsule": type = PrimitiveType.Capsule; break;
                case "cylinder": type = PrimitiveType.Cylinder; break;
                case "plane": type = PrimitiveType.Plane; break;
                case "quad": type = PrimitiveType.Quad; break;
                default: return Response.Error("UNKNOWN_PRIMITIVE", new { primitive_type });
            }

            if (!TryParseVector3(position, out var primPos, out var primPosErr))
                return Response.Error("INVALID_PARAM", new { param = "position", provided = position, expected = "Vector3 'x,y,z'", detail = primPosErr });
            if (!TryParseVector3(scale, out var primScale, out var primScaleErr))
                return Response.Error("INVALID_PARAM", new { param = "scale", provided = scale, expected = "Vector3 'x,y,z'", detail = primScaleErr });

            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            go.transform.position = primPos;
            go.transform.localScale = primScale;
            Selection.activeGameObject = go;

            return Response.Success($"Created {primitive_type} '{name}'.", GameObjectSerializer.Describe(go, includeComponents: false));
        }

        [Description("Delete a GameObject. Targets resolved through ObjectsHelper (instance id, name, path, tag, layer, component).")]
        [SceneEditingTool]
        public static object DeleteGameObject(
            [ToolParam("Identifier of the GameObject to delete")] string target,
            [ToolParam("How to resolve the target (by_id/by_name/by_path/by_tag/by_layer/by_component)", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            var info = new { instanceId = ObjectIdHelper.GetSerializableId(go), name = go.name };
            Undo.DestroyObjectImmediate(go);
            return Response.Success($"Deleted GameObject '{info.name}'.", info);
        }

        [Description("Duplicate a GameObject and optionally rename the copy.")]
        [SceneEditingTool]
        public static object DuplicateGameObject(
            [ToolParam("Identifier of the GameObject to duplicate")] string target,
            [ToolParam("Name for the duplicate", Required = false)] string new_name = null,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            var dup = Object.Instantiate(go);
            Undo.RegisterCreatedObjectUndo(dup, $"Duplicate {go.name}");
            dup.name = string.IsNullOrEmpty(new_name) ? go.name + " (Copy)" : new_name;
            Selection.activeGameObject = dup;

            return Response.Success($"Duplicated '{go.name}' as '{dup.name}'.",
                GameObjectSerializer.Describe(dup, includeComponents: false));
        }

        [Description("Rename a GameObject.")]
        [SceneEditingTool]
        public static object RenameGameObject(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("New name")] string new_name,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            var oldName = go.name;
            Undo.RecordObject(go, $"Rename {oldName} to {new_name}");
            go.name = new_name;
            return Response.Success($"Renamed '{oldName}' to '{new_name}'.",
                new { instanceId = ObjectIdHelper.GetSerializableId(go), name = go.name });
        }

        [Description("Set position, rotation, and/or scale on a GameObject's transform. " +
                     "Pass `target` for one object, or `targets` (comma-separated identifiers, or a single " +
                     "find spec that resolves to at most 100 objects) to apply the SAME values to many objects at once — " +
                     "the batch form returns per-target results.")]
        [SceneEditingTool]
        public static object SetTransform(
            [ToolParam("Identifier of a single GameObject (omit if using targets)", Required = false)] string target = null,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = null,
            [ToolParam("Euler rotation as 'x,y,z'", Required = false)] string rotation = null,
            [ToolParam("Scale as 'x,y,z'", Required = false)] string scale = null,
            [ToolParam("How to resolve target(s)", Required = false)] string find_method = null,
            [ToolParam("Comma-separated identifiers, or a single find spec resolving to at most 100 objects (alternative to target)", Required = false)] string targets = null)
        {
            // Validate every provided vector BEFORE touching any transform, so a malformed value
            // returns a clear INVALID_PARAM instead of silently writing (0,0,0) to one or many objects.
            var hasPos = !string.IsNullOrEmpty(position);
            var hasRot = !string.IsNullOrEmpty(rotation);
            var hasScl = !string.IsNullOrEmpty(scale);
            Vector3 pos = default, rot = default, scl = default;
            if (hasPos && !TryParseVector3(position, out pos, out var posErr))
                return Response.Error("INVALID_PARAM", new { param = "position", provided = position, expected = "Vector3 'x,y,z'", detail = posErr });
            if (hasRot && !TryParseVector3(rotation, out rot, out var rotErr))
                return Response.Error("INVALID_PARAM", new { param = "rotation", provided = rotation, expected = "Vector3 'x,y,z'", detail = rotErr });
            if (hasScl && !TryParseVector3(scale, out scl, out var sclErr))
                return Response.Error("INVALID_PARAM", new { param = "scale", provided = scale, expected = "Vector3 'x,y,z'", detail = sclErr });

            var selectorError = ValidateTargetSelectors(target, targets);
            if (selectorError != null)
                return selectorError;

            // Multi-target batch path
            if (!string.IsNullOrWhiteSpace(targets))
            {
                if (!TryResolveBatchTargets(targets, find_method, out var gos, out var resolutionError))
                    return resolutionError;

                var results = new List<object>();
                foreach (var g in gos)
                    results.Add(ApplyTransformAndDescribe(g, hasPos, pos, hasRot, rot, hasScl, scl));

                return Response.Success($"Updated transform of {gos.Count} object(s).",
                    new { successCount = gos.Count, results });
            }

            // Single-target path (response shape unchanged for backward compatibility)
            if (string.IsNullOrEmpty(target))
                return Response.Error("TARGET_OR_TARGETS_REQUIRED",
                    new { hint = "Pass `target` for one object or `targets` for many." });

            // searchInactive:true so this matches the multi-target ResolveMany path (which searches
            // inactive) — a single inactive target should resolve the same way whether via target or targets.
            var go = ObjectsHelper.FindObject(target, find_method, searchInactive: true);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            return Response.Success($"Updated transform of '{go.name}'.",
                ApplyTransformAndDescribe(go, hasPos, pos, hasRot, rot, hasScl, scl));
        }

        [Description("Reparent a GameObject. Pass empty parent to unparent.")]
        [SceneEditingTool]
        public static object SetParent(
            [ToolParam("Child GameObject identifier")] string child,
            [ToolParam("Parent GameObject identifier (empty to unparent)", Required = false)] string parent = null,
            [ToolParam("How to resolve targets", Required = false)] string find_method = null)
        {
            var childGo = ObjectsHelper.FindObject(child, find_method);
            if (childGo == null)
                return Response.Error("CHILD_NOT_FOUND", new { target = child, find_method });

            if (string.IsNullOrEmpty(parent))
            {
                Undo.SetTransformParent(childGo.transform, null, $"Unparent {childGo.name}");
                return Response.Success($"Unparented '{childGo.name}'.");
            }

            var parentGo = ObjectsHelper.FindObject(parent, find_method);
            if (parentGo == null)
                return Response.Error("PARENT_NOT_FOUND", new { target = parent, find_method });

            // Cycle protection: prevent setting parent to self or own descendant
            var t = parentGo.transform;
            while (t != null)
            {
                if (t == childGo.transform)
                    return Response.Error("CYCLE_DETECTED",
                        new { reason = "Parent is a descendant of child; would create a cycle." });
                t = t.parent;
            }

            Undo.SetTransformParent(childGo.transform, parentGo.transform, $"Parent {childGo.name} to {parentGo.name}");
            return Response.Success($"Parented '{childGo.name}' to '{parentGo.name}'.",
                new { childInstanceId = ObjectIdHelper.GetSerializableId(childGo), parentInstanceId = ObjectIdHelper.GetSerializableId(parentGo) });
        }

        [Description("Add a component to a GameObject. Returns the new component's instanceId.")]
        [SceneEditingTool]
        public static object AddComponent(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Component type name (e.g. 'Rigidbody', 'BoxCollider', 'AudioSource')")] string component_type,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            var type = TypeResolver.ResolveComponent(component_type);
            if (type == null)
                return Response.Error("COMPONENT_TYPE_NOT_FOUND", new { component_type });

            var comp = Undo.AddComponent(go, type);
            if (comp == null)
                return Response.Error("ADD_COMPONENT_FAILED", new { component_type, target = go.name });

            return Response.Success($"Added {component_type} to '{go.name}'.",
                new { gameObjectInstanceId = ObjectIdHelper.GetSerializableId(go),
                      componentInstanceId = ObjectIdHelper.GetSerializableId(comp),
                      type = comp.GetType().Name });
        }

        [Description("Set tag and/or layer on a GameObject.")]
        [SceneEditingTool]
        public static object SetTagAndLayer(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Tag to set", Required = false)] string tag = null,
            [ToolParam("Layer name to set", Required = false)] string layer = null,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            Undo.RecordObject(go, $"Set tag/layer of {go.name}");
            var changes = new List<string>();
            var warnings = new List<string>();

            if (!string.IsNullOrEmpty(tag))
            {
                try { go.tag = tag; changes.Add($"tag={tag}"); }
                catch (UnityException) { warnings.Add($"Tag '{tag}' is not defined; use add_tag first."); }
            }
            if (!string.IsNullOrEmpty(layer))
            {
                int idx = LayerMask.NameToLayer(layer);
                if (idx >= 0) { go.layer = idx; changes.Add($"layer={layer}"); }
                else warnings.Add($"Layer '{layer}' is not defined; use add_layer first.");
            }

            return Response.Success($"Updated '{go.name}'.", new
            {
                instanceId = ObjectIdHelper.GetSerializableId(go),
                changes,
                warnings
            });
        }

        [Description("Activate or deactivate a GameObject. Pass `target` for one object, or `targets` " +
                     "(comma-separated identifiers, or a single find spec resolving to at most 100 objects) for a batch — " +
                     "the batch form returns per-target results.")]
        [SceneEditingTool]
        public static object SetActive(
            [ToolParam("Identifier of a single GameObject (omit if using targets)", Required = false)] string target = null,
            [ToolParam("true to activate, false to deactivate")] string active = null,
            [ToolParam("How to resolve target(s)", Required = false)] string find_method = null,
            [ToolParam("Comma-separated identifiers, or a single find spec resolving to at most 100 objects (alternative to target)", Required = false)] string targets = null)
        {
            if (string.IsNullOrWhiteSpace(active))
                return Response.Error("ACTIVE_REQUIRED", new { hint = "Pass active='true' or active='false'." });
            if (!TryParseBoolean(active, out var isActive))
                return Response.Error("INVALID_PARAM",
                    new { param = "active", provided = active, expected = "Boolean true/false or 1/0" });

            var selectorError = ValidateTargetSelectors(target, targets);
            if (selectorError != null)
                return selectorError;

            // Multi-target batch path
            if (!string.IsNullOrWhiteSpace(targets))
            {
                if (!TryResolveBatchTargets(targets, find_method, out var gos, out var resolutionError))
                    return resolutionError;

                var results = new List<object>();
                foreach (var g in gos)
                {
                    Undo.RecordObject(g, $"Set active {g.name}");
                    g.SetActive(isActive);
                    results.Add(new { instanceId = ObjectIdHelper.GetSerializableId(g), name = g.name, activeSelf = g.activeSelf });
                }
                return Response.Success($"Set active = {isActive} on {gos.Count} object(s).",
                    new { active = isActive, successCount = gos.Count, results });
            }

            // Single-target path (response shape unchanged for backward compatibility)
            if (string.IsNullOrEmpty(target))
                return Response.Error("TARGET_OR_TARGETS_REQUIRED",
                    new { hint = "Pass `target` for one object or `targets` for many." });

            // searchInactive:true so an inactive object can be resolved and activated — you can't
            // activate what you can't find (and this matches the multi-target ResolveMany path).
            var go = ObjectsHelper.FindObject(target, find_method, searchInactive: true);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            Undo.RecordObject(go, $"Set active {go.name}");
            go.SetActive(isActive);
            return Response.Success($"Set '{go.name}' active = {isActive}.",
                new { instanceId = ObjectIdHelper.GetSerializableId(go), activeSelf = go.activeSelf });
        }

        [Description("Find GameObjects by id/name/path/tag/layer/component. Returns full structured results so the agent can chain by_id calls.")]
        [ReadOnlyTool]
        public static object FindGameObjects(
            [ToolParam("Search query; omit for a structured filter-only scan", Required = false)] string query = null,
            [ToolParam("Search method (by_id/by_name/by_path/by_tag/by_layer/by_component)", Required = false)] string find_method = null,
            [ToolParam("Include inactive objects", Required = false)] string include_inactive = null,
            [ToolParam("Limit results to children of this GameObject identifier (used with find_method=by_*)", Required = false)] string in_parent = null,
            [ToolParam("Maximum results to return (default 50)", Required = false)] string max = "50",
            [ToolParam("JSON {component, assembly?, where:[{property,op,value}], select:[serializedPaths]}. Predicates AND on the same component; no arbitrary property getters.", Required = false)] string filter = null,
            [ToolParam("Structured scope: scene, selection, prefabs; omitted preserves legacy query behavior", Required = false)] string scope = null,
            [ToolParam("JSON prefab paths/folders for scope=prefabs", Required = false)] string asset_paths = null,
            [ToolParam("Structured result page offset", Required = false)] int offset = 0,
            [ToolParam("Structured scan limit, 1..50000", Required = false)] int scan_limit = 10000,
            [ToolParam("Read a stable query snapshot page (120-second lifetime; lost on reload)", Required = false)] string snapshot_id = null)
        {
            bool inactive = include_inactive == "true" || include_inactive == "1";
            if (filter != null || scope != null || asset_paths != null || snapshot_id != null || offset != 0)
            {
                if (!int.TryParse(max, out var limit)) return Response.Error("INVALID_QUERY_LIMIT");
                return StructuredObjectQuery.Execute(query, find_method, inactive, in_parent, limit, filter, scope, asset_paths, offset, scan_limit, snapshot_id);
            }
            GameObject root = null;
            if (!string.IsNullOrEmpty(in_parent))
            {
                root = ObjectsHelper.FindObject(in_parent, null, searchInactive: inactive);
                if (root == null)
                    return Response.Error("PARENT_NOT_FOUND", new { in_parent });
            }

            var matches = ObjectsHelper.FindObjects(query, find_method, findAll: true,
                searchInactive: inactive, searchInChildren: root != null, root: root);

            int.TryParse(max, out var cap);
            if (cap <= 0) cap = 50;
            if (matches.Count > cap)
                matches = matches.GetRange(0, cap);

            return Response.Success($"Found {matches.Count} object(s).", GameObjectSerializer.DescribeMany(matches));
        }

        [Description("Get full info on a GameObject: transform, components (with instance ids), active state, tag, layer.")]
        [ReadOnlyTool]
        public static object GetGameObjectInfo(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("How to resolve target", Required = false)] string find_method = null,
            [ToolParam("Include immediate children list", Required = false)] string include_children = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method, searchInactive: true);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            bool kids = include_children == "true" || include_children == "1";
            return Response.Success($"GameObject '{go.name}'.",
                GameObjectSerializer.Describe(go, includeComponents: true, includeChildren: kids));
        }

        // -------- Helpers --------

        private static object ValidateTargetSelectors(string target, string targets)
        {
            var hasTarget = !string.IsNullOrWhiteSpace(target);
            var hasTargets = !string.IsNullOrWhiteSpace(targets);
            if (hasTarget && hasTargets)
            {
                return Response.Error("INVALID_PARAM", new
                {
                    param = "target/targets",
                    expected = "Pass exactly one of `target` or `targets`."
                });
            }

            if (!hasTarget && !hasTargets)
            {
                return Response.Error("TARGET_OR_TARGETS_REQUIRED",
                    new { hint = "Pass `target` for one object or `targets` for many." });
            }

            return null;
        }

        private static bool TryResolveBatchTargets(string targets, string findMethod,
            out List<GameObject> gameObjects, out object error)
        {
            gameObjects = ObjectsHelper.ResolveMany(
                targets,
                findMethod,
                searchInactive: true,
                maxResults: ObjectsHelper.DefaultBatchTargetLimit,
                limitExceeded: out var limitExceeded);

            if (limitExceeded)
            {
                error = Response.Error("TOO_MANY_TARGETS", new
                {
                    find_method = findMethod,
                    maxTargets = ObjectsHelper.DefaultBatchTargetLimit,
                    resolvedAtLeast = gameObjects.Count,
                    hint = "Narrow the selector or split the operation into smaller batches."
                });
                return false;
            }

            if (gameObjects.Count == 0)
            {
                error = Response.Error("NO_TARGETS_RESOLVED", new { targets, find_method = findMethod });
                return false;
            }

            error = null;
            return true;
        }

        // Record undo, apply the provided transform components, and return the structured echo
        // used by both the single- and multi-target paths of SetTransform.
        private static object ApplyTransformAndDescribe(GameObject go, bool hasPos, Vector3 pos, bool hasRot, Vector3 rot, bool hasScl, Vector3 scl)
        {
            Undo.RecordObject(go.transform, $"Set transform of {go.name}");
            if (hasPos) go.transform.position = pos;
            if (hasRot) go.transform.eulerAngles = rot;
            if (hasScl) go.transform.localScale = scl;
            return new
            {
                instanceId = ObjectIdHelper.GetSerializableId(go),
                name = go.name,
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z }
            };
        }

        // Parse an 'x,y,z' vector, reporting malformed input instead of silently returning
        // Vector3.zero (which used to produce a wrong write with a success response, e.g.
        // set_transform(position:'1,2') moving the object to the origin).
        private static bool TryParseVector3(string value, out Vector3 result, out string error)
        {
            result = Vector3.zero;
            error = null;
            var trimmed = (value ?? string.Empty).Trim('(', ')', ' ');
            var parts = trimmed.Split(',');
            if (parts.Length != 3)
            {
                error = $"expected 3 comma-separated numbers 'x,y,z', got {parts.Length}";
                return false;
            }
            if (float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) &&
                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
            {
                result = new Vector3(x, y, z);
                return true;
            }
            error = $"'{value}' has a non-numeric component";
            return false;
        }

        private static bool TryParseBoolean(string value, out bool result)
        {
            result = false;
            var normalized = (value ?? string.Empty).Trim();
            if (bool.TryParse(normalized, out result))
                return true;
            if (normalized == "1")
            {
                result = true;
                return true;
            }
            if (normalized == "0")
                return true;
            return false;
        }
    }
}
