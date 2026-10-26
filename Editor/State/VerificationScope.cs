// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Funplay.Editor.State
{
    [Serializable] internal sealed class VerificationLease
    {
        public string job_id, status = "running", phase = "observing", error, scene, instance_id, request_key;
        public int timeout_seconds;
        public bool requested_background;
        public double deadline;
        public bool background_owned, background_before, background_assigned, cleanup_pending;
        public List<string> warnings = new List<string>();
        public List<string> recording_ids = new List<string>(), extraction_ids = new List<string>(), input_ids = new List<string>();
        public ProjectExecutionContext context;
        public PlayerClockSample first_frame, last_frame;
    }

    // One bounded scope owns only explicitly enrolled resources. Observations never restore,
    // change focus/mode, clear dirty state or impersonate application lifecycle completion.
    [InitializeOnLoad] internal static class VerificationScope
    {
        private const string Key = "Funplay.Verification.Leases";
        private static List<VerificationLease> leases;
        static VerificationScope()
        {
            try { leases = JsonConvert.DeserializeObject<List<VerificationLease>>(SessionState.GetString(Key, "[]")) ?? new List<VerificationLease>(); }
            catch { leases = new List<VerificationLease>(); }
            leases.RemoveAll(x => x == null || !Guid.TryParseExact(x.job_id, "N", out _));
            foreach (var lease in leases)
            {
                lease.warnings = lease.warnings ?? new List<string>();
                lease.input_ids = lease.input_ids ?? new List<string>(); lease.recording_ids = lease.recording_ids ?? new List<string>();
                lease.extraction_ids = lease.extraction_ids ?? new List<string>();
            }
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += () => CloseActive("interrupted", "domain_reload");
            EditorApplication.quitting += () => CloseActive("interrupted", "editor_quit");
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingPlayMode) CloseActive("interrupted", "play_mode_exit"); };
            // A previous cleanup failure is retained. Reads never retry it implicitly.
            foreach (var lease in leases.Where(x => x.status == "running"))
            { lease.status = "interrupted"; lease.error = "Reload interrupted the scope; call end explicitly to retry owned cleanup."; lease.cleanup_pending = true; }
            Save();
        }
        internal static VerificationLease Find(string id) => leases.FirstOrDefault(x => x.job_id == id);
        internal static string ValidateOwner(string id)
        {
            if (id == null) return null;
            var lease = Find(id);
            return lease == null || lease.status != "running" || lease.instance_id != MCPProjectContext.Current.instance_id ? "VERIFICATION_SCOPE_NOT_ACTIVE" : null;
        }
        internal static object Start(int timeout, bool background, string requestKey = null)
        {
            if (timeout < 5 || timeout > 300) return Response.Error("INVALID_SCOPE_TIMEOUT");
            if (requestKey != null && (string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 128)) return Response.Error("INVALID_SCOPE_REQUEST_KEY");
            var previous = requestKey == null ? null : leases.FirstOrDefault(x => x.request_key == requestKey);
            if (previous != null) return previous.timeout_seconds == timeout && previous.requested_background == background
                ? Read(previous.job_id) : Response.Error("SCOPE_REQUEST_KEY_CONFLICT");
            if (leases.Any(x => x.status == "running" || x.cleanup_pending)) return Response.Error("VERIFICATION_SCOPE_ACTIVE_OR_CLEANUP_PENDING");
            if (!EditorApplication.isPlaying || !EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return Response.Error("STABLE_PLAY_MODE_REQUIRED");
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path)) return Response.Error("AUTHORED_SCENE_REQUIRED");
            var lease = new VerificationLease { job_id = Guid.NewGuid().ToString("N"), scene = SceneSignature(),
                request_key = requestKey, timeout_seconds = timeout, requested_background = background,
                instance_id = MCPProjectContext.Current.instance_id, context = MCPProjectContext.Current,
                deadline = PlayerLoopWitness.Wall + timeout, background_owned = background,
                background_before = Application.runInBackground, background_assigned = true, first_frame = PlayerLoopWitness.Latest };
            leases.Add(lease); while (leases.Count > 16) leases.RemoveAt(0);
            Save(); // Journal ownership before touching a setting.
            try { if (background) Application.runInBackground = true; }
            catch (Exception ex) { Close(lease, "failed", ex.Message); return Response.Error("VERIFICATION_SCOPE_START_FAILED", new { lease.job_id, lease.error, lease.cleanup_pending }); }
            return Read(lease.job_id);
        }
        internal static void Enroll(string id, string kind, string resource)
        {
            if (id == null) return;
            if (ValidateOwner(id) != null) throw new InvalidOperationException("VERIFICATION_SCOPE_NOT_ACTIVE");
            var lease = Find(id);
            (kind == "recording" ? lease.recording_ids : kind == "recording_frames" ? lease.extraction_ids : lease.input_ids).Add(resource);
            Save();
        }
        internal static object Read(string id, string requestKey = null)
        {
            var lease = id == null && requestKey != null ? leases.FirstOrDefault(x => x.request_key == requestKey) : Find(id);
            if (lease == null) return Response.Error("VERIFICATION_SCOPE_NOT_FOUND");
            return Response.Success("Verification scope: " + lease.status + ".", new {
                lease.job_id, lease.status, lease.phase, lease.error, lease.cleanup_pending, lease.warnings,
                lease.request_key, lease.timeout_seconds,
                lease.context, lease.scene, scene_unchanged = lease.scene == SceneSignature(),
                lease.background_owned, lease.recording_ids, lease.extraction_ids, lease.input_ids,
                lease.first_frame, last_frame = lease.status == "running" ? PlayerLoopWitness.Latest : lease.last_frame,
                player = PlayerLoopWitness.Snapshot(), application_accepted = (bool?)null,
                limits = "Owns enrolled jobs and the optional temporary background flag only; scenes, user dirty state, business data and network effects are not reverted."
            });
        }
        internal static object End(string id)
        {
            var lease = Find(id); if (lease == null) return Response.Error("VERIFICATION_SCOPE_NOT_FOUND");
            Close(lease, "completed", null); return Read(id);
        }
        internal static bool ShouldRestore(bool before, bool assigned, bool actual) => actual == assigned && before != actual;
        private static void CloseActive(string status, string error)
        { foreach (var lease in leases.Where(x => x.status == "running").ToArray()) Close(lease, status, error); }
        private static void Close(VerificationLease lease, string status, string error)
        {
            if (lease.status != "running" && !lease.cleanup_pending) return;
            lease.cleanup_pending = true; Save();
            lease.last_frame = PlayerLoopWitness.Latest;
            var failures = new List<string>();
            if (lease.instance_id != MCPProjectContext.Current.instance_id) failures.Add("Editor instance changed; no ownership assumed in the new process.");
            else
            {
                foreach (var id in lease.input_ids) Try(() => InputFrameJob.Cancel(id), failures);
                foreach (var id in lease.extraction_ids) Try(() => RecordingEvidenceService.Control(id, "cancel"), failures);
                foreach (var id in lease.recording_ids) Try(() => VideoRecordingFunctions.RecordGameView("stop", recording_id: id), failures);
                if (lease.background_owned)
                {
                    try
                    {
                        if (ShouldRestore(lease.background_before, lease.background_assigned, Application.runInBackground))
                            Application.runInBackground = lease.background_before;
                        else if (Application.runInBackground != lease.background_assigned)
                            lease.warnings.Add("Background flag changed since assignment; current user value preserved.");
                        lease.background_owned = false;
                    }
                    catch (Exception ex) { failures.Add(ex.Message); }
                }
            }
            lease.status = failures.Count > 0 ? "cleanup_pending" : status;
            lease.phase = lease.status; lease.error = failures.Count > 0 ? string.Join("; ", failures) : error;
            lease.cleanup_pending = failures.Count > 0;
            if (lease.scene != SceneSignature()) lease.warnings.Add("Scene set changed; no scene restoration attempted.");
            Save();
        }
        private static void Try(Func<object> operation, List<string> failures)
        {
            try {
                var receipt = Newtonsoft.Json.Linq.JObject.FromObject(operation());
                if (receipt.Value<bool?>("success") != true) failures.Add(receipt.ToString(Newtonsoft.Json.Formatting.None));
                else if (new[] { "recording", "stopping", "running", "decoding", "queued", "cleanup_pending" }.Contains((string)receipt["data"]?["status"]))
                    failures.Add("Owned resource has not settled: " + (string)receipt["data"]?["status"]);
            } catch (Exception ex) { failures.Add(ex.Message); }
        }
        private static void Tick() { foreach (var lease in leases.Where(x => x.status == "running" && PlayerLoopWitness.Wall >= x.deadline).ToArray()) Close(lease, "expired", "deadline"); }
        private static string SceneSignature() => string.Join("|", Enumerable.Range(0, SceneManager.sceneCount).Select(i => {
            var s = SceneManager.GetSceneAt(i); return s.path + ":" + s.handle;
        }));
        private static void Save() => SessionState.SetString(Key, JsonConvert.SerializeObject(leases));
    }
}
