// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using System.Linq;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tests
{
    public sealed class ProjectUIDefaultsTests
    {
        private GameObject root;
        private Scene scene;
        private bool dirty, hadConfig;
        private string originalConfig, folder;
        private Object[] selection;
        [SetUp] public void SetUp()
        {
            folder = null;
            scene = SceneManager.GetActiveScene(); dirty = scene.isDirty; selection = Selection.objects;
            hadConfig = File.Exists(ProjectUIDefaults.ConfigurationPath);
            if (hadConfig) originalConfig = File.ReadAllText(ProjectUIDefaults.ConfigurationPath);
            ProjectUIDefaults.Save(new UIProjectConfiguration());
            root = new GameObject("DefaultsTests_" + Guid.NewGuid().ToString("N"), typeof(RectTransform), typeof(Canvas));
        }
        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(root);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
            if (hadConfig) File.WriteAllText(ProjectUIDefaults.ConfigurationPath, originalConfig);
            else File.Delete(ProjectUIDefaults.ConfigurationPath);
            Selection.objects = selection;
            if (!dirty) typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, new object[] { scene });
        }
        [TestCase(0, 0, true, "tmp")]
        [TestCase(10, 1, true, "legacy")]
        [TestCase(1, 10, true, "tmp")]
        [TestCase(1, 1, true, "unresolved_mixed_project")]
        [TestCase(100, 1, false, "unresolved_incomplete_scan")]
        public void TextConventionRequiresEvidence(int legacy, int tmp, bool complete, string expected)
            => Assert.AreEqual(expected, ProjectUIDefaults.DecideText(legacy, tmp, complete));
        [TestCase("input_system", true, true, false, null)]
        [TestCase("input_system", true, true, true, null)]
        [TestCase("legacy", true, true, true, null)]
        [TestCase("legacy", false, false, true, null)]
        [TestCase("input_system", false, true, false, "INPUT_SYSTEM_NOT_AVAILABLE")]
        [TestCase("input_system", true, false, true, "INPUT_SYSTEM_DISABLED")]
        [TestCase("legacy", true, true, false, "LEGACY_INPUT_DISABLED")]
        public void InputPolicyHonorsPackageAndActiveHandlingWithoutChangingProjectSettings(string policy, bool available, bool modern, bool legacy, string error)
        {
            if (error == null) Assert.DoesNotThrow(() => ProjectUIDefaults.ValidateInputPolicy(policy, available, modern, legacy));
            else Assert.AreEqual(error, Assert.Throws<UIConfigurationException>(() => ProjectUIDefaults.ValidateInputPolicy(policy, available, modern, legacy)).Code);
        }
        [TestCase("{\"typo\":1}")]
        [TestCase("{\"schema_version\":2}")]
        [TestCase("{\"text_component\":\"wrong\"}")]
        [TestCase("{\"input_module\":\"wrong\"}")]
        [TestCase("{\"font_asset\":\"Assets/missing.asset\"}")]
        public void InvalidConfigurationDoesNotOverwrite(string json)
        {
            var before = File.ReadAllBytes(ProjectUIDefaults.ConfigurationPath);
            Assert.IsFalse((bool)JObject.FromObject(UIConfigurationFunctions.ConfigureUiDefaults(json))["success"]);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ProjectUIDefaults.ConfigurationPath));
        }
        [Test] public void ConfigurationRoundTripsBeforeReportingPersisted()
        {
            var result = JObject.FromObject(UIConfigurationFunctions.ConfigureUiDefaults("{\"text_component\":\"legacy\"}"));
            Assert.IsTrue((bool)result["data"]["persisted"]);
            Assert.AreEqual("legacy", ProjectUIDefaults.Load().text_component);
        }
        [Test] public void ExplicitLegacyCreatesNoTmpAndSupportsUndo()
        {
            var result = Create(text_component: "legacy");
            Assert.IsTrue((bool)result["success"], result.ToString());
            var created = ObjectIdHelper.ToObject((string)result["data"]["object_id"]) as GameObject;
            Assert.AreEqual("Hello", created.GetComponent<Text>().text);
            Assert.IsNotNull(created.GetComponent<Text>().font);
            Assert.IsFalse(created.GetComponent<Text>().raycastTarget);
            Assert.AreEqual(0, created.GetComponents<Outline>().Length);
            Undo.PerformUndo(); Assert.IsTrue(created == null);
        }
        [Test] public void InvalidGeometryCreatesNothing()
        {
            var count = root.transform.childCount;
            var result = JObject.FromObject(UIConfigurationFunctions.CreateProjectUi("text", "Bad", parent: ObjectIdHelper.GetSerializableId(root), size: "-1,20", text_component: "legacy"));
            Assert.IsFalse((bool)result["success"]); Assert.AreEqual(count, root.transform.childCount);
        }
        [Test] public void TmpCreationUsesTmpOrReportsMissingResourcesWithoutLegacyFallback()
        {
            var type = ProjectTypeCatalog.Candidates("TMPro.TMP_Settings").SingleOrDefault();
            if (type != null && Resources.Load("TMP Settings", type) != null)
            {
                var created = Create(text_component: "tmp");
                Assert.IsTrue((bool)created["success"], created.ToString());
                Assert.AreEqual("tmp", (string)created["data"]["text_component"]);
                var obj = (GameObject)ObjectIdHelper.ToObject((string)created["data"]["object_id"]);
                Assert.IsNull(obj.GetComponent<Text>()); Assert.IsNotNull(obj.GetComponent(ProjectUIDefaults.TmpType));
                return;
            }
            var count = root.transform.childCount;
            var result = Create(text_component: "tmp");
            Assert.IsFalse((bool)result["success"]);
            CollectionAssert.Contains(new[] { "TMP_RESOURCES_NOT_IMPORTED", "TMP_NOT_AVAILABLE" }, (string)result["code"]);
            Assert.AreEqual(count, root.transform.childCount);
        }
        [Test] public void PrefabTemplateRetainsLayoutReferencesAndTypography()
        {
            var path = MakeTemplate();
            var bytes = File.ReadAllBytes(path);
            var result = JObject.FromObject(UIConfigurationFunctions.CreateProjectUi("button", "Instance", parent: ObjectIdHelper.GetSerializableId(root), template_path: path));
            Assert.IsTrue((bool)result["success"], result.ToString());
            var go = ObjectIdHelper.ToObject((string)result["data"]["object_id"]) as GameObject;
            Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(go));
            Assert.AreEqual(new Vector2(222, 77), ((RectTransform)go.transform).sizeDelta);
            Assert.AreEqual("Authored", go.GetComponentInChildren<Text>().text);
            Assert.AreEqual(27, go.GetComponentInChildren<Text>().fontSize);
            Assert.AreEqual(go.GetComponent<Image>(), go.GetComponent<Button>().targetGraphic);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        }
        [Test] public void TmpMaterialPresetsRetainEffectsAndRejectAnotherAtlasBeforeMutation()
        {
            var font = AssetDatabase.FindAssets("t:TMP_FontAsset").Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadMainAssetAtPath).FirstOrDefault(x => x != null);
            if (font == null) Assert.Ignore("TMP Essential Resources are needed for the positive material fixture.");
            var sourceMaterial = font.GetType().GetField("material").GetValue(font) as Material;
            Assert.IsNotNull(sourceMaterial);
            var suffix = "FunplayFontTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", suffix); folder = "Assets/" + suffix;
            var preset = new Material(sourceMaterial); preset.SetFloat("_OutlineWidth", .2f);
            AssetDatabase.CreateAsset(preset, folder + "/Outline.mat");
            ProjectUIDefaults.Save(new UIProjectConfiguration { text_component = "tmp", font_asset = AssetDatabase.GetAssetPath(font), font_material = folder + "/Outline.mat" });
            var result = Create("tmp"); Assert.IsTrue((bool)result["success"], result.ToString());
            var go = (GameObject)ObjectIdHelper.ToObject((string)result["data"]["object_id"]);
            var text = go.GetComponent(ProjectUIDefaults.TmpType);
            var actualMaterial = (Material)text.GetType().GetProperty("fontSharedMaterial").GetValue(text);
            Assert.AreEqual(ObjectIdHelper.GetSerializableId(preset), ObjectIdHelper.GetSerializableId(actualMaterial));
            Assert.AreEqual(AssetDatabase.GetAssetPath(preset), AssetDatabase.GetAssetPath(actualMaterial));
            Assert.AreEqual(.2f, preset.GetFloat("_OutlineWidth")); Assert.IsNull(go.GetComponent<Outline>());
            var differentAtlas = new Texture2D(16, 16); AssetDatabase.CreateAsset(differentAtlas, folder + "/OtherAtlas.asset");
            var wrong = new Material(preset); wrong.SetTexture("_MainTex", differentAtlas); AssetDatabase.CreateAsset(wrong, folder + "/Wrong.mat");
            var before = File.ReadAllBytes(ProjectUIDefaults.ConfigurationPath);
            var rejected = JObject.FromObject(UIConfigurationFunctions.ConfigureUiDefaults(new JObject { ["text_component"] = "tmp", ["font_asset"] = AssetDatabase.GetAssetPath(font), ["font_material"] = folder + "/Wrong.mat" }.ToString()));
            Assert.IsFalse((bool)rejected["success"]); CollectionAssert.AreEqual(before, File.ReadAllBytes(ProjectUIDefaults.ConfigurationPath));
        }
        [Test] public void TemplateTextTypeIsNeverReplaced()
        {
            var count = root.transform.childCount;
            var result = JObject.FromObject(UIConfigurationFunctions.CreateProjectUi("button", "Instance", parent: ObjectIdHelper.GetSerializableId(root), template_path: MakeTemplate(), text_component: "tmp"));
            Assert.AreEqual("TEMPLATE_TEXT_TYPE_CONFLICT", (string)result["code"]);
            Assert.AreEqual(count + 1, root.transform.childCount); // authoring fixture, not an extra instance
        }
        [Test] public void TemplateExplicitOverridesAreAppliedOnlyToNewInstance()
        {
            var path = MakeTemplate();
            var result = JObject.FromObject(UIConfigurationFunctions.CreateProjectUi("button", "Instance", "Changed", ObjectIdHelper.GetSerializableId(root), path, size: "150,55", font_size: "31"));
            Assert.IsTrue((bool)result["success"], result.ToString());
            var go = (GameObject)ObjectIdHelper.ToObject((string)result["data"]["object_id"]);
            Assert.AreEqual(31, go.GetComponentInChildren<Text>().fontSize);
            Assert.AreEqual("Changed", go.GetComponentInChildren<Text>().text);
            Assert.AreEqual("Authored", AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<Text>().text);
        }
        [Test] public void CanvasCreationPreservesExistingEventSystem()
        {
            var es = new GameObject("ExistingEventSystem", typeof(EventSystem)); es.transform.SetParent(root.transform);
            var count = Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var result = JObject.FromObject(UIConfigurationFunctions.CreateProjectUi("canvas", "NestedCanvas", parent: ObjectIdHelper.GetSerializableId(root)));
            Assert.IsTrue((bool)result["success"], result.ToString());
            Assert.IsTrue((bool)result["data"]["existing_event_system_preserved"]);
            Assert.AreEqual(count, Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
            Assert.AreEqual(0, es.GetComponents<BaseInputModule>().Length);
        }
        [Test] public void AmbiguousLabelsRequireAnExplicitPath()
        {
            var path = MakeTemplate(true);
            var result = JObject.FromObject(UIConfigurationFunctions.CreateProjectUi("button", "Instance", parent: ObjectIdHelper.GetSerializableId(root), template_path: path));
            Assert.AreEqual("AMBIGUOUS_TEMPLATE_TEXT", (string)result["code"]);
            result = JObject.FromObject(UIConfigurationFunctions.CreateProjectUi("button", "Instance", "OnlyLabel", ObjectIdHelper.GetSerializableId(root), path, text_path: "Label"));
            Assert.IsTrue((bool)result["success"], result.ToString());
            var go = (GameObject)ObjectIdHelper.ToObject((string)result["data"]["object_id"]);
            Assert.AreEqual("Secondary", go.transform.Find("Other").GetComponent<Text>().text);
        }
        private JObject Create(string text_component) => JObject.FromObject(UIConfigurationFunctions.CreateProjectUi("text", "Created", "Hello", ObjectIdHelper.GetSerializableId(root), text_component: text_component));
        private string MakeTemplate(bool secondLabel = false)
        {
            if (folder == null) { var name = "FunplayDefaultsTests_" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name; }
            var go = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(Button)); go.transform.SetParent(root.transform);
            ((RectTransform)go.transform).sizeDelta = new Vector2(222, 77);
            go.GetComponent<Button>().targetGraphic = go.GetComponent<Image>();
            var label = new GameObject("Label", typeof(RectTransform), typeof(Text)); label.transform.SetParent(go.transform);
            var text = label.GetComponent<Text>(); text.text = "Authored"; text.fontSize = 27; text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (secondLabel) { var other = Object.Instantiate(label, go.transform); other.name = "Other"; other.GetComponent<Text>().text = "Secondary"; }
            var path = folder + "/Button.prefab"; PrefabUtility.SaveAsPrefabAsset(go, path); return path;
        }
    }
}
