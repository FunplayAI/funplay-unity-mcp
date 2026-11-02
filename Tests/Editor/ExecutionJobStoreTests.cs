// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using System.Linq;
using System.Text;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class ExecutionJobStoreTests
    {
        private string root;
        private long clock;
        private ExecutionJobStore store;
        private readonly ProjectExecutionContext context = new ProjectExecutionContext {
            project_id = new string('a', 64), instance_id = "owned-session", project_path = "/owned/fixture", editor_epoch = "epoch" };
        [SetUp] public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "FunplayExecutionStore_" + Guid.NewGuid().ToString("N")); clock = 1000000;
            store = new ExecutionJobStore(root, () => clock); Assert.IsNull(store.Error);
        }
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private ExecutionJobRecord Admit(string key = null, string hash = "same")
        {
            var error = store.Admit(ExecutionJobStore.Hash(hash), key, "execute_code", 300, context, out var record, out _);
            Assert.IsNull(error, JsonConvert.SerializeObject(error)); return record;
        }
        private string Complete(ExecutionJobRecord record, object result = null, bool executed = true)
        {
            var trace = new ScriptExecutionTrace { Id = record.job_id, Executed = executed };
            string json = ToolResultSerializer.Serialize(ExecutionResultEnvelope.Wrap(Response.Success("done", new { returnValue = result ?? "ok" }), trace));
            store.Complete(record.job_id, json, trace); return json;
        }
        private static string Code(object error) => (string)JObject.FromObject(error)["code"];

        [Test] public void StableKeysReturnTheSameReceiptAndChangedArgumentsFailBeforeAnotherExecution()
        {
            var first = Admit("write-once"); Complete(first);
            var error = store.Admit(ExecutionJobStore.Hash("same"), "write-once", "execute_code", 300, context, out var duplicate, out bool existing);
            Assert.IsNull(error); Assert.IsTrue(existing); Assert.AreEqual(first.job_id, duplicate.job_id); Assert.IsTrue(duplicate.executed.Value);
            Assert.AreEqual("EXECUTION_REQUEST_KEY_CONFLICT", Code(store.Admit(ExecutionJobStore.Hash("different"), "write-once", "execute_code", 300, context, out _, out _)));
        }
        [Test] public void ConcurrentDistinctExecutionIsRejectedButKeyRecoveryWorks()
        {
            var first = Admit("running");
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "running", "execute_code", 300, context, out var recovered, out bool existing));
            Assert.IsTrue(existing); Assert.AreEqual(first.job_id, recovered.job_id);
            Assert.AreEqual("EXECUTION_JOB_ACTIVE", Code(store.Admit(ExecutionJobStore.Hash("next"), "next", "execute_code", 300, context, out _, out _)));
        }
        [TestCase("queued", false)][TestCase("compiling", false)][TestCase("refreshing", false)][TestCase("executing", true)]
        public void RestartRetainsIdentityAndKeysWithoutReplayingUnknownWork(string phase, bool unknown)
        {
            var first = Admit("recover"); store.Update(first.job_id, x => { x.phase = phase; x.status = phase == "queued" ? "queued" : "running"; });
            store = new ExecutionJobStore(root, () => clock);
            var result = store.Find(null, "recover");
            Assert.AreEqual(first.job_id, result.job_id);
            Assert.AreEqual(unknown ? "outcome_unknown" : "interrupted", result.status);
            Assert.AreEqual(unknown ? (bool?)null : false, result.executed);
            Assert.IsNull(store.ActiveId);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "recover", "execute_code", 300, context, out var repeat, out bool existing));
            Assert.IsTrue(existing); Assert.AreEqual(result.status, repeat.status);
        }
        [Test] public void RecoveryDoesNotThrowAwayConfirmedExecutionWhenOnlyResultDeliveryWasInterrupted()
        {
            var first = Admit("returned"); store.Update(first.job_id, x => { x.phase = "serializing"; x.status = "running"; x.execution_status = "returned"; x.executed = true; });
            store = new ExecutionJobStore(root, () => clock);
            Assert.AreEqual(true, store.Find(first.job_id).executed);
            Assert.AreEqual("returned", store.Find(first.job_id).execution_status);
            Assert.AreEqual("outcome_unknown", store.Find(first.job_id).status);
        }
        [Test] public void CancellationIsExplicitAndCannotPretendToInterruptASynchronousUnityCall()
        {
            var first = Admit(); store.Update(first.job_id, x => { x.phase = "executing"; x.status = "running"; x.executed = null; });
            Assert.AreEqual("EXECUTION_NOT_INTERRUPTIBLE", Code(store.Cancel(first.job_id)));
            Assert.IsFalse(store.Find(first.job_id).cancel_requested);
            Assert.AreEqual("running", (string)store.Read(first.job_id, null)["data"]["status"]);
        }
        [Test] public void QueuedCancellationDoesNotCreateOrRunAResult()
        {
            var first = Admit("cancel"); Assert.IsNull(store.Cancel(first.job_id));
            Assert.AreEqual(false, store.Find(first.job_id).executed); Assert.AreEqual("cancelled", store.Find(first.job_id).status);
            Assert.IsNull(store.ActiveId); Assert.IsFalse((bool)store.Read(first.job_id, null)["data"]["result"]["available"]);
        }
        [Test] public void LargeSnapshotHasStableHashAndLosslessBoundedItemPages()
        {
            var first = Admit(); var rows = Enumerable.Range(0, 5000).Select(x => new { index = x, text = new string('x', 50) }).ToArray();
            var json = Complete(first, new { rows });
            var summary = store.Read(first.job_id, null);
            Assert.IsNull(summary["data"]["output"]); Assert.Greater((int)summary["data"]["result"]["bytes"], 344842);
            int offset = 0;
            while (offset < rows.Length)
            {
                var page = store.Read(first.job_id, null, "items", "/data/returnValue/rows", offset, 500)["data"]["result_page"];
                var items = (JArray)page["items"];
                Assert.LessOrEqual(Encoding.UTF8.GetByteCount(items.ToString(Formatting.None)), ExecutionJobStore.PageBytes);
                Assert.AreEqual(offset, (int)items[0]["index"]); Assert.AreEqual(ExecutionJobStore.Hash(json), (string)page["sha256"]);
                Assert.Greater((int)page["next_offset"], offset); offset = (int)page["next_offset"];
            }
            Assert.AreEqual(rows.Length, offset);
        }
        [Test] public void Utf8TextPagesPreserveSurrogatePairsAndCanReassembleTheExactSnapshot()
        {
            var first = Admit(); var json = Complete(first, string.Concat(Enumerable.Repeat("界😀", 1000)));
            int offset = 0; var rebuilt = new StringBuilder();
            while (offset < json.Length)
            {
                var page = store.Read(first.job_id, null, "text", offset: offset, limit: 257)["data"]["result_page"];
                rebuilt.Append((string)page["text"]); offset = (int)page["next_offset"];
            }
            Assert.AreEqual(json, rebuilt.ToString());
            int pair = json.IndexOf("😀", StringComparison.Ordinal);
            Assert.AreEqual("INVALID_TEXT_OFFSET", (string)store.Read(first.job_id, null, "text", offset: pair + 1)["code"]);
        }
        [Test] public void LargeSingleItemsGiveAnExplicitNarrowOrTextFallbackInsteadOfTruncation()
        {
            var first = Admit(); Complete(first, new[] { new string('x', ExecutionJobStore.PageBytes + 100) });
            var page = store.Read(first.job_id, null, "items")["data"]["result_page"];
            Assert.AreEqual("RESULT_ITEM_TOO_LARGE", (string)page["error_code"]); Assert.AreEqual(0, (int)page["next_offset"]);
        }
        [Test] public void ReadNeverExpiresDeletesCancelsOrForgetsAnything()
        {
            var first = Admit("retained"); Complete(first); clock += ExecutionJobStore.RetentionMs + 1;
            Assert.IsFalse((bool)store.Read(first.job_id, null)["data"]["result"]["available"]);
            Assert.IsTrue(File.Exists(Path.Combine(root, first.job_id + ".result.json")));
            Assert.IsNull(store.Cleanup(first.job_id)); Assert.IsFalse(File.Exists(Path.Combine(root, first.job_id + ".result.json")));
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "retained", "execute_code", 300, context, out var repeated, out bool existing));
            Assert.IsTrue(existing); Assert.AreEqual(first.job_id, repeated.job_id);
        }
        [Test] public void ChangedResultsFailIntegrityAndAreNotDeletedAsIfStillOwnedUnchangedBytes()
        {
            var first = Admit(); Complete(first); var path = Path.Combine(root, first.job_id + ".result.json"); File.AppendAllText(path, "changed");
            Assert.AreEqual("EXECUTION_RESULT_MISSING_OR_CHANGED", (string)store.Read(first.job_id, null, "text")["code"]);
            Assert.AreEqual("EXECUTION_CLEANUP_PENDING", Code(store.Cleanup(first.job_id))); Assert.IsTrue(File.Exists(path));
        }
        [Test] public void ForgettingRetryProtectionRequiresAnExplicitConfirmation()
        {
            var first = Admit("operator"); Complete(first);
            Assert.AreEqual("CONFIRM_FORGET_REQUIRED", Code(store.Forget(first.job_id, false)));
            Assert.IsTrue((bool)JObject.FromObject(store.Forget(first.job_id, true))["data"]["idempotency_protection_removed"]);
            var second = Admit("operator"); Assert.AreNotEqual(first.job_id, second.job_id);
        }
        [Test] public void UnclaimedFilesAreNotDeletedEvenWhenTheyHaveAnExecutionShapedName()
        {
            var first = Admit(); store.Cancel(first.job_id);
            string file = Path.Combine(root, first.job_id + ".result.json"); File.WriteAllText(file, "unclaimed operator data");
            Assert.AreEqual("EXECUTION_CLEANUP_PENDING", Code(store.Cleanup(first.job_id))); Assert.IsTrue(File.Exists(file));
        }
        [Test] public void ProtectedKeyCapacityFailsClosedWithoutForgettingEarlierExecutions()
        {
            var fixture = new ExecutionJournal();
            for (int i = 0; i < ExecutionJobStore.MaxKeys; i++) fixture.keys.Add(new ExecutionKeyRecord { job_id = Guid.NewGuid().ToString("N"),
                key_hash = ExecutionJobStore.Hash("key" + i), request_hash = ExecutionJobStore.Hash("same"), source_tool = "execute_code", instance_id = context.instance_id });
            File.WriteAllText(Path.Combine(root, "journal.json"), JsonConvert.SerializeObject(fixture));
            store = new ExecutionJobStore(root, () => clock); Assert.IsNull(store.Error);
            Assert.AreEqual("EXECUTION_KEY_CAPACITY", Code(store.Admit(ExecutionJobStore.Hash("new"), "fresh", "execute_code", 300, context, out _, out _)));
            Assert.AreEqual("EXECUTION_RECEIPT_EXPIRED", Code(store.Admit(ExecutionJobStore.Hash("same"), "key0", "execute_code", 300, context, out _, out _)));
        }
        [Test] public void CorruptJournalFailsClosedWithoutStartingNewWork()
        {
            File.WriteAllText(Path.Combine(root, "journal.json"), "{bad"); store = new ExecutionJobStore(root, () => clock);
            Assert.IsNotNull(store.Error);
            Assert.AreEqual("EXECUTION_JOURNAL_UNAVAILABLE", Code(store.Admit(ExecutionJobStore.Hash("test"), "k", "execute_code", 300, context, out _, out _)));
        }
        [Test] public void DiskFailureKeepsAnHonestReadableMemoryReceiptAndClosesAdmission()
        {
            var first = Admit("disk-failed"); var trace = new ScriptExecutionTrace { Id = first.job_id, Executed = true, Phase = "serializing" };
            var path = Path.Combine(root, "journal.json"); File.Delete(path); Directory.CreateDirectory(path);
            Assert.Throws<IOException>(() => store.Update(first.job_id, x => x.phase = "serializing"));
            store.FailInMemory(first.job_id, trace, new IOException("private path must not appear"));
            var receipt = store.Read(first.job_id, null, diagnostics: true);
            Assert.IsTrue((bool)receipt["success"]); Assert.AreEqual("failed", (string)receipt["data"]["status"]);
            Assert.IsTrue((bool)receipt["data"]["executed"]); Assert.IsFalse((bool)receipt["data"]["journal_available"]);
            StringAssert.DoesNotContain("private path", receipt.ToString());
            Assert.AreEqual("EXECUTION_JOURNAL_UNAVAILABLE", Code(store.Admit(ExecutionJobStore.Hash("new"), "new", "execute_code", 300, context, out _, out _)));
        }
        [Test] public void FailedAdmissionCannotLeaveAQueuedReceiptThatWillNeverRun()
        {
            Directory.CreateDirectory(Path.Combine(root, "journal.json"));
            var error = store.Admit(ExecutionJobStore.Hash("new"), "failed-admission", "execute_code", 300, context, out _, out _);
            Assert.AreEqual("EXECUTION_JOURNAL_UNAVAILABLE", Code(error));
            var job = store.Find(null, "failed-admission"); Assert.IsNotNull(job);
            Assert.AreEqual("failed", job.status); Assert.IsFalse(job.executed.Value); Assert.IsNull(store.ActiveId);
            Assert.IsFalse((bool)store.Read(job.job_id, null)["data"]["journal_available"]);
            Assert.AreEqual("EXECUTION_JOURNAL_UNAVAILABLE", (string)store.Read(Guid.NewGuid().ToString("N"), null)["code"]);
        }
        [Test] public void InvalidPathsFailExplicitlyAndDeletedLargeSnapshotsAreNeverReportedAvailable()
        {
            var first = Admit(); Complete(first, new { text = new string('x', 40000), items = new[] { 1, 2 } });
            Assert.AreEqual("INVALID_RESULT_PATH_OR_OFFSET", (string)store.Read(first.job_id, null, "items", "not-a-pointer")["code"]);
            Assert.AreEqual("INVALID_RESULT_PATH_OR_OFFSET", (string)store.Read(first.job_id, null, "items", "/data/absent")["code"]);
            Assert.AreEqual("INVALID_RESULT_PATH_OR_OFFSET", (string)store.Read(first.job_id, null, "items", "/data/returnValue/items", 9)["code"]);
            File.Delete(Path.Combine(root, first.job_id + ".result.json"));
            var summary = store.Read(first.job_id, null);
            Assert.IsFalse((bool)summary["data"]["result"]["available"]);
            Assert.AreEqual("missing_or_changed", (string)summary["data"]["result"]["integrity_status"]);
        }
        [Test] public void SnapshotQuotaEvictsOldOwnedPayloadsButRetainsRetryProtection()
        {
            string large = new string('x', 5 * 1024 * 1024);
            var first = Admit("quota-first"); Complete(first, large);
            for (int i = 0; i < 6; i++) { clock++; Complete(Admit("quota-" + i), large); }
            Assert.IsTrue(store.Find(first.job_id).result_deleted);
            Assert.LessOrEqual(Directory.GetFiles(root, "*.result.json").Sum(x => new FileInfo(x).Length), ExecutionJobStore.MaxStoredBytes);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "quota-first", "execute_code", 300, context, out var repeated, out bool existing));
            Assert.IsTrue(existing); Assert.AreEqual(first.job_id, repeated.job_id);
        }
        [Test] public void RecordEvictionRetainsAnExpiredKeyRatherThanReexecutingIt()
        {
            var first = Admit("evicted"); Complete(first);
            for (int i = 0; i < ExecutionJobStore.MaxRecords; i++) { clock++; Complete(Admit()); }
            Assert.IsNull(store.Find(first.job_id));
            Assert.AreEqual("EXECUTION_RECEIPT_EXPIRED", Code(store.Admit(ExecutionJobStore.Hash("same"), "evicted", "execute_code", 300, context, out _, out _)));
        }
        [Test] public void MediumJsonResultHasMeasuredBoundedDeliveryInsteadOfAFalseSizeDiagnosis()
        {
            var first = Admit(); var timer = System.Diagnostics.Stopwatch.StartNew();
            string json = Complete(first, JObject.FromObject(new { text = new string('x', 344842) }));
            long persist = timer.ElapsedMilliseconds; timer.Restart();
            var summary = store.Read(first.job_id, null, originalResult: true); long read = timer.ElapsedMilliseconds;
            Assert.IsNull(summary["data"]["output"]); Assert.IsTrue((bool)summary["success"]);
            Assert.Less(Encoding.UTF8.GetByteCount(summary.ToString(Formatting.None)), 8192);
            Assert.AreEqual(ExecutionJobStore.Hash(json), (string)summary["data"]["result"]["sha256"]);
            TestContext.WriteLine("medium_json_bytes=" + Encoding.UTF8.GetByteCount(json) + "; persist_ms=" + persist + "; summary_read_ms=" + read);
            Assert.Less(persist, 10000); Assert.Less(read, 2000);
        }
    }
}
