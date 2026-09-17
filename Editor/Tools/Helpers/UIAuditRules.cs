// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Funplay.Editor.Tools.Helpers
{
    internal sealed class UIAuditConfiguration
    {
        public List<UIRequiredReference> required_references = new List<UIRequiredReference>();
        public List<UIAuditSuppression> suppressions = new List<UIAuditSuppression>();
    }
    internal sealed class UIRequiredReference { public string component_type, property, asset_prefix; }
    internal sealed class UIAuditSuppression { public string rule, asset_prefix, object_path, reason; }
    internal sealed class UIAuditFinding
    {
        public string rule, severity, confidence, asset_path, object_path, object_id, component_type, property;
        public string message, suggestion, suppression_reason;
        public bool requires_runtime_validation, suppressed;
        public object evidence;
    }

    internal static class UIAuditRules
    {
        internal static readonly string[] RuleNames = {
            "sliced_without_border", "missing_script", "missing_reference", "required_reference",
            "transparent_raycast", "text_overflow", "outside_clip", "layout_ownership", "layout_requires_runtime"
        };

        internal static List<UIAuditFinding> Inspect(GameObject go, string asset, bool live, UIAuditConfiguration config)
        {
            var result = new List<UIAuditFinding>();
            var path = ObjectsHelper.GetGameObjectPath(go);
            var id = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
            Action<string, string, Component, string, string, object, string, bool> add =
                (rule, severity, component, property, message, evidence, suggestion, contextual) =>
                {
                    var finding = new UIAuditFinding
                    {
                        rule = rule, severity = severity, confidence = contextual ? "contextual" : "deterministic",
                        asset_path = asset, object_path = path, object_id = id,
                        component_type = component != null ? component.GetType().FullName : null,
                        property = property, message = message, evidence = evidence, suggestion = suggestion,
                        requires_runtime_validation = contextual
                    };
                    var suppression = config.suppressions.FirstOrDefault(x => x.rule == rule &&
                        (string.IsNullOrEmpty(x.asset_prefix) || asset.StartsWith(x.asset_prefix, StringComparison.Ordinal)) &&
                        (string.IsNullOrEmpty(x.object_path) || path == x.object_path));
                    if (suppression != null) { finding.suppressed = true; finding.suppression_reason = suppression.reason; }
                    result.Add(finding);
                };

            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    add("missing_script", "error", null, null, "Missing script component.", null,
                        "Restore the script or explicitly remove the obsolete component; do not rebuild the prefab.", false);
                    continue;
                }
                // Inspect serialized references without invoking user property getters or applying writes.
                using (var serialized = new SerializedObject(component))
                {
                    var p = serialized.GetIterator();
                    while (p.Next(true))
                    {
                        if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue == null && ObjectIdHelper.GetSerializedReferenceId(p) != "0")
                            add("missing_reference", "error", component, p.propertyPath, "Serialized reference points to a missing object.",
                                new { stored_instance_id = ObjectIdHelper.GetSerializedReferenceId(p) }, "Restore or intentionally clear this reference.", false);
                    }
                    foreach (var rule in config.required_references.Where(x => x.component_type == component.GetType().FullName &&
                        (string.IsNullOrEmpty(x.asset_prefix) || asset.StartsWith(x.asset_prefix, StringComparison.Ordinal))))
                    {
                        var required = serialized.FindProperty(rule.property);
                        if (required == null || required.propertyType != SerializedPropertyType.ObjectReference || required.objectReferenceValue == null)
                            add("required_reference", "error", component, rule.property, "Project-required reference is missing or the rule targets a non-reference field.",
                                new { property_exists = required != null }, "Check the rule and bind the intended project asset/object.", false);
                    }
                    if (component is InputField || component.GetType().FullName == "TMPro.TMP_InputField")
                    {
                        var text = serialized.FindProperty("m_TextComponent");
                        if (text != null && text.objectReferenceValue == null)
                            add("required_reference", "error", component, text.propertyPath, "Input field has no text component.", null,
                                "Bind its text component and viewport in the prefab before enabling it; verify caret/selection in Play Mode.", false);
                    }
                }

                if (component is Image image && image.type == Image.Type.Sliced)
                {
                    var sprite = image.overrideSprite;
                    if (sprite != null && sprite.border == Vector4.zero)
                        add("sliced_without_border", "error", image, "m_Sprite", "Sliced Image uses a Sprite with a zero Border.",
                            new { sprite = sprite.name, sprite_path = AssetDatabase.GetAssetPath(sprite), border = new[] { 0f, 0f, 0f, 0f }, packed = sprite.packed },
                            "Inspect source art and configure its Single/Multiple Sprite border, reimport, then verify Image readback and resizing. Do not invent uniform border values.", false);
                }
                if (component is Graphic graphic)
                {
                    if (live && graphic.isActiveAndEnabled && graphic.raycastTarget && RaycastsAllowed(graphic.transform) && EffectiveAlpha(graphic) <= .001f)
                        add("transparent_raycast", "warning", graphic, "m_RaycastTarget", "A transparent active Graphic can still receive UI raycasts.",
                            new { alpha = EffectiveAlpha(graphic), graphic.raycastTarget },
                            "Confirm whether this is an intentional hit area/modal blocker; use raycast_at_point before disabling it.", true);
                    if (live && graphic.isActiveAndEnabled)
                    {
                        var rect = graphic.rectTransform.rect;
                        bool overflow = false;
                        object measurements = null;
                        if (graphic is Text text && text.font != null && !string.IsNullOrEmpty(text.text))
                        {
                            overflow = text.preferredHeight > rect.height + 1 || (text.horizontalOverflow == HorizontalWrapMode.Overflow && text.preferredWidth > rect.width + 1);
                            measurements = new { width = rect.width, height = rect.height, preferred_width = text.preferredWidth, preferred_height = text.preferredHeight };
                        }
                        else if (graphic.GetType().FullName == "TMPro.TextMeshProUGUI")
                        {
                            var type = graphic.GetType();
                            // These are TMP's documented read-only layout observations, not arbitrary project getters.
                            overflow = (bool)(type.GetProperty("isTextOverflowing")?.GetValue(graphic) ?? false);
                            measurements = new { width = rect.width, height = rect.height, first_overflow_character = type.GetProperty("firstOverflowCharacterIndex")?.GetValue(graphic) };
                        }
                        if (overflow) add("text_overflow", "warning", graphic, null, "Text exceeds its current layout container.", measurements,
                            "Compare with the design and actual localized/runtime text; truncation or scrolling may be intentional.", true);
                        InspectClip(graphic, add);
                    }
                    else if (!live && (graphic is Text || graphic.GetType().FullName == "TMPro.TextMeshProUGUI"))
                        add("layout_requires_runtime", "info", graphic, null, "Prefab/saved-scene text geometry is not evaluated as a live Canvas.", null,
                            "Audit an instantiated, laid-out UI with representative data and target resolution.", true);
                }
            }
            InspectLayout(go, add);
            return result;
        }

        private static void InspectLayout(GameObject go, Action<string, string, Component, string, string, object, string, bool> add)
        {
            var rect = go.transform as RectTransform;
            if (rect == null) return;
            var fitter = go.GetComponent<ContentSizeFitter>();
            var aspect = go.GetComponent<AspectRatioFitter>();
            var parentGroup = rect.parent != null ? rect.parent.GetComponent<LayoutGroup>() : null;
            var element = go.GetComponent<LayoutElement>();
            if (parentGroup != null && parentGroup.enabled && !(element != null && element.ignoreLayout))
            {
                bool controlsWidth = parentGroup is GridLayoutGroup || parentGroup is HorizontalOrVerticalLayoutGroup hv && hv.childControlWidth;
                bool controlsHeight = parentGroup is GridLayoutGroup || parentGroup is HorizontalOrVerticalLayoutGroup vh && vh.childControlHeight;
                bool conflict = fitter != null && fitter.enabled &&
                    (controlsWidth && fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained || controlsHeight && fitter.verticalFit != ContentSizeFitter.FitMode.Unconstrained);
                conflict |= aspect != null && aspect.enabled && aspect.aspectMode != AspectRatioFitter.AspectMode.None;
                add("layout_ownership", conflict ? "warning" : "info", parentGroup, null,
                    conflict ? "Parent layout and a child fitter may drive the same RectTransform." : "Parent LayoutGroup owns this child's position; controlled dimensions override manual sizing.",
                    new { parent = ObjectsHelper.GetGameObjectPath(parentGroup.gameObject), controls_width = controlsWidth, controls_height = controlsHeight, driven_by = rect.drivenByObject != null ? rect.drivenByObject.GetType().FullName : null },
                    "Use the owning layout's padding/spacing or a LayoutElement; do not repeatedly write driven coordinates. Verify intentional fitter arrangements.", conflict);
            }
            if (fitter != null && fitter.enabled && aspect != null && aspect.enabled && aspect.aspectMode != AspectRatioFitter.AspectMode.None)
                add("layout_ownership", "warning", fitter, null, "ContentSizeFitter and AspectRatioFitter can compete for size.", null,
                    "Assign one owner to each size axis and verify the intended layout.", true);
        }

        private static void InspectClip(Graphic graphic, Action<string, string, Component, string, string, object, string, bool> add)
        {
            if (!(graphic is MaskableGraphic maskable) || !maskable.maskable) return;
            var corners = new Vector3[4]; graphic.rectTransform.GetWorldCorners(corners);
            for (var parent = graphic.transform.parent; parent != null; parent = parent.parent)
            {
                var mask = parent.GetComponent<RectMask2D>();
                if (mask == null || !mask.isActiveAndEnabled) continue;
                var rect = mask.rectTransform.rect;
                var padding = mask.padding;
                rect.xMin += padding.x; rect.yMin += padding.y; rect.xMax -= padding.z; rect.yMax -= padding.w;
                int outside = corners.Count(p => {
                    var local = mask.rectTransform.InverseTransformPoint(p);
                    return local.x < rect.xMin - .5f || local.x > rect.xMax + .5f || local.y < rect.yMin - .5f || local.y > rect.yMax + .5f;
                });
                if (outside == 0) continue;
                var scroll = mask.GetComponentInParent<ScrollRect>();
                bool scrollingContent = scroll != null && scroll.content != null && graphic.transform.IsChildOf(scroll.content);
                add("outside_clip", scrollingContent ? "info" : "warning", graphic, null,
                    scrollingContent ? "Graphic extends beyond a scrolling viewport (often intentional)." : "Graphic extends beyond an active RectMask2D.",
                    new { mask = ObjectsHelper.GetGameObjectPath(mask.gameObject), corners_outside = outside, scrolling_content = scrollingContent },
                    "Compare visible clipping with the design at runtime; do not expand scroll viewports merely to remove this finding.", true);
                break;
            }
        }
        internal static float EffectiveAlpha(Graphic graphic)
        {
            float alpha = graphic.color.a * graphic.canvasRenderer.GetAlpha();
            for (var t = graphic.transform; t != null; t = t.parent)
            {
                var groups = t.GetComponents<CanvasGroup>(); bool stop = false;
                foreach (var group in groups.Where(x => x.enabled)) { alpha *= group.alpha; stop |= group.ignoreParentGroups; }
                if (stop) break;
            }
            return alpha;
        }
        private static bool RaycastsAllowed(Transform transform)
        {
            for (var t = transform; t != null; t = t.parent)
            {
                bool stop = false;
                foreach (var group in t.GetComponents<CanvasGroup>().Where(x => x.enabled))
                { if (!group.blocksRaycasts) return false; stop |= group.ignoreParentGroups; }
                if (stop) break;
            }
            return true;
        }
    }
}
