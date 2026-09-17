// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Funplay.Editor.MCP.Server;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tests
{
    public sealed class StructuredInspectionTests
    {
        private GameObject root;
        private Scene scene;
        private bool dirty;
        private string folder;
        private Object[] selection;
        [SetUp] public void SetUp()
        {
            scene = SceneManager.GetActiveScene(); dirty = scene.isDirty; selection = Selection.objects;
            root = new GameObject("Inspection_" + Guid.NewGuid().ToString("N"), typeof(RectTransform));
        }
        [TearDown] public void TearDown()
        {
            Selection.objects = selection;
            Object.DestroyImmediate(root);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
            if (!dirty) typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, new object[] { scene });
        }
        private Image Add(string name, Image.Type type = Image.Type.Simple)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image)); go.transform.SetParent(root.transform, false);
            var image = go.GetComponent<Image>(); image.type = type; return image;
        }
        private JObject Query(string filter, int limit = 50, int scan = 1000) => JObject.FromObject(GameObjectFunctions.FindGameObjects(
            include_inactive: "true", in_parent: ObjectIdHelper.GetSerializableId(root), max: limit.ToString(), filter: filter, scope: "scene", scan_limit: scan));
        [Test] public void ComponentPredicatesAndProjectionUseSerializedValues()
        {
            var sliced = Add("Sliced", Image.Type.Sliced); Add("Simple");
            var response = Query("{\"component\":\"UnityEngine.UI.Image\",\"where\":[{\"property\":\"m_Type\",\"op\":\"eq\",\"value\":\"Sliced\"}],\"select\":[\"m_Type\",\"m_Color.a\"]}");
            Assert.IsTrue((bool)response["success"], response.ToString()); Assert.AreEqual(1, (int)response["data"]["total_matches"]);
            Assert.AreEqual(ObjectIdHelper.GetSerializableId(sliced.gameObject), (string)response["data"]["items"][0]["instanceId"]);
            Assert.AreEqual(1f, (float)response["data"]["items"][0]["components"][0]["properties"]["m_Color.a"]["value"]);
        }
        [Test] public void MissingPropertyIsNotSilentlyNoMatch()
        {
            Add("Image");
            var result = Query("{\"component\":\"Image\",\"where\":[{\"property\":\"typo\",\"value\":1}]}");
            Assert.IsFalse((bool)result["data"]["complete"]);
            Assert.AreEqual("PROPERTY_NOT_FOUND", (string)result["data"]["errors"][0]["code"]);
        }
        [Test] public void SnapshotPagesStayStableAfterSceneChanges()
        {
            Add("One"); Add("Two"); Add("Three");
            var result = Query("{\"component\":\"Image\"}", 1);
            var id = (string)result["data"]["snapshot_id"];
            Add("Later");
            var page = JObject.FromObject(GameObjectFunctions.FindGameObjects(max: "1", offset: 1, snapshot_id: id));
            Assert.AreEqual(3, (int)page["data"]["total_matches"]); Assert.AreEqual(1, page["data"]["items"].Count());
            Assert.AreEqual(2, (int)page["data"]["next_offset"]);
        }
        [Test] public void ScanCapIsExplicitAndUnknownSnapshotIsRejected()
        {
            Add("One"); Add("Two");
            var result = Query("{\"component\":\"Image\"}", scan: 1);
            Assert.IsFalse((bool)result["data"]["complete"]); Assert.AreEqual("scan_limit", (string)result["data"]["stop_reason"]);
            Assert.AreEqual("QUERY_SNAPSHOT_EXPIRED", (string)JObject.FromObject(GameObjectFunctions.FindGameObjects(snapshot_id: "unknown"))["code"]);
        }
        [TestCase("{\"component\":\"Image\",\"where\":[{\"property\":\"x\",\"op\":\"run\",\"value\":1}]}")]
        [TestCase("{\"wrong_field\":1}")]
        public void InvalidFilterFailsBeforeScanning(string json) => Assert.IsFalse((bool)Query(json)["success"]);
        [Test] public void OrderedAndNullPredicatesDoNotInvokeGetters()
        {
            var image = Add("Faded"); image.color = new Color(1, 1, 1, .1f);
            var result = Query("{\"component\":\"Image\",\"where\":[{\"property\":\"m_Color.a\",\"op\":\"lt\",\"value\":0.5},{\"property\":\"m_Sprite\",\"op\":\"is_null\"}]}");
            Assert.AreEqual(1, (int)result["data"]["total_matches"]);
        }
        [Test] public void ShortTypeNameAmbiguityIsReportedWithAssemblies()
        {
            var result = JObject.FromObject(InspectionFunctions.FindProjectTypes("WorkflowTwin", exact: true));
            Assert.IsTrue((bool)result["data"]["ambiguous"]); Assert.AreEqual(2, (int)result["data"]["total"]);
            Assert.IsNotEmpty((string)result["data"]["items"][0]["assembly_qualified_name"]);
            var exact = JObject.FromObject(InspectionFunctions.FindProjectTypes("Funplay.Editor.Tests.TypesA.WorkflowTwin", exact: true));
            Assert.IsFalse((bool)exact["data"]["ambiguous"]); Assert.AreEqual(1, (int)exact["data"]["total"]);
        }
        [Test] public void NonComponentProjectHelpersCanBeFound()
        {
            var result = JObject.FromObject(InspectionFunctions.FindProjectTypes(typeof(StructuredInspectionTests).FullName, true));
            Assert.AreEqual(1, (int)result["data"]["total"]); Assert.IsFalse((bool)result["data"]["items"][0]["is_component"]);
        }
        [Test] public void NestedWriteReturnsActualLiveReadbackAndAffectedScene()
        {
            var image = Add("Image");
            var result = JObject.FromObject(ComponentPropertyFunctions.SetComponentProperty(component_instance_id: ObjectIdHelper.GetSerializableId(image), property: "m_Color.r", value: "0.25"));
            Assert.IsTrue((bool)result["success"]); Assert.AreEqual(.25f, (float)result["data"]["newValue"]["value"]);
            Assert.IsFalse((bool)result["data"]["readback"]["persisted"]); Assert.AreEqual(.25f, image.color.r);
        }
        [Test] public void ImageInspectionReportsPartialFailuresWithoutMutation()
        {
            var image = Add("Image", Image.Type.Sliced);
            var result = JObject.FromObject(InspectionFunctions.InspectUiSprites("[{\"image_id\":\"" + ObjectIdHelper.GetSerializableId(image) + "\"},{\"image_id\":\"0\"}]"));
            Assert.AreEqual(1, (int)result["data"]["success_count"]); Assert.AreEqual(1, (int)result["data"]["failure_count"]);
            Assert.AreEqual("Sliced", (string)result["data"]["results"][0]["images"][0]["image_type"]);
            Assert.IsNull(image.sprite);
        }
        [Test] public void MultipleSpritesReturnLocalIdsAndIndividualBorders()
        {
            CreateFolder(); string path = folder + "/sprites.png";
            var texture = new Texture2D(32, 16);
            File.WriteAllBytes(path, texture.EncodeToPNG()); Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Multiple;
#pragma warning disable 0618
            importer.spritesheet = new[] {
                new SpriteMetaData { name = "Left", rect = new Rect(0,0,16,16), border = Vector4.one * 2 },
                new SpriteMetaData { name = "Right", rect = new Rect(16,0,16,16), border = Vector4.zero }
            };
#pragma warning restore 0618
            importer.SaveAndReimport();
            var bytes = File.ReadAllBytes(path + ".meta");
            var response = JObject.FromObject(InspectionFunctions.InspectUiSprites("[{\"asset_path\":\"" + path + "\"}]"));
            var sprites = response["data"]["results"][0]["sprites"];
            Assert.AreEqual(2, sprites.Count());
            Assert.AreNotEqual((long)sprites[0]["local_file_id"], (long)sprites[1]["local_file_id"]);
            Assert.IsTrue(sprites.Any(x => (bool)x["has_border"])); Assert.IsTrue(sprites.Any(x => !(bool)x["has_border"]));
            var image = Add("Slice", Image.Type.Sliced); image.sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().Single(x => x.name == "Right");
            Assert.IsTrue(UIAuditRules.Inspect(image.gameObject, "", true, new UIAuditConfiguration()).Any(x => x.rule == "sliced_without_border"));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path + ".meta"));
        }
        [Test] public void PrefabQueryRetainsVariantIdentityAndDoesNotSave()
        {
            CreateFolder(); var image = Add("Panel", Image.Type.Sliced);
            var prefab = PrefabUtility.SaveAsPrefabAsset(image.gameObject, folder + "/Base.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab); instance.transform.SetParent(root.transform);
            PrefabUtility.SaveAsPrefabAsset(instance, folder + "/Variant.prefab");
            var before = File.ReadAllBytes(folder + "/Variant.prefab");
            var result = JObject.FromObject(GameObjectFunctions.FindGameObjects(filter: "{\"component\":\"Image\"}", scope: "prefabs", asset_paths: "[\"" + folder + "\"]", include_inactive: "true"));
            Assert.AreEqual(2, (int)result["data"]["total_matches"]);
            Assert.IsTrue(result["data"]["items"].Any(x => ((string)x["asset_path"]).EndsWith("Variant.prefab")));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(folder + "/Variant.prefab"));
        }
        [Test] public void PackedAtlasCloneKeepsBorderWithoutAssumingRuntimeBinding()
        {
            CreateFolder(); var path = folder + "/Panel.png";
            var texture = new Texture2D(32, 32); File.WriteAllBytes(path, texture.EncodeToPNG()); Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single;
            importer.spriteBorder = new Vector4(4, 6, 4, 6); importer.SaveAndReimport();
            var atlas = new UnityEngine.U2D.SpriteAtlas(); var atlasPath = folder + "/UI.spriteatlas";
            AssetDatabase.CreateAsset(atlas, atlasPath);
            UnityEditor.U2D.SpriteAtlasExtensions.Add(atlas, new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(path) });
            var priorPackingMode = EditorSettings.spritePackerMode;
            Sprite packed = null;
            try
            {
                EditorSettings.spritePackerMode = SpritePackerMode.AlwaysOnAtlas;
                UnityEditor.U2D.SpriteAtlasUtility.PackAtlases(new[] { atlas }, EditorUserBuildSettings.activeBuildTarget, false);
                var previews = (Texture2D[])typeof(UnityEditor.U2D.SpriteAtlasExtensions)
                    .GetMethod("GetPreviewTextures", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(null, new object[] { atlas });
                Assert.IsNotEmpty(previews, "Fixture must contain a natively packed atlas texture.");
                Assert.IsTrue(atlas.CanBindTo(AssetDatabase.LoadAssetAtPath<Sprite>(path)));
                packed = atlas.GetSprite("Panel"); Assert.IsNotNull(packed);
                // Public PackAtlases unloads sprites afterwards. Editor clones need not yet be
                // runtime-bound, so packed is an observation, not an atlas-membership predicate.
                Assert.AreEqual(packed.packed, (bool)JObject.FromObject(SpriteInspection.Describe(packed))["packed"]);
                Assert.AreEqual(new Vector4(4, 6, 4, 6), packed.border);
                var image = Add("Packed", Image.Type.Sliced); image.overrideSprite = packed;
                var meta = File.ReadAllBytes(path + ".meta");
                Assert.IsFalse(UIAuditRules.Inspect(image.gameObject, "", true, new UIAuditConfiguration()).Any(x => x.rule == "sliced_without_border"));
                CollectionAssert.AreEqual(meta, File.ReadAllBytes(path + ".meta"));
                image.overrideSprite = null;
            }
            finally { if (packed != null) Object.DestroyImmediate(packed); EditorSettings.spritePackerMode = priorPackingMode; }
        }
        [Test] public void StructuredCapabilitiesAreCoreAndExecuteCodeIsFallback()
        {
            Assert.IsTrue(MCPToolExportPolicy.DefaultCoreTools.Contains("inspect_ui_sprites"));
            Assert.Greater(MCPToolExportPolicy.GetSortRank("execute_code", MCPToolExportProfile.Core), MCPToolExportPolicy.GetSortRank("inspect_ui_sprites", MCPToolExportProfile.Core));
            var data = JObject.FromObject(InspectionFunctions.GetToolCapabilities("execute_code"))["data"];
            Assert.AreEqual("fallback", (string)data["tools"].Single(x => (string)x["name"] == "execute_code")["role"]);
        }
        private void CreateFolder()
        {
            var name = "FunplayInspectionTests_" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
        }
    }
}
namespace Funplay.Editor.Tests.TypesA { internal class WorkflowTwin {} }
namespace Funplay.Editor.Tests.TypesB { internal class WorkflowTwin {} }
