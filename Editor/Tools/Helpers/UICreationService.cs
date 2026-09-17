// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Funplay.Editor.Tools.Builtins;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Funplay.Editor.Tools.Helpers
{
    internal static class UICreationService
    {
        internal static object Create(string kind, string name, string text, string parent, string position, string size,
            string anchor, string pivot, string fontSize, string renderMode, string textPolicy, string templatePath,
            string textPath, string fontPath, string materialPath, string inputPolicy)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return Response.Error("EDIT_MODE_REQUIRED", new { hint = "Author UI in Edit Mode/prefabs. Use a preview session for runtime validation." });
            GameObject created = null, createdEventSystem = null;
            var previousSelection = Selection.activeObject;
            int undoGroup = -1;
            try
            {
                if (!new[] { "canvas", "button", "text" }.Contains(kind) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("kind must be canvas/button/text and name must not be empty.");
                var config = ProjectUIDefaults.Load();
                var configuredTemplate = kind == "canvas" ? config.canvas : kind == "button" ? config.button : config.text;
                var template = !string.IsNullOrEmpty(templatePath) ? new UITemplate { asset_path = templatePath, text_path = textPath } : configuredTemplate;
                if (template != null && textPath != null) template = new UITemplate { asset_path = template.asset_path, text_path = textPath };
                GameObject prefab = template != null ? ProjectUIDefaults.ValidateTemplate(kind, template) : null;
                GameObject parentObject = null;
                if (kind != "canvas" || !string.IsNullOrWhiteSpace(parent))
                {
                    var matches = ObjectsHelper.FindObjects(parent ?? "Canvas", null, true, searchInactive: true);
                    if (matches.Count != 1 || EditorUtility.IsPersistent(matches[0]) || !(matches[0].transform is RectTransform))
                        throw new UIConfigurationException("UI_PARENT_NOT_UNIQUE", "Parent must identify exactly one live RectTransform (prefer its instance ID).");
                    parentObject = matches[0];
                }
                Vector2? requestedPosition = ParseVector(position, "position");
                Vector2? requestedSize = ParseVector(size, "size");
                if (requestedSize.HasValue && (requestedSize.Value.x <= 0 || requestedSize.Value.y <= 0)) throw new ArgumentException("size must be positive.");
                if (!string.IsNullOrEmpty(pivot))
                {
                    var p = ParseVector(pivot, "pivot").Value;
                    if (p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1) throw new ArgumentException("pivot must be in 0..1.");
                }
                ValidateAnchor(anchor);
                float? requestedFontSize = ParseFontSize(fontSize);
                RenderMode? requestedRender = null;
                if (renderMode != null)
                {
                    if (!Enum.TryParse<RenderMode>(renderMode, true, out var mode) || !Enum.IsDefined(typeof(RenderMode), mode)) throw new ArgumentException("Invalid Canvas render_mode.");
                    requestedRender = mode;
                }
                var conventions = (prefab == null && kind != "canvas" && (textPolicy == null || textPolicy == "auto") && config.text_component == "auto") ||
                                  (kind == "canvas" && (inputPolicy == null || inputPolicy == "auto") && config.input_module == "auto")
                    ? ProjectUIDefaults.Probe() : null;
                Component templateLabel = prefab != null && kind != "canvas" ? ProjectUIDefaults.ResolveTemplateText(prefab, template.text_path) : null;
                string resolvedText = kind == "canvas" ? null : templateLabel != null ? (templateLabel is Text ? "legacy" : "tmp") :
                    textPolicy != null && textPolicy != "auto" ? textPolicy : config.text_component != "auto" ? config.text_component : conventions?.recommended_text;
                if (kind != "canvas" && resolvedText != "tmp" && resolvedText != "legacy")
                    throw new UIConfigurationException("UI_CONVENTION_UNRESOLVED", "Text convention is tied or its scan is incomplete. Inspect get_ui_defaults and choose text_component explicitly or configure a template.");
                if (templateLabel != null && textPolicy != null && textPolicy != "auto" && textPolicy != resolvedText)
                    throw new UIConfigurationException("TEMPLATE_TEXT_TYPE_CONFLICT", "Requested text type differs from the template. Choose a matching template; existing components/bindings will not be replaced.");

                UnityEngine.Object font = null; Material material = null;
                bool overrideTemplateFont = !string.IsNullOrEmpty(fontPath);
                bool overrideTemplateMaterial = !string.IsNullOrEmpty(materialPath);
                if (kind != "canvas")
                {
                    var effectiveFontPath = prefab == null ? fontPath ?? config.font_asset : fontPath;
                    font = ResolveFont(resolvedText, effectiveFontPath, conventions, templateLabel);
                    string effectiveMaterialPath = prefab == null ? materialPath ?? config.font_material : materialPath;
                    if (!string.IsNullOrEmpty(effectiveMaterialPath))
                    {
                        material = AssetDatabase.LoadAssetAtPath<Material>(effectiveMaterialPath);
                        if (material == null) throw new UIConfigurationException("FONT_MATERIAL_NOT_FOUND", "font_material must identify a Material asset.");
                        if (resolvedText == "tmp")
                        {
                            ValidateTmpMaterial(font, material);
                        }
                    }
                }
                string resolvedInput = inputPolicy != null && inputPolicy != "auto" ? inputPolicy : config.input_module != "auto" ? config.input_module :
                    conventions?.recommended_input ?? ProjectUIDefaults.DecideInput(0, 0);
                var existingEvents = UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(x => x.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(x) && !EditorSceneManager.IsPreviewScene(x.gameObject.scene)).ToArray();
                var templateEvents = prefab != null ? prefab.GetComponentsInChildren<EventSystem>(true) : Array.Empty<EventSystem>();
                bool prefabStage = PrefabStageUtility.GetCurrentPrefabStage() != null;
                bool needsEventSystem = kind == "canvas" && existingEvents.Length == 0 && templateEvents.Length == 0 && !prefabStage;
                if (kind == "canvas" && existingEvents.Length > 0 && templateEvents.Length > 0)
                    throw new UIConfigurationException("TEMPLATE_EVENT_SYSTEM_CONFLICT", "Canvas template would duplicate an existing scene EventSystem. Use a template without one; the existing module is not replaced.");
                if (needsEventSystem) ProjectUIDefaults.ValidateInputPolicy(resolvedInput);

                Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Create UI " + name);
                if (prefab != null)
                {
                    created = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parentObject != null ? parentObject.transform : null);
                    created.name = name;
                }
                else
                {
                    created = new GameObject(name, typeof(RectTransform)); created.SetActive(false);
                    if (parentObject != null) created.transform.SetParent(parentObject.transform, false);
                }
                Undo.RegisterCreatedObjectUndo(created, "Create UI " + name);
                var rect = (RectTransform)created.transform;
                if (requestedPosition.HasValue) rect.anchoredPosition = requestedPosition.Value;
                else if (prefab == null) rect.anchoredPosition = Vector2.zero;
                if (requestedSize.HasValue) rect.sizeDelta = requestedSize.Value;
                else if (prefab == null && kind != "canvas") rect.sizeDelta = kind == "button" ? new Vector2(160, 40) : new Vector2(300, 60);
                UIFunctions.ApplyAnchorPreset(rect, anchor, pivot);
                Component label = null;
                if (kind == "canvas")
                {
                    var canvas = created.GetComponent<Canvas>();
                    if (canvas == null) canvas = created.AddComponent<Canvas>();
                    if (requestedRender.HasValue || prefab == null) canvas.renderMode = requestedRender ?? RenderMode.ScreenSpaceOverlay;
                    if (created.GetComponent<CanvasScaler>() == null) created.AddComponent<CanvasScaler>();
                    if (created.GetComponent<GraphicRaycaster>() == null) created.AddComponent<GraphicRaycaster>();
                    if (needsEventSystem)
                    {
                        createdEventSystem = new GameObject("EventSystem", typeof(EventSystem));
                        Undo.RegisterCreatedObjectUndo(createdEventSystem, "Create EventSystem");
                        if (resolvedInput == "legacy") createdEventSystem.AddComponent<StandaloneInputModule>();
                        else
                        {
                            var module = createdEventSystem.AddComponent(ProjectUIDefaults.NewInputType);
                            var assign = module.GetType().GetMethod("AssignDefaultActions", BindingFlags.Public | BindingFlags.Instance);
                            if (assign == null) throw new UIConfigurationException("INPUT_SYSTEM_SETUP_UNSUPPORTED", "Input module has no AssignDefaultActions API.");
                            assign.Invoke(module, null);
                        }
                    }
                }
                else
                {
                    if (prefab != null) label = ProjectUIDefaults.ResolveTemplateText(created, template.text_path);
                    else
                    {
                        var textObject = created;
                        if (kind == "button")
                        {
                            created.AddComponent<Image>().color = new Color(.2f, .5f, .9f, 1);
                            var button = created.AddComponent<Button>(); button.targetGraphic = created.GetComponent<Image>();
                            textObject = new GameObject("Label", typeof(RectTransform)); textObject.SetActive(false);
                            textObject.transform.SetParent(created.transform, false);
                            var textRect = (RectTransform)textObject.transform;
                            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one; textRect.sizeDelta = Vector2.zero;
                        }
                        label = resolvedText == "legacy" ? (Component)textObject.AddComponent<Text>() : textObject.AddComponent(ProjectUIDefaults.TmpType);
                        ((Graphic)label).raycastTarget = false; ((Graphic)label).color = Color.white;
                        if (label is Text legacy) legacy.alignment = TextAnchor.MiddleCenter;
                        else Set(label, "alignment", Enum.Parse(label.GetType().GetProperty("alignment").PropertyType, "Center"));
                        if (textObject != created) textObject.SetActive(true);
                    }
                    if (text != null || prefab == null) Set(label, "text", text ?? "");
                    if (prefab == null || overrideTemplateFont || label.GetType().GetProperty("font")?.GetValue(label) == null) Set(label, "font", font);
                    if (requestedFontSize.HasValue || prefab == null)
                        Set(label, "fontSize", label is Text ? (object)Mathf.RoundToInt(requestedFontSize ?? (kind == "button" ? 16 : 20)) : requestedFontSize ?? (kind == "button" ? 16f : 20f));
                    if (material != null && (prefab == null || overrideTemplateMaterial)) Set(label, label is Text ? "material" : "fontSharedMaterial", material);
                    if (prefab != null) PrefabUtility.RecordPrefabInstancePropertyModifications(label);
                }
                if (prefab == null) created.SetActive(true);
                else
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(created);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(rect);
                }
                EditorSceneManager.MarkSceneDirty(created.scene);
                Selection.activeGameObject = created; Undo.CollapseUndoOperations(undoGroup);
                return Response.Success("Created UI " + kind + " '" + name + "'.", new
                {
                    object_id = ObjectIdHelper.GetSerializableId(created), path = ObjectsHelper.GetGameObjectPath(created),
                    template = template?.asset_path, prefab_connection_retained = prefab != null, text_component = resolvedText,
                    text_component_id = ObjectIdHelper.GetSerializableId(label), font_asset = font != null ? AssetDatabase.GetAssetPath(font) : null,
                    created_event_system_id = createdEventSystem != null ? ObjectIdHelper.GetSerializableId(createdEventSystem) : null,
                    existing_event_system_preserved = existingEvents.Length > 0,
                    input_policy = needsEventSystem ? resolvedInput : "existing_or_template_unchanged",
                    rect = new { x = rect.anchoredPosition.x, y = rect.anchoredPosition.y, width = rect.sizeDelta.x, height = rect.sizeDelta.y },
                    persisted = false, note = "Authoring objects created in memory with Undo. Save the intended prefab/scene explicitly. Template label bindings and typography/layout are preserved unless explicitly overridden."
                });
            }
            catch (Exception ex)
            {
                if (undoGroup >= 0) Undo.RevertAllDownToGroup(undoGroup);
                if (created != null) UnityEngine.Object.DestroyImmediate(created);
                if (createdEventSystem != null) UnityEngine.Object.DestroyImmediate(createdEventSystem);
                Selection.activeObject = previousSelection;
                return Response.Error(ex is UIConfigurationException problem ? problem.Code : "UI_CREATION_FAILED", new { detail = ex.GetBaseException().Message });
            }
        }
        private static UnityEngine.Object ResolveFont(string policy, string path, UIConventionReport report, Component existing)
        {
            if (policy == "tmp")
            {
                if (ProjectUIDefaults.TmpType == null) throw new UIConfigurationException("TMP_NOT_AVAILABLE", "Install TextMeshPro for this project, or explicitly choose legacy Text for a legacy project.");
                var settingsType = ProjectTypeCatalog.Candidates("TMPro.TMP_Settings").SingleOrDefault();
                if (settingsType == null || Resources.Load("TMP Settings", settingsType) == null)
                    throw new UIConfigurationException("TMP_RESOURCES_NOT_IMPORTED", "Import TMP Essential Resources in this project first. No objects were created and no dependency was silently substituted.");
            }
            UnityEngine.Object font = !string.IsNullOrEmpty(path) ? AssetDatabase.LoadMainAssetAtPath(path) : existing?.GetType().GetProperty("font")?.GetValue(existing) as UnityEngine.Object;
            if (font == null && string.IsNullOrEmpty(path) && report != null)
                font = report.fonts.OrderByDescending(x => x.Value).Select(x => AssetDatabase.LoadMainAssetAtPath(x.Key)).FirstOrDefault(x => MatchesFont(x, policy));
            if (font == null && string.IsNullOrEmpty(path))
            {
                if (policy == "legacy") font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                else
                {
                    var type = ProjectTypeCatalog.Candidates("TMPro.TMP_Settings").Single();
                    using (var serialized = new SerializedObject(Resources.Load("TMP Settings", type))) font = serialized.FindProperty("m_defaultFontAsset")?.objectReferenceValue;
                }
            }
            if (!MatchesFont(font, policy)) throw new UIConfigurationException("FONT_ASSET_REQUIRED", "Choose a valid " + (policy == "tmp" ? "TMP Font Asset" : "Unity Font") + " for the selected text component.");
            return font;
        }
        internal static bool MatchesFont(UnityEngine.Object font, string policy) => font != null && (policy == "legacy" ? font is Font : font.GetType().FullName == "TMPro.TMP_FontAsset");
        internal static void ValidateTmpMaterial(UnityEngine.Object font, Material material)
        {
            // TMP_Asset.material is a public field in TMP 3.x, not a property.
            var fontMaterial = font?.GetType().GetField("material")?.GetValue(font) as Material;
            if (fontMaterial == null) fontMaterial = font?.GetType().GetProperty("material")?.GetValue(font) as Material;
            if (fontMaterial == null || !material.HasProperty("_MainTex") || !fontMaterial.HasProperty("_MainTex") ||
                material.GetTexture("_MainTex") != fontMaterial.GetTexture("_MainTex"))
                throw new UIConfigurationException("TMP_MATERIAL_FONT_MISMATCH", "Choose a material preset using the selected TMP Font Asset's atlas.");
        }
        private static void Set(Component component, string property, object value) => component.GetType().GetProperty(property).SetValue(component, value);
        private static Vector2? ParseVector(string text, string name)
        {
            if (text == null) return null;
            var parts = text.Split(',');
            if (parts.Length != 2 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)) throw new ArgumentException(name + " must be finite x,y.");
            return new Vector2(x, y);
        }
        private static float? ParseFontSize(string value)
        {
            if (value == null) return null;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || float.IsNaN(size) || size < 1 || size > 512) throw new ArgumentException("font_size must be 1..512.");
            return size;
        }
        private static void ValidateAnchor(string anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return;
            var normalized = anchor.ToLowerInvariant().Replace(" ", "").Replace("_", "-");
            if (!new[] { "top-left", "top-center", "top-right", "middle-left", "center", "middle-right", "bottom-left", "bottom-center", "bottom-right", "stretch-horizontal", "stretch-vertical", "stretch-full" }.Contains(normalized))
                throw new ArgumentException("Unknown anchor preset.");
        }
    }
}
