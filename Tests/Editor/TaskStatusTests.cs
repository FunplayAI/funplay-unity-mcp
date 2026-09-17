// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.State;
using Funplay.Editor.Threading;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class TaskStatusTests
    {
        private const string Id = "11111111111111111111111111111111";
        private static JObject Audit(string status = "running", int progress = 0) => TaskStatusService.Describe("ui_audit",
            Response.Success("audit", new { job_id = Id, status, scanned_objects = progress, complete = status == "completed", findings = new object[0] }));

        [TestCase("editor", "operation_id")]
        [TestCase("ui_audit", "job_id")]
        [TestCase("ui_preview", "session_id")]
        [TestCase("recording", "recording_id")]
        [TestCase("recording_frames", "job_id")]
        [TestCase("tests", "jobId")]
        public void SixKindsPreserveNativePayloadAndReturnDistinctHandles(string kind, string idField)
        {
            var state = new JObject { [idField] = Id, ["status"] = "running", ["sentinel"] = "original" };
            var data = kind == "editor" ? new JObject { ["operation"] = state } : kind == "ui_preview" ? new JObject { ["session"] = state } : state;
            var original = JObject.FromObject(Response.Success("original", data));
            var before = original.ToString();
            var result = TaskStatusService.Describe(kind, original);
            Assert.AreEqual(kind + ":" + Id, (string)result["data"]["task"]["task_id"]);
            Assert.AreEqual(before, original.ToString(), "Projection must not mutate the service receipt.");
            result["data"]["task"].Parent.Remove();
            Assert.IsTrue(JToken.DeepEquals(original, result));
        }

        [TestCase("editor")][TestCase("ui_audit")][TestCase("ui_preview")]
        [TestCase("recording")][TestCase("recording_frames")][TestCase("tests")]
        public void HandlesRoundTripWithoutLatestJobFallback(string kind)
        {
            string id = kind + ":" + Id, inferred = null;
            Assert.IsNull(TaskStatusService.Resolve(ref id, ref inferred, null));
            Assert.AreEqual(Id, id); Assert.AreEqual(kind, inferred);
        }
        [TestCase(null)][TestCase("22222222222222222222222222222222")]
        public void PreviewUsesSessionIdNotItsNestedEditorOperation(string operationId)
        {
            var state = new UIPreviewState { session_id = Id, operation_id = operationId, status = "ready", phase = "ready" };
            var receipt = TaskStatusService.Describe("ui_preview", Response.Success("ready", new { session = state }));
            Assert.IsTrue((bool)receipt["success"]);
            Assert.AreEqual("ui_preview:" + Id, (string)receipt["data"]["task"]["task_id"]);
            Assert.AreEqual(operationId, (string)receipt["data"]["session"]["operation_id"]);
        }
        [Test] public void MissingMismatchedAndMalformedHandlesFailClosed()
        {
            string id = null, kind = "ui_audit";
            Assert.IsNotNull(TaskStatusService.Resolve(ref id, ref kind, null));
            id = "ui_audit:" + Id; kind = "editor";
            Assert.IsNotNull(TaskStatusService.Resolve(ref id, ref kind, null));
            id = "../scene"; kind = "ui_preview";
            Assert.IsNotNull(TaskStatusService.Resolve(ref id, ref kind, null));
            id = null; kind = "recording";
            Assert.IsNotNull(TaskStatusService.Resolve(ref id, ref kind, "key"));
        }
        [TestCase("editor")][TestCase("ui_preview")]
        public void RecoveryKeysRequireExplicitSupportedKind(string kind)
        {
            string id = null;
            Assert.IsNull(TaskStatusService.Resolve(ref id, ref kind, "recover-after-reload"));
            Assert.IsNotNull(TaskStatusService.Resolve(ref id, ref kind, new string('x', 129)));
        }
        [Test] public void RevisionIgnoresScanCountersButDetectsCompletionAndErrors()
        {
            var a = Audit(); var b = Audit(progress: 500);
            Assert.AreEqual((string)a["data"]["task"]["revision"], (string)b["data"]["task"]["revision"]);
            var complete = Audit("completed", 500);
            Assert.AreNotEqual((string)a["data"]["task"]["revision"], (string)complete["data"]["task"]["revision"]);
            var failed = TaskStatusService.Describe("ui_audit", Response.Success("query", new { job_id = Id, status = "failed", error = "scan failed" }));
            Assert.IsTrue((bool)failed["data"]["task"]["wait_complete"]);
            Assert.AreEqual("failed", (string)failed["data"]["task"]["status"]);
            Assert.IsTrue((bool)failed["success"], "A successful status read must not disguise the task's failure status.");
        }
        [Test] public void RevisionDetectsCurrentReadinessIndependentlyOfHistoricalSuccess()
        {
            object Read(bool ready) => Response.Success("query", new { operation = new { operation_id = Id, status = "ready" }, current_editor = new { ready } });
            var a = TaskStatusService.Describe("editor", Read(true));
            var b = TaskStatusService.Describe("editor", Read(false), (string)a["data"]["task"]["revision"]);
            Assert.IsTrue((bool)b["data"]["task"]["changed"]);
            Assert.IsFalse((bool)b["data"]["current_editor"]["ready"]);
        }
        [TestCase("queued", false)][TestCase("running", false)][TestCase("decoding", false)]
        [TestCase("recording", false)][TestCase("stopping", false)][TestCase("preparing", false)]
        [TestCase("ending", false)][TestCase("ready", true)][TestCase("completed", true)]
        [TestCase("interrupted", true)][TestCase("failed", true)][TestCase("cancelled", true)]
        [TestCase("needs_attention", true)][TestCase("closed_with_warnings", true)][TestCase("cleaned", true)]
        public void WaitingStopsOnStableOrTerminalStatesWithoutClaimingSuccess(string status, bool complete)
        {
            Assert.AreEqual(complete, (bool)Audit(status)["data"]["task"]["wait_complete"]);
        }
        [TestCase(0, 1000)][TestCase(2, 2000)][TestCase(5, 5000)][TestCase(15, 10000)][TestCase(1000, 10000)]
        public void UnchangedStateBackoffIsBounded(double seconds, int delay) => Assert.AreEqual(delay, TaskStatusService.PollDelay(seconds));

        [Test] public void ShortTaskCompletesInSingleWaitWithoutModelPolling()
        {
            double now = 0; int reads = 0;
            var result = MCPTaskWaiter.WaitAsync(Audit(), 2, null,
                () => Task.FromResult(++reads == 2 ? Audit("completed") : Audit(progress: reads)), () => now,
                (ms, ct) => { now += ms / 1000d; return Task.CompletedTask; }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(2, reads); Assert.AreEqual("completed", (string)result["data"]["task"]["wait_reason"]);
            Assert.Less(now, 2);
        }
        [Test] public void LongPollReturnsOnPhaseChangeRatherThanProgressNoise()
        {
            double now = 0; int reads = 0; var initial = Audit(); var revision = (string)initial["data"]["task"]["revision"];
            var result = MCPTaskWaiter.WaitAsync(initial, 20, revision,
                () => Task.FromResult(++reads == 3 ? Audit("interrupted") : Audit(progress: reads)), () => now,
                (ms, ct) => { now += ms / 1000d; return Task.CompletedTask; }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(3, reads); Assert.AreEqual("interrupted", (string)result["data"]["task"]["status"]);
            Assert.Less(now, 1);
        }
        [Test] public void NewRevisionReturnsImmediatelyWithoutWaiting()
        {
            var result = MCPTaskWaiter.WaitAsync(Audit(), 20, new string('0', 64),
                () => throw new Exception("Must not read again"), () => 0, (ms, ct) => throw new Exception("Must not delay"), CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("changed", (string)result["data"]["task"]["wait_reason"]);
        }
        [TestCase(0)][TestCase(2)][TestCase(30)][TestCase(100)]
        public void WaitHasAHardTransportBoundWithoutCancellingTheTask(int seconds)
        {
            double now = 0;
            var result = MCPTaskWaiter.WaitAsync(Audit(), seconds, null, () => Task.FromResult(Audit()), () => now,
                (ms, ct) => { now += ms / 1000d; return Task.CompletedTask; }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(Math.Min(30, seconds), now, .002);
            Assert.AreEqual("running", (string)result["data"]["task"]["status"]);
            Assert.AreEqual(seconds == 0 ? "immediate" : "timeout", (string)result["data"]["task"]["wait_reason"]);
        }
        [Test] public void ClientCancellationStopsWaitingWithoutReadingOrControllingTheTask()
        {
            using (var cts = new CancellationTokenSource())
            {
                bool cancelled = false;
                try
                {
                    MCPTaskWaiter.WaitAsync(Audit(), 20, null,
                        () => throw new Exception("Cancelled waiter must not read"), () => 0,
                        (ms, ct) => { cts.Cancel(); return Task.CompletedTask; }, cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { cancelled = true; }
                Assert.IsTrue(cancelled);
            }
        }
        [Test] public void FractionalRemainingBudgetDoesNotLoseAWholeSecond()
        {
            double now = 0;
            var result = MCPTaskWaiter.WaitAsync(Audit(), 1.975, null, () => Task.FromResult(Audit()), () => now,
                (ms, ct) => { now += ms / 1000d; return Task.CompletedTask; }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(1.975, now, .002);
            Assert.AreEqual("timeout", (string)result["data"]["task"]["wait_reason"]);
        }
        [Test] public void AnnotatedLegacyPollingHintMatchesSharedTaskAdvice()
        {
            var receipt = TaskStatusService.Observe("editor", Response.Success("ready", new
            { operation = new { operation_id = Id, status = "ready" }, poll_after_ms = 1000 }));
            Assert.AreEqual(0, (int)receipt["data"]["poll_after_ms"]);
            receipt["data"]["task"]["poll_after_ms"] = 5000;
            TaskStatusService.AlignPollingHint(receipt);
            Assert.AreEqual(5000, (int)receipt["data"]["poll_after_ms"]);
        }
        [Test] public void LostOrUnexposedReceiptStopsWaitingWithoutRestart()
        {
            double now = 0;
            var error = JObject.FromObject(Response.Error("TASK_KIND_NOT_EXPOSED"));
            var result = MCPTaskWaiter.WaitAsync(Audit(), 20, null, () => Task.FromResult(error), () => now,
                (ms, ct) => { now += ms / 1000d; return Task.CompletedTask; }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsFalse((bool)result["success"]); Assert.AreEqual("TASK_KIND_NOT_EXPOSED", (string)result["code"]);
        }
        [Test] public void BlockedEditorReadReturnsLastSnapshotWithinBudget()
        {
            double now = 0;
            var blocked = new TaskCompletionSource<JObject>();
            var initial = Audit();
            var result = MCPTaskWaiter.WaitAsync(initial, 2, null, () => blocked.Task, () => now,
                (ms, ct) => { now += ms / 1000d; return Task.CompletedTask; }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(2, now, .002);
            Assert.AreEqual("read_timeout", (string)result["data"]["task"]["wait_reason"]);
            Assert.AreEqual((string)initial["data"]["task"]["snapshot_at"], (string)result["data"]["task"]["snapshot_at"]);
            Assert.IsFalse(blocked.Task.IsCompleted, "Timing out a wait must not control the task/provider.");
            blocked.SetResult(Audit("completed"));
        }
        [Test] public void BlockedInitialReadReturnsAnExplicitUnknownOutcome()
        {
            var blocked = new TaskCompletionSource<string>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = JObject.Parse(MCPTaskWaiter.InitialReadAsync(blocked.Task, 25, CancellationToken.None).GetAwaiter().GetResult());
            Assert.AreEqual("EDITOR_STATUS_UNAVAILABLE", (string)result["code"]);
            Assert.GreaterOrEqual((int)result["data"]["retry_after_ms"], 5000);
            Assert.Less(clock.Elapsed.TotalSeconds, 2);
            Assert.IsFalse(blocked.Task.IsCompleted);
            blocked.SetResult("{} ");
        }
        [Test] public void NonterminalPhaseChangeWakesARevisionWait()
        {
            double now = 0;
            JObject Snapshot(string phase) => TaskStatusService.Describe("editor", Response.Success("query",
                new { operation = new { operation_id = Id, status = "running", phase } }));
            var initial = Snapshot("importing");
            var result = MCPTaskWaiter.WaitAsync(initial, 20, (string)initial["data"]["task"]["revision"],
                () => Task.FromResult(Snapshot("compiling")), () => now,
                (ms, ct) => { now += ms / 1000d; return Task.CompletedTask; }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("changed", (string)result["data"]["task"]["wait_reason"]);
            Assert.IsFalse((bool)result["data"]["task"]["wait_complete"]);
            Assert.Less(now, 1);
        }
        [TestCase("record_game_view", "recording")]
        [TestCase("prepare_editor", "editor")]
        [TestCase("start_ui_preview_session", "ui_preview")]
        [TestCase("run_tests", "tests")]
        public void InteractionAndReloadSensitiveStartsDeliverReceiptFirst(string tool, string kind)
        {
            var idField = kind == "editor" ? "operation_id" : kind == "ui_preview" ? "session_id" : kind == "recording" ? "recording_id" : "job_id";
            var state = new JObject { [idField] = Id, ["status"] = "running" };
            var data = kind == "editor" ? new JObject { ["operation"] = state } : kind == "ui_preview" ? new JObject { ["session"] = state } : state;
            var response = TaskStatusService.Describe(kind, Response.Success("start", data));
            var result = JObject.Parse(MCPTaskWaiter.CompleteAsync(tool, new Dictionary<string, object>(),
                response.ToString(), null, CancellationToken.None).GetAwaiter().GetResult());
            Assert.AreEqual("receipt_first", (string)result["data"]["task"]["wait_reason"]);
        }
        [Test] public void LegacyErrorsKeepTheirOriginalStructuredObject()
        {
            var error = Response.Error("INVALID_RECORDING_ACTION");
            Assert.AreSame(error, TaskStatusService.Attach("recording", error));
        }
        [Test] public void UnifiedReadCannotBypassCustomizedExposure()
        {
            bool OnlyQuery(string name) => name == "get_task";
            Assert.IsFalse(TaskStatusService.CanRead("editor", OnlyQuery));
            Assert.IsFalse(TaskStatusService.CanRead("ui_preview", OnlyQuery));
            Assert.IsTrue(TaskStatusService.CanRead("editor", name => name == "prepare_editor"));
            Assert.IsTrue(TaskStatusService.CanRead("editor", name => name == "get_editor_operation"));
            Assert.IsFalse(TaskStatusService.CanRead("ui_audit", name => name == "cancel_ui_audit"));
        }
        [Test] public void QueryAndControlHaveDistinctReadOnlyBoundaries()
        {
            Assert.IsTrue(ToolRegistry.IsReadOnly(typeof(TaskStatusFunctions).GetMethod("GetTask")));
            Assert.IsFalse(ToolRegistry.IsReadOnly(typeof(UIAuditFunctions).GetMethod("CancelUiAudit")));
            Assert.IsFalse(ToolRegistry.IsReadOnly(typeof(EditorOperationFunctions).GetMethod("CancelEditorOperation")));
            Assert.IsFalse(typeof(TaskStatusFunctions).GetMethod("GetTask").GetParameters().Any(p => p.Name == "action"));
        }
        [TestCase("get_task", true)][TestCase("prepare_editor", false)]
        [TestCase("cancel_ui_audit", false)][TestCase("execute_code", false)]
        public void OnlySharedReadSkipsUnboundedOuterEditorDispatch(string tool, bool bounded)
        {
            var request = new MCPRequest { JsonRpc = "2.0", Method = "tools/call", Params = new Dictionary<string, object> { ["name"] = tool } };
            Assert.AreEqual(bounded, MCPServerService.IsBoundedStatusRequest(request));
            request.Method = "resources/read";
            Assert.IsFalse(MCPServerService.IsBoundedStatusRequest(request));
            Assert.IsFalse(MCPServerService.IsBoundedStatusRequest(null));
        }
        [Test] public void BrokerMayRepeatReadOnlyStatusButNeverRepeatsMutations()
        {
            var request = new MCPRequest { JsonRpc = "2.0", Method = "tools/call", IsBrokerRedelivery = true,
                Params = new Dictionary<string, object> { ["name"] = "get_task" } };
            Assert.IsNull(MCPServerService.TryCreateBrokerRedeliveryResponse(request));
        }
        [TestCase(-1)][TestCase(31)]
        public void InvalidWaitIsRejectedBeforeStartingAnyJob(int seconds)
        {
            foreach (var response in new[] { EditorOperationFunctions.PrepareEditor(wait_seconds: seconds),
                UIAuditFunctions.AuditUi(wait_seconds: seconds), UIPreviewFunctions.StartUiPreviewSession(wait_seconds: seconds),
                RecordingEvidenceFunctions.ExtractRecordingFrames(wait_seconds: seconds) })
                Assert.AreEqual("INVALID_WAIT", (string)JObject.FromObject(response)["code"]);
        }
        [Test] public void FocusedCoreRetainsDailyWorkAndSafeCancellationWhileFullKeepsManagement()
        {
            Assert.AreEqual(40, MCPToolExportPolicy.DefaultCoreTools.Count);
            foreach (var name in new[] { "get_task", "prepare_editor", "audit_ui", "cancel_ui_audit", "cancel_editor_operation", "record_game_view", "set_prefab_properties", "find_game_objects" })
                Assert.Contains(name, MCPToolExportPolicy.DefaultCoreTools.ToArray());
            foreach (var name in new[] { "get_editor_operation", "list_editor_operations", "get_ui_audit", "get_ui_preview_session",
                "start_ui_preview_session", "end_ui_preview_session", "configure_ui_defaults", "mark_recording", "get_performance_snapshot",
                "capture_simulator_view", "capture_editor_window", "request_recompile", "wait_for_compilation", "enter_play_mode", "exit_play_mode" })
            {
                Assert.IsNotNull(ToolRegistry.GetMethod(name), name + " must remain implemented");
                Assert.IsFalse(MCPToolExportPolicy.DefaultCoreTools.Contains(name), name);
                Assert.IsTrue(MCPToolExportPolicy.IsToolAllowed(name, MCPToolExportProfile.Full, false, null, false, null));
                Assert.IsTrue(MCPToolExportPolicy.IsToolAllowed(name, MCPToolExportProfile.Core, true, new[] { name }, false, null), "Custom lists must stay authoritative");
            }
        }
    }
}
