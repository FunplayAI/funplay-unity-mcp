// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Funplay.Editor.Tests
{
    public sealed class CapabilityFunctionsTests
    {
        private const string TempFolder = "Assets/__FunplayCapabilityTests";

        [Test]
        public void CapabilitySurface_IncludesSafeToolsAndExcludesRiskyGlobalTools()
        {
            ToolRegistry.ScanAssemblies();

            Assert.IsNotNull(ToolRegistry.GetMethod("get_asset_import_settings"));
            Assert.IsNotNull(ToolRegistry.GetMethod("find_references"));
            Assert.IsNotNull(ToolRegistry.GetMethod("physics_raycast"));
            Assert.IsNotNull(ToolRegistry.GetMethod("get_project_settings"));
            Assert.IsNull(ToolRegistry.GetMethod("get_editor_pref"));
            Assert.IsNull(ToolRegistry.GetMethod("dismiss_dialog"));
        }

        [Test]
        public void AssetImportSettings_ValidateWholeRequestBeforeReimport()
        {
            EnsureFolder(TempFolder);
            var assetPath = TempFolder + "/import-test.png";
            var absolutePath = Path.GetFullPath(assetPath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            try
            {
                texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
                texture.Apply();
                File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                Assert.IsNotNull(importer);
                var originalSize = importer.maxTextureSize;

                var invalid = AssetImportFunctions.SetAssetImportSettings(
                    assetPath,
                    "{\"maxTextureSize\":64,\"unknownField\":true}");
                AssertError(invalid, "INVALID_SETTINGS");
                Assert.AreEqual(originalSize, ((TextureImporter)AssetImporter.GetAtPath(assetPath)).maxTextureSize);

                var valid = AssetImportFunctions.SetAssetImportSettings(
                    assetPath,
                    "{\"maxTextureSize\":64,\"isReadable\":true}");
                AssertSuccess(valid);

                importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                Assert.AreEqual(64, importer.maxTextureSize);
                Assert.IsTrue(importer.isReadable);
                AssertSuccess(AssetImportFunctions.GetAssetImportSettings(assetPath));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                DeleteTempFolder();
            }
        }

        [Test]
        public void MeshPhysicsParticleAndTimelineTools_ExerciseSceneObjects()
        {
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var collider2DObject = new GameObject("CapabilityCollider2D_" + Guid.NewGuid().ToString("N"));
            var particleObject = new GameObject("CapabilityParticle_" + Guid.NewGuid().ToString("N"));
            var noDirector = new GameObject("CapabilityNoDirector_" + Guid.NewGuid().ToString("N"));

            try
            {
                cube.name = "CapabilityCube_" + Guid.NewGuid().ToString("N");
                var meshResult = MeshFunctions.GetMeshInfo(cube.name);
                AssertSuccess(meshResult);
                var meshData = GetProperty<object>(meshResult, "data");
                var meshes = GetProperty<List<object>>(meshData, "meshes");
                var mesh = cube.GetComponent<MeshFilter>().sharedMesh;
                var meshId = GetProperty<string>(meshes[0], "instanceId");
                Assert.That(meshId, Is.EqualTo(ObjectIdHelper.GetSerializableId(mesh)));
                Assert.That(ObjectIdHelper.ToObject(meshId), Is.SameAs(mesh));
                AssertSuccess(PhysicsQueryFunctions.PhysicsRaycast("0,0,-5", "0,0,1", 20f));
                AssertSuccess(PhysicsQueryFunctions.PhysicsOverlap("0,0,0", "box", size: "2,2,2"));
                AssertError(PhysicsQueryFunctions.PhysicsRaycast("NaN,0,0", "0,0,1"), "INVALID_PARAM");

                collider2DObject.transform.position = new Vector3(3f, 4f, 0f);
                collider2DObject.AddComponent<BoxCollider2D>();
                AssertSuccess(PhysicsQueryFunctions.Physics2DOverlapPoint("3,4"));

                var particle = particleObject.AddComponent<ParticleSystem>();
                AssertSuccess(ParticleFunctions.ParticleControl(particleObject.name, "simulate", 0.1f));
                Assert.That(particle.time, Is.EqualTo(0.1f).Within(0.02f));
                AssertError(ParticleFunctions.ParticleControl(particleObject.name, "simulate", float.NaN), "INVALID_PARAM");

                AssertError(TimelineFunctions.DirectorEvaluate(noDirector.name), "PLAYABLE_DIRECTOR_NOT_FOUND");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
                UnityEngine.Object.DestroyImmediate(collider2DObject);
                UnityEngine.Object.DestroyImmediate(particleObject);
                UnityEngine.Object.DestroyImmediate(noDirector);
                RestoreSceneDirtiness(scene, wasDirty);
            }
        }

        [Test]
        public void ComponentBatchTools_CopyPasteAndAddWithUndoTracking()
        {
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            var source = new GameObject("CapabilitySource_" + Guid.NewGuid().ToString("N"));
            var target = new GameObject("CapabilityTarget_" + Guid.NewGuid().ToString("N"));
            var sourceCollider = source.AddComponent<BoxCollider>();
            var targetCollider = target.AddComponent<BoxCollider>();
            sourceCollider.size = new Vector3(2f, 3f, 4f);

            try
            {
                AssertSuccess(ComponentBatchFunctions.CopyComponent(source.name, "BoxCollider"));
                AssertSuccess(ComponentBatchFunctions.PasteComponentValues(target.name, "BoxCollider"));
                Assert.AreEqual(sourceCollider.size, targetCollider.size);

                AssertSuccess(ComponentBatchFunctions.AddComponentToMany(target.name, "Rigidbody"));
                Assert.IsNotNull(target.GetComponent<Rigidbody>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(target);
                RestoreSceneDirtiness(scene, wasDirty);
            }
        }

        [Test]
        public void MaterialAndLightingSetters_ValidateBeforeMutationAndReadBack()
        {
            EnsureFolder(TempFolder);
            var materialPath = TempFolder + "/capability.mat";
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            Assert.IsNotNull(shader, "A test shader with a color property is required.");
            var material = new Material(shader);

            var originalAmbientMode = RenderSettings.ambientMode;
            var requestedMode = originalAmbientMode == UnityEngine.Rendering.AmbientMode.Flat ? "Skybox" : "Flat";
            try
            {
                AssetDatabase.CreateAsset(material, materialPath);
                var colorProperty = material.HasProperty("_Color") ? "_Color" : "_BaseColor";
                Assert.IsTrue(material.HasProperty(colorProperty));

                AssertError(MaterialFunctions.SetMaterialProperty(materialPath, colorProperty, "NaN,0,0,1", "color"), "INVALID_PARAM");
                AssertSuccess(MaterialFunctions.SetMaterialProperty(materialPath, colorProperty, "0.2,0.4,0.6,1", "color"));
                AssertSuccess(MaterialFunctions.GetMaterialProperties(materialPath));

                var invalidLighting = LightingFunctions.SetLightingSettings(
                    ambient_mode: requestedMode,
                    fog_color: "not-a-color");
                AssertError(invalidLighting, "INVALID_PARAM");
                Assert.AreEqual(originalAmbientMode, RenderSettings.ambientMode,
                    "An invalid later parameter must not leave ambientMode partially changed.");
                AssertSuccess(LightingFunctions.GetLightingSettings());
            }
            finally
            {
                DeleteTempFolder();
            }
        }

        [UnityTest]
        public IEnumerator FindReferences_FindsPrefabAndHonorsScanLimit()
        {
            EnsureFolder(TempFolder);
            var materialPath = TempFolder + "/referenced.mat";
            var prefabPath = TempFolder + "/referencer.prefab";
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader);
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);

            try
            {
                AssetDatabase.CreateAsset(material, materialPath);
                go.GetComponent<Renderer>().sharedMaterial = material;
                PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
                AssetDatabase.SaveAssets();

                var task = ReferenceFunctions.FindReferences(
                    materialPath,
                    max_results: 20,
                    include_referenced_by: true,
                    max_assets_scanned: 5000,
                    max_scan_seconds: 10);
                while (!task.IsCompleted)
                    yield return null;
                if (task.Exception != null)
                    throw task.Exception;

                AssertSuccess(task.Result);
                var data = GetProperty<object>(task.Result, "data");
                var referencedBy = (IEnumerable)GetProperty<object>(data, "referenced_by");
                var foundPrefab = false;
                foreach (var item in referencedBy)
                {
                    if (string.Equals(item as string, prefabPath, StringComparison.Ordinal))
                        foundPrefab = true;
                }
                Assert.IsTrue(foundPrefab, "Expected reverse dependency scan to find the generated prefab.");
                AssertSuccess(ReferenceFunctions.FindBrokenReferences("assets", prefabPath));

                var limitedTask = ReferenceFunctions.FindReferences(
                    materialPath,
                    include_referenced_by: true,
                    max_assets_scanned: 1,
                    max_scan_seconds: 10);
                while (!limitedTask.IsCompleted)
                    yield return null;
                if (limitedTask.Exception != null)
                    throw limitedTask.Exception;

                AssertSuccess(limitedTask.Result);
                var limitedData = GetProperty<object>(limitedTask.Result, "data");
                Assert.AreEqual(1, GetProperty<int>(limitedData, "scanned"));
                Assert.IsTrue(GetProperty<bool>(limitedData, "referenced_by_truncated"));
                Assert.AreEqual("asset_limit", GetProperty<string>(limitedData, "reverse_scan_stop_reason"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                DeleteTempFolder();
            }
        }

        [Test]
        public void ProjectAndUndoStateTools_ReturnStructuredSuccess()
        {
            AssertSuccess(ProjectSettingsFunctions.GetProjectSettings());
            AssertSuccess(UndoFunctions.GetUndoState());
        }

        private static void AssertSuccess(object result)
        {
            Assert.IsNotNull(result);
            Assert.IsTrue(GetProperty<bool>(result, "success"), result.ToString());
        }

        private static void AssertError(object result, string code)
        {
            Assert.IsNotNull(result);
            Assert.IsFalse(GetProperty<bool>(result, "success"));
            Assert.AreEqual(code, GetProperty<string>(result, "code"));
        }

        private static T GetProperty<T>(object target, string name)
        {
            var property = target.GetType().GetProperty(name);
            Assert.IsNotNull(property, $"Missing property '{name}' on {target.GetType().FullName}.");
            return (T)property.GetValue(target);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var name = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static void DeleteTempFolder()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.DeleteAsset(TempFolder);
        }

        private static void RestoreSceneDirtiness(Scene scene, bool wasDirty)
        {
            if (wasDirty || !scene.IsValid())
                return;

            var method = typeof(EditorSceneManager).GetMethod(
                "ClearSceneDirtiness",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            method?.Invoke(null, new object[] { scene });
        }
    }
}
