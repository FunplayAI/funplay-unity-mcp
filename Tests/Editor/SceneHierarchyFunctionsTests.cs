// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.Tools.Builtins;
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

                var sceneInfo = SceneFunctions.GetSceneInfo();
                Assert.That(sceneInfo, Does.Contain("Scene: " + activeScene.name + " (active)"));
                Assert.That(sceneInfo, Does.Contain(activeRootName));
                Assert.That(sceneInfo, Does.Contain("Scene: " + additiveScene.name + " (additive)"));
                Assert.That(sceneInfo, Does.Contain(additiveRootName));
                Assert.That(sceneInfo, Does.Contain(inactiveRootName));
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
