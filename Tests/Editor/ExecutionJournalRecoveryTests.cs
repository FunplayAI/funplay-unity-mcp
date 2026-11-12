// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class ExecutionJournalRecoveryTests
    {
        private string root;
        private readonly ProjectExecutionContext context = new ProjectExecutionContext { project_id = new string('a', 64),
            instance_id = "owned-io-session", project_path = "/owned/fixture", editor_epoch = "epoch" };
        [SetUp] public void SetUp() => root = Path.Combine(Path.GetTempPath(), "FunplayJournalRecovery_" + Guid.NewGuid().ToString("N"));
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private ExecutionJobRecord Complete(ExecutionJobStore store)
        {
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "write-once", "execute_code", 300, context, out var record, out _));
            var trace = new ScriptExecutionTrace { Id = record.job_id, Executed = true };
            store.Complete(record.job_id, ToolResultSerializer.Serialize(ExecutionResultEnvelope.Wrap(Response.Success("done", new { returnValue = 42 }), trace)), trace);
            return record;
        }
        private sealed class NativeIoError : IOException
        { internal NativeIoError(int code) : base("PRIVATE /Users/private/locked-journal") { HResult = unchecked((int)0x80070000) | code; } }
        [Test] public void KnownSharingViolationsRetryOnlyTheAtomicReplacementAndKeepTheOriginalUntilCommit()
        {
            int failures = 0; string original = null;
            var store = new ExecutionJobStore(root, replace: (temp, target) => {
                if (original != null && failures++ < 2) { Assert.AreEqual(original, File.ReadAllText(target)); throw new NativeIoError(32); }
                File.Replace(temp, target, null);
            });
            var record = Complete(store); original = File.ReadAllText(Path.Combine(root, "journal.json"));
            store.Update(record.job_id, x => x.persist_ms = 7);
            Assert.IsNull(store.Error); Assert.AreEqual(3, failures); Assert.AreEqual(7, store.Find(record.job_id).persist_ms);
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
        }
        [TestCase(32, 3)][TestCase(33, 3)][TestCase(5, 1)][TestCase(112, 1)]
        public void FailedAtomicWritePreservesHistoryAndExplicitRecoveryRetainsKeys(int nativeCode, int attempts)
        {
            bool fail = false; int actual = 0;
            var store = new ExecutionJobStore(root, replace: (temp, target) => { if (fail) { actual++; throw new NativeIoError(nativeCode); } File.Replace(temp, target, null); });
            var record = Complete(store); string path = Path.Combine(root, "journal.json"), original = File.ReadAllText(path);
            string result = File.ReadAllText(Path.Combine(root, record.job_id + ".result.json")); fail = true;
            var failure = Assert.Throws<ExecutionStorageException>(() => store.Update(record.job_id, x => x.phase = "serializing"));
            Assert.AreEqual(attempts, actual); Assert.AreEqual(original, File.ReadAllText(path));
            store.FailInMemory(record.job_id, new ScriptExecutionTrace { Id = record.job_id, Executed = true, Phase = "serializing" }, failure);
            var observed = store.Read(record.job_id, null, diagnostics: true);
            Assert.IsFalse((bool)observed["data"]["journal_available"]); Assert.IsTrue((bool)observed["data"]["executed"]);
            var detail = observed["data"]["journal_diagnostic"];
            Assert.AreEqual("replace_existing", (string)detail["io_operation"]); Assert.AreEqual("journal", (string)detail["file_role"]);
            Assert.AreEqual(nativeCode, (int)detail["native_error_code"]); Assert.AreEqual(attempts, (int)detail["io_attempts"]);
            StringAssert.DoesNotContain("PRIVATE", observed.ToString()); StringAssert.DoesNotContain("/Users/private", observed.ToString());
            Assert.AreEqual(original, File.ReadAllText(path), "A read must not perform recovery or another persistence attempt.");
            Assert.IsFalse((bool)ExecutionJson.Object(store.RecoverJournal())["success"]);
            fail = false; var recovered = ExecutionJson.Object(store.RecoverJournal()); Assert.IsTrue((bool)recovered["success"], recovered.ToString());
            Assert.AreEqual(0, (int)recovered["data"]["commands_replayed"]); Assert.AreEqual(1, (int)recovered["data"]["protected_keys"]);
            Assert.IsNull(store.Error); Assert.AreEqual(result, File.ReadAllText(Path.Combine(root, record.job_id + ".result.json")));
            store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "write-once", "execute_code", 300, context, out var repeated, out bool existing));
            Assert.IsTrue(existing); Assert.AreEqual(record.job_id, repeated.job_id); Assert.IsTrue(repeated.executed.Value);
        }
        [Test] public void FailedAdmissionRecoveryPreservesTheNeverExecutedReceiptRatherThanRequeuingIt()
        {
            bool fail = false;
            var store = new ExecutionJobStore(root, replace: (temp, target) => { if (fail) throw new NativeIoError(5); File.Replace(temp, target, null); });
            Complete(store); fail = true;
            Assert.IsNotNull(store.Admit(ExecutionJobStore.Hash("new"), "new-key", "execute_code", 300, context, out _, out _));
            var rejected = store.Find(null, "new-key"); Assert.IsNotNull(rejected); Assert.IsFalse(rejected.executed.Value);
            fail = false; Assert.IsTrue((bool)ExecutionJson.Object(store.RecoverJournal())["success"]);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("new"), "new-key", "execute_code", 300, context, out var repeated, out bool existing));
            Assert.IsTrue(existing); Assert.AreEqual(rejected.job_id, repeated.job_id); Assert.AreEqual("failed", repeated.status); Assert.IsNull(store.ActiveId);
        }
        [TestCase(false)][TestCase(true)] public void RecoveryDoesNotOverwriteAnExternallyChangedOrDeletedJournal(bool delete)
        {
            bool fail = false;
            var store = new ExecutionJobStore(root, replace: (temp, target) => { if (fail) throw new NativeIoError(5); File.Replace(temp, target, null); });
            var record = Complete(store); fail = true;
            var failure = Assert.Throws<ExecutionStorageException>(() => store.Update(record.job_id, x => x.persist_ms = 7));
            store.FailInMemory(record.job_id, new ScriptExecutionTrace { Executed = true }, failure); fail = false;
            string path = Path.Combine(root, "journal.json"); if (delete) File.Delete(path); else File.AppendAllText(path, "changed externally");
            string expected = delete ? null : File.ReadAllText(path);
            Assert.IsFalse((bool)ExecutionJson.Object(store.RecoverJournal())["success"]); Assert.IsNotNull(store.Error);
            if (delete) Assert.IsFalse(File.Exists(path)); else Assert.AreEqual(expected, File.ReadAllText(path));
        }
        [Test] public void RecoveringAnUnreadableJournalNeverTurnsAMissingFileIntoAnEmptyHistory()
        {
            Directory.CreateDirectory(root); string path = Path.Combine(root, "journal.json"); File.WriteAllText(path, "{bad");
            var store = new ExecutionJobStore(root); Assert.IsNotNull(store.Error); File.Delete(path);
            Assert.IsFalse((bool)ExecutionJson.Object(store.RecoverJournal())["success"]); Assert.IsFalse(File.Exists(path));
        }
        [Test] public void RecoveryRefusesAnActiveExecutionInsteadOfAssumingItCanBeRestarted()
        {
            var store = new ExecutionJobStore(root);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "active", "execute_code", 300, context, out var job, out _));
            Assert.AreEqual("EXECUTION_STILL_RUNNING", (string)ExecutionJson.Object(store.RecoverJournal())["code"]);
            Assert.AreEqual("queued", store.Find(job.job_id).status);
        }
        [Test] public void ReplacementThatCommittedBeforeThrowingCanBeRecoveredWithoutReplaying()
        {
            bool failAfterCommit = false;
            var store = new ExecutionJobStore(root, replace: (temp, target) => {
                File.Replace(temp, target, null);
                if (failAfterCommit) throw new NativeIoError(5);
            });
            Complete(store); failAfterCommit = true;
            Assert.IsNotNull(store.Admit(ExecutionJobStore.Hash("next"), "commit-then-error", "execute_code", 300, context, out _, out _));
            string id = store.Find(null, "commit-then-error").job_id;
            failAfterCommit = false; Assert.IsTrue((bool)ExecutionJson.Object(store.RecoverJournal())["success"]);
            store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("next"), "commit-then-error", "execute_code", 300, context, out var repeated, out bool existing));
            Assert.IsTrue(existing); Assert.AreEqual(id, repeated.job_id); Assert.IsFalse(repeated.executed.Value);
            Assert.AreEqual("failed", repeated.status); Assert.IsNull(store.ActiveId);
        }
        [Test] public void AnUnreadableJournalCannotBeReplacedWithEmptyHistoryThroughRecovery()
        {
            Directory.CreateDirectory(root); string path = Path.Combine(root, "journal.json"); File.WriteAllText(path, "{bad");
            var store = new ExecutionJobStore(root); Assert.IsNotNull(store.Error);
            const string replacement = "{\"schema_version\":1,\"jobs\":[],\"keys\":[]}"; File.WriteAllText(path, replacement);
            Assert.IsFalse((bool)ExecutionJson.Object(store.RecoverJournal())["success"]);
            Assert.AreEqual(replacement, File.ReadAllText(path)); Assert.IsNotNull(store.Error);
        }
    }
}
