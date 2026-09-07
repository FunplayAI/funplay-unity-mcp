// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Services;
using Funplay.Editor.Settings;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Funplay.Editor.Tests
{
    public sealed class RecentActivityPanelTests
    {
        private EditorWindow _window;
        private MCPInteractionLog _log;
        private FunplayMCPRecentActivityPanel _panel;
        private string _settingsProjectPath;
        private readonly Queue<Action> _callbacks = new Queue<Action>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (Application.isBatchMode ||
                SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("These UI tests require a graphics-enabled interactive Unity Editor.");
            _log = null;
            _panel = null;
            _settingsProjectPath = null;
            _callbacks.Clear();
            _window = ScriptableObject.CreateInstance<EditorWindow>();
            _window.titleContent = new GUIContent("Funplay Activity Tests");
            _window.ShowUtility();
            _window.position = new Rect(80, 80, 480, 640);
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            if (_log != null && _panel != null)
                _log.OnEntryAdded -= _panel.OnEntryAdded;
            _panel?.Dispose();
            _callbacks.Clear();
            if (_window != null)
                _window.Close();
            if (_settingsProjectPath != null && Directory.Exists(_settingsProjectPath))
                Directory.Delete(_settingsProjectPath, true);
        }

        [Test]
        public void History_OnlyLatestBuildsDetails_AndSummariesAreReadable()
        {
            _log = new MCPInteractionLog();
            _log.Add("first", MCPToolCallStatus.Success,
                "{\"success\":true,\"message\":\"Imported assets.\",\"data\":{\"count\":3}}");
            _log.Add("second", MCPToolCallStatus.Success, "Second result");
            BuildPanel();

            var rows = Rows();
            Assert.AreEqual(2, rows.Length);
            AssertCollapsed(rows[0]);
            AssertExpanded(rows[1]);
            StringAssert.Contains("Imported assets.", Summary(rows[0]).text);
            StringAssert.Contains("Count: 3", Summary(rows[0]).text);
            StringAssert.DoesNotContain("\"success\"", Summary(rows[0]).text);
            StringAssert.DoesNotContain("{", Summary(rows[0]).text);

            Click(Summary(rows[0]));
            AssertExpanded(rows[0]);
            Assert.IsTrue(Details(rows[0]).Query<Label>().ToList().Any(label => label.text == "Count:"));
            Click(Header(rows[0]));
            AssertCollapsed(rows[0]);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Display_KeepsTextBeyondCompactResourceSummary(bool json)
        {
            var message = new string('x', 320) + " visible tail";
            _log = new MCPInteractionLog();
            _log.Add("long", MCPToolCallStatus.Success,
                json ? "{\"success\":true,\"message\":\"" + message + "\"}" : message);
            var entry = _log.GetEntries()[0];
            Assert.LessOrEqual(entry.ResultSummary.Length, 200);
            Assert.AreEqual(message, entry.DisplayResult);
            BuildPanel();
            Assert.AreEqual(message, Summary(Rows()[0]).text);

            _log.Add("bounded", MCPToolCallStatus.Success, new string('x', 9000));
            Assert.LessOrEqual(_log.GetEntries()[0].DisplayResult.Length,
                MCPInteractionLog.MaxDisplayResultCharacters);
        }

        [Test]
        public void NewEntry_CollapsesAutomaticLatest_ButKeepsManualChoices()
        {
            _log = new MCPInteractionLog();
            _log.Add("old", MCPToolCallStatus.Success, "Old");
            _log.Add("latest", MCPToolCallStatus.Success, "Latest");
            BuildPanel();
            var old = Rows()[0];
            var previousLatest = Rows()[1];
            Click(Summary(old));

            _log.Add("next", MCPToolCallStatus.Success, "Next");
            FlushCallbacks();
            Assert.AreEqual(3, Rows().Length);
            AssertExpanded(old);
            AssertCollapsed(previousLatest);
            var latest = Rows().Last();
            AssertExpanded(latest);

            // Manually reopening an auto-expanded row pins it across later entries.
            Click(Header(latest));
            Click(Summary(latest));
            _log.Add("another", MCPToolCallStatus.Success, "Another");
            FlushCallbacks();
            Assert.AreEqual(4, Rows().Length);
            AssertExpanded(latest);
            var manuallyCollapsed = Rows().Last();
            Click(Header(manuallyCollapsed));
            _log.Add("last", MCPToolCallStatus.Success, "Last");
            FlushCallbacks();
            Assert.AreEqual(5, Rows().Length);
            AssertCollapsed(manuallyCollapsed);
            AssertExpanded(old);
            AssertExpanded(latest);
            AssertExpanded(Rows().Last());
        }

        [Test]
        public void ExpandAll_ExpandsHistoryAndNewEntries_ButKeepsManualCollapse()
        {
            var settings = CreateSettings(expandAll: true);
            _log = new MCPInteractionLog();
            _log.Add("old", MCPToolCallStatus.Success, "Old");
            _log.Add("latest", MCPToolCallStatus.Success, "Latest");
            BuildPanel(settings);
            foreach (var row in Rows())
                AssertExpanded(row);
            var manuallyCollapsed = Rows()[0];
            Click(Header(manuallyCollapsed));

            _log.Add("new", MCPToolCallStatus.Success, "New");
            FlushCallbacks();
            AssertCollapsed(manuallyCollapsed);
            AssertExpanded(Rows()[1]);
            AssertExpanded(Rows()[2]);
        }

        [Test]
        public void ExpansionPreference_AppliesLiveWithoutOverridingManualChoices()
        {
            var settings = CreateSettings(expandAll: false);
            _log = new MCPInteractionLog();
            _log.Add("manual-open", MCPToolCallStatus.Success, "Manually open");
            _log.Add("automatic", MCPToolCallStatus.Success, "Follow the preference");
            _log.Add("manual-closed", MCPToolCallStatus.Success, "Manually closed");
            BuildPanel(settings);
            var rows = Rows();
            Click(Summary(rows[0]));
            Click(Header(rows[2]));

            settings.MCPRecentActivityExpandedByDefault = true;
            AssertExpanded(rows[0]);
            AssertExpanded(rows[1]);
            AssertCollapsed(rows[2]);
            _log.Add("latest", MCPToolCallStatus.Success, "Latest");
            FlushCallbacks();

            settings.MCPRecentActivityExpandedByDefault = false;
            AssertExpanded(rows[0]);
            AssertCollapsed(rows[1]);
            AssertCollapsed(rows[2]);
            AssertExpanded(Rows().Last());
            settings.MCPRecentActivityExpandedByDefault = true;
            AssertExpanded(rows[1]);
            AssertCollapsed(rows[2]);
        }

        [Test]
        public void DisablingExpandAll_ReleasesHistoricalTextures_AndHandlesEmptyLatest()
        {
            var settings = CreateSettings(expandAll: true);
            _log = new MCPInteractionLog();
            _log.Add("image", MCPToolCallStatus.Success, MakeImage());
            _log.Add("empty", MCPToolCallStatus.Success, string.Empty);
            BuildPanel(settings);
            var imageRow = Rows()[0];
            var texture = imageRow.Q<Image>().image as Texture2D;
            Assert.IsTrue(texture != null);

            settings.MCPRecentActivityExpandedByDefault = false;
            AssertCollapsed(imageRow);
            Assert.IsTrue(texture == null);
            settings.MCPRecentActivityExpandedByDefault = true;
            AssertExpanded(imageRow);
        }

        [Test]
        public void ReopeningPanel_ReadsPersistedPreference_AndResetsManualOverrides()
        {
            var settings = CreateSettings(expandAll: true);
            _log = new MCPInteractionLog();
            _log.Add("old", MCPToolCallStatus.Success, "Old");
            _log.Add("latest", MCPToolCallStatus.Success, "Latest");
            BuildPanel(settings);
            Click(Header(Rows()[0]));
            AssertCollapsed(Rows()[0]);

            _log.OnEntryAdded -= _panel.OnEntryAdded;
            _panel.Dispose();
            _window.rootVisualElement.Clear();
            var reloaded = new SettingsController(new TestApplicationPaths(_settingsProjectPath));
            BuildPanel(reloaded);
            foreach (var row in Rows())
                AssertExpanded(row);
        }

        [Test]
        public void UnrelatedSettingsChange_DoesNotRebuildExpandedDetails()
        {
            var settings = CreateSettings(expandAll: true);
            _log = new MCPInteractionLog();
            _log.Add("image", MCPToolCallStatus.Success, MakeImage());
            BuildPanel(settings);
            var image = Rows()[0].Q<Image>();
            var texture = image.image;

            settings.PluginDebugLoggingEnabled = true;
            Assert.AreSame(image, Rows()[0].Q<Image>());
            Assert.AreSame(texture, image.image);
            AssertExpanded(Rows()[0]);
        }

        [Test]
        public void Screenshot_IsDecodedOnExpand_AndDestroyedOnCollapse()
        {
            _log = new MCPInteractionLog();
            _log.Add("image", MCPToolCallStatus.Success, MakeImage());
            _log.Add("latest", MCPToolCallStatus.Success, "Latest");
            BuildPanel();
            var row = Rows()[0];
            AssertCollapsed(row);
            Assert.IsNull(row.Q<Image>());

            Click(Summary(row));
            var firstTexture = row.Q<Image>().image as Texture2D;
            Assert.IsNotNull(firstTexture);
            Assert.AreEqual(4, firstTexture.width);
            Click(Header(row));
            AssertCollapsed(row);
            Assert.IsTrue(firstTexture == null, "Collapsing must destroy the decoded Unity texture.");

            Click(Summary(row));
            var nextTexture = row.Q<Image>().image as Texture2D;
            Assert.IsTrue(nextTexture != null);
            _panel.Dispose();
            Assert.IsTrue(nextTexture == null, "Disposing must release manually expanded previews.");
        }

        [Test]
        public void AutomaticCollapse_ReleasesScreenshot()
        {
            _log = new MCPInteractionLog();
            _log.Add("image", MCPToolCallStatus.Success, MakeImage());
            BuildPanel();
            var row = Rows()[0];
            var texture = row.Q<Image>().image as Texture2D;
            Assert.IsTrue(texture != null);
            _log.Add("next", MCPToolCallStatus.Success, "Next");
            FlushCallbacks();
            Assert.AreEqual(2, Rows().Length);
            AssertCollapsed(row);
            Assert.IsTrue(texture == null);
        }

        [Test]
        public void Clear_RemovesRowsAndTextures_AndInvalidatesQueuedEntries()
        {
            _log = new MCPInteractionLog();
            _log.Add("image", MCPToolCallStatus.Success, MakeImage());
            BuildPanel();
            var texture = Rows()[0].Q<Image>().image as Texture2D;
            _log.Add("queued", MCPToolCallStatus.Success, "Must not reappear after Clear");

            var button = _window.rootVisualElement.Q<Button>("recent-activity-clear");
            using (var evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = button;
                button.SendEvent(evt);
            }
            Assert.IsEmpty(_log.GetEntries());
            Assert.IsEmpty(Rows());
            Assert.IsTrue(texture == null);
            FlushCallbacks();
            Assert.IsEmpty(Rows());

            _log.Add("fresh", MCPToolCallStatus.Success, "Fresh");
            FlushCallbacks();
            Assert.AreEqual(1, Rows().Length);
            AssertExpanded(Rows()[0]);
        }

        [Test]
        public void Capacity_EvictsOldRowsAndTheirTextures()
        {
            _log = new MCPInteractionLog(2);
            _log.Add("image", MCPToolCallStatus.Success, MakeImage());
            _log.Add("second", MCPToolCallStatus.Success, "Second");
            BuildPanel();
            var oldest = Rows()[0];
            Click(Summary(oldest));
            var texture = oldest.Q<Image>().image as Texture2D;

            _log.Add("third", MCPToolCallStatus.Success, "Third");
            FlushCallbacks();
            Assert.AreEqual(2, Rows().Length);
            Assert.IsNull(oldest.parent);
            Assert.IsTrue(texture == null);
            Assert.AreEqual("Third", Summary(Rows().Last()).text);
        }

        [Test]
        public void RebuildAndDispose_InvalidateQueuedCallbacks()
        {
            _log = new MCPInteractionLog();
            _log.Add("image", MCPToolCallStatus.Success, MakeImage());
            BuildPanel();
            var texture = Rows()[0].Q<Image>().image as Texture2D;
            _log.Add("queued", MCPToolCallStatus.Success, "Queued");
            _window.rootVisualElement.Clear();
            _panel.AddTo(_window.rootVisualElement);
            Assert.IsTrue(texture == null);
            FlushCallbacks();
            Assert.AreEqual(2, Rows().Length, "The history rebuild already includes the queued entry.");

            _log.Add("disposed", MCPToolCallStatus.Success, "Must not appear");
            _panel.Dispose();
            FlushCallbacks();
            Assert.IsEmpty(Rows());
        }

        [UnityTest]
        public IEnumerator Tooltip_UsesSameReadableTextOnHeaderChildAndSummary()
        {
            _log = new MCPInteractionLog();
            _log.Add("json", MCPToolCallStatus.Error,
                "{\"success\":false,\"code\":\"EXAMPLE_ERROR\",\"error\":\"Readable error\"}");
            BuildPanel();
            var row = Rows()[0];
            Click(Header(row));
            yield return WaitFor(() => row.worldBound.width > 0);
            foreach (var target in new VisualElement[] { Header(row), Header(row).Q<Label>(), Summary(row) })
            {
                using (var evt = TooltipEvent.GetPooled())
                {
                    evt.target = target;
                    target.SendEvent(evt);
                    Assert.AreEqual(_log.GetEntries()[0].DisplayResult, evt.tooltip);
                    Assert.Greater(evt.rect.width, 0);
                }
            }
        }

        [UnityTest]
        public IEnumerator Summary_ResizesNarrowWideAndBackWithoutLosingText()
        {
            var message = new string('x', 320) + " visible tail";
            _log = new MCPInteractionLog();
            _log.Add("long", MCPToolCallStatus.Success, message);
            BuildPanel();
            Click(Header(Rows()[0]));
            var label = Summary(Rows()[0]);
            yield return WaitFor(() => label.resolvedStyle.width > 0);
            var narrow = label.resolvedStyle.width;
            Assert.Greater(narrow, 0);
            _window.position = new Rect(80, 80, 1100, 640);
            yield return WaitFor(() => label.resolvedStyle.width > narrow + 400);
            var wide = label.resolvedStyle.width;
            Assert.Greater(wide, narrow + 400);
            _window.position = new Rect(80, 80, 450, 640);
            yield return WaitFor(() => label.resolvedStyle.width < wide - 400);
            Assert.Less(label.resolvedStyle.width, wide - 400);
            Assert.AreEqual(message, label.text);
        }

        [Test]
        public void InvalidScreenshot_DoesNotPreventDetailsOrToggling()
        {
            _log = new MCPInteractionLog();
            _log.Add("bad-image", MCPToolCallStatus.Success, "data:image/png;base64,not base64!");
            BuildPanel();
            var row = Rows()[0];
            AssertExpanded(row);
            Assert.IsNull(row.Q<Image>());
            Click(Header(row));
            AssertCollapsed(row);
            Click(Summary(row));
            AssertExpanded(row);
        }

        private void BuildPanel(ISettingsController settings = null)
        {
            // Do not invoke Unity's global delayCall (which may contain unrelated project
            // callbacks). Control only this panel's queue to test clear/rebuild races exactly.
            _panel = new FunplayMCPRecentActivityPanel(_log, callback => _callbacks.Enqueue(callback),
                settings ?? CreateSettings(expandAll: false));
            _panel.AddTo(_window.rootVisualElement);
            _log.OnEntryAdded += _panel.OnEntryAdded;
        }

        private SettingsController CreateSettings(bool expandAll)
        {
            _settingsProjectPath = Path.Combine(Path.GetTempPath(), "FunplayActivityTests_" + Guid.NewGuid().ToString("N"));
            return new SettingsController(new TestApplicationPaths(_settingsProjectPath))
            {
                MCPRecentActivityExpandedByDefault = expandAll
            };
        }

        private sealed class TestApplicationPaths : IApplicationPaths
        {
            public TestApplicationPaths(string projectPath) { ProjectPath = projectPath; }
            public string ProjectPath { get; }
            public string AssetsPath => Path.Combine(ProjectPath, "Assets");
            public string TempPath => Path.Combine(ProjectPath, "Temp", "Funplay");
            public string DataPath => AssetsPath;
            public string PersistentDataPath => Path.Combine(ProjectPath, "PersistentData");
        }

        private VisualElement[] Rows() => _window.rootVisualElement
            .Q<ScrollView>("recent-activity-scroll").contentContainer.Children().ToArray();
        private static VisualElement Header(VisualElement row) => row.Q("recent-activity-header");
        private static VisualElement Details(VisualElement row) => row.Q("recent-activity-details");
        private static Label Summary(VisualElement row) => row.Q<Label>("recent-activity-summary");

        private static void Click(VisualElement target)
        {
            using (var evt = ClickEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, clickCount = 1 }))
            {
                evt.target = target;
                target.SendEvent(evt);
            }
        }

        private static IEnumerator WaitFor(Func<bool> ready)
        {
            var deadline = EditorApplication.timeSinceStartup + 5;
            while (!ready())
            {
                Assert.Less(EditorApplication.timeSinceStartup, deadline, "Timed out waiting for the Editor UI.");
                yield return null;
            }
        }

        private void FlushCallbacks()
        {
            while (_callbacks.Count > 0)
                _callbacks.Dequeue()();
        }

        private static void AssertCollapsed(VisualElement row)
        {
            Assert.AreEqual(DisplayStyle.None, Details(row).style.display.value);
            Assert.AreEqual(0, Details(row).childCount, "Hidden rows must not retain detail subtrees.");
            Assert.AreEqual(DisplayStyle.Flex, Summary(row).style.display.value);
        }

        private static void AssertExpanded(VisualElement row)
        {
            Assert.AreEqual(DisplayStyle.Flex, Details(row).style.display.value);
            Assert.Greater(Details(row).childCount, 0);
            Assert.AreEqual(DisplayStyle.None, Summary(row).style.display.value);
        }

        private static string MakeImage()
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(Enumerable.Repeat(Color.cyan, 16).ToArray());
                texture.Apply();
                return "data:image/png;base64," + Convert.ToBase64String(texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
