// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Funplay.Editor.Tools.Helpers
{
    /// <summary>
    /// Bridges Unity's legacy InstanceID API and Unity 6 EntityId API behind one MCP-facing id.
    /// IDs handed to clients are always strings — long InstanceIDs are formatted with invariant culture
    /// so the JSON shape stays identical across Unity versions.
    /// </summary>
    internal static class ObjectIdHelper
    {
#if UNITY_6000_4_OR_NEWER
        private const int CacheCompactThreshold = 1024;

        private static readonly Dictionary<string, WeakReference<UnityObject>> EntityIdCache =
            new Dictionary<string, WeakReference<UnityObject>>();

        // EntityId.Parse may or may not be public depending on Unity build — prefer public, fall back
        // to internal so the cache miss path keeps working until the API stabilises.
        private static readonly MethodInfo EntityIdParseMethod = ResolveEntityIdParseMethod();

        private static MethodInfo ResolveEntityIdParseMethod()
        {
            var publicParse = typeof(EntityId).GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            if (publicParse != null) return publicParse;

            return typeof(EntityId).GetMethod(
                "Parse",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
        }
#endif

        public static string GetSerializableId(UnityObject obj)
        {
            if (obj == null)
                return "0";

#if UNITY_6000_4_OR_NEWER
            var id = obj.GetEntityId().ToString();
            CacheEntityId(id, obj);
            return id;
#else
            return obj.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        public static UnityObject ToObject(string objectId)
        {
            if (string.IsNullOrWhiteSpace(objectId) || objectId == "0")
                return null;

            objectId = objectId.Trim();

#if UNITY_6000_4_OR_NEWER
            if (EntityIdCache.TryGetValue(objectId, out var cached) &&
                cached.TryGetTarget(out var cachedObject) &&
                cachedObject != null)
            {
                return cachedObject;
            }

            if (EntityIdParseMethod != null && TryParseEntityId(objectId, out var entityId))
            {
                var resolved = EditorUtility.EntityIdToObject(entityId);
                if (resolved != null)
                    CacheEntityId(objectId, resolved);
                return resolved;
            }

            // No global scan fallback: rely on cached round-trips + the official Parse API.
            // Callers should fall back to assetPath when this returns null.
            return null;
#else
            return long.TryParse(objectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? EditorUtility.InstanceIDToObject(unchecked((int)id))
                : null;
#endif
        }

#if UNITY_6000_4_OR_NEWER
        private static void CacheEntityId(string id, UnityObject obj)
        {
            EntityIdCache[id] = new WeakReference<UnityObject>(obj);
            if (EntityIdCache.Count > CacheCompactThreshold)
                CompactCache();
        }

        private static void CompactCache()
        {
            List<string> dead = null;
            foreach (var kv in EntityIdCache)
            {
                if (!kv.Value.TryGetTarget(out var target) || target == null)
                {
                    dead ??= new List<string>();
                    dead.Add(kv.Key);
                }
            }
            if (dead == null) return;
            foreach (var k in dead)
                EntityIdCache.Remove(k);
        }

        private static bool TryParseEntityId(string objectId, out EntityId entityId)
        {
            entityId = EntityId.None;
            if (EntityIdParseMethod == null)
                return false;

            try
            {
                entityId = (EntityId)EntityIdParseMethod.Invoke(null, new object[] { objectId });
                return entityId.IsValid();
            }
            catch (Exception)
            {
                entityId = EntityId.None;
                return false;
            }
        }
#endif
    }
}
