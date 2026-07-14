// Copyright (C) Funplay. Licensed under MIT.

using System;
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

        // Recursively collect property names and leaf values from a structured (anonymous-object) tool
        // result into one string, so assertions can verify the payload without a JSON dependency.
        private static string DumpValues(object o)
        {
            if (o == null) return "";
            if (o is string s) return s + " ";
            if (o is System.Collections.IEnumerable en)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in en) sb.Append(DumpValues(item));
                return sb.ToString();
            }
            var t = o.GetType();
            if (t.IsPrimitive) return o + " ";
            var sb2 = new System.Text.StringBuilder();
            foreach (var p in t.GetProperties())
                sb2.Append(p.Name).Append('=').Append(DumpValues(p.GetValue(o)));
            return sb2.ToString();
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
                new GameObject(activeRootName);

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

                // GetSceneInfo now returns a structured object ({success,message,data:{count,scenes:[{name,path,active,isDirty,rootObjects}]}}).
                // Flatten it (no JSON dependency in the test assembly) and assert the scene names, the
                // active flag, and the root object names are all present in the payload.
                var sceneInfo = SceneFunctions.GetSceneInfo();
                var sceneInfoDump = DumpValues(sceneInfo);
                Assert.That(sceneInfoDump, Does.Contain(activeScene.name));
                Assert.That(sceneInfoDump, Does.Contain("active=True"));
                Assert.That(sceneInfoDump, Does.Contain(activeRootName));
                Assert.That(sceneInfoDump, Does.Contain(additiveScene.name));
                Assert.That(sceneInfoDump, Does.Contain(additiveRootName));
                Assert.That(sceneInfoDump, Does.Contain(inactiveRootName));
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
