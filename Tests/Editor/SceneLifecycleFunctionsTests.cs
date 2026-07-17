// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Funplay.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.Tests
{
    public sealed class SceneLifecycleFunctionsTests
    {
        [Test]
        public void LoadSceneAdditive_LoadsPlaceholderOnceAndReportsLoadedCount()
        {
            using (var scope = new SceneTestScope())
            {
                scope.CreateSavedScene("Active.unity", NewSceneMode.Single);
                var targetPath = scope.CreateClosedSceneAsset("Target.unity");
                var placeholder = EditorSceneManager.OpenScene(targetPath, OpenSceneMode.AdditiveWithoutLoading);
                Assert.IsTrue(placeholder.IsValid());
                Assert.IsFalse(placeholder.isLoaded);

                var first = SceneFunctions.LoadSceneAdditive(targetPath);
                AssertSuccess(first);
                var firstData = GetProperty<object>(first, "data");
                var firstLoaded = GetProperty<object>(firstData, "loaded");
                Assert.IsFalse(GetProperty<bool>(firstLoaded, "alreadyLoaded"));
                Assert.AreEqual(targetPath, GetProperty<string>(firstLoaded, "path"));
                Assert.AreEqual(2, GetProperty<int>(firstData, "loadedSceneCount"));
                Assert.AreEqual(2, CountLoadedScenes());
                Assert.AreEqual(2, SceneManager.sceneCount, "The unloaded placeholder should be removed.");

                var second = SceneFunctions.LoadSceneAdditive(targetPath);
                AssertSuccess(second);
                var secondData = GetProperty<object>(second, "data");
                var secondLoaded = GetProperty<object>(secondData, "loaded");
                Assert.IsTrue(GetProperty<bool>(secondLoaded, "alreadyLoaded"));
                Assert.AreEqual(2, GetProperty<int>(secondData, "loadedSceneCount"));
                Assert.AreEqual(2, SceneManager.sceneCount, "Idempotent loading must not duplicate the scene.");
            }
        }

        [Test]
        public void LoadSceneAdditive_RejectsUnsafeOrMissingAssetPaths()
        {
            using (var scope = new SceneTestScope())
            {
                scope.CreateSavedScene("Active.unity", NewSceneMode.Single);
                var targetPath = scope.CreateClosedSceneAsset("Target.unity");
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                Assert.IsNotNull(projectRoot);
                var absolutePath = Path.Combine(projectRoot, targetPath);

                AssertError(SceneFunctions.LoadSceneAdditive(absolutePath), "INVALID_SCENE_PATH");
                AssertError(
                    SceneFunctions.LoadSceneAdditive("Assets/../ProjectSettings/Fake.unity"),
                    "INVALID_SCENE_PATH");
                AssertError(
                    SceneFunctions.LoadSceneAdditive(scope.TempFolder + "/Missing.unity"),
                    "SCENE_ASSET_NOT_FOUND");
                Assert.IsFalse(IsSceneLoaded(targetPath));
            }
        }

        [Test]
        public void UnloadScene_ResolvesTargetBeforeApplyingLastSceneGuard()
        {
            using (var scope = new SceneTestScope())
            {
                var active = scope.CreateSavedScene("Active.unity", NewSceneMode.Single);

                AssertError(SceneFunctions.UnloadScene("MissingScene"), "SCENE_NOT_LOADED");
                AssertError(SceneFunctions.UnloadScene(active.path), "CANNOT_UNLOAD_LAST_SCENE");
                Assert.IsTrue(active.isLoaded);
            }
        }

        [Test]
        public void UnloadScene_RequiresForceForDirtySceneAndReadsBackClosure()
        {
            using (var scope = new SceneTestScope())
            {
                scope.CreateSavedScene("Active.unity", NewSceneMode.Single);
                var additive = scope.CreateSavedScene("Additive.unity", NewSceneMode.Additive);
                scope.CreateObjectInScene(additive, "DirtyObject");
                Assert.IsTrue(additive.isDirty);

                AssertError(SceneFunctions.UnloadScene(additive.path), "SCENE_HAS_UNSAVED_CHANGES");
                Assert.IsTrue(IsSceneLoaded(additive.path));

                var forced = SceneFunctions.UnloadScene(additive.path, true);
                AssertSuccess(forced);
                var data = GetProperty<object>(forced, "data");
                Assert.IsTrue(GetProperty<bool>(data, "discardedUnsavedChanges"));
                Assert.AreEqual(1, GetProperty<int>(data, "loadedSceneCount"));
                Assert.IsFalse(IsSceneLoaded(additive.path));
            }
        }

        [Test]
        public void UnloadScene_RejectsAmbiguousSceneNameAndAcceptsExactPath()
        {
            using (var scope = new SceneTestScope())
            {
                scope.CreateSavedScene("Active.unity", NewSceneMode.Single);
                var first = scope.CreateSavedScene("A/Duplicate.unity", NewSceneMode.Additive);
                var second = scope.CreateSavedScene("B/Duplicate.unity", NewSceneMode.Additive);

                var ambiguous = SceneFunctions.UnloadScene("Duplicate");
                AssertError(ambiguous, "SCENE_NAME_AMBIGUOUS");
                var ambiguousData = GetProperty<object>(ambiguous, "data");
                Assert.AreEqual(2, ToObjects(GetProperty<object>(ambiguousData, "matches")).Count);
                Assert.IsTrue(first.isLoaded);
                Assert.IsTrue(second.isLoaded);

                var exact = SceneFunctions.UnloadScene(first.path);
                AssertSuccess(exact);
                Assert.IsFalse(IsSceneLoaded(first.path));
                Assert.IsTrue(IsSceneLoaded(second.path));
            }
        }

        [Test]
        public void SaveAllScenes_SavesEveryDirtyLoadedSceneAndReportsReadback()
        {
            using (var scope = new SceneTestScope())
            {
                var active = scope.CreateSavedScene("Active.unity", NewSceneMode.Single);
                var additive = scope.CreateSavedScene("Additive.unity", NewSceneMode.Additive);
                scope.CreateObjectInScene(active, "ActiveDirtyObject");
                scope.CreateObjectInScene(additive, "AdditiveDirtyObject");

                var dirtyBefore = SceneFunctions.ListDirtyScenes();
                AssertSuccess(dirtyBefore);
                Assert.AreEqual(2, GetProperty<int>(GetProperty<object>(dirtyBefore, "data"), "count"));

                AssertError(
                    SceneFunctions.SaveAllScenes(),
                    "MULTIPLE_DIRTY_SCENES_CONFIRMATION_REQUIRED");
                Assert.IsTrue(active.isDirty);
                Assert.IsTrue(additive.isDirty);

                var saved = SceneFunctions.SaveAllScenes(true);
                AssertSuccess(saved);
                var savedData = GetProperty<object>(saved, "data");
                Assert.AreEqual(2, GetProperty<int>(savedData, "count"));
                Assert.AreEqual(0, GetProperty<int>(savedData, "remainingDirtyCount"));
                Assert.IsTrue(ToObjects(GetProperty<object>(savedData, "scenes"))
                    .All(scene => !GetProperty<bool>(scene, "isDirty")));
                Assert.IsFalse(active.isDirty);
                Assert.IsFalse(additive.isDirty);

                var secondSave = SceneFunctions.SaveAllScenes();
                AssertSuccess(secondSave);
                Assert.AreEqual(0, GetProperty<int>(GetProperty<object>(secondSave, "data"), "count"));
            }
        }

        [Test]
        public void SaveAllScenes_RejectsUntitledSceneWithoutOpeningSaveDialog()
        {
            using (var scope = new SceneTestScope())
            {
                var untitled = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("UnsavedObject");
                EditorSceneManager.MarkSceneDirty(untitled);
                Assert.IsEmpty(untitled.path);
                Assert.IsTrue(untitled.isDirty);

                AssertError(SceneFunctions.SaveAllScenes(), "UNTITLED_SCENE_REQUIRES_PATH");
                Assert.IsEmpty(untitled.path);
                Assert.IsTrue(untitled.isDirty);
            }
        }

        [Test]
        public void ListScenes_ReturnsSortedProjectScenePaths()
        {
            using (var scope = new SceneTestScope())
            {
                var active = scope.CreateSavedScene("Z_Active.unity", NewSceneMode.Single);
                var secondPath = scope.CreateClosedSceneAsset("B/Second.unity");
                var firstPath = scope.CreateClosedSceneAsset("A/First.unity");

                var result = SceneFunctions.ListScenes();
                AssertSuccess(result);
                var paths = ((IEnumerable)GetProperty<object>(
                    GetProperty<object>(result, "data"),
                    "scenes")).Cast<string>().ToList();

                CollectionAssert.AreEqual(paths.OrderBy(path => path, StringComparer.Ordinal).ToList(), paths);
                CollectionAssert.IsSubsetOf(
                    new[] { active.path, firstPath, secondPath },
                    paths);
                Assert.IsTrue(paths.All(path => path.StartsWith("Assets/", StringComparison.Ordinal)));
            }
        }

        [Test]
        public void PlayModeDomainReloadExpectation_RespectsEditorOptions()
        {
            Assert.IsTrue(SceneFunctions.IsDomainReloadExpectedForPlayModeTransition(
                false,
                EnterPlayModeOptions.DisableDomainReload));
            Assert.IsTrue(SceneFunctions.IsDomainReloadExpectedForPlayModeTransition(
                true,
                EnterPlayModeOptions.None));
            Assert.IsTrue(SceneFunctions.IsDomainReloadExpectedForPlayModeTransition(
                true,
                EnterPlayModeOptions.DisableSceneReload));
            Assert.IsFalse(SceneFunctions.IsDomainReloadExpectedForPlayModeTransition(
                true,
                EnterPlayModeOptions.DisableDomainReload));
            Assert.IsFalse(SceneFunctions.IsDomainReloadExpectedForPlayModeTransition(
                true,
                EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload));
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

        private static T GetProperty<T>(object obj, string name)
        {
            Assert.NotNull(obj);
            var property = obj.GetType().GetProperty(name);
            Assert.NotNull(property, $"Missing property '{name}' on {obj.GetType().FullName}.");
            return (T)property.GetValue(obj);
        }

        private static List<object> ToObjects(object value)
        {
            return ((IEnumerable)value).Cast<object>().ToList();
        }

        private static int CountLoadedScenes()
        {
            int count = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isLoaded)
                    count++;
            }
            return count;
        }

        private static bool IsSceneLoaded(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string Describe(object value)
        {
            return value == null ? "null" : value.ToString();
        }

        private sealed class SceneTestScope : IDisposable
        {
            private readonly SceneSetup[] _originalSetup;
            private readonly bool _canRestoreOriginalSetup;

            public SceneTestScope()
            {
                _originalSetup = EditorSceneManager.GetSceneManagerSetup();
                _canRestoreOriginalSetup = CanSafelyRestoreSceneSetup(_originalSetup);
                if (!Application.isBatchMode && !_canRestoreOriginalSetup)
                {
                    Assert.Ignore(
                        "Skipping scene lifecycle test because the interactive editor has dirty or untitled scenes.");
                }

                TempFolder = "Assets/__FunplayMcpSceneLifecycleTests_" + Guid.NewGuid().ToString("N");
                EnsureFolder(TempFolder);
            }

            public string TempFolder { get; }

            public Scene CreateSavedScene(string relativePath, NewSceneMode mode)
            {
                var assetPath = TempFolder + "/" + relativePath.Replace('\\', '/');
                EnsureFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, assetPath), assetPath);
                return scene;
            }

            public string CreateClosedSceneAsset(string relativePath)
            {
                var scene = CreateSavedScene(relativePath, NewSceneMode.Additive);
                var path = scene.path;
                Assert.IsTrue(EditorSceneManager.CloseScene(scene, true), path);
                return path;
            }

            public GameObject CreateObjectInScene(Scene scene, string name)
            {
                Assert.IsTrue(scene.IsValid());
                Assert.IsTrue(scene.isLoaded);

                var gameObject = new GameObject(name);
                SceneManager.MoveGameObjectToScene(gameObject, scene);
                Assert.IsTrue(EditorSceneManager.MarkSceneDirty(scene));
                return gameObject;
            }

            public void Dispose()
            {
                try
                {
                    if (_canRestoreOriginalSetup)
                        EditorSceneManager.RestoreSceneManagerSetup(_originalSetup);
                    else if (Application.isBatchMode)
                        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
                finally
                {
                    if (AssetDatabase.IsValidFolder(TempFolder))
                        AssetDatabase.DeleteAsset(TempFolder);
                }
            }

            private static bool CanSafelyRestoreSceneSetup(SceneSetup[] setup)
            {
                if (setup == null || setup.Length == 0)
                    return false;

                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && (scene.isDirty || string.IsNullOrEmpty(scene.path)))
                        return false;
                }

                foreach (var scene in setup)
                {
                    if (string.IsNullOrEmpty(scene.path) || !File.Exists(scene.path))
                        return false;
                }

                return true;
            }

            private static void EnsureFolder(string folder)
            {
                if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
                    return;

                var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
                var name = Path.GetFileName(folder);
                if (string.IsNullOrEmpty(parent))
                    throw new InvalidOperationException("Temporary test folder must be under Assets.");

                EnsureFolder(parent);
                AssetDatabase.CreateFolder(parent, name);
            }
        }
    }
}
