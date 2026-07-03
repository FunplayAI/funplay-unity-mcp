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
    public sealed class PerformanceFunctionsTests
    {
        [Test]
        public void PerformanceTools_CountObjectsAcrossLoadedAdditiveScenes()
        {
            var originalSetup = EditorSceneManager.GetSceneManagerSetup();
            bool canRestoreOriginalSetup = CanRestoreSceneSetup(originalSetup);
            if (!Application.isBatchMode && !canRestoreOriginalSetup)
                Assert.Ignore("Skipping performance multi-scene test because the interactive editor has unsaved untitled scenes.");

            var suffix = Guid.NewGuid().ToString("N");
            var tempFolder = "Assets/__FunplayMcpPerformanceTests";
            var activeScenePath = tempFolder + "/Active_" + suffix + ".unity";
            var additiveScenePath = tempFolder + "/Additive_" + suffix + ".unity";

            try
            {
                EnsureFolder(tempFolder);

                var activeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Assert.IsTrue(EditorSceneManager.SaveScene(activeScene, activeScenePath));
                new GameObject("PerformanceActiveRoot_" + suffix);

                var additiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.IsTrue(EditorSceneManager.SaveScene(additiveScene, additiveScenePath));
                var additiveCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                additiveCube.name = "PerformanceAdditiveCube_" + suffix;
                SceneManager.MoveGameObjectToScene(additiveCube, additiveScene);

                Assert.IsTrue(SceneManager.SetActiveScene(activeScene));

                var snapshot = PerformanceFunctions.GetPerformanceSnapshot(include_scene_counts: true);
                Assert.That(snapshot, Does.Contain("Scene(s): " + activeScene.name + ", " + additiveScene.name));
                Assert.That(snapshot, Does.Contain("- GameObjects: 2 total, 2 active"));
                Assert.That(snapshot, Does.Contain("- Renderers: 1"));
                Assert.That(snapshot, Does.Contain("- Triangles: 12"));
                Assert.That(snapshot, Does.Contain("- Colliders: 1"));

                var analysis = PerformanceFunctions.AnalyzeSceneComplexity(top_n: 5, include_inactive: true);
                Assert.That(analysis, Does.Contain("Scene(s): " + activeScene.name + ", " + additiveScene.name));
                Assert.That(analysis, Does.Contain("Objects: 2 total, 2 active"));
                Assert.That(analysis, Does.Contain("Renderers: 1, Triangles: 12"));
                Assert.That(analysis, Does.Contain("Physics: 1 colliders"));
                Assert.That(analysis, Does.Contain("PerformanceAdditiveCube_" + suffix));
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
