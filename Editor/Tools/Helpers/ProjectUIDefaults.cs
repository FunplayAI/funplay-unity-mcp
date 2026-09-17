// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Funplay.Editor.Tools.Helpers
{
    internal sealed class UITemplate { public string asset_path, text_path; }
    internal sealed class UIProjectConfiguration
    {
        public int schema_version = 1;
        public string text_component = "auto", font_asset, font_material, input_module = "auto";
        public UITemplate canvas, button, text;
    }
    internal sealed class UIConventionReport
    {
        public int legacy_text_count, tmp_text_count, legacy_input_modules, new_input_modules, scanned_prefabs, candidate_prefabs;
        public bool complete;
        public string recommended_text, recommended_input, most_used_font;
        public Dictionary<string, int> fonts = new Dictionary<string, int>();
    }
    internal sealed class UIConfigurationException : Exception
    {
        internal readonly string Code;
        internal UIConfigurationException(string code, string message) : base(message) { Code = code; }
    }

    internal static class ProjectUIDefaults
    {
        internal static readonly string ConfigurationPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "ProjectSettings/FunplayMcp.UI.json");
        internal static Type TmpType => ProjectTypeCatalog.Candidates("TMPro.TextMeshProUGUI", null, true).SingleOrDefault();
        internal static Type NewInputType => ProjectTypeCatalog.Candidates("UnityEngine.InputSystem.UI.InputSystemUIInputModule", null, true).SingleOrDefault();
        internal static UIProjectConfiguration Load() => File.Exists(ConfigurationPath) ? Parse(File.ReadAllText(ConfigurationPath)) : new UIProjectConfiguration();
        internal static UIProjectConfiguration Parse(string json)
        {
            var config = JsonConvert.DeserializeObject<UIProjectConfiguration>(json, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
            if (config == null || config.schema_version != 1) throw new ArgumentException("Expected UI configuration schema_version=1.");
            if (!new[] { "auto", "tmp", "legacy" }.Contains(config.text_component)) throw new ArgumentException("text_component must be auto, tmp or legacy.");
            if (!new[] { "auto", "input_system", "legacy" }.Contains(config.input_module)) throw new ArgumentException("input_module must be auto, input_system or legacy.");
            foreach (var pair in new[] { ("canvas", config.canvas), ("button", config.button), ("text", config.text) })
                if (pair.Item2 != null) ValidateTemplate(pair.Item1, pair.Item2);
            if (!string.IsNullOrEmpty(config.font_asset))
            {
                var font = AssetDatabase.LoadMainAssetAtPath(config.font_asset);
                if (!(config.text_component == "auto" ? UICreationService.MatchesFont(font, "tmp") || UICreationService.MatchesFont(font, "legacy") : UICreationService.MatchesFont(font, config.text_component)))
                    throw new ArgumentException("Configured font_asset must match the selected text type (Font or TMP_FontAsset).");
            }
            if (!string.IsNullOrEmpty(config.font_material) && AssetDatabase.LoadAssetAtPath<Material>(config.font_material) == null) throw new ArgumentException("Configured font_material is not a Material asset.");
            if (!string.IsNullOrEmpty(config.font_asset) && !string.IsNullOrEmpty(config.font_material))
            {
                var font = AssetDatabase.LoadMainAssetAtPath(config.font_asset);
                if (UICreationService.MatchesFont(font, "tmp")) UICreationService.ValidateTmpMaterial(font, AssetDatabase.LoadAssetAtPath<Material>(config.font_material));
            }
            return config;
        }
        internal static void Save(UIProjectConfiguration config)
        {
            string temp = ConfigurationPath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(config, Formatting.Indented));
            if (File.Exists(ConfigurationPath)) File.Replace(temp, ConfigurationPath, null); else File.Move(temp, ConfigurationPath);
        }
        internal static GameObject ValidateTemplate(string kind, UITemplate template)
        {
            UIAuditService.ValidateAssetPath(template.asset_path);
            if (!template.asset_path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("UI templates must be prefab assets.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(template.asset_path);
            if (prefab == null || !(prefab.transform is RectTransform)) throw new ArgumentException("UI template needs a RectTransform root: " + template.asset_path);
            if (kind == "canvas" && prefab.GetComponent<Canvas>() == null) throw new ArgumentException("Canvas template needs a Canvas on its root.");
            if (kind == "button" && prefab.GetComponent<Button>() == null) throw new ArgumentException("Button template needs a Button on its root.");
            if (kind != "canvas") ResolveTemplateText(prefab, template.text_path);
            return prefab;
        }
        internal static Component ResolveTemplateText(GameObject root, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var matches = new List<Transform> { root.transform };
                foreach (var part in path.Trim('/').Split('/'))
                    matches = matches.SelectMany(t => t.Cast<Transform>().Where(child => child.name == part)).ToList();
                if (matches.Count != 1) throw new UIConfigurationException("AMBIGUOUS_TEMPLATE_TEXT", "text_path must identify exactly one label relative to the prefab root.");
                var labels = matches[0].GetComponents<UnityEngine.UI.Graphic>().Where(IsText).Cast<Component>().ToArray();
                if (labels.Length != 1) throw new UIConfigurationException("TEMPLATE_TEXT_NOT_FOUND", "text_path must have exactly one Text/TextMeshProUGUI component.");
                return labels[0];
            }
            var candidates = root.GetComponentsInChildren<Graphic>(true).Where(IsText).Cast<Component>().ToArray();
            if (candidates.Length != 1) throw new UIConfigurationException("AMBIGUOUS_TEMPLATE_TEXT", "Template has zero or multiple text labels. Configure text_path; labels are never guessed or all replaced.");
            return candidates[0];
        }
        private static bool IsText(Graphic graphic) => graphic is Text || graphic.GetType().FullName == "TMPro.TextMeshProUGUI";
        internal static UIConventionReport Probe(int prefabLimit = 200)
        {
            if (prefabLimit < 1 || prefabLimit > 2000) throw new ArgumentException("prefab_scan_limit must be 1..2000.");
            var report = new UIConventionReport();
            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            report.candidate_prefabs = paths.Length;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var path in paths)
            {
                if (report.scanned_prefabs >= prefabLimit || clock.ElapsedMilliseconds >= 1500) break;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) Count(prefab, report, false);
                report.scanned_prefabs++;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene)) foreach (var root in scene.GetRootGameObjects()) Count(root, report, true);
            }
            report.complete = report.scanned_prefabs == report.candidate_prefabs;
            report.recommended_text = DecideText(report.legacy_text_count, report.tmp_text_count, report.complete);
            report.most_used_font = report.fonts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).FirstOrDefault();
            report.recommended_input = DecideInput(report.legacy_input_modules, report.new_input_modules);
            return report;
        }
        private static void Count(GameObject root, UIConventionReport report, bool live)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || live && PrefabUtility.GetCorrespondingObjectFromSource(component) != null) continue;
                if (component is Text legacy) { report.legacy_text_count++; CountFont(legacy.font, report); }
                else if (component.GetType().FullName == "TMPro.TextMeshProUGUI")
                {
                    report.tmp_text_count++; CountFont(component.GetType().GetProperty("font")?.GetValue(component) as UnityEngine.Object, report);
                }
                else if (component is StandaloneInputModule) report.legacy_input_modules++;
                else if (component.GetType().FullName == "UnityEngine.InputSystem.UI.InputSystemUIInputModule") report.new_input_modules++;
            }
        }
        private static void CountFont(UnityEngine.Object font, UIConventionReport report)
        {
            if (font == null) return;
            var path = AssetDatabase.GetAssetPath(font); if (string.IsNullOrEmpty(path)) return;
            report.fonts[path] = report.fonts.TryGetValue(path, out var count) ? count + 1 : 1;
        }
        internal static string DecideText(int legacy, int tmp, bool complete)
        {
            if (!complete) return "unresolved_incomplete_scan";
            if (legacy == 0 && tmp == 0) return "tmp";
            if (legacy == tmp) return "unresolved_mixed_project";
            return tmp > legacy ? "tmp" : "legacy";
        }
        internal static string DecideInput(int legacy, int modern)
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            return "input_system";
#elif ENABLE_LEGACY_INPUT_MANAGER && !ENABLE_INPUT_SYSTEM
            return "legacy";
#else
            if (legacy > modern) return "legacy";
            return NewInputType != null ? "input_system" : "legacy";
#endif
        }
        internal static void ValidateInputPolicy(string policy)
        {
            bool modern = false, legacy = false;
#if ENABLE_INPUT_SYSTEM
            modern = true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            legacy = true;
#endif
            ValidateInputPolicy(policy, NewInputType != null, modern, legacy);
        }
        internal static void ValidateInputPolicy(string policy, bool modernAvailable, bool modernEnabled, bool legacyEnabled)
        {
            if (policy == "input_system" && !modernAvailable) throw new UIConfigurationException("INPUT_SYSTEM_NOT_AVAILABLE", "Install/enable Unity Input System or explicitly choose a supported legacy input policy.");
            if (policy == "input_system" && !modernEnabled) throw new UIConfigurationException("INPUT_SYSTEM_DISABLED", "Active Input Handling does not enable Input System. Change it deliberately and recompile first.");
            if (policy == "legacy" && !legacyEnabled) throw new UIConfigurationException("LEGACY_INPUT_DISABLED", "Active Input Handling does not enable the legacy input manager. Use input_system or change the project setting deliberately.");
            if (policy != "legacy" && policy != "input_system") throw new ArgumentException("Unsupported input module policy.");
        }
    }
}
