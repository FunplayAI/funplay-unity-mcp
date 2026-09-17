// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Funplay.Editor.Tests
{
    public sealed class UIPreviewGuardsTests
    {
        [Test] public void RuntimeSceneSnapshotUsesLiveSceneIdentity()
        {
            var current = UIPreviewSession.RuntimeScenes();
            Assert.AreEqual(SceneManager.sceneCount, current.Length);
            Assert.AreEqual(SceneManager.GetActiveScene().path, current.Single(x => x.active).path);
            for (int i = 0; i < current.Length; i++) Assert.AreEqual(SceneManager.GetSceneAt(i).isLoaded, current[i].loaded);
        }
        [Test] public void ViewReadbackDetectsClampedZoomAndChangedPresets()
        {
            var original = new PreviewViewState { window_id = "1", group = "Standalone", size_signature = "portrait", zoom_supported = true, scale_x = .95f, scale_y = .95f };
            var current = JsonConvert.DeserializeObject<PreviewViewState>(JsonConvert.SerializeObject(original));
            Assert.IsTrue(PreviewGameView.Matches(original, current));
            current.scale_x = 1; Assert.IsFalse(PreviewGameView.Matches(original, current));
            current.scale_x = .95f; current.size_signature = "landscape"; Assert.IsFalse(PreviewGameView.Matches(original, current));
        }
        [Test] public void OriginalUntitledAndDirtyScenesAreNeverDiscarded()
        {
            StringAssert.Contains("UNSAVED_SCENE", UIPreviewSession.ValidateOriginalScenes(new[] { new PreviewSceneEntry { path = "" } }));
            StringAssert.Contains("DIRTY_SCENE", UIPreviewSession.ValidateOriginalScenes(new[] { new PreviewSceneEntry { path = "Assets/Saved.unity", dirty = true } }));
        }
        [Test] public void DiscardFlagNeverAuthorizesUnrelatedSceneClosure()
        {
            var state = new UIPreviewState { scene_path = "Assets/Own.unity" };
            StringAssert.Contains("SCENE_SETUP_CHANGED", UIPreviewSession.EndGuard(new[] { new PreviewSceneEntry { path = "Assets/User.unity" } }, state, true));
            StringAssert.Contains("SCENE_SETUP_CHANGED", UIPreviewSession.EndGuard(new[] { new PreviewSceneEntry { path = "Assets/Own.unity" }, new PreviewSceneEntry { path = "Assets/User.unity" } }, state, true));
        }
        [Test] public void DirtyOwnedPreviewRequiresExplicitDiscard()
        {
            var state = new UIPreviewState { scene_path = "Assets/Own.unity" };
            var scenes = new[] { new PreviewSceneEntry { path = state.scene_path, dirty = true } };
            Assert.IsNotNull(UIPreviewSession.EndGuard(scenes, state, false)); Assert.IsNull(UIPreviewSession.EndGuard(scenes, state, true));
        }
        [Test] public void JournalMustProveExactOwnership()
        {
            var id = Guid.NewGuid().ToString("N");
            var state = new UIPreviewState { session_id = id, owned_folder = "Assets/FunplayMcpPreviews/" + id,
                scene_path = "Assets/FunplayMcpPreviews/" + id + "/Preview.unity", original_scenes = new[] { new PreviewSceneEntry { path = "Assets/Scene.unity" } } };
            Assert.IsTrue(UIPreviewSession.ValidState(state));
            var restored = JsonConvert.DeserializeObject<UIPreviewState>(JsonConvert.SerializeObject(state));
            Assert.IsTrue(UIPreviewSession.ValidState(restored));
            restored.scene_path = "Assets/User.unity"; Assert.IsFalse(UIPreviewSession.ValidState(restored));
            restored.scene_path = state.scene_path; restored.owned_folder = "Assets"; Assert.IsFalse(UIPreviewSession.ValidState(restored));
        }
        [TestCase(0, 10)] [TestCase(127, 128)] [TestCase(4097, 128)]
        public void InvalidResolutionFailsBeforeMutation(int width, int height) => Assert.Throws<ArgumentException>(() => PreviewGameView.ValidateDimensions(width, height));
    }
    public sealed class UIPreviewSessionTests
    {
        private SceneSetup[] original;
        private string folder, baseline, session;
        private string originalHash;
        [SetUp] public void SetUp()
        {
            folder = null; session = null; original = null;
            var sceneProblem = UIPreviewSession.ValidateOriginalScenes(UIPreviewSession.CurrentScenes());
            if (sceneProblem != null) Assert.Ignore("Protect current scene. Run this integration fixture from a saved clean validation scene: " + sceneProblem);
            original = EditorSceneManager.GetSceneManagerSetup();
            var name = "FunplayPreviewTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            baseline = folder + "/Baseline.unity"; Assert.IsTrue(EditorSceneManager.SaveScene(scene, baseline));
            originalHash = UIPreviewSession.Hash(baseline);
        }
        [UnityTearDown] public IEnumerator TearDown()
        {
            if (session != null)
            {
                var receipt = Session();
                if (!(bool)receipt["IsClosed"])
                {
                    // Test owns only its preview/baseline; never close unrelated user scenes.
                    var previewPath = (string)receipt["scene_path"];
                    if (File.Exists(previewPath)) EditorSceneManager.OpenScene(previewPath, OpenSceneMode.Single);
                    UIPreviewSession.End(session, true);
                    var until = EditorApplication.timeSinceStartup + 15;
                    while (!(bool)Session()["IsClosed"] && EditorApplication.timeSinceStartup < until) yield return null;
                    Assert.IsTrue((bool)Session()["IsClosed"], Session().ToString());
                }
            }
            if (original != null) EditorSceneManager.RestoreSceneManagerSetup(original);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }
        [UnityTest] public IEnumerator NormalEndRestoresSceneSelectionAndRemovesOnlyOwnedAsset()
        {
            Selection.activeGameObject = Camera.main.gameObject;
            var selected = GlobalObjectId.GetGlobalObjectIdSlow(Selection.activeGameObject).ToString();
            Start(); yield return AwaitReady();
            var temporary = (string)Session()["scene_path"];
            Assert.IsTrue(File.Exists(temporary));
            End(); yield return AwaitClosed();
            Assert.AreEqual(baseline, SceneManager.GetActiveScene().path);
            Assert.AreEqual(selected, GlobalObjectId.GetGlobalObjectIdSlow(Selection.activeObject).ToString());
            Assert.IsTrue((bool)Session()["selection_restored"]);
            Assert.AreEqual(originalHash, UIPreviewSession.Hash(baseline));
            Assert.IsFalse(File.Exists(temporary));
            Assert.IsTrue((bool)JObject.FromObject(UIPreviewSession.End(session, false))["success"]);
        }
        [UnityTest] public IEnumerator StartKeyIsIdempotentAndConflictingParametersFail()
        {
            string key = Guid.NewGuid().ToString("N");
            Start(key: key);
            var duplicate = JObject.FromObject(UIPreviewSession.Start(null, null, false, 0, 0, key));
            Assert.AreEqual(session, (string)duplicate["data"]["session"]["session_id"]);
            Assert.AreEqual("PREVIEW_REQUEST_KEY_CONFLICT", (string)JObject.FromObject(UIPreviewSession.Start(null, null, true, 0, 0, key))["code"]);
            Assert.AreEqual("PREVIEW_SESSION_ACTIVE", (string)JObject.FromObject(UIPreviewSession.Start(null, null, false, 0, 0, "other"))["code"]);
            yield return AwaitReady(); End(); yield return AwaitClosed();
        }
        [UnityTest] public IEnumerator TemporaryResolutionRestoresAndVerifiesGameViewAfterRepaint()
        {
            var originalView = PreviewGameView.Capture();
            if (originalView == null) Assert.Ignore("A supported Game View is required.");
            var response = JObject.FromObject(UIPreviewSession.Start(null, null, false, 640, 360, null));
            Assert.IsTrue((bool)response["success"], response.ToString()); session = (string)response["data"]["session"]["session_id"];
            yield return AwaitReady(); End(); yield return AwaitClosed();
            Assert.IsTrue((bool)Session()["view_restored"], Session().ToString());
            Assert.IsTrue(PreviewGameView.Matches(originalView, PreviewGameView.Capture()));
        }
        [UnityTest] public IEnumerator UserAddedScenePreventsRestoreEvenWithDiscardFlag()
        {
            Start(); yield return AwaitReady();
            var extra = EditorSceneManager.OpenScene(baseline, OpenSceneMode.Additive);
            var response = JObject.FromObject(UIPreviewSession.End(session, true));
            Assert.AreEqual("PREVIEW_RESTORE_BLOCKED", (string)response["code"]); Assert.IsTrue(extra.isLoaded);
            EditorSceneManager.CloseScene(extra, true); End(); yield return AwaitClosed();
        }
        [UnityTest] public IEnumerator UnsavedPreviewChangesNeedExplicitDiscard()
        {
            Start(); yield return AwaitReady();
            new GameObject("IntentionalPreviewChange");
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Assert.AreEqual("PREVIEW_RESTORE_BLOCKED", (string)JObject.FromObject(UIPreviewSession.End(session, false))["code"]);
            End(true); yield return AwaitClosed();
        }
        [UnityTest] public IEnumerator SavedPreviewChangesAreDetectedAndRecoveryCanBeRetried()
        {
            Start(); yield return AwaitReady();
            new GameObject("SavedChange"); EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            End(); yield return AwaitStatus("needs_attention");
            StringAssert.Contains("PREVIEW_FILE_CHANGED", (string)Session()["error"]);
            End(true); yield return AwaitClosed(); Assert.AreEqual(originalHash, UIPreviewSession.Hash(baseline));
        }
        [UnityTest] public IEnumerator ExtraFilesInOwnedFolderAreRetained()
        {
            Start(); yield return AwaitReady();
            var extra = (string)Session()["owned_folder"] + "/keep-user-note.txt"; File.WriteAllText(extra, "keep");
            End(); yield return AwaitClosed();
            Assert.IsTrue(File.Exists(extra));
            File.Delete(extra); AssetDatabase.DeleteAsset((string)Session()["owned_folder"]);
        }
        [UnityTest] public IEnumerator RealPrefabRemainsConnectedAndSourceUnchanged()
        {
            var go = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            var prefabPath = folder + "/Panel.prefab"; PrefabUtility.SaveAsPrefabAsset(go, prefabPath); UnityEngine.Object.DestroyImmediate(go);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene()); originalHash = UIPreviewSession.Hash(baseline);
            var hash = UIPreviewSession.Hash(prefabPath);
            Start("[\"" + prefabPath + "\"]"); yield return AwaitReady();
            var panel = GameObject.Find("Panel");
            Assert.IsNotNull(panel); Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(panel));
            Assert.IsNotNull(panel.GetComponentInParent<Canvas>());
            End(); yield return AwaitClosed(); Assert.AreEqual(hash, UIPreviewSession.Hash(prefabPath));
        }
        [UnityTest] public IEnumerator SceneTemplateIsCopiedNotOpenedForEditing()
        {
            Start(template: baseline); yield return AwaitReady();
            Assert.AreNotEqual(baseline, SceneManager.GetActiveScene().path);
            Assert.AreEqual(originalHash, UIPreviewSession.Hash(baseline));
            End(); yield return AwaitClosed();
        }
        [UnityTest] public IEnumerator MissingPrefabFailsWithoutSceneMutation()
        {
            var result = JObject.FromObject(UIPreviewSession.Start("[\"Assets/missing.prefab\"]", null, false, 0, 0, null));
            Assert.IsFalse((bool)result["success"]); Assert.AreEqual(baseline, SceneManager.GetActiveScene().path);
            yield return null;
        }
        private void Start(string prefabs = null, string template = null, string key = null)
        {
            var response = JObject.FromObject(UIPreviewSession.Start(prefabs, template, false, 0, 0, key));
            Assert.IsTrue((bool)response["success"], response.ToString()); session = (string)response["data"]["session"]["session_id"];
        }
        private void End(bool discard = false)
        {
            var result = JObject.FromObject(UIPreviewSession.End(session, discard)); Assert.IsTrue((bool)result["success"], result.ToString());
        }
        private JToken Session() => JObject.FromObject(UIPreviewSession.Get(session))["data"]["session"];
        private IEnumerator AwaitReady() => AwaitStatus("ready");
        private IEnumerator AwaitClosed() => AwaitStatus("closed", "closed_with_warnings");
        private IEnumerator AwaitStatus(params string[] expected)
        {
            var deadline = EditorApplication.timeSinceStartup + 15;
            while (!expected.Contains((string)Session()["status"]))
            {
                if ((string)Session()["status"] == "needs_attention") Assert.Fail(Session().ToString());
                Assert.Less(EditorApplication.timeSinceStartup, deadline, Session().ToString()); yield return null;
            }
        }
    }
}
