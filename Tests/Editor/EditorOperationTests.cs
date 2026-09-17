// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
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
            coordinator.Start("play", true, 30, null, 100);
            var failing = new EditorOperationCoordinator(records, "session", () => throw new System.IO.IOException("disk full"), 100);
            Assert.Throws<System.IO.IOException>(() => failing.Tick(state, 101));
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
