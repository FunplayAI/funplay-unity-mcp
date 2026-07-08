// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    /// <summary>
    /// Batch component operations built on <see cref="UnityEditorInternal.ComponentUtility"/>:
    /// copy a component from one GameObject, then paste its values onto (or add it to) many
    /// GameObjects at once. Targets resolve through <see cref="ObjectsHelper"/> so a comma-separated
    /// list OR a single find spec (name/tag/layer/component that matches many) both work.
    ///
    /// The copy buffer is the editor's own global component clipboard; a lightweight static flag
    /// tracks whether copy_component ran this domain so the paste tools can fail fast with a clear
    /// code instead of silently pasting stale/empty data.
    /// </summary>
    [ToolProvider("ComponentBatch")]
    internal static class ComponentBatchFunctions
    {
        // Best-effort marker: set by CopyComponent, reset on domain reload (same lifetime as the
        // editor copy buffer). Lets paste tools return NO_COMPONENT_COPIED instead of a false success.
        private static bool s_hasCopied;
        private static string s_copiedTypeName;

        [Description("Copy a component from a source GameObject into the editor's SHARED component clipboard so its " +
                     "values can be pasted onto other objects with paste_component_values. Resolves the source " +
                     "through ObjectsHelper (instance id, name, path, tag, layer, component). NOTE: this overwrites " +
                     "Unity's global component clipboard (the same one a manual Ctrl+C uses), so it is not a pure " +
                     "read -- a subsequent manual paste by the user will paste this component.")]
        public static object CopyComponent(
            [ToolParam("Source GameObject identifier (instance id, name, path, tag…)")] string source_target,
            [ToolParam("Component type name to copy (e.g. 'Rigidbody', 'BoxCollider', 'UnityEngine.UI.Image')")] string component_type,
            [ToolParam("How to resolve the source target", Required = false)] string find_method = null)
        {
            if (string.IsNullOrEmpty(component_type))
                return Response.Error("COMPONENT_TYPE_REQUIRED");

            var go = ObjectsHelper.FindObject(source_target, find_method, searchInactive: true);
            if (go == null)
                return Response.Error("SOURCE_NOT_FOUND", new { source_target, find_method });

            var comp = ResolveComponentOnGo(go, component_type);
            if (comp == null)
            {
                var available = string.Join(", ", go.GetComponents<Component>()
                    .Where(c => c != null).Select(c => c.GetType().Name));
                return Response.Error("COMPONENT_NOT_ON_SOURCE",
                    new { source = go.name, component_type, available });
            }

            bool copied = ComponentUtility.CopyComponent(comp);
            if (!copied)
                return Response.Error("COPY_FAILED",
                    new { component_type, source = go.name, hint = "Unity refused to copy this component." });

            s_hasCopied = true;
            s_copiedTypeName = comp.GetType().Name;

            return Response.Success($"Copied {comp.GetType().Name} from '{go.name}'.", new
            {
                copied = true,
                component = new
                {
                    instanceId = ObjectIdHelper.GetSerializableId(comp),
                    type = comp.GetType().Name,
                    fullType = comp.GetType().FullName,
                    gameObject = new { instanceId = ObjectIdHelper.GetSerializableId(go), name = go.name }
                }
            });
        }

        [Description("Paste the editor's SHARED component clipboard (last filled by copy_component) onto many GameObjects. " +
                     "`targets` is a comma-separated list of identifiers, or a single find spec that resolves to many " +
                     "(e.g. a tag with find_method=by_tag). When as_new is true a NEW component is added to each target; " +
                     "otherwise the clipboard values are pasted onto each target's existing component of component_type. " +
                     "Returns per-target success so partial failures are diagnosable. NOTE: Unity exposes no API to read " +
                     "back the clipboard, so this pastes whatever is currently on the shared editor clipboard -- if a " +
                     "manual Ctrl+C or another copy happened since copy_component, that newer component is what gets pasted.")]
        [SceneEditingTool]
        public static object PasteComponentValues(
            [ToolParam("Comma-separated identifiers, or a single find spec resolving to many")] string targets,
            [ToolParam("Component type to paste onto each target (required unless as_new). e.g. 'Rigidbody'", Required = false)] string component_type = null,
            [ToolParam("true to paste as a NEW component on each target instead of onto an existing one", Required = false)] bool as_new = false,
            [ToolParam("How to resolve targets", Required = false)] string find_method = null)
        {
            if (!s_hasCopied)
                return Response.Error("NO_COMPONENT_COPIED",
                    new { hint = "Call copy_component first to fill the component clipboard." });

            if (!as_new && string.IsNullOrEmpty(component_type))
                return Response.Error("COMPONENT_TYPE_REQUIRED",
                    new { hint = "Provide component_type to locate the existing component, or set as_new=true." });

            var gos = ResolveManyTargets(targets, find_method);
            if (gos.Count == 0)
                return Response.Error("NO_TARGETS_RESOLVED", new { targets, find_method });

            var results = new List<object>();
            int success = 0;
            foreach (var go in gos)
            {
                try
                {
                    if (as_new)
                    {
                        // Snapshot components before, so we can undo-register the NEW one after.
                        // RegisterCompleteObjectUndo(go) alone does NOT capture a component added by
                        // PasteComponentAsNew (it's a separate Object), so Ctrl+Z would leave it behind.
                        var before = go.GetComponents<Component>();
                        bool ok = ComponentUtility.PasteComponentAsNew(go);
                        if (ok)
                        {
                            foreach (var c in go.GetComponents<Component>())
                                if (c != null && Array.IndexOf(before, c) < 0)
                                    Undo.RegisterCreatedObjectUndo(c, "Paste Component As New");
                            success++;
                        }
                        results.Add(ok
                            ? (object)new { target = go.name, instanceId = ObjectIdHelper.GetSerializableId(go), ok = true }
                            : new { target = go.name, instanceId = ObjectIdHelper.GetSerializableId(go), ok = false, error = "PASTE_AS_NEW_FAILED" });
                    }
                    else
                    {
                        var comp = ResolveComponentOnGo(go, component_type);
                        if (comp == null)
                        {
                            results.Add(new { target = go.name, instanceId = ObjectIdHelper.GetSerializableId(go), ok = false, error = "COMPONENT_NOT_ON_TARGET" });
                            continue;
                        }
                        Undo.RecordObject(comp, "Paste Component Values");
                        bool ok = ComponentUtility.PasteComponentValues(comp);
                        if (ok) success++;
                        results.Add(ok
                            ? (object)new { target = go.name, instanceId = ObjectIdHelper.GetSerializableId(go), ok = true }
                            : new { target = go.name, instanceId = ObjectIdHelper.GetSerializableId(go), ok = false, error = "PASTE_VALUES_FAILED" });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new { target = go.name, instanceId = ObjectIdHelper.GetSerializableId(go), ok = false, error = ex.Message });
                }
            }

            return Response.Success(
                $"Pasted the component clipboard onto {success} of {gos.Count} target(s).",
                new { lastCopiedViaMcp = s_copiedTypeName, asNew = as_new, successCount = success, failCount = gos.Count - success, results });
        }

        [Description("Add a component of component_type to many GameObjects in one Undo-tracked call. " +
                     "`targets` is a comma-separated list of identifiers, or a single find spec that resolves to many. " +
                     "Returns per-target success with each new component's instanceId.")]
        [SceneEditingTool]
        public static object AddComponentToMany(
            [ToolParam("Comma-separated identifiers, or a single find spec resolving to many")] string targets,
            [ToolParam("Component type name to add (e.g. 'BoxCollider', 'UnityEngine.UI.Image')")] string component_type,
            [ToolParam("How to resolve targets", Required = false)] string find_method = null)
        {
            if (string.IsNullOrEmpty(component_type))
                return Response.Error("COMPONENT_TYPE_REQUIRED");

            var type = TypeResolver.ResolveComponent(component_type);
            if (type == null)
                return Response.Error("COMPONENT_TYPE_NOT_FOUND", new { component_type });

            var gos = ResolveManyTargets(targets, find_method);
            if (gos.Count == 0)
                return Response.Error("NO_TARGETS_RESOLVED", new { targets, find_method });

            var results = new List<object>();
            int success = 0;
            foreach (var go in gos)
            {
                try
                {
                    var comp = Undo.AddComponent(go, type);
                    if (comp != null)
                    {
                        success++;
                        results.Add(new { target = go.name, instanceId = ObjectIdHelper.GetSerializableId(go), ok = true, componentInstanceId = ObjectIdHelper.GetSerializableId(comp) });
                    }
                    else
                    {
                        results.Add(new { target = go.name, instanceId = ObjectIdHelper.GetSerializableId(go), ok = false, error = "ADD_COMPONENT_FAILED" });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new { target = go.name, instanceId = ObjectIdHelper.GetSerializableId(go), ok = false, error = ex.Message });
                }
            }

            return Response.Success(
                $"Added {type.Name} to {success} of {gos.Count} target(s).",
                new { type = type.Name, successCount = success, failCount = gos.Count - success, results });
        }

        // -------- Helpers --------

        /// <summary>
        /// Resolve the given component type on a GameObject: TypeResolver first (handles full/namespaced
        /// names), then a case-insensitive fallback across attached components (matches ComponentProperty).
        /// Returns null if none match.
        /// </summary>
        private static Component ResolveComponentOnGo(GameObject go, string typeName)
        {
            if (go == null || string.IsNullOrEmpty(typeName))
                return null;

            var type = TypeResolver.ResolveComponent(typeName);
            if (type != null)
            {
                var c = go.GetComponent(type);
                if (c != null) return c;
            }

            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                if (string.Equals(c.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.GetType().FullName, typeName, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        /// <summary>
        /// Resolve MANY GameObjects from a comma-separated list OR a single find spec. Each token is
        /// resolved via <see cref="ObjectsHelper.FindObjects"/> with findAll, so one token can expand to
        /// many (e.g. a tag/name shared by several objects). Results are de-duplicated, order preserved.
        /// </summary>
        private static List<GameObject> ResolveManyTargets(string targets, string findMethod)
        {
            var results = new List<GameObject>();
            if (string.IsNullOrWhiteSpace(targets))
                return results;

            foreach (var raw in targets.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0)
                    continue;

                var found = ObjectsHelper.FindObjects(token, findMethod, findAll: true, searchInactive: true);
                foreach (var go in found)
                {
                    if (go != null && !results.Contains(go))
                        results.Add(go);
                }
            }
            return results;
        }
    }
}
