// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Inspection")]
    internal static class RenderInspectionFunctions
    {
        [Description("Read actual loaded object/prefab render-resource bindings: component/material slots, particle trails, shader/textures, prefab source/overrides, camera/layer/sorting/canvas/masks. Never reads Renderer.material(s) or materialForRendering, creates materials, activates objects or saves. Bounded 1..200 render users and 32 cameras; incomplete scans are explicit. Pair with an actual authored-scene capture; isSupported is NOT visual acceptance.")]
        [ReadOnlyTool]
        public static object InspectRenderBindings(
            [ToolParam("Verified GameObject ID or persistent Assets prefab path")] string target,
            [ToolParam("Include descendants", Required = false)] bool recursive = true,
            [ToolParam("Maximum render components, 1..200", Required = false)] int limit = 100)
        {
            if (limit < 1 || limit > 200) return Response.Error("INVALID_RENDER_LIMIT");
            try
            {
                var root = target != null && target.StartsWith("Assets/", StringComparison.Ordinal)
                    ? AssetDatabase.LoadAssetAtPath<GameObject>(target) : ObjectIdHelper.ToObject(target) as GameObject;
                if (root == null) return Response.Error("RENDER_ROOT_NOT_FOUND");
                var bindings = new List<object>();
                var transforms = new Stack<Transform>(); transforms.Push(root.transform);
                int scanned = 0; bool complete = true; var failures = new List<string>();
                while (transforms.Count > 0)
                {
                    var t = transforms.Pop();
                    if (++scanned > 5000) { complete = false; break; }
                    var components = t.GetComponents<Component>();
                    foreach (var component in components)
                    {
                        if (!(component is Renderer) && !(component is Graphic)) continue;
                        if (bindings.Count == limit) { complete = false; break; }
                        try { bindings.Add(Describe(component)); }
                        catch (Exception ex) { complete = false; failures.Add(ObjectsHelper.GetGameObjectPath(t.gameObject) + ": " + ex.Message); }
                    }
                    if (bindings.Count >= limit && (transforms.Count > 0 || recursive && t.childCount > 0)) { complete = false; break; }
                    if (recursive) for (int i = t.childCount - 1; i >= 0; i--) transforms.Push(t.GetChild(i));
                }
                var cameras = Resources.FindObjectsOfTypeAll<Camera>().Where(c => c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded).OrderBy(c => c.depth).Take(33).ToArray();
                var graph = Newtonsoft.Json.Linq.JArray.FromObject(bindings);
                bool truncated = graph.Any(b => b.Value<bool?>("slots_complete") == false || b.Value<bool?>("overrides_complete") == false ||
                    b["materials"].Any(m => m.Value<bool?>("property_available") == false || m["material"]?.Type == Newtonsoft.Json.Linq.JTokenType.Object &&
                        (m["material"].Value<bool?>("properties_complete") == false || m["material"].Value<bool?>("keywords_complete") == false)));
                complete &= !truncated;
                return Response.Success("Render users inspected without material instantiation; visual correctness remains unverified.", new {
                    context = MCPProjectContext.Current, player_frame = PlayerLoopWitness.Latest, observed_at = DateTime.UtcNow.ToString("O"),
                    root = Identity(root), scope = EditorUtility.IsPersistent(root) ? "prefab_asset_not_runtime_instance" : "loaded_instance",
                    scanned_objects = scanned, complete = complete && cameras.Length <= 32, stop_reason = complete && cameras.Length <= 32 ? null : "limit_or_read_error",
                    failures, bindings, pipeline = Identity(GraphicsSettings.currentRenderPipeline),
                    cameras = cameras.Take(32).Select(c => new { camera = Identity(c), c.enabled, active = c.gameObject.activeInHierarchy,
                        c.depth, c.cullingMask, rect = c.rect.ToString(), c.targetDisplay, target = Identity(c.targetTexture),
                        clear_flags = c.clearFlags.ToString(), background = c.backgroundColor.ToString(), c.orthographic, c.fieldOfView, c.nearClipPlane, c.farClipPlane }).ToArray(),
                    rendered_correctly = (bool?)null,
                    limits = "Configured shared bindings, not GPU draw-call proof. MaterialPropertyBlock, project custom Graphic overrides, SRP renderer features and replacement shaders require separate evidence; no inspector overlay substitutes for the real camera render."
                });
            }
            catch (Exception ex) { return Response.Error("RENDER_INSPECTION_FAILED", new { detail = ex.Message }); }
        }
        private static object Describe(Component component)
        {
            var go = component.gameObject; var renderer = component as Renderer; var graphic = component as Graphic;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(component);
            var materials = new List<object>();
            if (renderer != null)
            {
                var slots = renderer.sharedMaterials;
                for (int i = 0; i < Math.Min(slots.Length, 64); i++) materials.Add(new { slot = "sharedMaterials[" + i + "]", material = MaterialInfo(slots[i]) });
                if (renderer is ParticleSystemRenderer)
                {
                    var property = new SerializedObject(renderer).FindProperty("m_TrailMaterial");
                    materials.Add(new { slot = "particle_trails", property_available = property != null,
                        reference_id = property == null ? null : ObjectIdHelper.GetSerializedReferenceId(property), material = MaterialInfo(property?.objectReferenceValue as Material) });
                }
            }
            else
            {
                var property = new SerializedObject(graphic).FindProperty("m_Material");
                materials.Add(new { slot = "configured_graphic_material", property_available = property != null,
                    reference_id = property == null ? null : ObjectIdHelper.GetSerializedReferenceId(property), material = MaterialInfo(property?.objectReferenceValue as Material), uses_default_when_null = true });
            }
            var canvas = go.GetComponentInParent<Canvas>(true);
            var mods = PrefabUtility.IsPartOfPrefabInstance(component) ? PrefabUtility.GetPropertyModifications(PrefabUtility.GetNearestPrefabInstanceRoot(go)) : null;
            return new {
                component = Identity(component), path = ObjectsHelper.GetGameObjectPath(go), type = component.GetType().FullName,
                active = go.activeInHierarchy, enabled = renderer != null ? renderer.enabled : graphic.enabled, go.layer,
                prefab_source = Identity(source), overrides = mods?.Where(m => m.target == source).Take(64).Select(m => new { m.propertyPath, m.value, object_reference = Identity(m.objectReference) }).ToArray(),
                overrides_complete = mods == null || mods.Count(m => m.target == source) <= 64,
                materials, slots_complete = renderer == null || renderer.sharedMaterials.Length <= 64,
                sorting_layer = renderer != null ? renderer.sortingLayerName : canvas != null ? canvas.sortingLayerName : null,
                sorting_order = renderer != null ? renderer.sortingOrder : canvas != null ? canvas.sortingOrder : 0,
                sprite = Identity(component is SpriteRenderer sr ? sr.sprite : component is Image image ? image.overrideSprite : null),
                raw_texture = Identity(component is RawImage raw ? raw.texture : null),
                canvas = canvas == null ? null : new { identity = Identity(canvas), mode = canvas.renderMode.ToString(), camera = Identity(canvas.worldCamera), canvas.overrideSorting, canvas.targetDisplay },
                masks = go.GetComponentsInParent<Mask>(true).Select(m => new { identity = Identity(m), m.enabled, m.showMaskGraphic }).ToArray(),
                rect_masks = go.GetComponentsInParent<RectMask2D>(true).Select(m => new { identity = Identity(m), m.enabled, padding = m.padding.ToString() }).ToArray(),
                canvas_groups = go.GetComponentsInParent<CanvasGroup>(true).Select(g => new { identity = Identity(g), g.alpha, g.interactable, g.blocksRaycasts }).ToArray()
            };
        }
        private static object MaterialInfo(Material material)
        {
            if (material == null) return null;
            var names = material.GetTexturePropertyNames();
            return new { identity = Identity(material), shader = Identity(material.shader), shader_supported = material.shader != null && material.shader.isSupported,
                material.renderQueue, keywords = material.shaderKeywords.Take(64).ToArray(), keywords_complete = material.shaderKeywords.Length <= 64,
                properties_complete = names.Length <= 64, textures = names.Take(64).Select(n => new { property = n, texture = Identity(material.GetTexture(n)),
                    scale = material.GetTextureScale(n).ToString(), offset = material.GetTextureOffset(n).ToString() }).ToArray() };
        }
        internal static object Identity(Object obj)
        {
            if (obj == null) return null;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long id);
            return new { instance_id = ObjectIdHelper.GetSerializableId(obj), obj.name, type = obj.GetType().FullName,
                asset_path = AssetDatabase.GetAssetPath(obj), guid, local_file_id = id, persistent = EditorUtility.IsPersistent(obj) };
        }
    }
}
