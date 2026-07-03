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
    public sealed class PrefabStageFunctionsTests
    {
        [Test]
        public void PrefabStageTools_SaveDiscardAndProtectDirtyStage()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                Assert.Ignore("Skipping prefab-stage tool test because a Prefab Stage is already open in the interactive editor.");

            var suffix = Guid.NewGuid().ToString("N");
            var tempFolder = "Assets/__FunplayMcpPrefabStageTests";
            var prefabPath = tempFolder + "/Primary_" + suffix + ".prefab";
            var otherPrefabPath = tempFolder + "/Other_" + suffix + ".prefab";
            var savedChildName = "SavedChild_" + suffix;
            var discardedChildName = "DiscardedChild_" + suffix;

            try
            {
                EnsureFolder(tempFolder);
                CreatePrefabAsset(prefabPath, "PrimaryRoot_" + suffix);
                CreatePrefabAsset(otherPrefabPath, "OtherRoot_" + suffix);

                Assert.That(PrefabFunctions.OpenPrefabStage(tempFolder + "/Missing.prefab"), Does.Contain("PREFAB_NOT_FOUND"));

                Assert.That(PrefabFunctions.OpenPrefabStage(prefabPath), Does.Contain("Prefab stage opened"));
                AddChildToCurrentStage(savedChildName);

                Assert.That(PrefabFunctions.OpenPrefabStage(prefabPath), Does.Contain("Prefab stage already open"));
                Assert.That(CurrentStageRoot().transform.Find(savedChildName), Is.Not.Null);

                Assert.That(PrefabFunctions.OpenPrefabStage(otherPrefabPath), Does.Contain("ANOTHER_STAGE_DIRTY"));

                Assert.That(PrefabFunctions.SavePrefabStage(), Does.Contain("Prefab stage saved"));
                Assert.That(PrefabFunctions.ClosePrefabStage(save: true), Does.Contain("Prefab stage closed"));

                var savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.That(savedPrefab.transform.Find(savedChildName), Is.Not.Null);

                Assert.That(PrefabFunctions.OpenPrefabStage(prefabPath), Does.Contain("Prefab stage opened"));
                AddChildToCurrentStage(discardedChildName);
                Assert.That(PrefabFunctions.ClosePrefabStage(save: false), Does.Contain("edits discarded"));

                savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.That(savedPrefab.transform.Find(savedChildName), Is.Not.Null);
                Assert.That(savedPrefab.transform.Find(discardedChildName), Is.Null);
                Assert.That(PrefabFunctions.SavePrefabStage(), Does.Contain("NO_PREFAB_STAGE_OPEN"));
            }
            finally
            {
                CloseAnyPrefabStageDiscardingChanges();
                if (AssetDatabase.IsValidFolder(tempFolder))
                    AssetDatabase.DeleteAsset(tempFolder);
            }
        }

        private static void CreatePrefabAsset(string path, string rootName)
        {
            var root = new GameObject(rootName);
            try
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                Assert.That(prefab, Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static GameObject CurrentStageRoot()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Assert.That(stage, Is.Not.Null);
            return stage.prefabContentsRoot;
        }

        private static void AddChildToCurrentStage(string childName)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Assert.That(stage, Is.Not.Null);

            var child = new GameObject(childName);
            SceneManager.MoveGameObjectToScene(child, stage.scene);
            child.transform.SetParent(stage.prefabContentsRoot.transform, false);
            EditorSceneManager.MarkSceneDirty(stage.scene);
        }

        private static void CloseAnyPrefabStageDiscardingChanges()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return;

            stage.ClearDirtiness();
            StageUtility.GoToMainStage();
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
