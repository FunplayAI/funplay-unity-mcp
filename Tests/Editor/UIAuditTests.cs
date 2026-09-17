// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tests
{
    public sealed class UIAuditTests
    {
        private Scene previous, scene;
        private bool previousDirty;
        private readonly List<Object> owned = new List<Object>();
        private UIAuditConfiguration config;
        private string folder;
        [SetUp] public void SetUp()
        {
            previous = SceneManager.GetActiveScene();
            previousDirty = previous.isDirty;
            scene = previous;
            config = new UIAuditConfiguration();
        }
        [TearDown] public void TearDown()
        {
            foreach (var item in owned) if (item != null) Object.DestroyImmediate(item);
            owned.Clear();
            if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            if (!previousDirty && previous.IsValid()) typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, new object[] { previous });
            if (folder != null) { AssetDatabase.DeleteAsset(folder); folder = null; }
        }
        private GameObject Make(string name, params Type[] types)
        {
            var go = new GameObject(name, types);
            owned.Add(go);
            return go;
        }
        private Image Image(Vector4 border)
        {
            var image = Make("Panel", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            var texture = new Texture2D(64, 64); owned.Add(texture);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 64, 64), Vector2.one * .5f, 100, 0, SpriteMeshType.FullRect, border);
            owned.Add(sprite); image.sprite = sprite; image.type = UnityEngine.UI.Image.Type.Sliced;
            return image;
        }
        private List<UIAuditFinding> Inspect(GameObject go, bool live = true) => UIAuditRules.Inspect(go, "Assets/UI/Test.prefab", live, config);
        [Test] public void DestroyedReferencedSpriteIsDistinguishedFromOptionalNull()
        {
            var image = Image(Vector4.one);
            Object.DestroyImmediate(image.sprite);
            Assert.IsTrue(Inspect(image.gameObject).Any(x => x.rule == "missing_reference" && x.property == "m_Sprite"));
            // Image's setter compares Unity fake-null objects and can skip this assignment.
            using (var serialized = new SerializedObject(image))
            {
                serialized.FindProperty("m_Sprite").objectReferenceValue = null;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.IsFalse(Inspect(image.gameObject).Any(x => x.rule == "missing_reference" && x.property == "m_Sprite"));
        }
        [Test] public void MissingComponentSlotIsReportedWithoutRemovingIt()
        {
            var name = "FunplayMissingScriptTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            var source = Make("MissingScript", typeof(RectTransform), typeof(Image));
            var path = folder + "/Missing.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, path);
            var script = MonoScript.FromMonoBehaviour(source.GetComponent<Image>());
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
            Assert.IsNotEmpty(guid);
            System.IO.File.WriteAllText(path, System.IO.File.ReadAllText(path).Replace(guid, "00000000000000000000000000000001"));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var before = System.IO.File.ReadAllBytes(path);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            int missing = go.GetComponents<Component>().Count(x => x == null);
            Assert.Greater(missing, 0, "Fixture must contain a real missing component slot.");
            Assert.AreEqual(missing, Inspect(go).Count(x => x.rule == "missing_script"));
            Assert.AreEqual(missing, go.GetComponents<Component>().Count(x => x == null));
            CollectionAssert.AreEqual(before, System.IO.File.ReadAllBytes(path));
        }
        [Test] public void SlicedWithoutBorderIsDeterministicAndDoesNotModifyImage()
        {
            var image = Image(Vector4.zero);
            var findings = Inspect(image.gameObject);
            Assert.AreEqual(1, findings.Count(x => x.rule == "sliced_without_border"));
            Assert.AreEqual("deterministic", findings.First(x => x.rule == "sliced_without_border").confidence);
            Assert.AreEqual(Vector4.zero, image.sprite.border);
            Assert.AreEqual(UnityEngine.UI.Image.Type.Sliced, image.type);
        }
        [Test] public void OneAxisBorderIsValidAndEffectiveOverrideIsChecked()
        {
            var image = Image(new Vector4(5, 0, 5, 0));
            Assert.IsFalse(Inspect(image.gameObject).Any(x => x.rule == "sliced_without_border"));
            image.overrideSprite = Image(Vector4.zero).sprite;
            Assert.IsTrue(Inspect(image.gameObject).Any(x => x.rule == "sliced_without_border"));
        }
        [Test] public void SimpleImageIsNotMisclassified()
        {
            var image = Image(Vector4.zero); image.type = UnityEngine.UI.Image.Type.Simple;
            Assert.IsFalse(Inspect(image.gameObject).Any(x => x.rule == "sliced_without_border"));
        }
        [Test] public void TransparentBlockerIsContextualAndInactiveOrNonRaycastingIsNotFlagged()
        {
            var image = Image(Vector4.one); image.color = Color.clear;
            Assert.IsTrue(Inspect(image.gameObject).Any(x => x.rule == "transparent_raycast" && x.requires_runtime_validation));
            image.raycastTarget = false; Assert.IsFalse(Inspect(image.gameObject).Any(x => x.rule == "transparent_raycast"));
            image.raycastTarget = true; image.gameObject.SetActive(false);
            Assert.IsFalse(Inspect(image.gameObject).Any(x => x.rule == "transparent_raycast"));
        }
        [Test] public void CanvasGroupRaycastAndIgnoreParentPolicyAreRespected()
        {
            var parent = Make("Group", typeof(RectTransform), typeof(CanvasGroup));
            var image = Image(Vector4.one); image.transform.SetParent(parent.transform);
            var group = parent.GetComponent<CanvasGroup>(); group.alpha = 0;
            Assert.IsTrue(Inspect(image.gameObject).Any(x => x.rule == "transparent_raycast"));
            group.blocksRaycasts = false;
            Assert.IsFalse(Inspect(image.gameObject).Any(x => x.rule == "transparent_raycast"));
            var childGroup = image.gameObject.AddComponent<CanvasGroup>(); childGroup.ignoreParentGroups = true;
            Assert.AreEqual(1, UIAuditRules.EffectiveAlpha(image));
        }
        [Test] public void RendererFadeIsReportedWithoutChangingRaycastPolicy()
        {
            var image = Image(Vector4.one); image.canvasRenderer.SetAlpha(0);
            Assert.IsTrue(Inspect(image.gameObject).Any(x => x.rule == "transparent_raycast"));
            Assert.IsTrue(image.raycastTarget); Assert.AreEqual(0, image.canvasRenderer.GetAlpha());
        }
        [Test] public void RequiredRulesDoNotTreatAllOptionalNullsAsErrors()
        {
            var image = Image(Vector4.one);
            Assert.IsFalse(Inspect(image.gameObject).Any(x => x.rule == "required_reference"));
            config.required_references.Add(new UIRequiredReference { component_type = "UnityEngine.UI.Image", property = "m_Material" });
            Assert.IsTrue(Inspect(image.gameObject).Any(x => x.rule == "required_reference"));
        }
        [Test] public void InputFieldMustHaveTextBinding()
        {
            var go = Make("Input", typeof(RectTransform), typeof(InputField));
            Assert.IsTrue(Inspect(go).Any(x => x.rule == "required_reference"));
        }
        [Test] public void SuppressionRetainsFindingAndReason()
        {
            var image = Image(Vector4.zero);
            config.suppressions.Add(new UIAuditSuppression { rule = "sliced_without_border", asset_prefix = "Assets/UI/", reason = "pending source-art replacement" });
            var finding = Inspect(image.gameObject).First(x => x.rule == "sliced_without_border");
            Assert.IsTrue(finding.suppressed); Assert.IsNotEmpty(finding.suppression_reason);
        }
        [Test] public void ConflictingFittersAreDetectedButSameObjectGroupAndFitterAreNotAutomaticallyAnError()
        {
            var parent = Make("Layout", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            parent.GetComponent<HorizontalLayoutGroup>().childControlWidth = true;
            var child = Make("Child", typeof(RectTransform), typeof(ContentSizeFitter)); child.transform.SetParent(parent.transform);
            child.GetComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            Assert.IsTrue(Inspect(child).Any(x => x.rule == "layout_ownership" && x.severity == "warning"));
            parent.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            Assert.IsFalse(Inspect(parent).Any(x => x.rule == "layout_ownership" && x.severity == "warning"));
            child.AddComponent<LayoutElement>().ignoreLayout = true;
            Assert.IsFalse(Inspect(child).Any(x => x.rule == "layout_ownership"));
        }
        [Test] public void ClipBoundaryIsNotOutsideAndScrollContentIsInformational()
        {
            var mask = Make("Viewport", typeof(RectTransform), typeof(RectMask2D));
            var image = Image(Vector4.one); image.transform.SetParent(mask.transform, false);
            Assert.IsFalse(Inspect(image.gameObject).Any(x => x.rule == "outside_clip"));
            image.rectTransform.sizeDelta = new Vector2(300, 300);
            Assert.IsTrue(Inspect(image.gameObject).Any(x => x.rule == "outside_clip" && x.severity == "warning"));
            mask.AddComponent<ScrollRect>().content = image.rectTransform;
            Assert.IsTrue(Inspect(image.gameObject).Any(x => x.rule == "outside_clip" && x.severity == "info"));
        }
        [Test] public void AssetTextDefersRuntimeLayout()
        {
            var text = Make("Text", typeof(RectTransform), typeof(Text));
            Assert.IsTrue(Inspect(text, false).Any(x => x.rule == "layout_requires_runtime"));
            Assert.IsFalse(Inspect(text, false).Any(x => x.rule == "text_overflow"));
        }
        [TestCase("{\"typo\":true}")]
        [TestCase("{\"suppressions\":[{\"rule\":\"sliced_without_border\"}]}")]
        [TestCase("{\"required_references\":[{}]}")]
        public void InvalidConfigurationFailsClearly(string json) => Assert.Catch(() => UIAuditService.LoadConfiguration(json));
        [TestCase("../Assets")][TestCase("Packages/pkg")][TestCase("/tmp/test.prefab")]
        public void AssetTraversalIsRejected(string path) => Assert.Throws<ArgumentException>(() => UIAuditService.ValidateAssetPath(path));
        [UnityTest] public IEnumerator SceneJobIsBoundedAndCancelable()
        {
            var root = Make("Root", typeof(RectTransform));
            for (int i = 0; i < 5; i++) Make("Child" + i, typeof(RectTransform)).transform.SetParent(root.transform);
            var response = JObject.FromObject(UIAuditService.Start("scene", null, "[\"" + ObjectIdHelper.GetSerializableId(root) + "\"]", "{}", true, 2, 100, 10));
            Assert.IsTrue((bool)response["success"]); var id = (string)response["data"]["job_id"];
            for (int i = 0; i < 100; i++)
            {
                yield return null;
                var data = JObject.FromObject(UIAuditService.Read(id, 0, 100))["data"];
                if ((string)data["status"] == "running") continue;
                Assert.AreEqual("object_limit", (string)data["stop_reason"]); Assert.IsFalse((bool)data["complete"]);
                break;
            }
            response = JObject.FromObject(UIAuditService.Start("scene", null, "[\"" + ObjectIdHelper.GetSerializableId(root) + "\"]", "{}", true, 100, 100, 10));
            Assert.IsTrue((bool)response["success"]); id = (string)response["data"]["job_id"];
            Assert.AreEqual("cancelled", (string)JObject.FromObject(UIAuditService.Cancel(id))["data"]["status"]);
        }
        [UnityTest] public IEnumerator SavedPrefabAndSceneAuditPreservesSourcesAndDirtyLiveScene()
        {
            var suffix = "FunplayAuditTests_" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", suffix); folder = "Assets/" + suffix;
            var go = Make("Prefab", typeof(RectTransform), typeof(Image));
            string prefab = folder + "/Panel.prefab", savedScene = folder + "/Audit.unity";
            PrefabUtility.SaveAsPrefabAsset(go, prefab); EditorSceneManager.SaveScene(scene, savedScene, true);
            go.name = "UnsavedUserEdit"; EditorSceneManager.MarkSceneDirty(scene);
            var beforePrefab = System.IO.File.ReadAllBytes(prefab); var beforeScene = System.IO.File.ReadAllBytes(savedScene);
            var response = JObject.FromObject(UIAuditService.Start("assets", "[\"" + folder + "\"]", null, "{}", true, 1000, 1000, 20));
            var id = (string)response["data"]["job_id"]; bool finished = false;
            for (int i = 0; i < 200; i++)
            {
                yield return null;
                var data = JObject.FromObject(UIAuditService.Read(id, 0, 100))["data"];
                if ((string)data["status"] == "running") continue;
                Assert.IsTrue((bool)data["complete"], data.ToString()); finished = true; break;
            }
            Assert.IsTrue(finished); Assert.IsTrue(scene.isDirty); Assert.AreEqual("UnsavedUserEdit", go.name);
            CollectionAssert.AreEqual(beforePrefab, System.IO.File.ReadAllBytes(prefab));
            CollectionAssert.AreEqual(beforeScene, System.IO.File.ReadAllBytes(savedScene));
        }
    }
}
