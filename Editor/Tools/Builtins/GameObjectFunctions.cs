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

        [Description("Set position, rotation, and/or scale on a GameObject's transform.")]
        [SceneEditingTool]
        public static object SetTransform(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = null,
            [ToolParam("Euler rotation as 'x,y,z'", Required = false)] string rotation = null,
            [ToolParam("Scale as 'x,y,z'", Required = false)] string scale = null,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            // Validate every provided vector BEFORE touching the transform, so a malformed value
            // returns a clear INVALID_PARAM instead of silently writing (0,0,0).
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

            Undo.RecordObject(go.transform, $"Set transform of {go.name}");

            if (hasPos) go.transform.position = pos;
            if (hasRot) go.transform.eulerAngles = rot;
            if (hasScl) go.transform.localScale = scl;

            return Response.Success($"Updated transform of '{go.name}'.", new
            {
                instanceId = ObjectIdHelper.GetSerializableId(go),
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z }
            });
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

        [Description("Activate or deactivate a GameObject.")]
        [SceneEditingTool]
        public static object SetActive(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("true to activate, false to deactivate")] string active,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return Response.Error("TARGET_NOT_FOUND", new { target, find_method });

            bool isActive = active == "true" || active == "1";
            Undo.RecordObject(go, $"Set active {go.name}");
            go.SetActive(isActive);
            return Response.Success($"Set '{go.name}' active = {isActive}.",
                new { instanceId = ObjectIdHelper.GetSerializableId(go), activeSelf = go.activeSelf });
        }

        [Description("Find GameObjects by id/name/path/tag/layer/component. Returns full structured results so the agent can chain by_id calls.")]
        [ReadOnlyTool]
        public static object FindGameObjects(
            [ToolParam("Search query (id, name, path, tag name, layer name/index, or component type)")] string query,
            [ToolParam("Search method (by_id/by_name/by_path/by_tag/by_layer/by_component)", Required = false)] string find_method = null,
            [ToolParam("Include inactive objects", Required = false)] string include_inactive = null,
            [ToolParam("Limit results to children of this GameObject identifier (used with find_method=by_*)", Required = false)] string in_parent = null,
            [ToolParam("Maximum results to return (default 50)", Required = false)] string max = "50")
        {
            bool inactive = include_inactive == "true" || include_inactive == "1";
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
    }
}
