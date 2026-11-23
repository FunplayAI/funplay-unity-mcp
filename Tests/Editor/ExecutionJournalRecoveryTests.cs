// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using System.Linq;
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
        {
            internal NativeIoError(int code, int facility = unchecked((int)0x80070000))
                : base("PRIVATE /Users/private/locked-journal") { HResult = facility | code; }
        }
        [TestCase(32)] [TestCase(33)] [TestCase(1175)]
        public void KnownReplacementErrorsRetryOnlyTheAtomicWriteAndKeepTheOriginalUntilCommit(int nativeCode)
        {
            int replacements = 0; string original = null, firstTemporary = null;
            var store = new ExecutionJobStore(root, replace: (temp, target) => {
                if (original != null)
                {
                    Assert.AreEqual(original, File.ReadAllText(target));
                    Assert.IsTrue(File.Exists(temp));
                    firstTemporary = firstTemporary ?? temp;
                    Assert.AreEqual(firstTemporary, temp, "Retries must reuse the same replacement file.");
                    if (++replacements <= 2) throw new NativeIoError(nativeCode);
                }
                File.Replace(temp, target, null);
            });
            var record = Complete(store); original = File.ReadAllText(Path.Combine(root, "journal.json"));
            int updates = 0;
            store.Update(record.job_id, x => { updates++; x.persist_ms = 7; });
            Assert.IsNull(store.Error); Assert.AreEqual(3, replacements); Assert.AreEqual(1, updates);
            Assert.AreEqual(7, new ExecutionJobStore(root).Find(record.job_id).persist_ms);
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
            original = null;
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("following"), "following", "execute_code", 300, context, out var next, out _));
            Assert.AreEqual("queued", next.status, "A recovered write must not block the next request.");
        }
        [TestCase(32, 3)][TestCase(33, 3)][TestCase(1175, 3)][TestCase(5, 1)][TestCase(112, 1)]
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
            Assert.AreEqual("EXECUTION_JOURNAL_UNAVAILABLE", (string)ExecutionJson.Object(
                store.Admit(ExecutionJobStore.Hash("blocked"), "blocked", "execute_code", 300, context, out _, out _))["code"]);
            Assert.AreEqual(attempts, actual, "Admission must remain closed after exhausted retries.");
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
                if (failAfterCommit) throw new UnauthorizedAccessException("Unverified replacement error");
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
        [Test]
        public void ConfirmedReplacementAfterIOExceptionKeepsAdmissionOpenAndDoesNotRepeatIt()
        {
            bool throwAfterCommit = false;
            int faultingReplacements = 0;
            var store = new ExecutionJobStore(root, replace: (temp, target) =>
            {
                File.Replace(temp, target, null);
                if (throwAfterCommit) { faultingReplacements++; throw new NativeIoError(1175); }
            });
            var completed = Complete(store);
            throwAfterCommit = true;
            var hash = ExecutionJobStore.Hash("next");
            Assert.IsNull(store.Admit(hash, "commit-then-error", "execute_code", 300, context,
                out var admitted, out bool existing));
            Assert.IsFalse(existing); Assert.IsNull(store.Error);
            Assert.IsNull(store.Admit(hash, "commit-then-error", "execute_code", 300, context,
                out var duplicate, out existing));
            Assert.IsTrue(existing); Assert.AreEqual(admitted.job_id, duplicate.job_id);
            Assert.AreEqual(1, faultingReplacements, "Verified persistence must not issue a second replacement.");
            var restarted = new ExecutionJobStore(root);
            Assert.AreEqual("completed", restarted.Find(completed.job_id).status);
            Assert.AreEqual("interrupted", restarted.Find(admitted.job_id).status);
            Assert.IsFalse(restarted.Find(admitted.job_id).executed.Value);
            Assert.IsNull(restarted.ActiveId);
        }

        [Test]
        public void Transient1175DuringFinalSaveKeepsTheExecutedResultAndKey()
        {
            var hash = ExecutionJobStore.Hash("normal command");
            int saveAttempts = 0;
            var store = new ExecutionJobStore(root, replace: (temporary, target) =>
            {
                if (Path.GetFileName(target) == "journal.json" &&
                    File.ReadAllText(temporary).Contains("\"status\":\"completed\"") && ++saveAttempts <= 2)
                {
                    StringAssert.Contains("\"phase\":\"persisting\"", File.ReadAllText(target));
                    throw new NativeIoError(1175);
                }
                File.Replace(temporary, target, null);
            });
            Assert.IsNull(store.Admit(hash, "result-once", "execute_code", 300, context, out var job, out _));
            var trace = new ScriptExecutionTrace { Id = job.job_id, Executed = true };
            string payload = ToolResultSerializer.Serialize(ExecutionResultEnvelope.Wrap(Response.Success("returned"), trace));
            store.Complete(job.job_id, payload, trace);
            Assert.AreEqual(3, saveAttempts);
            Assert.IsNull(store.Error);
            Assert.AreEqual(payload, File.ReadAllText(Path.Combine(root, job.job_id + ".result.json")));

            var resumed = new ExecutionJobStore(root);
            Assert.IsNull(resumed.Admit(hash, "result-once", "execute_code", 300, context, out var receipt, out bool existing));
            Assert.IsTrue(existing);
            Assert.AreEqual(job.job_id, receipt.job_id);
            Assert.AreEqual("completed", receipt.status);
            Assert.IsTrue(receipt.executed.Value);
            Assert.AreEqual(ExecutionJobStore.Hash(payload), receipt.result_sha256);
            Assert.IsNull(resumed.ActiveId);
        }

        [TestCase("destination_changed")]
        [TestCase("destination_missing")]
        [TestCase("temporary_changed")]
        [TestCase("temporary_missing")]
        [TestCase("intended_bytes_but_temporary_still_present")]
        public void RetryRequiresTheExactOriginalAndStagedBytes(string changed)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "journal.json");
            File.WriteAllText(path, "original");
            int attempts = 0;
            string staged = null;
            string expected = "original";
            var failure = Assert.Throws<ExecutionStorageException>(() => ExecutionAtomicFile.Write(path, "intended", "journal",
                ExecutionJobStore.Hash("original"), (temporary, target) =>
                {
                    attempts++;
                    staged = temporary;
                    switch (changed)
                    {
                        case "destination_changed": File.WriteAllText(target, expected = "external"); break;
                        case "destination_missing": File.Delete(target); expected = null; break;
                        case "temporary_changed": File.WriteAllText(temporary, "staged externally"); break;
                        case "temporary_missing": File.Delete(temporary); break;
                        default: File.Copy(temporary, target, true); expected = "intended"; break;
                    }
                    throw new NativeIoError(1175);
                }));
            Assert.AreEqual(1, attempts);
            Assert.AreEqual(1, failure.Attempts);
            Assert.IsInstanceOf<InvalidDataException>(failure.InnerException);
            Assert.AreEqual(expected, File.Exists(path) ? File.ReadAllText(path) : null);
            if (changed == "temporary_changed") Assert.AreEqual("staged externally", File.ReadAllText(staged));
            if (changed == "destination_missing") Assert.AreEqual("intended", File.ReadAllText(staged));
        }

        [TestCase(1176, true)]
        [TestCase(1177, false)]
        public void PartialReplacementErrorsAreNotRetriedAndRetainStagedEvidence(int nativeCode, bool oldNameRemoved)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "journal.json");
            File.WriteAllText(path, "original");
            var failure = Assert.Throws<ExecutionStorageException>(() => ExecutionAtomicFile.Write(path, "intended", "journal",
                ExecutionJobStore.Hash("original"), (temporary, target) =>
                {
                    if (oldNameRemoved) File.Delete(target); // Simulates the documented native state in this owned directory.
                    throw new NativeIoError(nativeCode);
                }));
            Assert.AreEqual(1, failure.Attempts);
            Assert.AreEqual(nativeCode, failure.HResult & 0xffff);
            Assert.AreEqual(!oldNameRemoved, File.Exists(path));
            Assert.AreEqual("intended", File.ReadAllText(Directory.GetFiles(root, "*.tmp").Single()));
        }

        [Test]
        public void A1175LowWordFromAnotherFacilityIsNotRetryable()
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "journal.json");
            File.WriteAllText(path, "original");
            var failure = Assert.Throws<ExecutionStorageException>(() => ExecutionAtomicFile.Write(path, "intended", "journal",
                ExecutionJobStore.Hash("original"), (_, __) => throw new NativeIoError(1175, unchecked((int)0x80040000))));
            Assert.AreEqual(1, failure.Attempts);
            Assert.AreEqual(unchecked((int)0x80040497), failure.HResult);
            Assert.AreEqual("original", File.ReadAllText(path));
            Assert.IsEmpty(Directory.GetFiles(root, "*.tmp"));
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
