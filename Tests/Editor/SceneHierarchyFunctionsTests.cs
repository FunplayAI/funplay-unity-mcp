// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Linq;
using System.IO;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.Tests
{
    public sealed class SceneHierarchyFunctionsTests
    {
        [Test]
        public void FindTarget_ResolvesInactiveObjectsByNamePathAndInstanceId()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var scene = SceneManager.GetActiveScene();
            var wasDirty = scene.isDirty;
            GameObject firstRoot = null;
            GameObject secondRoot = null;
            GameObject inactiveByName = null;

            try
            {
                firstRoot = new GameObject("FirstRoot_" + suffix);
                secondRoot = new GameObject("SecondRoot_" + suffix);
                var duplicateName = "Duplicate_" + suffix;
                var firstChild = new GameObject(duplicateName);
                var secondChild = new GameObject(duplicateName);
                inactiveByName = new GameObject("Inactive_" + suffix);

                firstChild.transform.SetParent(firstRoot.transform);
                secondChild.transform.SetParent(secondRoot.transform);
                firstChild.SetActive(false);
                inactiveByName.SetActive(false);

                Assert.AreSame(inactiveByName, ObjectsHelper.FindTarget(inactiveByName.name));
                Assert.AreSame(firstChild, ObjectsHelper.FindTarget(firstRoot.name + "/" + duplicateName));
                Assert.AreSame(secondChild, ObjectsHelper.FindTarget(secondRoot.name + "/" + duplicateName));
                Assert.AreSame(firstChild, ObjectsHelper.FindTarget(ObjectIdHelper.GetSerializableId(firstChild)));
            }
            finally
            {
                if (firstRoot != null) UnityEngine.Object.DestroyImmediate(firstRoot);
                if (secondRoot != null) UnityEngine.Object.DestroyImmediate(secondRoot);
                if (inactiveByName != null) UnityEngine.Object.DestroyImmediate(inactiveByName);
                if (!wasDirty && scene.IsValid())
                {
                    var clearDirtiness = typeof(EditorSceneManager).GetMethod(
                        "ClearSceneDirtiness",
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    clearDirtiness?.Invoke(null, new object[] { scene });
                }
            }
        }

        private static T GetProperty<T>(object obj, string name)
        {
            Assert.NotNull(obj);
            var property = obj.GetType().GetProperty(name);
            Assert.NotNull(property, $"Missing property '{name}' on {obj.GetType().FullName}.");
            return (T)property.GetValue(obj);
        }

        [Test]
        public void HierarchyAndSceneInfo_IncludeLoadedAdditiveScenes()
        {
            var originalSetup = EditorSceneManager.GetSceneManagerSetup();
            bool canRestoreOriginalSetup = CanRestoreSceneSetup(originalSetup);
            if (!Application.isBatchMode && !canRestoreOriginalSetup)
                Assert.Ignore("Skipping additive-scene test because the interactive editor has unsaved untitled scenes.");

            Scene additiveScene = default;

            string suffix = Guid.NewGuid().ToString("N");
            string tempFolder = "Assets/__FunplayMcpSceneHierarchyTests";
            string activeScenePath = tempFolder + "/Active_" + suffix + ".unity";
            string additiveScenePath = tempFolder + "/Additive_" + suffix + ".unity";
            string activeRootName = "FunplayActiveRoot_" + suffix;
            string additiveRootName = "FunplayAdditiveRoot_" + suffix;
            string inactiveRootName = "FunplayInactiveAdditiveRoot_" + suffix;

            try
            {
                EnsureFolder(tempFolder);

                var activeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Assert.IsTrue(EditorSceneManager.SaveScene(activeScene, activeScenePath));
                var activeRoot = new GameObject(activeRootName);

                additiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.IsTrue(additiveScene.IsValid());
                Assert.IsTrue(EditorSceneManager.SaveScene(additiveScene, additiveScenePath));
                var additiveRoot = new GameObject(additiveRootName);
                SceneManager.MoveGameObjectToScene(additiveRoot, additiveScene);
                var inactiveRoot = new GameObject(inactiveRootName);
                SceneManager.MoveGameObjectToScene(inactiveRoot, additiveScene);
                inactiveRoot.SetActive(false);

                Assert.IsTrue(SceneManager.SetActiveScene(activeScene));

                var hierarchy = HierarchyFunctions.GetHierarchy(
                    depth: 1,
                    include_components: false,
                    include_inactive: true);

                Assert.That(hierarchy, Does.Contain("Scene: " + activeScene.name));
                Assert.That(hierarchy, Does.Contain(activeRootName));
                Assert.That(hierarchy, Does.Contain("Scene: " + additiveScene.name + " (additive)"));
                Assert.That(hierarchy, Does.Contain(additiveRootName));
                Assert.That(hierarchy, Does.Contain(inactiveRootName + " [INACTIVE]"));

                var rootLookup = HierarchyFunctions.GetHierarchy(
                    root_name: inactiveRootName,
                    depth: 1,
                    include_components: false,
                    include_inactive: true);

                Assert.That(rootLookup, Does.Contain(inactiveRootName + " [INACTIVE]"));
                Assert.That(rootLookup, Does.Not.Contain("GAME_OBJECT_NOT_FOUND"));

                var sceneInfo = SceneFunctions.GetSceneInfo();
                Assert.IsTrue(GetProperty<bool>(sceneInfo, "success"));
                var data = GetProperty<object>(sceneInfo, "data");
                Assert.AreEqual(2, GetProperty<int>(data, "count"));
                var scenes = ((IEnumerable)GetProperty<object>(data, "scenes")).Cast<object>().ToList();

                var activeInfo = scenes.Single(item => GetProperty<string>(item, "name") == activeScene.name);
                Assert.IsTrue(GetProperty<bool>(activeInfo, "active"));
                Assert.IsTrue(GetProperty<bool>(activeInfo, "isLoaded"));
                Assert.AreEqual(activeScenePath, GetProperty<string>(activeInfo, "path"));
                var activeRoots = ((IEnumerable)GetProperty<object>(activeInfo, "rootObjects")).Cast<object>().ToList();
                var activeRootInfo = activeRoots.Single(item => GetProperty<string>(item, "name") == activeRootName);
                Assert.AreEqual(
                    ObjectIdHelper.GetSerializableId(activeRoot),
                    GetProperty<string>(activeRootInfo, "instanceId"));

                var additiveInfo = scenes.Single(item => GetProperty<string>(item, "name") == additiveScene.name);
                Assert.IsFalse(GetProperty<bool>(additiveInfo, "active"));
                Assert.IsTrue(GetProperty<bool>(additiveInfo, "isLoaded"));
                Assert.AreEqual(additiveScenePath, GetProperty<string>(additiveInfo, "path"));
                var additiveRoots = ((IEnumerable)GetProperty<object>(additiveInfo, "rootObjects")).Cast<object>().ToList();
                CollectionAssert.AreEquivalent(
                    new[] { additiveRootName, inactiveRootName },
                    additiveRoots.Select(item => GetProperty<string>(item, "name")).ToArray());
                var additiveRootInfo = additiveRoots.Single(item => GetProperty<string>(item, "name") == additiveRootName);
                Assert.AreEqual(
                    ObjectIdHelper.GetSerializableId(additiveRoot),
                    GetProperty<string>(additiveRootInfo, "instanceId"));
            }
            finally
            {
                if (canRestoreOriginalSetup)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
                }
                else if (Application.isBatchMode)
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }

                if (AssetDatabase.IsValidFolder(tempFolder))
                    AssetDatabase.DeleteAsset(tempFolder);
            }
        }

        private static bool CanRestoreSceneSetup(SceneSetup[] setup)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.isDirty)
                    return false;
            }

            foreach (var scene in setup)
            {
                if (string.IsNullOrEmpty(scene.path) || !File.Exists(scene.path))
                    return false;
            }

            return setup.Length > 0;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent))
                throw new InvalidOperationException("Temporary test folder must be under Assets.");

            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
