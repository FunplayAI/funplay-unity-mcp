// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static Funplay.Editor.Tools.Builtins.VideoRecordingFunctions;

namespace Funplay.Editor.Tests
{
    public sealed class VideoRecordingFunctionsTests
    {
        [TestCase(0, 15, 1280)]
        [TestCase(121, 15, 1280)]
        [TestCase(10, 0, 1280)]
        [TestCase(10, 61, 1280)]
        [TestCase(10, 15, 127)]
        [TestCase(10, 15, 1921)]
        public void Options_RejectUnboundedWork(int duration, int fps, int dimension)
        {
            Assert.IsNotNull(ValidateOptions(duration, fps, dimension));
            var response = RecordGameView(duration_seconds: duration, fps: fps, max_dimension: dimension);
            Assert.AreEqual("INVALID_RECORDING_OPTIONS", response.GetType().GetProperty("code").GetValue(response));
        }

        [TestCase(1, 1, 128)]
        [TestCase(120, 60, 1920)]
        [TestCase(10, 15, 1280)]
        public void Options_AcceptBoundaries(int duration, int fps, int dimension)
        {
            Assert.IsNull(ValidateOptions(duration, fps, dimension));
        }

        [TestCase(1920, 1080, 1280, 1280, 720)]
        [TestCase(1080, 1920, 1280, 720, 1280)]
        [TestCase(641, 481, 1280, 640, 480)]
        [TestCase(640, 480, 1280, 640, 480)]
        [TestCase(3840, 2160, 1920, 1920, 1080)]
        public void OutputSize_IsEvenBoundedAndNeverUpscaled(int width, int height, int limit, int expectedWidth, int expectedHeight)
        {
            Assert.AreEqual(new Vector2Int(expectedWidth, expectedHeight), ResolveOutputSize(width, height, limit));
        }

        [TestCase(0, 720)]
        [TestCase(1280, 1)]
        public void OutputSize_RejectsUnrenderedView(int width, int height)
        {
            Assert.Throws<ArgumentException>(() => ResolveOutputSize(width, height, 1280));
        }

        [Test]
        public void Tool_IsInCoreAndExportsOnlyTheActionEntryPoint()
        {
            Assert.IsTrue(MCPToolExportPolicy.DefaultCoreTools.Contains("record_game_view"));
            Assert.AreEqual(typeof(VideoRecordingFunctions), ToolRegistry.GetMethod("record_game_view").DeclaringType);
            var methods = typeof(VideoRecordingFunctions).GetMethods(BindingFlags.Public | BindingFlags.Static);
            Assert.AreEqual(1, methods.Length);
            Assert.AreEqual("RecordGameView", methods[0].Name);
            Assert.IsTrue(methods[0].GetParameters().All(p => p.HasDefaultValue));
            Assert.IsFalse(ToolRegistry.IsReadOnly(methods[0]));
        }

        [Test]
        public void ViewVisibility_RejectsMissingOrUnhostedWindow()
        {
            Assert.IsFalse(IsViewRendering(null));
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            try { Assert.IsFalse(IsViewRendering(window)); }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void RecordingId_CannotStopANewerJob()
        {
            var state = NewState();
            Assert.IsTrue(MatchesRecording(state, state.recording_id));
            Assert.IsTrue(MatchesRecording(state, null));
            Assert.IsFalse(MatchesRecording(state, "older-recording"));
            Assert.IsFalse(MatchesRecording(null, null));
        }

        [Test]
        public void InvalidAction_ReturnsStructuredError()
        {
            var response = RecordGameView("invalid");
            Assert.AreEqual("INVALID_RECORDING_ACTION", response.GetType().GetProperty("code").GetValue(response));
        }

        [Test]
        public void SlowCaptures_KeepRealTimestampsWithoutCatchupBursts()
        {
            var state = NewState();
            var sink = new FakeSink();
            var session = new RecordingSession(state, sink, 100);
            session.Tick(100);
            session.Tick(100.05);
            session.Tick(100.25);
            session.Tick(101.5);
            session.Tick(101.5);

            CollectionAssert.AreEqual(new[] { 0d, 0.25d, 1.5d }, sink.Timestamps);
            Assert.AreEqual(3, state.frame_count);
            Assert.AreEqual(1.5, state.last_frame_seconds);
            Assert.IsFalse(state.ready);
            session.Finish("manual", 102);
        }

        [Test]
        public void FirstCapture_StartsAtZeroWithoutShiftingLaterFrames()
        {
            var state = NewState();
            var sink = new FakeSink();
            var session = new RecordingSession(state, sink, 100);
            session.Tick(100.25);
            session.Tick(100.5);
            CollectionAssert.AreEqual(new[] { 0d, 0.5d }, sink.Timestamps);
            Assert.AreEqual(0.5, state.last_frame_seconds);
            session.Finish("manual", 101);
        }

        [Test]
        public void DurationLimit_FinalizesOnceAndMarksFileReady()
        {
            var state = NewState();
            var sink = new FakeSink();
            var session = new RecordingSession(state, sink, 100);
            session.Tick(100);
            session.Tick(109);
            session.Tick(110);
            session.Tick(111);
            session.RequestStop();
            session.Finish("manual", 112);

            Assert.IsTrue(session.IsFinished);
            Assert.AreEqual("completed", state.status);
            Assert.AreEqual("duration_limit", state.stop_reason);
            Assert.AreEqual(10, state.elapsed_seconds);
            Assert.AreEqual(2, state.frame_count);
            Assert.IsTrue(state.ready);
            Assert.AreEqual(1024, state.bytes);
            Assert.AreEqual(1, sink.CompleteCount);
            Assert.AreEqual(1, sink.DisposeCount);
        }

        [Test]
        public void Stop_IsDeferredUntilEditorTickAndSafeToRepeat()
        {
            var state = NewState();
            var sink = new FakeSink();
            var session = new RecordingSession(state, sink, 0);
            session.Tick(0);
            session.RequestStop();
            session.RequestStop();
            Assert.AreEqual("stopping", state.status);
            Assert.IsFalse(state.ready);
            Assert.AreEqual(0, sink.CompleteCount);

            session.Tick(0.5);
            session.RequestStop();
            Assert.AreEqual("completed", state.status);
            Assert.AreEqual("manual", state.stop_reason);
            Assert.AreEqual(1, state.frame_count);
            Assert.IsTrue(state.ready);
        }

        [Test]
        public void CaptureFailure_FinalizesPartialFileButDoesNotClaimSuccess()
        {
            var state = NewState();
            var sink = new FakeSink();
            var session = new RecordingSession(state, sink, 0);
            session.Tick(0);
            sink.FailCapture = true;
            session.Tick(1);

            Assert.AreEqual("failed", state.status);
            Assert.AreEqual("capture_failed", state.stop_reason);
            Assert.AreEqual("capture broke", state.error);
            Assert.IsFalse(state.ready);
            Assert.AreEqual(1, sink.CompleteCount);
            Assert.AreEqual(1, sink.DisposeCount);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void FinalizationFailure_AlwaysDisposesAndReportsFailure(bool failComplete, bool failDispose)
        {
            var state = NewState();
            var sink = new FakeSink { FailComplete = failComplete, FailDispose = failDispose };
            var session = new RecordingSession(state, sink, 0);
            session.Tick(0);
            session.Finish("manual", 1);

            Assert.AreEqual("failed", state.status);
            Assert.IsNotNull(state.error);
            Assert.IsFalse(state.ready);
            Assert.AreEqual(1, sink.DisposeCount);
        }

        [TestCase(false, 1024)]
        [TestCase(true, 0)]
        public void EmptyRecording_IsNotReady(bool captureFrame, long bytes)
        {
            var state = NewState();
            var sink = new FakeSink { Bytes = bytes };
            var session = new RecordingSession(state, sink, 0);
            if (captureFrame) session.Tick(0);
            session.Finish("manual", 1);
            Assert.AreEqual("failed", state.status);
            Assert.IsFalse(state.ready);
            Assert.AreEqual(1, sink.DisposeCount);
        }

        [TestCase("domain_reload", true, "interrupted")]
        [TestCase("editor_quit", true, "interrupted")]
        [TestCase("play_mode_exit", false, "completed")]
        public void LifecycleInterruption_FinalizesPlayableReceipt(string reason, bool interrupted, string status)
        {
            var state = NewState();
            var sink = new FakeSink();
            var session = new RecordingSession(state, sink, 0);
            session.Tick(0);
            session.Tick(1);
            session.Finish(reason, 2, interrupted);

            Assert.AreEqual(status, state.status);
            Assert.AreEqual(reason, state.stop_reason);
            Assert.IsTrue(state.ready);
            Assert.IsFalse(state.has_audio);
            var restored = JsonUtility.FromJson<RecordingState>(JsonUtility.ToJson(state));
            Assert.AreEqual(state.recording_id, restored.recording_id);
            Assert.AreEqual(2, restored.frame_count);
            Assert.AreEqual(1024, restored.bytes);
            Assert.IsTrue(restored.ready);
            Assert.AreEqual(1, sink.DisposeCount);
        }

        private static RecordingState NewState() => new RecordingState
        {
            recording_id = "test-recording", fps = 10, duration_seconds = 10
        };

        private sealed class FakeSink : IVideoSink
        {
            internal readonly List<double> Timestamps = new List<double>();
            internal long Bytes = 1024;
            internal int CompleteCount;
            internal int DisposeCount;
            internal bool FailCapture;
            internal bool FailComplete;
            internal bool FailDispose;

            public void Capture(double timestamp)
            {
                if (FailCapture) throw new InvalidOperationException("capture broke");
                Timestamps.Add(timestamp);
            }

            public long Complete()
            {
                CompleteCount++;
                if (FailComplete) throw new InvalidOperationException("complete broke");
                return Bytes;
            }

            public void Dispose()
            {
                DisposeCount++;
                if (FailDispose) throw new InvalidOperationException("dispose broke");
            }
        }
    }
}
