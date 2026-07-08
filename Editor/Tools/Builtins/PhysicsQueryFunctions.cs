// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Globalization;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    /// <summary>
    /// Read-only physics query tools. All queries run against colliders already present in the
    /// open scene(s), so they work in Edit Mode (static colliders) without entering Play Mode.
    /// </summary>
    [ToolProvider("Physics")]
    internal static class PhysicsQueryFunctions
    {
        [Description("Cast a 3D ray against scene colliders (Physics.RaycastAll) and return every hit ordered nearest-first. " +
                     "Works in Edit Mode against static colliders. origin/direction are 'x,y,z' strings; direction is normalized internally.")]
        [ReadOnlyTool]
        public static object PhysicsRaycast(
            [ToolParam("Ray origin as 'x,y,z'")] string origin,
            [ToolParam("Ray direction as 'x,y,z' (normalized internally)")] string direction,
            [ToolParam("Maximum ray distance", Required = false)] float max_distance = 1000f,
            [ToolParam("Layer mask bitfield (-1 = all layers)", Required = false)] int layer_mask = -1,
            [ToolParam("Maximum hits to return", Required = false)] int max_hits = 20)
        {
            Vector3 o, d;
            try { o = ParseVec3(origin); }
            catch (FormatException ex)
            {
                return Response.Error("INVALID_PARAM", new { param = "origin", provided = origin, expected = "Vector3 'x,y,z'", detail = ex.Message });
            }
            try { d = ParseVec3(direction); }
            catch (FormatException ex)
            {
                return Response.Error("INVALID_PARAM", new { param = "direction", provided = direction, expected = "Vector3 'x,y,z'", detail = ex.Message });
            }

            if (d.sqrMagnitude < 1e-12f)
                return Response.Error("INVALID_PARAM", new { param = "direction", provided = direction, expected = "non-zero Vector3 'x,y,z'" });

            // In Edit Mode Unity does not auto-step physics; with autoSyncTransforms off (the
            // default) the PhysX scene can hold stale collider poses after a transform edit.
            // Sync first so queries reflect current geometry.
            Physics.SyncTransforms();
            var hits = Physics.RaycastAll(new Ray(o, d.normalized), max_distance, layer_mask);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            int cap = max_hits > 0 ? max_hits : 20;
            var list = new List<object>();
            foreach (var h in hits)
            {
                if (list.Count >= cap) break;
                var col = h.collider;
                var go = col != null ? col.gameObject : null;
                list.Add(new
                {
                    collider = col != null ? col.name : null,
                    path = go != null ? ObjectsHelper.GetGameObjectPath(go) : null,
                    instanceId = go != null ? ObjectIdHelper.GetSerializableId(go) : "0",
                    point = new { x = h.point.x, y = h.point.y, z = h.point.z },
                    normal = new { x = h.normal.x, y = h.normal.y, z = h.normal.z },
                    distance = h.distance
                });
            }

            return Response.Success($"Raycast hit {list.Count} collider(s) (of {hits.Length} total).",
                new { count = list.Count, totalHits = hits.Length, hits = list });
        }

        [Description("Overlap query against 3D scene colliders using Physics.OverlapSphere (shape=sphere) or Physics.OverlapBox (shape=box). " +
                     "Returns the overlapping colliders. Works in Edit Mode against static colliders. center/size are 'x,y,z' strings.")]
        [ReadOnlyTool]
        public static object PhysicsOverlap(
            [ToolParam("Center as 'x,y,z'")] string center,
            [ToolParam("Shape: 'sphere' or 'box'", Required = false)] string shape = "sphere",
            [ToolParam("Sphere radius (used when shape=sphere)", Required = false)] float radius = 1f,
            [ToolParam("Box full size as 'x,y,z' (used when shape=box)", Required = false)] string size = "1,1,1",
            [ToolParam("Layer mask bitfield (-1 = all layers)", Required = false)] int layer_mask = -1,
            [ToolParam("Maximum hits to return", Required = false)] int max_hits = 50)
        {
            Vector3 c;
            try { c = ParseVec3(center); }
            catch (FormatException ex)
            {
                return Response.Error("INVALID_PARAM", new { param = "center", provided = center, expected = "Vector3 'x,y,z'", detail = ex.Message });
            }

            Physics.SyncTransforms(); // reflect current poses in Edit Mode (see PhysicsRaycast note)
            Collider[] cols;
            var shapeLower = (shape ?? "sphere").Trim().ToLowerInvariant();
            switch (shapeLower)
            {
                case "sphere":
                    cols = Physics.OverlapSphere(c, radius, layer_mask);
                    break;
                case "box":
                    Vector3 sz;
                    try { sz = ParseVec3(size); }
                    catch (FormatException ex)
                    {
                        return Response.Error("INVALID_PARAM", new { param = "size", provided = size, expected = "Vector3 'x,y,z'", detail = ex.Message });
                    }
                    // OverlapBox takes half-extents; `size` is the full box dimension.
                    cols = Physics.OverlapBox(c, sz * 0.5f, Quaternion.identity, layer_mask);
                    break;
                default:
                    return Response.Error("INVALID_PARAM", new { param = "shape", provided = shape, expected = "'sphere' or 'box'" });
            }

            int cap = max_hits > 0 ? max_hits : 50;
            var list = new List<object>();
            foreach (var col in cols)
            {
                if (list.Count >= cap) break;
                if (col == null) continue;
                var go = col.gameObject;
                list.Add(new
                {
                    collider = col.name,
                    path = ObjectsHelper.GetGameObjectPath(go),
                    instanceId = ObjectIdHelper.GetSerializableId(go)
                });
            }

            return Response.Success($"Overlap ({shapeLower}) matched {list.Count} collider(s) (of {cols.Length} total).",
                new { count = list.Count, totalHits = cols.Length, shape = shapeLower, hits = list });
        }

        [Description("2D overlap-point query (Physics2D.OverlapPointAll) for the 2D merge board. " +
                     "Returns every 2D collider overlapping the point. Works in Edit Mode. point is an 'x,y' string.")]
        [ReadOnlyTool]
        public static object Physics2DOverlapPoint(
            [ToolParam("Point as 'x,y'")] string point,
            [ToolParam("Layer mask bitfield (-1 = all layers)", Required = false)] int layer_mask = -1)
        {
            Vector2 p;
            try { p = ParseVec2(point); }
            catch (FormatException ex)
            {
                return Response.Error("INVALID_PARAM", new { param = "point", provided = point, expected = "Vector2 'x,y'", detail = ex.Message });
            }

            Physics2D.SyncTransforms(); // reflect current poses in Edit Mode (see PhysicsRaycast note)
            var cols = Physics2D.OverlapPointAll(p, layer_mask);
            var list = new List<object>();
            foreach (var col in cols)
            {
                if (col == null) continue;
                var go = col.gameObject;
                list.Add(new
                {
                    collider = col.name,
                    path = ObjectsHelper.GetGameObjectPath(go),
                    instanceId = ObjectIdHelper.GetSerializableId(go)
                });
            }

            return Response.Success($"Physics2D overlap-point matched {list.Count} collider(s).",
                new { count = list.Count, hits = list });
        }

        // -------- Helpers --------

        // Parse an 'x,y,z' vector. Throws FormatException on wrong arity (or a non-numeric
        // component, via float.Parse) so callers can translate it into a clean INVALID_PARAM.
        private static Vector3 ParseVec3(string value)
        {
            var trimmed = (value ?? string.Empty).Trim('(', ')', ' ');
            var parts = trimmed.Split(',');
            if (parts.Length != 3)
                throw new FormatException($"expected 3 comma-separated numbers 'x,y,z', got {parts.Length}");
            return new Vector3(
                float.Parse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                float.Parse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        // Parse an 'x,y' vector. Throws FormatException on wrong arity (or a non-numeric
        // component, via float.Parse) so callers can translate it into a clean INVALID_PARAM.
        private static Vector2 ParseVec2(string value)
        {
            var trimmed = (value ?? string.Empty).Trim('(', ')', ' ');
            var parts = trimmed.Split(',');
            if (parts.Length != 2)
                throw new FormatException($"expected 2 comma-separated numbers 'x,y', got {parts.Length}");
            return new Vector2(
                float.Parse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture));
        }
    }
}
