// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Reflection;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.Media;
using UnityEngine;
using UnityEngine.Rendering;

namespace Funplay.Editor.Tools.Builtins
{
    [InitializeOnLoad]
    [ToolProvider("Video")]
    internal static class VideoRecordingFunctions
    {
        private const string StateKey = "Funplay.VideoRecording.LastJob";
        private static readonly FieldInfo ViewParentField = typeof(EditorWindow).GetField(
            "m_Parent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly PropertyInfo ActualViewProperty = typeof(EditorWindow).Assembly.GetType("UnityEditor.HostView")?.GetProperty(
            "actualView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static RecordingSession _active;
        private static RecordingState _last;
        private static double _lastSavedAt;

        static VideoRecordingFunctions()
        {
            var saved = SessionState.GetString(StateKey, string.Empty);
            if (!string.IsNullOrEmpty(saved))
            {
                try { _last = JsonUtility.FromJson<RecordingState>(saved); }
                catch { /* A corrupt session receipt must not prevent editor startup. */ }
            }
            if (_last != null && (_last.status == "recording" || _last.status == "stopping"))
            {
                _last.status = "interrupted";
                _last.stop_reason = "domain_reload";
                _last.error = "Recording state survived, but the encoder did not finish before reload.";
                _last.ready = false;
                SaveState();
            }
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += BeforeQuit;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }

        [Description("Record a silent MP4 of the rendered Game View, including UI, on macOS or Windows. " +
                     "Use action=start in Play Mode, then perform actions and poll action=status or request action=stop. " +
                     "Start and stop return immediately; recording ends automatically at duration_seconds. " +
                     "Only read the local video path when ready=true. No video/base64 is sent through MCP. " +
                     "Use this for visual review, not frame-accurate performance measurement. Requires a visible, rendering Game View.")]
        public static object RecordGameView(
            [ToolParam("start, status, or stop", Required = false)] string action = "start",
            [ToolParam("Maximum wall-clock duration in seconds, 1-120. Used by start only.", Required = false)] int duration_seconds = 10,
            [ToolParam("Target capture rate, 1-60 frames per second. Slow frames retain their real timestamps.", Required = false)] int fps = 15,
            [ToolParam("Maximum output width or height, 128-1920. Preserves aspect ratio without upscaling.", Required = false)] int max_dimension = 1280,
            [ToolParam("Recording ID returned by start. Recommended for status/stop to avoid controlling a newer recording.", Required = false)] string recording_id = null)
        {
            switch ((action ?? "start").Trim().ToLowerInvariant())
            {
                case "status":
                case "stop":
                    if (_last == null)
                        return Response.Error("NO_VIDEO_RECORDING", new { hint = "Start a recording first." });
                    if (!MatchesRecording(_last, recording_id))
                        return Response.Error("RECORDING_NOT_FOUND", new { recording_id, latest_recording_id = _last.recording_id });
                    if (string.Equals(action.Trim(), "stop", StringComparison.OrdinalIgnoreCase) && _active != null)
                    {
                        _active.RequestStop();
                        SaveState();
                    }
                    return Response.Success("Video recording status: " + _last.status + ".", _last);
                case "start":
                    return Start(duration_seconds, fps, max_dimension);
                default:
                    return Response.Error("INVALID_RECORDING_ACTION", new { action, accepted = new[] { "start", "status", "stop" } });
            }
        }

        private static object Start(int durationSeconds, int fps, int maxDimension)
        {
            if (_active != null)
                return Response.Error("VIDEO_RECORDING_ACTIVE", _last);
            var problem = ValidateOptions(durationSeconds, fps, maxDimension);
            if (problem != null)
                return Response.Error("INVALID_RECORDING_OPTIONS", new { hint = problem });
            if (!EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                return Response.Error("NOT_IN_PLAY_MODE", new { hint = "Enter Play Mode and wait for reload recovery before recording." });
            if (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.WindowsEditor)
                return Response.Error("VIDEO_ENCODER_UNAVAILABLE", new { hint = "MP4 recording currently supports macOS and Windows Editors." });
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return Response.Error("VIDEO_CAPTURE_UNAVAILABLE", new { hint = "Recording needs a graphics-enabled Unity Editor." });

            GameViewVideoSink sink = null;
            try
            {
                var view = ScreenshotFunctions.GetMainPlayModeView() as EditorWindow;
                if (!IsViewRendering(view) || !ScreenshotFunctions.TryGetPlayModeViewRenderTexture(view, out var source))
                    return Response.Error("GAME_VIEW_NOT_READY", new { hint = "Open the Game tab, let it render, and retry." });

                var size = ResolveOutputSize(source.width, source.height, maxDimension);
                var id = Guid.NewGuid().ToString("N");
                var directory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", "FunplayMcp", "Recordings");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "game-view-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + id + ".mp4");
                var state = new RecordingState
                {
                    recording_id = id,
                    path = path,
                    width = size.x,
                    height = size.y,
                    fps = fps,
                    duration_seconds = durationSeconds,
                    started_at = DateTime.UtcNow.ToString("o")
                };
                sink = new GameViewVideoSink(view, source.width, source.height, state);
                _active = new RecordingSession(state, sink, EditorApplication.timeSinceStartup);
                _last = state;
                sink = null;
                SaveState();
                EditorApplication.update += Update;
                view.Repaint();
                return Response.Success("Recording started. Perform actions, then poll status until ready=true or request stop.", state);
            }
            catch (Exception ex)
            {
                sink?.Dispose();
                return Response.Error("VIDEO_RECORDING_START_FAILED", new { message = ex.Message });
            }
        }

        private static void Update()
        {
            if (_active == null)
                return;
            _active.Tick(EditorApplication.timeSinceStartup);
            if (_active.IsFinished)
            {
                _active = null;
                EditorApplication.update -= Update;
                SaveState();
            }
            else if (EditorApplication.timeSinceStartup - _lastSavedAt >= 1)
                SaveState();
        }

        private static void SaveState()
        {
            if (_last != null)
                SessionState.SetString(StateKey, JsonUtility.ToJson(_last));
            _lastSavedAt = EditorApplication.timeSinceStartup;
        }

        private static void BeforeReload() => FinishActive("domain_reload", true);
        private static void BeforeQuit() => FinishActive("editor_quit", true);

        private static void PlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
                FinishActive("play_mode_exit", false);
        }

        private static void FinishActive(string reason, bool interrupted)
        {
            if (_active == null)
                return;
            _active.Finish(reason, EditorApplication.timeSinceStartup, interrupted);
            _active = null;
            EditorApplication.update -= Update;
            SaveState();
        }

        internal static string ValidateOptions(int durationSeconds, int fps, int maxDimension)
        {
            if (durationSeconds < 1 || durationSeconds > 120)
                return "duration_seconds must be between 1 and 120.";
            if (fps < 1 || fps > 60)
                return "fps must be between 1 and 60.";
            if (maxDimension < 128 || maxDimension > 1920)
                return "max_dimension must be between 128 and 1920.";
            return null;
        }

        internal static bool MatchesRecording(RecordingState state, string id)
        {
            return state != null && (string.IsNullOrEmpty(id) || string.Equals(id, state.recording_id, StringComparison.Ordinal));
        }

        internal static bool IsViewRendering(EditorWindow view)
        {
            if (view == null || ViewParentField == null || ActualViewProperty == null)
                return false;
            var parent = ViewParentField.GetValue(view);
            // EditorWindow.Repaint is a no-op for an inactive dock tab. Reject it instead of
            // silently recording the last render texture as if it were a fresh video.
            return parent != null && ReferenceEquals(ActualViewProperty.GetValue(parent), view);
        }

        internal static Vector2Int ResolveOutputSize(int width, int height, int maxDimension)
        {
            if (width < 2 || height < 2)
                throw new ArgumentException("The rendered Game View is too small to encode.");
            var scale = Math.Min(1d, maxDimension / (double)Math.Max(width, height));
            // H.264 requires even dimensions. Rounding down also keeps the requested size limit.
            return new Vector2Int(Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
        }

        [Serializable]
        internal sealed class RecordingState
        {
            public string recording_id;
            public string status = "recording";
            public string path;
            public string mime_type = "video/mp4";
            public string started_at;
            public int width;
            public int height;
            public int fps;
            public int duration_seconds;
            public int frame_count;
            public double elapsed_seconds;
            public double last_frame_seconds;
            public bool has_audio;
            public bool ready;
            public long bytes;
            public string stop_reason;
            public string error;
        }

        internal interface IVideoSink : IDisposable
        {
            void Capture(double timestamp);
            long Complete();
        }

        // Clock and encoder are separate so stop, failure and reload behavior can be tested without
        // a native codec. Timestamps are wall-clock values; never fill a slow frame with a burst of
        // duplicate captures, or speed up the resulting movie by pretending captures were on time.
        internal sealed class RecordingSession
        {
            private readonly RecordingState _state;
            private readonly IVideoSink _sink;
            private readonly double _started;
            private double _nextFrame;
            public bool IsFinished { get; private set; }

            internal RecordingSession(RecordingState state, IVideoSink sink, double now)
            {
                _state = state;
                _sink = sink;
                _started = now;
            }

            internal void RequestStop()
            {
                if (!IsFinished)
                    _state.status = "stopping";
            }

            internal void Tick(double now)
            {
                if (IsFinished)
                    return;
                var elapsed = Math.Max(0, now - _started);
                _state.elapsed_seconds = elapsed;
                if (_state.status == "stopping" || elapsed >= _state.duration_seconds)
                {
                    Finish(_state.status == "stopping" ? "manual" : "duration_limit", now);
                    return;
                }
                if (elapsed < _nextFrame)
                    return;
                try
                {
                    // The native encoder can prepend an empty frame if the first timestamp is
                    // positive. Anchor only the first frame at zero; subsequent gaps stay real.
                    _sink.Capture(_state.frame_count == 0 ? 0 : elapsed);
                    _state.frame_count++;
                    _state.last_frame_seconds = elapsed;
                    _nextFrame = elapsed + 1d / _state.fps;
                }
                catch (Exception ex)
                {
                    _state.error = ex.Message;
                    Finish("capture_failed", now);
                }
            }

            internal void Finish(string reason, double now, bool interrupted = false)
            {
                if (IsFinished)
                    return;
                IsFinished = true;
                _state.elapsed_seconds = Math.Max(0, now - _started);
                _state.stop_reason = reason;
                try
                {
                    _state.bytes = _sink.Complete();
                    if (_state.frame_count == 0 || _state.bytes == 0)
                        _state.error = _state.error ?? "No video frames were finalized.";
                }
                catch (Exception ex)
                {
                    _state.error = _state.error ?? ex.Message;
                }
                finally
                {
                    try { _sink.Dispose(); }
                    catch (Exception ex) { _state.error = _state.error ?? ex.Message; }
                }
                _state.status = _state.error != null ? "failed" : interrupted ? "interrupted" : "completed";
                _state.ready = _state.error == null && _state.frame_count > 0 && _state.bytes > 0;
            }
        }

        private sealed class GameViewVideoSink : IVideoSink
        {
            private readonly EditorWindow _view;
            private readonly int _sourceWidth;
            private readonly int _sourceHeight;
            private readonly RecordingState _state;
            private RenderTexture _target;
            private Texture2D _frame;
            private MediaEncoder _encoder;

            internal GameViewVideoSink(EditorWindow view, int sourceWidth, int sourceHeight, RecordingState state)
            {
                _view = view;
                _sourceWidth = sourceWidth;
                _sourceHeight = sourceHeight;
                _state = state;
                try
                {
                    _target = new RenderTexture(state.width, state.height, 0, RenderTextureFormat.ARGB32);
                    _target.Create();
                    _frame = new Texture2D(state.width, state.height, TextureFormat.RGBA32, false);
                    _encoder = new MediaEncoder(state.path, new VideoTrackAttributes
                    {
                        frameRate = new MediaRational(state.fps),
                        width = (uint)state.width,
                        height = (uint)state.height,
                        includeAlpha = false
                    });
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Capture(double timestamp)
            {
                if (!IsViewRendering(_view))
                    throw new InvalidOperationException("Game View is hidden or closed. Keep the Game tab visible and start a new recording.");
                if (_view == null || !ScreenshotFunctions.TryGetPlayModeViewRenderTexture(_view, out var source))
                    throw new InvalidOperationException("Game View is no longer available. Open the Game tab and start a new recording.");
                if (source.width != _sourceWidth || source.height != _sourceHeight)
                    throw new InvalidOperationException("Game View resolution changed during recording. Start a new recording at the new size.");

                var previous = RenderTexture.active;
                try
                {
                    // Match screenshot orientation while reusing the same GPU/CPU buffers every frame.
                    var flip = ScreenshotFunctions.ShouldFlipPlayModeViewRenderTexture();
                    Graphics.Blit(source, _target, new Vector2(1, flip ? -1 : 1), new Vector2(0, flip ? 1 : 0));
                    RenderTexture.active = _target;
                    _frame.ReadPixels(new Rect(0, 0, _state.width, _state.height), 0, 0, false);
                    _frame.Apply(false, false);
                    if (!_encoder.AddFrame(_frame, new MediaTime((long)(timestamp * 1000000), 1000000, 1)))
                        throw new InvalidOperationException("Unity's video encoder rejected a frame.");
                }
                finally
                {
                    RenderTexture.active = previous;
                }
                _view.Repaint();
            }

            public long Complete()
            {
                var encoder = _encoder;
                _encoder = null;
                encoder?.Dispose();
                return File.Exists(_state.path) ? new FileInfo(_state.path).Length : 0;
            }

            public void Dispose()
            {
                try
                {
                    var encoder = _encoder;
                    _encoder = null;
                    encoder?.Dispose();
                }
                finally
                {
                    if (_target != null)
                    {
                        _target.Release();
                        UnityEngine.Object.DestroyImmediate(_target);
                        _target = null;
                    }
                    if (_frame != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_frame);
                        _frame = null;
                    }
                }
            }
        }
    }
}
