// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.State;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace Funplay.Editor.Tests
{
    public sealed class ContinuousDragTests
    {
        [TestCase(0.05f, null)][TestCase(10f, null)][TestCase(0f, "INVALID_DRAG_DURATION")]
        [TestCase(-1f, "INVALID_DRAG_DURATION")][TestCase(11f, "INVALID_DRAG_DURATION")]
        [TestCase(float.NaN, "INVALID_DRAG_DURATION")][TestCase(float.PositiveInfinity, "INVALID_DRAG_DURATION")]
        public void DurationIsValidatedNotSilentlyClamped(float duration, string error)
            => Assert.AreEqual(error, InputFrameJob.ValidateDrag(duration, "left", "mouse", 0, 0));

        [TestCase("left", "mouse", null)][TestCase("right", "mouse", null)][TestCase("middle", "mouse", null)]
        [TestCase("left", "touch", null)][TestCase("right", "touch", "TOUCH_REQUIRES_LEFT_BUTTON")]
        [TestCase("typo", "mouse", "INVALID_MOUSE_BUTTON")][TestCase("left", "pen", "INVALID_POINTER_DEVICE")]
        public void BackendAndButtonAreExplicit(string button, string pointer, string error)
            => Assert.AreEqual(error, InputFrameJob.ValidateDrag(0.5f, button, pointer, 0, 0));

        [TestCase(-1f, 0f)][TestCase(0f, -1f)][TestCase(6f, 0f)][TestCase(0f, 6f)]
        [TestCase(float.NaN, 0f)][TestCase(0f, float.PositiveInfinity)]
        public void InvalidHoldDurationsFail(float press, float end)
            => Assert.AreEqual("INVALID_DRAG_HOLD_DURATION", InputFrameJob.ValidateDrag(0.5f, "left", "mouse", press, end));

        [Test] public void UnsupportedRoutesAndInvalidControlNeverDispatch()
        {
            foreach (var pair in new[] {
                new { route = "legacy_polling", code = "LEGACY_POLLING_INJECTION_UNSUPPORTED" },
                new { route = "auto", code = "INVALID_INPUT_ROUTE" },
                new { route = "direct_button", code = "INVALID_INPUT_ROUTE" } })
                Assert.AreEqual(pair.code, (string)JObject.FromObject(InputInteractionFunctions.SimulateMouseDrag(input_route: pair.route))["code"]);
            Assert.AreEqual("INVALID_INPUT_ACTION", (string)JObject.FromObject(InputInteractionFunctions.SimulateMouseDrag(action: "status"))["code"]);
            Assert.AreEqual("INVALID_INPUT_TASK_ID", (string)JObject.FromObject(InputInteractionFunctions.SimulateMouseDrag(action: "cancel"))["code"]);
        }

        [Test] public void ExistingToolAndUnifiedReadKeepTheirPermissionsAndCoreCount()
        {
            var method = typeof(InputInteractionFunctions).GetMethod("SimulateMouseDrag");
            Assert.IsFalse(ToolRegistry.IsReadOnly(method));
            Assert.IsTrue(TaskStatusService.CanRead("input", x => x == "simulate_mouse_drag"));
            Assert.IsFalse(TaskStatusService.CanRead("input", x => x == "get_task"));
            Assert.AreEqual(40, MCPToolExportPolicy.DefaultCoreTools.Count);
            string id = null, kind = "input";
            Assert.IsNull(TaskStatusService.Resolve(ref id, ref kind, "lost-drag-response"));
        }

        [Test] public void DragParticipatesInBoundedMcpWaitWithoutASecondQueryTool()
        {
            var receipt = TaskStatusService.Describe("input", new { success = true, data = new { job_id = Guid.Empty.ToString("N"), status = "completed" } });
            var result = JObject.Parse(MCPTaskWaiter.CompleteAsync("simulate_mouse_drag", new Dictionary<string, object>(), receipt.ToString(), null, CancellationToken.None).GetAwaiter().GetResult());
            Assert.AreEqual("completed", (string)result["data"]["task"]["wait_reason"]);
            Assert.AreEqual("INVALID_WAIT", (string)JObject.FromObject(InputInteractionFunctions.SimulateMouseDrag(wait_seconds: 31))["code"]);
        }

        [Test] public void EvidenceIsBoundedWithoutLosingPhaseEdgesOrInventingAcceptance()
        {
            var job = new InputFrameReceipt();
            InputFrameJob.AddEdge(job, new InputFrameEdge { frame = 1, down = true, milestone = true });
            for (int i = 2; i <= 1000; i++) InputFrameJob.AddEdge(job, new InputFrameEdge { frame = i, held = true, milestone = i == 50 });
            InputFrameJob.AddEdge(job, new InputFrameEdge { frame = 1001, up = true, milestone = true });
            Assert.AreEqual(1001, job.observed_frames); Assert.AreEqual(256, job.edges.Count);
            Assert.IsTrue(job.edge_samples_truncated); Assert.AreEqual(1, job.edges[0].frame);
            Assert.IsTrue(job.edges.Any(x => x.frame == 50)); Assert.IsTrue(job.edges.Last().up);
            Assert.IsNull(job.application_accepted);
            var roundtrip = JsonConvert.DeserializeObject<InputFrameReceipt>(JsonConvert.SerializeObject(job));
            Assert.AreEqual(256, roundtrip.edges.Count);
            var point = JsonConvert.DeserializeObject<InputFramePoint>(JsonConvert.SerializeObject(new InputFramePoint(new Vector2(12, 34))));
            Assert.AreEqual(new Vector2(12, 34), point.Vector);
        }

#if ENABLE_INPUT_SYSTEM
        internal sealed class OwnedInputSettings : IDisposable
        {
            private readonly InputSettings original = InputSystem.settings;
            private readonly InputSettings temporary;
            internal OwnedInputSettings()
            {
                // Isolate the test from the user's other foreground Editors. No project
                // asset is modified and the production tool never changes these settings.
                temporary = UnityEngine.Object.Instantiate(original);
                temporary.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                temporary.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                InputSystem.settings = temporary;
            }
            public void Dispose() { InputSystem.settings = original; UnityEngine.Object.DestroyImmediate(temporary); }
        }
        private static void PrepareOwnedScene()
        {
            if (!Environment.GetCommandLineArgs().Contains("-funplayOwnedTestSession")) Assert.Ignore("Requires an explicitly owned FunplayMcp test session.");
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        }
        private static IEnumerator WaitForOwnedFrame()
        {
            Assert.IsTrue((bool)JObject.FromObject(InputFrameJob.Availability())["available"], "Dynamic Input System required for actual-frame tests.");
            double deadline = EditorApplication.timeSinceStartup + 10;
            while (PlayerLoopWitness.Latest == null) { Assert.Less(EditorApplication.timeSinceStartup, deadline); yield return null; }
        }
        private static JObject Read(string id) => JObject.FromObject(InputFrameJob.Read(id));
        private static string Id(object receipt)
        {
            var json = JObject.FromObject(receipt); Assert.IsTrue((bool)json["success"], json.ToString());
            return (string)json["data"]["job_id"];
        }
        private static IEnumerator Wait(string id)
        {
            double deadline = EditorApplication.timeSinceStartup + 12;
            while ((string)Read(id)["data"]["status"] == "running") { Assert.Less(EditorApplication.timeSinceStartup, deadline, Read(id).ToString()); yield return null; }
        }
        private static void AssertCompleted(string id, float duration)
        {
            var data = Read(id)["data"];
            Assert.AreEqual("completed", (string)data["status"], data.ToString());
            foreach (string flag in new[] { "down_observed", "held_observed", "up_observed", "target_observed", "cleanup_verified", "complete" }) Assert.IsTrue((bool)data[flag], flag);
            Assert.AreEqual(JTokenType.Null, data["application_accepted"].Type);
            Assert.GreaterOrEqual((double)data["target_reached_wall_seconds"] - (double)data["move_started_wall_seconds"], duration - 0.002);
            Assert.IsNull(InputSystem.GetDeviceById((int)data["device_id"]));
            var edges = data["edges"].ToArray();
            Assert.Greater(edges.Length, 3); Assert.Less((int)edges.First(x => (bool)x["down"])["frame"], (int)edges.Last(x => (bool)x["up"])["frame"]);
            Assert.IsFalse((bool)edges.Last()["held"], "The final frame must really be released.");
        }

        [UnityTest] public IEnumerator OwnedSessionMouseAndTouchAreReadAcrossActualUpdates()
        {
            PrepareOwnedScene(); yield return new EnterPlayMode(); yield return WaitForOwnedFrame();
            var inputSettings = new OwnedInputSettings();
            bool background = Application.runInBackground; float timeScale = Time.timeScale;
            string scope = Id(VerificationScope.Start(30, true, Guid.NewGuid().ToString("N")));
            var go = new GameObject("ContinuousDragPollingProbe"); var probe = go.AddComponent<ContinuousDragPollingProbe>();
            try
            {
                foreach (var pointer in new[] { "mouse", "touch" })
                foreach (var button in pointer == "mouse" ? new[] { "left", "right", "middle" } : new[] { "left" })
                {
                    Time.timeScale = pointer == "touch" ? 0 : 1; // Pointer timing must not depend on the scaled game clock.
                    var start = new Vector2(30, 60); var end = new Vector2(230, 160); string key = Guid.NewGuid().ToString("N");
                    var receipt = InputFrameJob.StartDrag(start, end, 0.25f, button, null, pointer, 0.05f, 0.05f, scope, key);
                    string id = Id(receipt); probe.Configure((int)Read(id)["data"]["device_id"], pointer, button, start, end);
                    Assert.AreEqual(id, Id(InputFrameJob.StartDrag(start, end, 0.25f, button, null, pointer, 0.05f, 0.05f, scope, key)), "Retry must not replay.");
                    Assert.AreEqual("INPUT_REQUEST_KEY_CONFLICT", (string)JObject.FromObject(InputFrameJob.StartDrag(start, end, 0.5f, button, null, pointer, 0.05f, 0.05f, scope, key))["code"]);
                    Assert.IsFalse(InputFrameJob.CanRead(id, null, tool => tool == "simulate_mouse_click"));
                    Assert.IsTrue(InputFrameJob.CanRead(id, null, tool => tool == "simulate_mouse_drag"));
                    yield return Wait(id); AssertCompleted(id, 0.25f);
                    Assert.Greater(probe.heldFrames, 2); Assert.Greater(probe.intermediatePositions, 1);
                    Assert.AreEqual(1, probe.completedDrops, "A real Update consumer must see a valid start-to-target release.");
                    Assert.AreEqual(id, (string)JObject.FromObject(InputFrameJob.Read(null, key))["data"]["job_id"]);
                    Assert.AreEqual(id, Id(InputFrameJob.StartDrag(start, end, 0.25f, button, null, pointer, 0.05f, 0.05f, scope, key)), "Completed gestures must not replay.");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VerificationScope.End(scope); Time.timeScale = timeScale; inputSettings.Dispose(); }
            Assert.AreEqual(background, Application.runInBackground);
            yield return new ExitPlayMode();
        }

        [UnityTest] public IEnumerator OwnedSessionUguiReceivesActualBeginDragDropAndEndDrag()
        {
            PrepareOwnedScene(); yield return new EnterPlayMode(); yield return WaitForOwnedFrame();
            var inputSettings = new OwnedInputSettings();
            string scope = Id(VerificationScope.Start(30, true, Guid.NewGuid().ToString("N")));
            var canvas = new GameObject("DragCanvas", typeof(Canvas), typeof(GraphicRaycaster)); canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var events = new GameObject("DragEvents", typeof(EventSystem), typeof(InputSystemUIInputModule));
            events.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
            var source = CreateTarget(canvas.transform, "DragSource", new Vector2(0.25f, 0.5f));
            var target = CreateTarget(canvas.transform, "DragTarget", new Vector2(0.75f, 0.5f)); target.source = source;
            try
            {
                yield return null; Canvas.ForceUpdateCanvases();
                foreach (string pointer in new[] { "mouse", "touch" })
                {
                    source.ResetEvidence(); target.ResetEvidence();
                    string key = Guid.NewGuid().ToString("N");
                    string id = Id(InputInteractionFunctions.SimulateMouseDrag(0.25f, 0.5f, 0.75f, 0.5f, duration: 0.3f,
                        coordinate_space: "normalized", pointer_device: pointer, verification_id: scope, request_key: key));
                    // Only this test's UI actions are paired. Unrelated physical pointers
                    // must not move the fixture's unified pointer during an automated run.
                    events.GetComponent<InputSystemUIInputModule>().actionsAsset.devices = new[] { InputSystem.GetDeviceById((int)Read(id)["data"]["device_id"]) };
                    yield return Wait(id); AssertCompleted(id, 0.3f); yield return null;
                    Assert.AreEqual(1, source.beginCount, pointer); Assert.Greater(source.dragCount, 1, pointer);
                    string evidence = pointer + " source=" + source.Trace + " target=" + target.Trace + " screen=" + Screen.width + "x" + Screen.height + " geometry=" + Read(id)["data"]["geometry"];
                    Assert.AreEqual(1, source.endCount, evidence); Assert.AreEqual(1, target.dropCount, evidence);
                    Assert.Greater(source.endFrame, source.beginFrame, pointer);
                    Assert.Greater(target.dropFrame, source.beginFrame, pointer);
                    var task = JObject.FromObject(TaskStatusFunctions.GetTask(task_id: "input:" + id, wait_seconds: 0));
                    Assert.AreEqual("completed", (string)task["data"]["task"]["status"], task.ToString());
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(canvas); UnityEngine.Object.DestroyImmediate(events); VerificationScope.End(scope); inputSettings.Dispose(); }
            yield return new ExitPlayMode();
        }
        private static ContinuousDragUguiProbe CreateTarget(Transform parent, string name, Vector2 anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ContinuousDragUguiProbe)); go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform; rect.anchorMin = rect.anchorMax = anchor; rect.sizeDelta = new Vector2(80, 80); rect.anchoredPosition = Vector2.zero;
            return go.GetComponent<ContinuousDragUguiProbe>();
        }

        [UnityTest] public IEnumerator OwnedSessionCancelTimeoutPauseAndPlayExitCleanOnlyOwnedInput()
        {
            PrepareOwnedScene(); yield return new EnterPlayMode(); yield return WaitForOwnedFrame();
            var inputSettings = new OwnedInputSettings();
            string scope = Id(VerificationScope.Start(30, true, Guid.NewGuid().ToString("N")));
            var guard = InputSystem.AddDevice<Mouse>("UnrelatedDragGuard"); int guardId = guard.deviceId;
            try
            {
                foreach (string mode in new[] { "cancel", "timeout", "pause", "update_mode" })
                {
                    string id = Id(InputFrameJob.StartDrag(new Vector2(10, 10), new Vector2(100, 100), 2, "left", null, "mouse", 0, 0, scope, null));
                    var owned = InputSystem.GetDeviceById((int)Read(id)["data"]["device_id"]); bool actionCancelled = false;
                    using (var action = new InputAction(type: InputActionType.Button, binding: "/" + owned.name + "/leftButton"))
                    {
                        action.canceled += _ => actionCancelled = true; action.Enable();
                        double deadline = EditorApplication.timeSinceStartup + 5;
                        while (!(bool)Read(id)["data"]["down_observed"]) { Assert.Less(EditorApplication.timeSinceStartup, deadline); yield return null; }
                        Assert.IsTrue(action.IsPressed());
                        if (mode == "cancel")
                        {
                            var result = JObject.FromObject(InputInteractionFunctions.SimulateMouseDrag(action: "cancel", task_id: "input:" + id));
                            Assert.AreEqual("cancelled", (string)result["data"]["status"], result.ToString());
                        }
                        else if (mode == "timeout") InputFrameJob.TickAt((double)Read(id)["data"]["deadline_wall_seconds"] + 1);
                        else if (mode == "pause") { EditorApplication.isPaused = true; InputFrameJob.TickAt(PlayerLoopWitness.Wall); EditorApplication.isPaused = false; }
                        else
                        {
                            var before = InputSystem.settings.updateMode;
                            try { InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsInFixedUpdate; yield return null; }
                            finally { InputSystem.settings.updateMode = before; }
                        }
                        if ((string)Read(id)["data"]["status"] == "running") yield return Wait(id);
                        Assert.IsTrue(actionCancelled, "Owned reset/removal must cancel held actions.");
                        var data = Read(id)["data"]; Assert.IsFalse((bool)data["complete"]); Assert.IsTrue((bool)data["cleanup_verified"]);
                        Assert.IsNull(InputSystem.GetDeviceById((int)data["device_id"])); Assert.IsTrue(guard.added); Assert.AreEqual(guardId, guard.deviceId);
                        Assert.AreNotEqual("running", (string)data["status"]);
                    }
                }
                string exitId = Id(InputFrameJob.StartDrag(new Vector2(10, 10), new Vector2(100, 100), 2, "left", null, "touch", 0, 0, scope, null));
                SessionState.SetString("Funplay.DragTest.ExitId", exitId);
            }
            finally { if (guard.added) InputSystem.RemoveDevice(guard); VerificationScope.End(scope); inputSettings.Dispose(); }
            // A separate unscoped job proves the input service also owns Play-exit cleanup.
            string playExit = Id(InputFrameJob.StartDrag(new Vector2(10, 10), new Vector2(100, 100), 2, "left", null, "touch", 0, 0, null, null));
            SessionState.SetString("Funplay.DragTest.ExitId", playExit);
            yield return new ExitPlayMode();
            var stopped = Read(SessionState.GetString("Funplay.DragTest.ExitId", ""))["data"];
            Assert.AreEqual("interrupted", (string)stopped["status"], stopped.ToString()); Assert.IsTrue((bool)stopped["cleanup_verified"]);
            SessionState.EraseString("Funplay.DragTest.ExitId");
        }
        [UnityTearDown] public IEnumerator RestoreOwnedPlayState()
        {
            if (Environment.GetCommandLineArgs().Contains("-funplayOwnedTestSession") && EditorApplication.isPlaying)
            { EditorApplication.isPaused = false; yield return new ExitPlayMode(); }
        }
#endif
    }

#if ENABLE_INPUT_SYSTEM
    // Independent gameplay consumer: no driver or test calls Update, merges or pointer callbacks directly.
    public sealed class ContinuousDragPollingProbe : MonoBehaviour
    {
        private int deviceId; private string pointer, button; private Vector2 start, end; private bool started;
        public int heldFrames, intermediatePositions, completedDrops;
        public void Configure(int id, string kind, string mouseButton, Vector2 from, Vector2 to)
        { deviceId = id; pointer = kind; button = mouseButton; start = from; end = to; started = false; heldFrames = intermediatePositions = completedDrops = 0; }
        private void Update()
        {
            var device = InputSystem.GetDeviceById(deviceId); if (device == null) return;
            var mouse = device as Mouse; var touch = device as Touchscreen;
            var control = pointer == "touch" ? touch.primaryTouch.press : button == "right" ? mouse.rightButton : button == "middle" ? mouse.middleButton : mouse.leftButton;
            Vector2 point = pointer == "touch" ? touch.primaryTouch.position.ReadValue() : mouse.position.ReadValue();
            if (control.wasPressedThisFrame && Vector2.Distance(point, start) < 0.1f) started = true;
            if (started && control.isPressed) { heldFrames++; if (Vector2.Distance(point, start) > 1 && Vector2.Distance(point, end) > 1) intermediatePositions++; }
            if (started && control.wasReleasedThisFrame && !control.isPressed && heldFrames > 2 && intermediatePositions > 0 && Vector2.Distance(point, end) < 0.1f)
            { completedDrops++; started = false; }
        }
    }
    public sealed class ContinuousDragUguiProbe : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        public ContinuousDragUguiProbe source;
        public int beginCount, dragCount, endCount, dropCount, beginFrame, endFrame, dropFrame;
        public string Trace = "";
        public void ResetEvidence() { beginCount = dragCount = endCount = dropCount = beginFrame = endFrame = dropFrame = 0; Trace = ""; }
        public void OnBeginDrag(PointerEventData data) { beginCount++; beginFrame = Time.frameCount; Trace += "begin@" + Time.frameCount + ";"; }
        public void OnDrag(PointerEventData data) { dragCount++; }
        public void OnEndDrag(PointerEventData data) { endCount++; endFrame = Time.frameCount; Trace += "end@" + Time.frameCount + ":" + data.position + ":" + data.pointerCurrentRaycast.gameObject?.name + ";"; }
        public void OnDrop(PointerEventData data) { if (source != null && data.pointerDrag == source.gameObject) { dropCount++; dropFrame = Time.frameCount; Trace += "drop@" + Time.frameCount + ";"; } }
    }
#endif
}
