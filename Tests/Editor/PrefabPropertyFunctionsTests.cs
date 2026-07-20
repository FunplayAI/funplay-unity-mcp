// Copyright (C) Funplay. Licensed under MIT.

using System;
using Funplay.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Funplay.Editor.Tests
{
    /// <summary>
    /// set_prefab_property / set_prefab_properties edit a .prefab ASSET in place via SerializedObject
    /// WITHOUT opening a prefab stage, so no live-scene layout/TMP/Spine recompute can mutate the graph.
    /// These tests pin the load-bearing guarantee: editing one field changes ONLY that field and never
    /// opens a prefab stage (the open_prefab_stage+SaveAsPrefabAsset three-piece freezes recomputed
    /// RectTransform/font values and can zero Spine references — this path must not).
    /// </summary>
    public sealed class PrefabPropertyFunctionsTests
    {
        private string _prefabPath;

        [SetUp]
        public void SetUp()
        {
            _prefabPath = "Assets/__FunplayPrefabPropTest_" + Guid.NewGuid().ToString("N") + ".prefab";

            var root = new GameObject("Root", typeof(RectTransform));
            var child = new GameObject("Child", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            child.transform.SetParent(root.transform, false);

            var img = child.GetComponent<Image>();
            img.raycastTarget = true;
            img.color = Color.white;
            child.GetComponent<RectTransform>().sizeDelta = new Vector2(123f, 45f);

            PrefabUtility.SaveAsPrefabAsset(root, _prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_prefabPath) && AssetDatabase.LoadAssetAtPath<GameObject>(_prefabPath) != null)
                AssetDatabase.DeleteAsset(_prefabPath);
        }

        [Test]
        public void SetPrefabProperty_ChangesOnlyTargetField_AndOpensNoStage()
        {
            var stageBefore = PrefabStageUtility.GetCurrentPrefabStage();

            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "m_RaycastTarget", "false", "Child");

            AssertSuccess(result);

            // The tool must NOT have opened / switched a prefab stage.
            Assert.AreEqual(stageBefore, PrefabStageUtility.GetCurrentPrefabStage(),
                "set_prefab_property must not open a prefab stage.");

            // Reload the asset from disk and confirm ONLY the target field changed.
            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(_prefabPath);
            var child = reloaded.transform.Find("Child");
            Assert.IsNotNull(child);
            var img = child.GetComponent<Image>();

            Assert.IsFalse(img.raycastTarget, "Target field should be updated.");
            Assert.AreEqual(Color.white, img.color, "Unrelated field (color) must be untouched.");
            Assert.AreEqual(new Vector2(123f, 45f),
                child.GetComponent<RectTransform>().sizeDelta,
                "Unrelated RectTransform sizeDelta must be untouched (no layout freeze).");
        }

        [Test]
        public void SetPrefabProperty_EchoesPostWriteValue()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "m_RaycastTarget", "false", "Child");
            AssertSuccess(result);

            var data = GetProperty<object>(result, "data");
            Assert.AreEqual("m_RaycastTarget", GetProperty<string>(data, "property"));
            var newValue = GetProperty<object>(data, "newValue");
            Assert.IsNotNull(newValue, "Response should echo the post-write value.");
        }

        [Test]
        public void SetPrefabProperties_AppliesMultipleFields()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperties(
                _prefabPath, "Image",
                "{\"m_RaycastTarget\": false, \"m_Color\": {\"r\":1,\"g\":0,\"b\":0,\"a\":1}}",
                "Child");
            AssertSuccess(result);

            var data = GetProperty<object>(result, "data");
            Assert.AreEqual(2, GetProperty<int>(data, "successCount"));
            Assert.AreEqual(0, GetProperty<int>(data, "failCount"));

            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(_prefabPath);
            var img = reloaded.transform.Find("Child").GetComponent<Image>();
            Assert.IsFalse(img.raycastTarget);
            Assert.AreEqual(Color.red, img.color);
        }

        [Test]
        public void SetPrefabProperty_RootPathResolvesRoot()
        {
            // Empty game_object_path targets the prefab root itself.
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "RectTransform", "m_AnchorMin", "{\"x\":0.25,\"y\":0.75}", null);
            AssertSuccess(result);

            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(_prefabPath);
            var rt = reloaded.GetComponent<RectTransform>();
            Assert.AreEqual(new Vector2(0.25f, 0.75f), rt.anchorMin);
        }

        [Test]
        public void SetPrefabProperty_MissingPrefabReturnsStructuredError()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                "Assets/__FunplayDoesNotExist_" + Guid.NewGuid().ToString("N") + ".prefab",
                "Image", "m_RaycastTarget", "false", "Child");
            AssertError(result, "PREFAB_NOT_FOUND");
        }

        [Test]
        public void SetPrefabProperty_MissingGameObjectReturnsStructuredError()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "m_RaycastTarget", "false", "NoSuchChild/Nope");
            AssertError(result, "PREFAB_GAMEOBJECT_NOT_FOUND");
        }

        [Test]
        public void SetPrefabProperty_MissingComponentReturnsStructuredError()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Rigidbody2D", "m_Mass", "1", "Child");
            AssertError(result, "COMPONENT_NOT_FOUND_ON_TARGET");
        }

        [Test]
        public void SetPrefabProperty_MissingPropertyReturnsStructuredError()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "", "false", "Child");
            AssertError(result, "PROPERTY_REQUIRED");
        }

        [Test]
        public void SetPrefabProperties_EmptyObjectReturnsStructuredError()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperties(
                _prefabPath, "Image", "{}", "Child");
            AssertError(result, "PROPERTIES_REQUIRED");
        }

        [Test]
        public void SavePrefabStage_WarnsWhenLayoutDriversPresent()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                Assert.Ignore("A prefab stage is already open in the interactive editor.");
                return;
            }

            // A ContentSizeFitter implements ILayoutSelfController -> recomputes RectTransform in a stage.
            var layoutPrefabPath = "Assets/__FunplayLayoutWarnTest_" + Guid.NewGuid().ToString("N") + ".prefab";
            var root = new GameObject("LayoutRoot", typeof(RectTransform), typeof(ContentSizeFitter));
            PrefabUtility.SaveAsPrefabAsset(root, layoutPrefabPath);
            UnityEngine.Object.DestroyImmediate(root);

            try
            {
                Assert.That(PrefabFunctions.OpenPrefabStage(layoutPrefabPath), Does.Contain("Prefab stage opened"));
                var saveResult = PrefabFunctions.SavePrefabStage();
                Assert.That(saveResult, Does.Contain("⚠"), "Save should warn about layout drivers.");
                Assert.That(saveResult, Does.Contain("set_prefab_property"), "Warning should steer to the safe path.");
                PrefabFunctions.ClosePrefabStage(save: false);
            }
            finally
            {
                if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                    PrefabFunctions.ClosePrefabStage(save: false);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(layoutPrefabPath) != null)
                    AssetDatabase.DeleteAsset(layoutPrefabPath);
            }
        }

        // -------- assertion helpers (mirror StructuredToolReturnsTests) --------

        private static void AssertSuccess(object result)
        {
            Assert.IsTrue(GetProperty<bool>(result, "success"), Describe(result));
        }

        private static void AssertError(object result, string expectedCode)
        {
            Assert.IsFalse(GetProperty<bool>(result, "success"), Describe(result));
            Assert.AreEqual(expectedCode, GetProperty<string>(result, "code"));
        }

        private static T GetProperty<T>(object target, string name)
        {
            Assert.IsNotNull(target);
            var property = target.GetType().GetProperty(name);
            Assert.IsNotNull(property, $"Missing property '{name}' on {target.GetType().FullName}.");
            return (T)property.GetValue(target);
        }

        private static string Describe(object value)
        {
            return value == null ? "null" : value.ToString();
        }
    }
}
