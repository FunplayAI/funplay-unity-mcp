// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Media;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using static Funplay.Editor.Tools.Builtins.VideoRecordingFunctions;

namespace Funplay.Editor.Tests
{
    public sealed class RecordingEvidenceTests
    {
        private RecordingState recording;
        private string jobId;
        [SetUp] public void SetUp() { recording = null; jobId = null; }
        [TearDown] public void TearDown()
        {
            if (jobId != null)
            {
                RecordingEvidenceService.Control(jobId, "cancel");
                RecordingEvidenceService.Control(jobId, "cleanup");
            }
            if (recording != null)
            {
                File.Delete(recording.path);
                File.Delete(Path.Combine(RecordingDirectory, recording.recording_id + ".json"));
            }
        }
        [TestCase("[]")]
        [TestCase("[-1]")]
        [TestCase("[99]")]
        [TestCase("[\"0\"]")]
        [TestCase("[null]")]
        [TestCase("{}")]
        [TestCase("[NaN]")]
        public void InvalidTimestampsAreRejected(string json) => Assert.Catch(() => RecordingEvidenceService.ParseTimes(json, 2));
        [Test] public void TimestampRequestsAreSortedDeduplicatedAndBounded()
        {
            CollectionAssert.AreEqual(new[] { 0d, .5d, 2d }, RecordingEvidenceService.ParseTimes("[2,0,0.5,2]", 2));
            Assert.Catch(() => RecordingEvidenceService.ParseTimes("[" + string.Join(",", Enumerable.Repeat("0", 17)) + "]", 2));
        }
        [Test] public void MarkerUsesRecordingClockNotLatestCapturedFrame()
        {
            var state = new RecordingState { fps = 10, duration_seconds = 10 };
            var session = new RecordingSession(state, new Sink(), 100);
            session.Tick(100.25);
            Assert.AreEqual(0, state.last_frame_seconds);
            var marker = session.AddMarker(102.25, "scroll", "middle", new Vector2(50, 60), null, null);
            Assert.AreEqual(2.25, marker.seconds);
            Assert.AreEqual(0, state.last_frame_seconds);
            Assert.AreEqual(50, marker.x);
            var restored = JsonUtility.FromJson<RecordingState>(JsonUtility.ToJson(state));
            Assert.AreEqual(marker.marker_id, restored.markers[0].marker_id);
            session.Finish("manual", 103);
            Assert.IsNull(session.AddMarker(104, "click", "late", null, null, null));
        }
        [Test] public void MarkersHaveExplicitCapacityLoss()
        {
            var state = new RecordingState();
            var session = new RecordingSession(state, new Sink(), 0);
            for (int i = 0; i < 256; i++) Assert.IsNotNull(session.AddMarker(i, "explicit", "mark", null, null, null));
            Assert.IsNull(session.AddMarker(257, "explicit", "overflow", null, null, null));
            Assert.AreEqual(1, state.dropped_markers); session.Finish("manual", 1);
        }
        [Test] public void UnknownRecordingAndUnknownFrameAreExplicit()
        {
            Assert.IsNull(FindRecording("../../other"));
            Assert.AreEqual("RECORDING_NOT_FOUND", (string)JObject.FromObject(RecordingEvidenceService.Start(Guid.NewGuid().ToString("N"), "[0]"))["code"]);
            Assert.AreEqual("FRAME_NOT_AVAILABLE", (string)JObject.FromObject(RecordingEvidenceService.ReadFrame("missing", 0, true))["code"]);
        }
        [Test] public void UnfinishedAndChangedRecordingsAreRejected()
        {
            MakeReceipt(false);
            Assert.AreEqual("RECORDING_NOT_READY", (string)JObject.FromObject(RecordingEvidenceService.Start(recording.recording_id, "[0]"))["code"]);
            recording.ready = true; recording.bytes = 900; SaveReceipt();
            Assert.AreEqual("RECORDING_FILE_MISSING_OR_CHANGED", (string)JObject.FromObject(RecordingEvidenceService.Start(recording.recording_id, "[0]"))["code"]);
        }
        [Test] public void QueuedJobCanBeCancelledAndCleanupDoesNotDeleteVideo()
        {
            MakeReceipt(true);
            Start("[0]");
            var cancelled = JObject.FromObject(RecordingEvidenceService.Control(jobId, "cancel"));
            Assert.AreEqual("cancelled", (string)cancelled["data"]["status"]);
            RecordingEvidenceService.Control(jobId, "cancel");
            RecordingEvidenceService.Control(jobId, "cleanup");
            Assert.IsTrue(File.Exists(recording.path));
        }
        [UnityTest] public IEnumerator NativeDecoderReturnsActualTimesAndImagePixels()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Needs the graphics-enabled macOS/Windows native encoder.");
            MakeReceipt(true);
            File.Delete(recording.path); // Native encoder creates a new file, never overwrites.
            var frame = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            try
            {
                using (var encoder = new MediaEncoder(recording.path, new VideoTrackAttributes { width = 128, height = 128, frameRate = new MediaRational(10) }))
                {
                    var times = new[] { 0, .2, .9 };
                    var colors = new[] { Color.red, Color.green, Color.blue };
                    for (int i = 0; i < times.Length; i++)
                    {
                        frame.SetPixels(Enumerable.Repeat(colors[i], 128 * 128).ToArray()); frame.Apply();
                        Assert.IsTrue(encoder.AddFrame(frame, new MediaTime((long)(times[i] * 1000), 1000)));
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(frame); }
            recording.bytes = new FileInfo(recording.path).Length; SaveReceipt(); Start("[0,0.1,0.8]");
            var timeout = EditorApplication.timeSinceStartup + 30;
            while (RecordingEvidenceService.Find(jobId).status == "queued" || RecordingEvidenceService.Find(jobId).status == "decoding")
            {
                Assert.Less(EditorApplication.timeSinceStartup, timeout, "Native extraction stalled");
                yield return null;
            }
            var job = RecordingEvidenceService.Find(jobId);
            Assert.IsTrue(job.complete, job.error); Assert.AreEqual(3, job.frames.Count);
            var expected = new[] { 0, .2, .9 };
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(expected[i], job.frames[i].actual_seconds, .02);
                Assert.IsFalse(job.frames[i].geometry.interactive);
                var texture = new Texture2D(2, 2);
                try
                {
                    texture.LoadImage(File.ReadAllBytes(job.frames[i].path));
                    Assert.AreEqual(128, texture.width);
                    var pixel = texture.GetPixel(16, 16);
                    Assert.Greater(i == 0 ? pixel.r : i == 1 ? pixel.g : pixel.b, .7f);
                }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
            }
            var response = JObject.FromObject(RecordingEvidenceService.ReadFrame(jobId, 1, true));
            Assert.IsTrue(((string)response["data"]["inline_image"]).StartsWith("data:image/png;base64,"));
            var paths = job.frames.Select(x => x.path).ToArray();
            RecordingEvidenceService.Control(jobId, "cleanup");
            Assert.IsTrue(paths.All(x => !File.Exists(x))); Assert.IsTrue(File.Exists(recording.path));
        }
        private void MakeReceipt(bool ready)
        {
            recording = new RecordingState { recording_id = Guid.NewGuid().ToString("N"), width = 128, height = 128, ready = ready, last_frame_seconds = .9, frame_count = 3 };
            Directory.CreateDirectory(RecordingDirectory);
            recording.path = Path.Combine(RecordingDirectory, "test-" + recording.recording_id + ".mp4");
            File.WriteAllBytes(recording.path, new byte[] { 0 }); recording.bytes = 1; SaveReceipt();
        }
        private void SaveReceipt() => File.WriteAllText(Path.Combine(RecordingDirectory, recording.recording_id + ".json"), JsonUtility.ToJson(recording));
        private void Start(string times)
        {
            var result = JObject.FromObject(RecordingEvidenceService.Start(recording.recording_id, times));
            Assert.IsTrue((bool)result["success"], result.ToString()); jobId = (string)result["data"]["job_id"];
        }
        private sealed class Sink : IVideoSink { public void Capture(double time) {} public long Complete() => 1; public void Dispose() {} }
    }
}
