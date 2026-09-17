// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Funplay.Editor.Tools.Builtins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Media;
using UnityEngine;

namespace Funplay.Editor.Tools.Helpers
{
    [Serializable] internal sealed class RecordingFrame
    {
        public int index;
        public double requested_seconds, actual_seconds, delta_seconds;
        public string path;
        public int width, height;
        public VisualGeometry geometry;
    }
    [Serializable] internal sealed class RecordingFramesJob
    {
        public string job_id, recording_id, status = "queued", error, started_at;
        public double[] timestamps;
        public List<RecordingFrame> frames = new List<RecordingFrame>();
        public int decoded_frames;
        public bool complete;
        public string selection_policy = "First decoded presentation timestamp at or after each request; inspect delta_seconds. Not a nearest-frame or exact-time guarantee.";
    }
    [InitializeOnLoad]
    internal static class RecordingEvidenceService
    {
        private const string StateKey = "Funplay.RecordingFrames.Jobs";
        private static List<RecordingFramesJob> jobs;
        private static RecordingFramesJob active;
        private static IDisposable decoder;
        private static MethodInfo nextFrame;
        private static Texture2D texture;
        private static VideoRecordingFunctions.RecordingState source;
        private static double deadline, lastTimestamp;
        private static int nextRequest;
        static RecordingEvidenceService()
        {
            try { jobs = JsonConvert.DeserializeObject<List<RecordingFramesJob>>(SessionState.GetString(StateKey, "[]")) ?? new List<RecordingFramesJob>(); }
            catch { jobs = new List<RecordingFramesJob>(); }
            jobs.RemoveAll(x => x == null || !Guid.TryParseExact(x.job_id, "N", out _));
            foreach (var job in jobs) job.frames = job.frames ?? new List<RecordingFrame>();
            foreach (var job in jobs.Where(x => x.status == "queued" || x.status == "decoding"))
            { job.status = "interrupted"; job.error = "Domain reload interrupted decoder; completed PNG receipts remain available. Start a new extraction for remaining timestamps."; }
            AssemblyReloadEvents.beforeAssemblyReload += () => Finish("interrupted", "domain_reload");
            EditorApplication.quitting += () => Finish("interrupted", "editor_quit");
            Save();
        }
        internal static RecordingFramesJob Find(string id) => jobs.FirstOrDefault(x => x.job_id == id);
        internal static double[] ParseTimes(string json, double lastFrame)
        {
            var array = JArray.Parse(json);
            if (array.Count < 1 || array.Count > 16) throw new ArgumentException("Request 1..16 timestamps.");
            if (array.Any(x => x.Type != JTokenType.Integer && x.Type != JTokenType.Float)) throw new ArgumentException("Timestamps must be numbers in seconds.");
            var times = array.Select(x => (double)x).ToArray();
            if (times.Any(x => double.IsNaN(x) || double.IsInfinity(x) || x < 0 || x > lastFrame + .000001))
                throw new ArgumentException("Timestamps must be finite and within 0..last_frame_seconds. Markers after the final frame have no recorded visual evidence.");
            return times.Distinct().OrderBy(x => x).ToArray();
        }
        internal static object Start(string recordingId, string timestamps)
        {
            if (active != null) return Response.Error("FRAME_EXTRACTION_ACTIVE", active);
            try
            {
                var recording = VideoRecordingFunctions.FindRecording(recordingId);
                if (recording == null) return Response.Error("RECORDING_NOT_FOUND");
                if (!recording.ready) return Response.Error("RECORDING_NOT_READY", recording);
                var times = ParseTimes(timestamps, recording.last_frame_seconds);
                if (!File.Exists(recording.path) || new FileInfo(recording.path).Length != recording.bytes ||
                    (File.GetAttributes(recording.path) & FileAttributes.ReparsePoint) != 0)
                    return Response.Error("RECORDING_FILE_MISSING_OR_CHANGED");
                if (recording.width < 2 || recording.width > 1920 || recording.height < 2 || recording.height > 1920) return Response.Error("INVALID_RECORDING_DIMENSIONS");
                // Unity provides no public Edit Mode video decoder. Feature-detect its narrow native
                // decoder interface; unsupported editor versions fail explicitly, with no shell tools.
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.Media.MediaDecoder");
                nextFrame = type?.GetMethod("GetNextFrame", new[] { typeof(Texture2D), typeof(MediaTime).MakeByRefType() });
                if (nextFrame == null) return Response.Error("VIDEO_DECODER_UNAVAILABLE", new { hint = "This Unity Editor does not expose the supported MediaDecoder interface." });
                active = new RecordingFramesJob { job_id = Guid.NewGuid().ToString("N"), recording_id = recordingId, timestamps = times, started_at = DateTime.UtcNow.ToString("O") };
                source = recording; jobs.Add(active); while (jobs.Count > 8) jobs.RemoveAt(0);
                nextRequest = 0; lastTimestamp = -1; deadline = EditorApplication.timeSinceStartup + 120;
                Save(); EditorApplication.update += Tick;
                return Response.Success("Frame extraction queued; poll until complete. No scene/Play Mode changes.", active);
            }
            catch (Exception ex) { return Response.Error("INVALID_FRAME_EXTRACTION", new { detail = ex.GetBaseException().Message }); }
        }
        internal static object Control(string id, string action)
        {
            var job = Find(id); if (job == null) return Response.Error("FRAME_JOB_NOT_FOUND");
            if (action == "cancel" && active == job) Finish("cancelled", null);
            else if (action == "cleanup")
            {
                if (active == job) return Response.Error("FRAME_JOB_ACTIVE");
                // Exact recorded files only. Never delete the source recording or an arbitrary directory.
                foreach (var frame in job.frames)
                    if (frame.path == FramePath(job, frame.index) && File.Exists(frame.path)) File.Delete(frame.path);
                job.status = "cleaned"; job.complete = false; Save();
            }
            else if (action != "status" && action != "cancel") return Response.Error("INVALID_FRAME_ACTION");
            return Response.Success("Frame extraction status: " + job.status + ".", job);
        }
        internal static object ReadFrame(string id, int index, bool inline)
        {
            var job = Find(id); var frame = job?.frames.FirstOrDefault(x => x.index == index);
            if (frame == null || frame.path != FramePath(job, index) || !File.Exists(frame.path)) return Response.Error("FRAME_NOT_AVAILABLE");
            bool inlineFallback = inline && new FileInfo(frame.path).Length > ScreenshotFunctions.MaxInlineScreenshotBytes;
            var bytes = inline && !inlineFallback ? File.ReadAllBytes(frame.path) : null;
            return Response.Success("Recorded frame; historical evidence, not current clickable geometry.", new
            {
                job_id = id, recording_id = job.recording_id, frame,
                geometry = frame.geometry,
                inline_fallback = inlineFallback,
                inline_image = bytes != null ? "data:image/png;base64," + Convert.ToBase64String(bytes) : null
            }, new { funplay_capture = 1 });
        }
        private static string FramePath(RecordingFramesJob job, int index) => Path.Combine(VideoRecordingFunctions.RecordingDirectory, "Frames", job.job_id, index.ToString("D2") + ".png");
        private static void Tick()
        {
            if (active == null) return;
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) { Finish("failed", "FRAME_EXTRACTION_TIMEOUT"); return; }
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (decoder == null)
                {
                    decoder = (IDisposable)Activator.CreateInstance(nextFrame.DeclaringType, new object[] { source.path });
                    texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                    active.status = "decoding"; Save();
                }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    object[] args = { texture, default(MediaTime) };
                    bool available = (bool)nextFrame.Invoke(decoder, args);
                    if (!available) { Finish("failed", "END_OF_VIDEO_BEFORE_REQUESTED_TIMESTAMP"); return; }
                    var mediaTime = (MediaTime)args[1];
                    double actual = (double)mediaTime;
                    if (!mediaTime.rate.isValid || double.IsNaN(actual) || actual < lastTimestamp)
                    { Finish("failed", "INVALID_DECODED_TIMESTAMP"); return; }
                    lastTimestamp = actual; active.decoded_frames++;
                    if (active.decoded_frames > 7500) { Finish("failed", "FRAME_DECODE_LIMIT"); return; }
                    while (nextRequest < active.timestamps.Length && actual + .000001 >= active.timestamps[nextRequest])
                    {
                        var frame = new RecordingFrame { index = nextRequest, requested_seconds = active.timestamps[nextRequest],
                            actual_seconds = actual, delta_seconds = actual - active.timestamps[nextRequest],
                            width = texture.width, height = texture.height, path = FramePath(active, nextRequest) };
                        frame.geometry = source.geometry != null ? JsonUtility.FromJson<VisualGeometry>(JsonUtility.ToJson(source.geometry)) :
                            VisualCoordinates.Create("recording_frame", source.width, source.height, texture.width, texture.height, false);
                        frame.geometry.interactive = false; frame.geometry.surface = "recording_frame";
                        frame.geometry.capture_id = active.job_id + ":" + nextRequest;
                        frame.geometry.note = "Historical recording geometry. Captured_at refers to recording start; use frame.actual_seconds for the presentation timestamp. Never use this ID for live clicks.";
                        Directory.CreateDirectory(Path.GetDirectoryName(frame.path));
                        File.WriteAllBytes(frame.path, texture.EncodeToPNG()); active.frames.Add(frame); nextRequest++;
                        Save();
                    }
                    if (nextRequest == active.timestamps.Length) { Finish("completed", null); return; }
                } while (clock.ElapsedMilliseconds < 8);
            }
            catch (Exception ex) { Finish("failed", ex.GetBaseException().Message); }
        }
        private static void Finish(string status, string error)
        {
            if (active == null) return;
            active.status = status; active.complete = status == "completed"; active.error = error;
            try { decoder?.Dispose(); }
            catch (Exception ex) { active.error = active.error ?? ex.Message; active.status = "failed"; active.complete = false; }
            finally
            {
                decoder = null;
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                texture = null; source = null; active = null;
                EditorApplication.update -= Tick; Save();
            }
        }
        private static void Save() => SessionState.SetString(StateKey, JsonConvert.SerializeObject(jobs));
    }
}
