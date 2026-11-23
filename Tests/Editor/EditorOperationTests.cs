// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using Funplay.Editor.State;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class EditorOperationTests
    {
        private EditorOperationCoordinator coordinator;
        private EditorOperationSnapshot state;
        private List<EditorOperation> records;
        private int saves;
        [SetUp] public void SetUp()
        {
            records = new List<EditorOperation>(); saves = 0;
            coordinator = new EditorOperationCoordinator(records, "session", () => saves++, 100);
            state = new EditorOperationSnapshot();
        }
        [Test] public void ReceiptPrecedesAnyAction()
        {
            var op = coordinator.Start("edit", true, 30, "k", 100);
            Assert.AreEqual("queued", op.phase); Assert.Greater(saves, 0);
            Assert.AreEqual(EditorOperationAction.None, coordinator.Tick(state, 100.1));
            Assert.AreEqual(EditorOperationAction.Refresh, coordinator.Tick(state, 101));
            Assert.AreEqual(2, op.step); Assert.Greater(saves, 1);
        }
        [Test] public void CorruptJournalIsRejectedBeforeAnyActionCanResume()
        {
            Assert.IsTrue(EditorOperationCoordinator.ValidJournal(records));
            var op = coordinator.Start("edit", false, 30, null, 100);
            Assert.IsTrue(EditorOperationCoordinator.ValidJournal(records));
            records.Add(null); Assert.IsFalse(EditorOperationCoordinator.ValidJournal(records)); records.RemoveAt(1);
            op.history = null; Assert.IsFalse(EditorOperationCoordinator.ValidJournal(records));
            op.history = new List<EditorOperationEvent>(); op.step = 99; Assert.IsFalse(EditorOperationCoordinator.ValidJournal(records));
            op.step = 0; op.deadline_seconds = double.NaN; Assert.IsFalse(EditorOperationCoordinator.ValidJournal(records));
        }
        [Test] public void EditModeRequiresStableReadback()
        {
            var op = coordinator.Start("edit", false, 30, null, 100);
            coordinator.Tick(state, 101); coordinator.Tick(state, 102);
            Assert.IsFalse(op.Terminal);
            coordinator.Tick(state, 103);
            Assert.AreEqual("ready", op.status);
        }
        [Test] public void PlayExitsBeforeRefreshAndEntersOnlyAfterReady()
        {
            state.is_playing = true;
            var op = coordinator.Start("play", true, 30, null, 100);
            Assert.AreEqual(EditorOperationAction.ExitPlay, coordinator.Tick(state, 101));
            Assert.AreEqual(EditorOperationAction.None, coordinator.Tick(state, 102));
            state.is_playing = false;
            Assert.AreEqual(EditorOperationAction.Refresh, coordinator.Tick(state, 103));
            state.refresh_pending = true; coordinator.Tick(state, 104);
            Assert.AreEqual(2, op.step);
            state.refresh_pending = false;
            coordinator.Tick(state, 105);
            Assert.AreEqual(EditorOperationAction.EnterPlay, coordinator.Tick(state, 106));
            coordinator.Tick(state, 107); Assert.IsFalse(op.Terminal);
            state.is_playing = true;
            coordinator.Tick(state, 108); coordinator.Tick(state, 109);
            Assert.AreEqual("ready", op.status);
        }
        [Test] public void ExistingPlayWithoutRefreshDoesNotExit()
        {
            state.is_playing = true;
            coordinator.Start("play", false, 30, null, 100);
            for (int i = 101; i <= 104; i++) Assert.AreEqual(EditorOperationAction.None, coordinator.Tick(state, i));
            Assert.AreEqual("ready", records[0].status);
        }
        [Test] public void IdenticalKeyReturnsOriginalAndConflictingKeyFails()
        {
            var op = coordinator.Start("edit", true, 30, "same", 100);
            Assert.AreSame(op, coordinator.Start("edit", true, 30, "same", 101));
            Assert.Throws<InvalidOperationException>(() => coordinator.Start("play", true, 30, "same", 101));
            Assert.Throws<InvalidOperationException>(() => coordinator.Start("edit", true, 30, "different", 101));
        }
        [Test] public void CancelIsIdempotentAndNeverPerformsNextAction()
        {
            var op = coordinator.Start("play", true, 30, null, 100);
            coordinator.Cancel(op, 101); coordinator.Cancel(op, 102);
            Assert.AreEqual("cancelled", op.status);
            Assert.AreEqual(EditorOperationAction.None, coordinator.Tick(state, 103));
        }
        [Test] public void TimeoutAlsoAppliesWhileCompiling()
        {
            var op = coordinator.Start("play", true, 5, null, 100);
            state.is_compiling = true;
            coordinator.Tick(state, 106);
            Assert.AreEqual("OPERATION_TIMEOUT", op.error_code);
        }
        [TestCase(true)] [TestCase(false)] public void CompilationErrorsPreventEnteringPlay(bool nativeFlag)
        {
            var op = coordinator.Start("play", false, 30, null, 100);
            if (nativeFlag) state.compilation_failed = true;
            else coordinator.CompilerErrors(new[] { "source.cs(1,1): bad" }, 101);
            Assert.AreEqual(EditorOperationAction.None, coordinator.Tick(state, 102));
            Assert.AreEqual("COMPILATION_FAILED", op.error_code);
        }
        [Test] public void FailedRefreshDoesNotClaimReady()
        {
            var op = coordinator.Start("edit", true, 30, null, 100);
            coordinator.Tick(state, 101); state.refresh_error = "import failed";
            coordinator.Tick(state, 102);
            Assert.AreEqual("REFRESH_FAILED", op.error_code);
        }
        [Test] public void ReloadPersistsIdAndDoesNotRepeatRefresh()
        {
            var op = coordinator.Start("play", true, 30, "recover", 100);
            coordinator.Tick(state, 101); coordinator.Reload(102);
            var restored = JsonConvert.DeserializeObject<List<EditorOperation>>(JsonConvert.SerializeObject(records));
            var fresh = new EditorOperationCoordinator(restored, "session", () => {}, 103);
            Assert.AreEqual(op.operation_id, fresh.Active.operation_id);
            Assert.AreEqual("reloading", fresh.Active.phase);
            Assert.AreEqual(EditorOperationAction.None, fresh.Tick(state, 104));
            Assert.AreEqual(EditorOperationAction.EnterPlay, fresh.Tick(state, 105));
        }
        [Test] public void NewEditorSessionInterruptsWithoutReplaying()
        {
            var op = coordinator.Start("play", true, 30, null, 100);
            var fresh = new EditorOperationCoordinator(records, "new-session", () => {}, 102);
            Assert.AreEqual("interrupted", op.status);
            Assert.AreEqual("EDITOR_RESTARTED", op.error_code);
            Assert.AreEqual(EditorOperationAction.None, fresh.Tick(state, 103));
        }
        [Test] public void BusyEditorResetsStabilityAndDoesNotFinishEarly()
        {
            var op = coordinator.Start("edit", false, 30, null, 100);
            coordinator.Tick(state, 101); coordinator.Tick(state, 102);
            state.is_updating = true; coordinator.Tick(state, 103);
            state.is_updating = false; coordinator.Tick(state, 104);
            Assert.IsFalse(op.Terminal);
            coordinator.Tick(state, 105); Assert.AreEqual("ready", op.status);
        }
        [Test] public void PersistenceFailurePreventsAction()
        {
            var op = coordinator.Start("play", true, 30, null, 100);
            var failing = new EditorOperationCoordinator(records, "session", () => throw new IOException("disk full"), 100);
            Assert.Throws<IOException>(() => failing.Tick(state, 101));
            Assert.IsTrue(op.Terminal);
            Assert.AreEqual("OPERATION_JOURNAL_UNAVAILABLE", op.error_code);
            Assert.AreEqual(EditorOperationAction.None, failing.Tick(state, 102));
        }

        [Test]
        public void FailedAdmissionCannotDispatchAnEditorAction()
        {
            var failing = new EditorOperationCoordinator(records, "session", () => throw new IOException("disk full"), 100);
            Assert.Throws<IOException>(() => failing.Start("edit", true, 30, "persist-first", 100));
            Assert.AreEqual("failed", records[0].status);
            Assert.AreEqual("OPERATION_JOURNAL_UNAVAILABLE", records[0].error_code);
            Assert.AreEqual(EditorOperationAction.None, failing.Tick(state, 101));
        }

        [Test]
        public void FailedFinalSaveDoesNotExposeAnUndurableReadyState()
        {
            var withFailure = new EditorOperationCoordinator(records, "session", () =>
            {
                if (records[0].status == "ready") throw new IOException("disk full");
            }, 100);
            var op = withFailure.Start("edit", false, 30, "finish", 100);
            withFailure.Tick(state, 101);
            withFailure.Tick(state, 102);
            Assert.Throws<IOException>(() => withFailure.Tick(state, 103));
            Assert.AreEqual("failed", op.status);
            Assert.AreEqual("OPERATION_JOURNAL_UNAVAILABLE", op.error_code);
        }

        private sealed class ReplaceError : IOException
        {
            internal ReplaceError() { HResult = unchecked((int)0x80070497); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PrepareUsesGuardedReplacementBeforeDispatchingEachAction(bool persistentFailure)
        {
            var root = Path.Combine(Path.GetTempPath(), "FunplayOperationJournal_" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "editor-operations.json");
            try
            {
                int attempts = 0;
                string oldText = null;
                var journal = new EditorOperationJournal(path, (temporary, target) =>
                {
                    if (oldText != null)
                    {
                        Assert.AreEqual(oldText, File.ReadAllText(target));
                        if (++attempts <= 2 || persistentFailure) throw new ReplaceError();
                    }
                    File.Replace(temporary, target, null);
                });
                var saved = journal.Load();
                var machine = new EditorOperationCoordinator(saved, "session", () => journal.Save(saved), 100);
                var op = machine.Start("edit", true, 30, "prepare-once", 100);
                oldText = File.ReadAllText(path);
                state.is_playing = true;
                if (persistentFailure)
                {
                    var error = Assert.Throws<ExecutionStorageException>(() => machine.Tick(state, 101));
                    Assert.AreEqual("editor_operation_journal", error.Role);
                    Assert.AreEqual("replace_existing", error.Operation);
                    Assert.AreEqual(unchecked((int)0x80070497), error.HResult);
                    Assert.AreEqual(3, error.Attempts);
                    Assert.AreEqual(oldText, File.ReadAllText(path));
                    Assert.AreEqual("OPERATION_JOURNAL_UNAVAILABLE", op.error_code);
                    Assert.AreEqual(EditorOperationAction.None, machine.Tick(state, 102));
                }
                else
                {
                    Assert.AreEqual(EditorOperationAction.ExitPlay, machine.Tick(state, 101));
                    Assert.AreEqual(EditorOperationAction.None, machine.Tick(state, 102), "No second ExitPlay action.");
                    Assert.AreSame(op, machine.Start("edit", true, 30, "prepare-once", 102));
                    var persisted = new EditorOperationJournal(path).Load()[0];
                    Assert.AreEqual(op.operation_id, persisted.operation_id);
                    Assert.AreEqual("exiting_play", persisted.phase);
                }
                Assert.AreEqual(3, attempts);
                Assert.IsEmpty(Directory.GetFiles(root, "*.tmp"));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OperationJournalNeverOverwritesAnExternalChangeOrMissingHistory(bool deleted)
        {
            var root = Path.Combine(Path.GetTempPath(), "FunplayOperationJournal_" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "editor-operations.json");
            try
            {
                var journal = new EditorOperationJournal(path);
                var loaded = journal.Load();
                var machine = new EditorOperationCoordinator(loaded, "session", () => journal.Save(loaded), 100);
                machine.Start("play", true, 30, "no-rewrite", 100);
                if (deleted) File.Delete(path);
                else File.WriteAllText(path, "external");
                var error = Assert.Throws<ExecutionStorageException>(() => machine.Tick(state, 101));
                Assert.IsInstanceOf<InvalidDataException>(error.InnerException);
                Assert.AreEqual(0, error.Attempts);
                Assert.AreEqual(deleted ? null : "external", File.Exists(path) ? File.ReadAllText(path) : null);
                Assert.AreEqual(EditorOperationAction.None, machine.Tick(state, 102));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public void CorruptOperationJournalCannotBeReplacedByAnEmptyHistory()
        {
            var root = Path.Combine(Path.GetTempPath(), "FunplayOperationJournal_" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "editor-operations.json");
            Directory.CreateDirectory(root);
            try
            {
                const string invalid = "[null]";
                File.WriteAllText(path, invalid);
                var journal = new EditorOperationJournal(path);
                Assert.Throws<InvalidDataException>(() => journal.Load());
                Assert.Throws<InvalidDataException>(() => journal.Save(new List<EditorOperation>()));
                Assert.AreEqual(invalid, File.ReadAllText(path));
                Assert.IsEmpty(Directory.GetFiles(root, "*.tmp"));
            }
            finally { Directory.Delete(root, true); }
        }
        [Test] public void ReadinessIsCurrentNotHistorical()
        {
            var op = coordinator.Start("edit", false, 30, null, 100);
            for (int i = 101; i <= 104; i++) coordinator.Tick(state, i);
            state.is_compiling = true;
            Assert.AreEqual("ready", op.status); Assert.IsFalse(state.ready);
        }
        [TestCase("bad", 30)] [TestCase("edit", 4)] [TestCase("play", 901)]
        public void InvalidInputIsRejected(string target, int timeout)
            => Assert.Throws<ArgumentException>(() => coordinator.Start(target, true, timeout, null, 100));
        [Test] public void JournalRetentionIsBounded()
        {
            for (int i = 0; i < 40; i++) coordinator.Cancel(coordinator.Start("edit", false, 30, "k" + i, 100), 101);
            Assert.AreEqual(32, records.Count); Assert.AreEqual("k8", records[0].request_key);
        }
    }
}
