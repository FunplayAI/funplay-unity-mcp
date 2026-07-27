// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Funplay.Editor.Tests
{
    public sealed class PrefabPropertyFunctionsTests
    {
        private readonly List<string> _assetPaths = new List<string>();
        private string _prefabPath;

        [SetUp]
        public void SetUp()
        {
            _assetPaths.Clear();
            _prefabPath = NewAssetPath("PrefabProp", ".prefab");

            var root = new GameObject("Root", typeof(RectTransform));
            var child = new GameObject("Child", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            child.transform.SetParent(root.transform, false);

            var image = child.GetComponent<Image>();
            image.raycastTarget = true;
            image.color = Color.white;
            child.GetComponent<RectTransform>().sizeDelta = new Vector2(123f, 45f);

            Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(root, _prefabPath));
            UnityEngine.Object.DestroyImmediate(root);
        }

        [TearDown]
        public void TearDown()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && _assetPaths.Contains(stage.assetPath))
                PrefabFunctions.ClosePrefabStage(save: false);

            for (var index = _assetPaths.Count - 1; index >= 0; index--)
            {
                if (AssetDatabase.LoadMainAssetAtPath(_assetPaths[index]) != null)
                    AssetDatabase.DeleteAsset(_assetPaths[index]);
            }
        }

        [Test]
        public void SetPrefabProperty_PersistsTargetAndPreservesUnrelatedFieldsWithoutOpeningStage()
        {
            var stageBefore = PrefabStageUtility.GetCurrentPrefabStage();

            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "m_RaycastTarget", "false", "Child");

            AssertSuccess(result);
            Assert.AreEqual(stageBefore, PrefabStageUtility.GetCurrentPrefabStage());

            var reloaded = ForceReloadPrefab(_prefabPath);
            var child = reloaded.transform.Find("Child");
            Assert.IsNotNull(child);
            var image = child.GetComponent<Image>();

            Assert.IsFalse(image.raycastTarget);
            Assert.AreEqual(Color.white, image.color);
            Assert.AreEqual(new Vector2(123f, 45f), child.GetComponent<RectTransform>().sizeDelta);
        }

        [Test]
        public void SetPrefabProperty_ReturnsPersistedReadbackAndSelectorMetadata()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "m_RaycastTarget", "false", "Child");
            AssertSuccess(result);

            var data = GetProperty<object>(result, "data");
            Assert.AreEqual(_prefabPath, GetProperty<string>(data, "prefabPath"));
            Assert.AreEqual("Child", GetProperty<string>(data, "gameObjectPath"));
            Assert.AreEqual(0, GetProperty<int>(data, "gameObjectIndex"));
            Assert.AreEqual(0, GetProperty<int>(data, "componentIndex"));
            Assert.AreEqual("m_RaycastTarget", GetProperty<string>(data, "property"));
            Assert.IsNotNull(GetProperty<object>(data, "newValue"));
        }

        [Test]
        public void SetPrefabProperties_AppliesMultipleFieldsAndReadsThemBack()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperties(
                _prefabPath,
                "UnityEngine.UI.Image",
                "{\"m_RaycastTarget\": false, \"m_Color\": {\"r\":1,\"g\":0,\"b\":0,\"a\":1}}",
                "Child");
            AssertSuccess(result);

            var data = GetProperty<object>(result, "data");
            Assert.AreEqual(2, GetProperty<int>(data, "successCount"));
            Assert.AreEqual(0, GetProperty<int>(data, "failCount"));

            var image = ForceReloadPrefab(_prefabPath).transform.Find("Child").GetComponent<Image>();
            Assert.IsFalse(image.raycastTarget);
            Assert.AreEqual(Color.red, image.color);
        }

        [Test]
        public void SetPrefabProperty_NormalizesPublicFieldNameToSerializedField()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "RectTransform", "anchorMin", "{\"x\":0.25,\"y\":0.75}");
            AssertSuccess(result);

            var rectTransform = ForceReloadPrefab(_prefabPath).GetComponent<RectTransform>();
            Assert.AreEqual(new Vector2(0.25f, 0.75f), rectTransform.anchorMin);
        }

        [Test]
        public void SetPrefabProperty_ResolvesBaseComponentTypeOnDerivedComponent()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Transform", "m_LocalScale", "{\"x\":2,\"y\":3,\"z\":4}");
            AssertSuccess(result);

            Assert.AreEqual(new Vector3(2f, 3f, 4f), ForceReloadPrefab(_prefabPath).transform.localScale);
        }

        [Test]
        public void SetPrefabProperty_RejectsReflectionOnlyMembers()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "name", "\"Renamed\"", "Child");

            AssertError(result, "PREFAB_PROPERTY_SET_FAILED");
            Assert.AreEqual("Child", ForceReloadPrefab(_prefabPath).transform.Find("Child").name);
        }

        [Test]
        public void SetPrefabProperties_AllFieldsFail_ReturnsErrorWithoutSaving()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperties(
                _prefabPath, "Image", "{\"notAField\": 1, \"alsoMissing\": true}", "Child");

            AssertError(result, "PREFAB_PROPERTIES_SET_FAILED");
            Assert.IsTrue(ForceReloadPrefab(_prefabPath).transform.Find("Child").GetComponent<Image>().raycastTarget);
        }

        [TestCase("../Outside.prefab")]
        [TestCase("Assets/../Outside.prefab")]
        [TestCase("Assets\\Bad.prefab")]
        [TestCase("Packages/com.example/Bad.prefab")]
        [TestCase("/tmp/Bad.prefab")]
        [TestCase("Assets/NotAPrefab.asset")]
        public void SetPrefabProperty_RejectsNonNormalizedOrNonPrefabPaths(string path)
        {
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                path, "Image", "m_RaycastTarget", "false", "Child");
            AssertError(result, "INVALID_PREFAB_PATH");
        }

        [Test]
        public void SetPrefabProperty_ReadOnlyPrefabReturnsStructuredError()
        {
            var fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, _prefabPath);
            var originalAttributes = File.GetAttributes(fullPath);
            try
            {
                File.SetAttributes(fullPath, originalAttributes | FileAttributes.ReadOnly);
                var result = ComponentPropertyFunctions.SetPrefabProperty(
                    _prefabPath, "Image", "m_RaycastTarget", "false", "Child");
                AssertError(result, "PREFAB_NOT_EDITABLE");
            }
            finally
            {
                File.SetAttributes(fullPath, originalAttributes);
            }
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
        public void SetPrefabProperty_DuplicateHierarchyPathRequiresExplicitIndex()
        {
            var path = NewAssetPath("DuplicatePath", ".prefab");
            var root = new GameObject("Root");
            for (var index = 0; index < 2; index++)
            {
                var child = new GameObject("Duplicate", typeof(AudioSource));
                child.transform.SetParent(root.transform, false);
            }
            Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(root, path));
            UnityEngine.Object.DestroyImmediate(root);

            var ambiguous = ComponentPropertyFunctions.SetPrefabProperty(
                path, "AudioSource", "m_Volume", "0.25", "Duplicate");
            AssertError(ambiguous, "AMBIGUOUS_PREFAB_GAMEOBJECT");

            var selected = ComponentPropertyFunctions.SetPrefabProperty(
                path, "AudioSource", "m_Volume", "0.25", "Duplicate", game_object_index: 1);
            AssertSuccess(selected);

            var reloaded = ForceReloadPrefab(path);
            Assert.AreEqual(1f, reloaded.transform.GetChild(0).GetComponent<AudioSource>().volume);
            Assert.AreEqual(0.25f, reloaded.transform.GetChild(1).GetComponent<AudioSource>().volume);
        }

        [Test]
        public void SetPrefabProperty_DuplicateComponentsRequireExplicitIndex()
        {
            var path = NewAssetPath("DuplicateComponent", ".prefab");
            var root = new GameObject("Root");
            root.AddComponent<AudioSource>();
            root.AddComponent<AudioSource>();
            Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(root, path));
            UnityEngine.Object.DestroyImmediate(root);

            var ambiguous = ComponentPropertyFunctions.SetPrefabProperty(
                path, "AudioSource", "m_Volume", "0.4");
            AssertError(ambiguous, "AMBIGUOUS_PREFAB_COMPONENT");

            var selected = ComponentPropertyFunctions.SetPrefabProperty(
                path, "AudioSource", "m_Volume", "0.4", component_index: 1);
            AssertSuccess(selected);

            var sources = ForceReloadPrefab(path).GetComponents<AudioSource>();
            Assert.AreEqual(1f, sources[0].volume);
            Assert.AreEqual(0.4f, sources[1].volume);
        }

        [Test]
        public void SetPrefabProperty_PersistsAssetObjectReference()
        {
            var materialPath = NewAssetPath("Material", ".mat");
            var shader = Shader.Find("UI/Default");
            Assert.IsNotNull(shader, "UI/Default shader is required for this test.");
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, materialPath);

            var value = "{\"assetPath\":\"" + materialPath + "\"}";
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "m_Material", value, "Child");
            AssertSuccess(result);

            var image = ForceReloadPrefab(_prefabPath).transform.Find("Child").GetComponent<Image>();
            Assert.AreEqual(materialPath, AssetDatabase.GetAssetPath(image.material));
        }

        [Test]
        public void SetPrefabProperty_PersistsVariantOverrideWithoutChangingBase()
        {
            var variantPath = NewAssetPath("Variant", ".prefab");
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(_prefabPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            Assert.IsNotNull(instance);
            Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(instance, variantPath));
            UnityEngine.Object.DestroyImmediate(instance);

            var result = ComponentPropertyFunctions.SetPrefabProperty(
                variantPath, "Image", "m_RaycastTarget", "false", "Child");
            AssertSuccess(result);

            Assert.IsFalse(ForceReloadPrefab(variantPath).transform.Find("Child").GetComponent<Image>().raycastTarget);
            Assert.IsTrue(ForceReloadPrefab(_prefabPath).transform.Find("Child").GetComponent<Image>().raycastTarget);
        }

        [Test]
        public void SetPrefabProperty_PersistsNestedPrefabOverrideWithoutChangingNestedAsset()
        {
            var containerPath = NewAssetPath("NestedContainer", ".prefab");
            var container = new GameObject("Container");
            var nestedAsset = AssetDatabase.LoadAssetAtPath<GameObject>(_prefabPath);
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(nestedAsset);
            Assert.IsNotNull(nested);
            nested.transform.SetParent(container.transform, false);
            Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(container, containerPath));
            UnityEngine.Object.DestroyImmediate(container);

            var nestedChildPath = nestedAsset.name + "/Child";
            var result = ComponentPropertyFunctions.SetPrefabProperty(
                containerPath, "Image", "m_RaycastTarget", "false", nestedChildPath);
            AssertSuccess(result);

            Assert.IsFalse(ForceReloadPrefab(containerPath).transform.Find(nestedChildPath).GetComponent<Image>().raycastTarget);
            Assert.IsTrue(ForceReloadPrefab(_prefabPath).transform.Find("Child").GetComponent<Image>().raycastTarget);
        }

        [Test]
        public void SetPrefabProperty_InvalidIndexesReturnStructuredErrors()
        {
            var negative = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "m_RaycastTarget", "false", "Child", game_object_index: -2);
            AssertError(negative, "INVALID_PARAM");

            var outOfRange = ComponentPropertyFunctions.SetPrefabProperty(
                _prefabPath, "Image", "m_RaycastTarget", "false", "Child", component_index: 2);
            AssertError(outOfRange, "PREFAB_COMPONENT_INDEX_OUT_OF_RANGE");
        }

        [Test]
        public void SetPrefabProperties_EmptyObjectReturnsStructuredError()
        {
            var result = ComponentPropertyFunctions.SetPrefabProperties(
                _prefabPath, "Image", "{}", "Child");
            AssertError(result, "PROPERTIES_REQUIRED");
        }

        [Test]
        public void PrefabAssetSetters_AreAvailableInDefaultCoreProfile()
        {
            Assert.IsTrue(MCPToolExportPolicy.DefaultCoreTools.Contains("set_prefab_property"));
            Assert.IsTrue(MCPToolExportPolicy.DefaultCoreTools.Contains("set_prefab_properties"));
        }

        [Test]
        public void SavePrefabStage_WarnsWhenLayoutDriversPresent()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                Assert.Ignore("A prefab stage is already open in the interactive editor.");

            var path = NewAssetPath("LayoutWarning", ".prefab");
            var root = new GameObject("LayoutRoot", typeof(RectTransform), typeof(ContentSizeFitter));
            Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(root, path));
            UnityEngine.Object.DestroyImmediate(root);

            Assert.That(PrefabFunctions.OpenPrefabStage(path), Does.Contain("Prefab stage opened"));
            var saveResult = PrefabFunctions.SavePrefabStage();
            Assert.That(saveResult, Does.Contain("WARNING:"));
            Assert.That(saveResult, Does.Contain("set_prefab_property"));
            PrefabFunctions.ClosePrefabStage(save: false);
        }

        private string NewAssetPath(string label, string extension)
        {
            var path = "Assets/__Funplay" + label + "Test_" + Guid.NewGuid().ToString("N") + extension;
            _assetPaths.Add(path);
            return path;
        }

        private static GameObject ForceReloadPrefab(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(prefab, "Expected prefab to reload from " + path);
            return prefab;
        }

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
            Assert.IsNotNull(property, "Missing property '" + name + "' on " + target.GetType().FullName + ".");
            return (T)property.GetValue(target);
        }

        private static string Describe(object value)
        {
            return value == null ? "null" : value.ToString();
        }
    }
}
