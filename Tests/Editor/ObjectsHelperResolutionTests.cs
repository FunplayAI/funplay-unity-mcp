// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.Tests
{
    public sealed class ObjectsHelperResolutionTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private Scene _scene;
        private bool _wasSceneDirty;

        [SetUp]
        public void SetUp()
        {
            _scene = SceneManager.GetActiveScene();
            _wasSceneDirty = _scene.IsValid() && _scene.isDirty;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var gameObject in _objects)
            {
                if (gameObject != null)
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
            _objects.Clear();
            RestoreSceneDirtiness(_scene, _wasSceneDirty);
        }

        [Test]
        public void AutoDetect_NumericNameFallsBackWhenNoGameObjectIdResolves()
        {
            var namedObject = CreateObject("PendingNumericName");
            var numericName = FindUnresolvedNumericGameObjectId();
            Assert.That(ObjectIdHelper.GetSerializableId(namedObject), Is.Not.EqualTo(numericName));
            namedObject.name = numericName;

            Assert.That(
                ObjectsHelper.FindObject(numericName, searchInactive: true),
                Is.SameAs(namedObject));
            Assert.That(
                ObjectsHelper.FindObject(
                    numericName,
                    ObjectsHelper.MethodById,
                    searchInactive: true),
                Is.Null,
                "Explicit by_id must stay strict.");
            Assert.That(
                ObjectsHelper.FindObject(
                    numericName,
                    ObjectsHelper.MethodByName,
                    searchInactive: true),
                Is.SameAs(namedObject));

            var toolResult = GameObjectFunctions.FindGameObjects(
                numericName,
                include_inactive: "true");
            Assert.That(GetProperty<bool>(toolResult, "success"), Is.True);
            var matches = ((IEnumerable)GetProperty<object>(toolResult, "data"))
                .Cast<object>()
                .ToList();
            Assert.That(matches, Has.Count.EqualTo(1));
            Assert.That(GetProperty<string>(matches[0], "name"), Is.EqualTo(numericName));
            Assert.That(
                GetProperty<string>(matches[0], "instanceId"),
                Is.EqualTo(ObjectIdHelper.GetSerializableId(namedObject)));
        }

        [Test]
        public void AutoDetect_ValidGameObjectIdWinsOverSameNumericName()
        {
            var idTarget = CreateObject("IdTarget");
            var id = ObjectIdHelper.GetSerializableId(idTarget);
            Assert.That(long.TryParse(id, out _), Is.True, "Unity object IDs must stay numeric.");

            var sameNamedObject = CreateObject(id);
            Assert.That(ObjectIdHelper.GetSerializableId(sameNamedObject), Is.Not.EqualTo(id));

            var automatic = ObjectsHelper.FindObjects(
                id,
                findAll: true,
                searchInactive: true);
            Assert.That(automatic, Is.EqualTo(new[] { idTarget }));
            Assert.That(
                ObjectsHelper.FindObject(id, ObjectsHelper.MethodById, searchInactive: true),
                Is.SameAs(idTarget));
            Assert.That(
                ObjectsHelper.FindObject(id, ObjectsHelper.MethodByName, searchInactive: true),
                Is.SameAs(sameNamedObject));
        }

        private GameObject CreateObject(string name)
        {
            var gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }

        private static string FindUnresolvedNumericGameObjectId()
        {
            const long start = 2000000000L;
            for (var offset = 0; offset < 10000; offset++)
            {
                var candidate = (start - offset).ToString();
                if (!(ObjectIdHelper.ToObject(candidate) is GameObject))
                    return candidate;
            }

            Assert.Fail("Could not find an unused numeric GameObject identifier for the test.");
            return null;
        }

        private static T GetProperty<T>(object target, string name)
        {
            Assert.That(target, Is.Not.Null);
            var property = target.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, $"Missing property '{name}' on {target.GetType().Name}.");
            return (T)property.GetValue(target);
        }

        private static void RestoreSceneDirtiness(Scene scene, bool wasDirty)
        {
            if (wasDirty || !scene.IsValid())
                return;

            var method = typeof(EditorSceneManager).GetMethod(
                "ClearSceneDirtiness",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(null, new object[] { scene });
        }
    }
}
