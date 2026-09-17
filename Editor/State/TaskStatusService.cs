// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Funplay.Editor.DI;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Settings;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.State
{
    // Adapts existing receipts; their IDs, journals, ownership and recovery stay with each service.
    // All provider reads and the bounded observation cache are accessed on the Editor thread.
    internal static class TaskStatusService
    {
        internal static readonly string[] Kinds = { "editor", "ui_audit", "ui_preview", "recording", "recording_frames", "tests" };
        private sealed class Observation { internal string revision; internal double changedAt, seenAt; }
        private static readonly Dictionary<string, Observation> observations = new Dictionary<string, Observation>();

        internal static object ValidateWait(int seconds) => seconds < 0 || seconds > 30
            ? Response.Error("INVALID_WAIT", new { wait_seconds = "0..30; zero returns immediately" }) : null;

        internal static string[] AccessTools(string kind)
        {
            switch (kind)
            {
                case "editor": return new[] { "prepare_editor", "get_editor_operation" };
                case "ui_audit": return new[] { "audit_ui", "get_ui_audit" };
                case "ui_preview": return new[] { "start_ui_preview_session", "end_ui_preview_session", "get_ui_preview_session" };
                case "recording": return new[] { "record_game_view" };
                case "recording_frames": return new[] { "extract_recording_frames", "get_recording_frame" };
                case "tests": return new[] { "run_tests", "get_test_job" };
                default: return Array.Empty<string>();
            }
        }

        internal static bool CanRead(string kind, Func<string, bool> allowed) => AccessTools(kind).Any(allowed);
        private static bool Allowed(string tool)
        {
            var settings = RootScopeServices.Services?.GetService(typeof(ISettingsController)) as ISettingsController;
            return ToolRegistry.IsEnabled(tool) && MCPToolExportPolicy.IsToolAllowed(tool,
                MCPToolExportPolicy.Parse(settings?.MCPToolExportProfile), settings?.MCPCoreToolsConfigured ?? false,
                settings?.MCPCoreTools, settings?.MCPFullToolsConfigured ?? false, settings?.MCPFullTools);
        }

        internal static JObject Query(string taskId, string kind, string key, string afterRevision, int offset, int limit)
        {
            var problem = Resolve(ref taskId, ref kind, key);
            if (problem != null) return JObject.FromObject(problem);
            if (afterRevision != null && (afterRevision.Length != 64 || afterRevision.Any(c => !"0123456789abcdef".Contains(c))))
                return JObject.FromObject(Response.Error("INVALID_TASK_REVISION"));
            if (offset < 0 || limit < 1 || limit > 500) return JObject.FromObject(Response.Error("INVALID_PAGE"));
            if (!CanRead(kind, Allowed)) return JObject.FromObject(Response.Error("TASK_KIND_NOT_EXPOSED", new
            { kind, hint = "Enable the relevant task or legacy status tool deliberately; get_task does not bypass disabled tools or custom exposure." }));

            object receipt;
            switch (kind)
            {
                case "editor": receipt = EditorOperationService.Get(taskId, key); break;
                case "ui_audit": receipt = UIAuditService.Read(taskId, offset, limit); break;
                case "ui_preview": receipt = UIPreviewSession.Get(taskId, key); break;
                case "recording": receipt = VideoRecordingFunctions.RecordGameView("status", recording_id: taskId); break;
                case "recording_frames": receipt = RecordingEvidenceService.Control(taskId, "status"); break;
                case "tests": receipt = TestRunnerFunctions.GetTestJob(taskId); break;
                default: return JObject.FromObject(Response.Error("INVALID_TASK_KIND"));
            }
            return Observe(kind, receipt, afterRevision);
        }

        // Never guess a latest job: namespaced IDs prevent querying/replacing a different task.
        internal static object Resolve(ref string id, ref string kind, string key)
        {
            if (id != null && id.Contains(":"))
            {
                var parts = id.Split(new[] { ':' }, 2);
                if (kind != null && kind != parts[0]) return Response.Error("TASK_KIND_MISMATCH");
                kind = parts[0]; id = parts[1];
            }
            if (!Kinds.Contains(kind)) return Response.Error("INVALID_TASK_KIND", new { accepted = Kinds });
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(key)) return Response.Error("TASK_ID_OR_REQUEST_KEY_REQUIRED");
            if (id != null && !Guid.TryParse(id, out _)) return Response.Error("INVALID_TASK_ID");
            if (key != null && (key.Length > 128 || string.IsNullOrWhiteSpace(key) || kind != "editor" && kind != "ui_preview"))
                return Response.Error("INVALID_TASK_REQUEST_KEY", new { hint = "request_key lookup is supported only for editor and ui_preview." });
            return null;
        }

        internal static JObject Observe(string kind, object receipt, string afterRevision = null)
        {
            var result = Describe(kind, receipt, afterRevision);
            var task = result["data"]?["task"] as JObject;
            if (task == null) return result;
            var identity = (string)task["task_id"];
            var revision = (string)task["revision"];
            double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (!observations.TryGetValue(identity, out var observation))
            {
                if (observations.Count >= 128) observations.Remove(observations.OrderBy(x => x.Value.seenAt).First().Key);
                observations[identity] = observation = new Observation { revision = revision, changedAt = now };
            }
            if (observation.revision != revision) { observation.revision = revision; observation.changedAt = now; }
            observation.seenAt = now;
            task["poll_after_ms"] = (bool)task["wait_complete"] ? 0 : PollDelay(now - observation.changedAt);
            AlignPollingHint(result);
            return result;
        }

        // Annotated receipts have a single polling recommendation, even when the native
        // service also supplies the legacy data.poll_after_ms field.
        internal static void AlignPollingHint(JObject receipt)
        {
            var data = receipt["data"] as JObject;
            if (data?["poll_after_ms"] != null && data["task"]?["poll_after_ms"] != null)
                data["poll_after_ms"] = data["task"]["poll_after_ms"].DeepClone();
        }

        internal static int PollDelay(double quietSeconds) => quietSeconds < 2 ? 1000 : quietSeconds < 5 ? 2000 : quietSeconds < 15 ? 5000 : 10000;

        internal static object Attach(string kind, object receipt)
        {
            var envelope = receipt as JObject ?? JObject.FromObject(receipt);
            // Preserve legacy structured error objects for direct C# callers as well as JSON clients.
            return envelope.Value<bool?>("success") == true ? Observe(kind, envelope) : receipt;
        }

        // Pure projection used in tests and by all start receipts. Keep native payloads intact.
        internal static JObject Describe(string kind, object receipt, string afterRevision = null)
        {
            var result = receipt is JObject json ? (JObject)json.DeepClone() : JObject.FromObject(receipt);
            if (result.Value<bool?>("success") != true) return result;
            var data = result["data"] as JObject;
            if (data == null) return result;
            var state = kind == "editor" ? data["operation"] as JObject : kind == "ui_preview" ? data["session"] as JObject : data;
            if (state == null) return JObject.FromObject(Response.Error("TASK_NOT_FOUND"));
            // A preview also carries its nested Editor operation_id (which may be JSON null).
            // Never select the first ID-shaped field: it identifies a different owned task.
            string id = kind == "editor" ? state.Value<string>("operation_id") :
                kind == "ui_preview" ? state.Value<string>("session_id") :
                kind == "recording" ? state.Value<string>("recording_id") :
                state.Value<string>("job_id") ?? state.Value<string>("jobId");
            if (string.IsNullOrEmpty(id)) return JObject.FromObject(Response.Error("TASK_RECEIPT_INVALID"));
            string status = state.Value<string>("status") ?? "running", phase = state.Value<string>("phase") ?? status;
            // Progress counters/timestamps are deliberately excluded. A decoding frame, test case,
            // elapsed second or scanned object must not wake the model on every Editor update.
            var stable = new JObject { ["kind"] = kind, ["id"] = id, ["status"] = status, ["phase"] = phase };
            foreach (var name in new[] { "error", "error_code", "message", "compilation_errors", "complete", "ready", "stop_reason",
                "scenes_restored", "view_restored", "selection_restored", "assets_cleaned", "warnings", "possiblyStuck" })
                if (state[name] != null) stable[name] = state[name].DeepClone();
            if (data["current_editor"] != null) stable["current_editor"] = data["current_editor"].DeepClone();
            string revision;
            using (var sha = SHA256.Create()) revision = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(stable.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
            bool pending = new[] { "running", "queued", "decoding", "recording", "stopping", "setting_up", "preparing", "ending" }.Contains(status);
            data["task"] = new JObject
            {
                ["task_id"] = kind + ":" + id, ["kind"] = kind, ["id"] = id, ["status"] = status, ["phase"] = phase,
                ["revision"] = revision, ["changed"] = afterRevision == null || revision != afterRevision,
                ["wait_complete"] = !pending, ["poll_after_ms"] = pending ? 1000 : 0, ["wait_reason"] = "snapshot", ["waited_ms"] = 0,
                ["snapshot_at"] = DateTime.UtcNow.ToString("O")
            };
            return result;
        }
    }
}
