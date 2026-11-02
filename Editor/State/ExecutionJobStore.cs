// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.State
{
    [Serializable] internal sealed class ExecutionJobRecord
    {
        public string job_id, request_hash, key_hash, source_tool, status = "queued", phase = "queued";
        public string error_code, execution_status = "not_executed", serialization_status = "not_started";
        public bool? executed = false;
        public bool cancel_requested, result_success, result_deleted, cleanup_pending;
        public string result_sha256, result_code, result_message, exception_type;
        public int result_bytes, timeout_seconds;
        public long created_ms, started_ms, finished_ms, expires_ms;
        public double queue_ms, refresh_ms, compile_ms, execute_ms, serialize_ms, persist_ms;
        public int revision;
        public ProjectExecutionContext context;
        public JObject recovery, diagnostic;
        [JsonIgnore] internal bool Terminal => status != "queued" && status != "running";
    }

    [Serializable] internal sealed class ExecutionKeyRecord
    {
        public string key_hash, request_hash, job_id, source_tool, instance_id;
    }
    [Serializable] internal sealed class ExecutionJournal
    {
        public int schema_version = 1;
        public List<ExecutionJobRecord> jobs = new List<ExecutionJobRecord>();
        public List<ExecutionKeyRecord> keys = new List<ExecutionKeyRecord>();
    }

    // No Unity APIs: admission, journal snapshots and payload reads remain usable while the
    // Editor thread is executing a synchronous API. Only the runner invokes Unity/user code.
    internal sealed class ExecutionJobStore
    {
        internal const int MaxRecords = 128, MaxKeys = 4096, InlineBytes = 16 * 1024, PageBytes = 16 * 1024;
        internal const long MaxStoredBytes = 32L * 1024 * 1024, RetentionMs = 24L * 60 * 60 * 1000;
        private readonly object gate = new object();
        private readonly string root, journalPath;
        private readonly Func<long> now;
        private ExecutionJournal journal;
        internal string Error { get; private set; }

        internal ExecutionJobStore(string root, Func<long> clock = null)
        {
            this.root = Path.GetFullPath(root); journalPath = Path.Combine(this.root, "journal.json");
            now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try
            {
                Directory.CreateDirectory(this.root);
                journal = File.Exists(journalPath) ? ReadJournal() : new ExecutionJournal();
                // A previous domain/process cannot continue arbitrary user code. Preserve the ID,
                // key and last known phase, but never enqueue/replay anything during recovery.
                bool changed = false;
                foreach (var job in journal.jobs.Where(x => !x.Terminal))
                {
                    bool returned = job.execution_status == "returned";
                    bool mayHaveRun = returned || job.phase == "executing" || job.phase == "serializing" || job.phase == "persisting";
                    job.status = mayHaveRun ? "outcome_unknown" : "interrupted";
                    job.execution_status = returned ? "returned" : mayHaveRun ? "outcome_unknown" : "not_executed";
                    job.executed = returned ? job.executed : mayHaveRun ? (bool?)null : false;
                    job.error_code = "EXECUTION_INTERRUPTED"; job.phase = "recovered_without_replay";
                    job.finished_ms = now(); job.revision++; changed = true;
                }
                if (changed) Save();
            }
            catch (Exception ex) { journal = new ExecutionJournal(); Error = ex.GetType().Name; }
        }

        internal static string Hash(string value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant(); }
        private static bool Id(string id) => Guid.TryParseExact(id, "N", out _);
        private static bool Digest(string value) => value?.Length == 64 && value.All(c => "0123456789abcdef".Contains(c));
        private static bool Source(string value) => value == "execute_code" || value == "replay_execute_code";
        private string PayloadPath(string id)
        { if (!Id(id)) throw new ArgumentException("INVALID_EXECUTION_ID"); return Path.Combine(root, id + ".result.json"); }

        private ExecutionJournal ReadJournal()
        {
            var info = new FileInfo(journalPath);
            if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized journal");
            var value = JsonConvert.DeserializeObject<ExecutionJournal>(File.ReadAllText(journalPath), new JsonSerializerSettings {
                TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore, MaxDepth = ToolResultSerializer.MaxDepth });
            if (value == null || value.schema_version != 1 || value.jobs == null || value.keys == null ||
                value.jobs.Count > MaxRecords || value.keys.Count > MaxKeys ||
                value.jobs.Any(x => x == null || !Id(x.job_id) || x.context == null || !Digest(x.context.project_id) ||
                    string.IsNullOrWhiteSpace(x.context.instance_id) || !Digest(x.request_hash) || x.key_hash != null && !Digest(x.key_hash) ||
                    !new[] { "queued", "running", "completed", "failed", "cancelled", "interrupted", "outcome_unknown" }.Contains(x.status) ||
                    string.IsNullOrWhiteSpace(x.phase) || x.phase.Length > 80 || x.created_ms < 0 || x.revision < 0 ||
                    x.timeout_seconds < 5 || x.timeout_seconds > 900 ||
                    x.result_bytes < 0 || x.result_bytes > ToolResultSerializer.MaxBytes ||
                    x.result_sha256 != null && !Digest(x.result_sha256) || !Source(x.source_tool)) ||
                value.keys.Any(x => x == null || !Id(x.job_id) || !Digest(x.key_hash) || !Digest(x.request_hash) || !Source(x.source_tool)) ||
                value.jobs.Select(x => x.job_id).Distinct().Count() != value.jobs.Count ||
                value.keys.Select(x => x.key_hash).Distinct().Count() != value.keys.Count)
                throw new InvalidDataException("Invalid execution journal; nothing was replayed");
            return value;
        }

        private static ExecutionJobRecord Copy(ExecutionJobRecord record) => record == null ? null :
            JsonConvert.DeserializeObject<ExecutionJobRecord>(JsonConvert.SerializeObject(record));
        internal ExecutionJobRecord Find(string id, string key = null)
        {
            lock (gate)
            {
                if (id == null && key != null)
                { var entry = journal.keys.FirstOrDefault(x => x.key_hash == Hash(key)); id = entry?.job_id; }
                return Copy(journal.jobs.FirstOrDefault(x => x.job_id == id));
            }
        }
        internal string ActiveId { get { lock (gate) return journal.jobs.FirstOrDefault(x => !x.Terminal)?.job_id; } }
        internal string Owner(string id, string key = null)
        {
            lock (gate) return Find(id, key)?.source_tool ?? journal.keys.FirstOrDefault(x =>
                key != null ? x.key_hash == Hash(key) : x.job_id == id)?.source_tool;
        }

        internal object Admit(string requestHash, string key, string sourceTool, int timeout, ProjectExecutionContext context,
            out ExecutionJobRecord admitted, out bool existing)
        {
            lock (gate)
            {
                admitted = null; existing = false;
                if (Error != null) return Response.Error("EXECUTION_JOURNAL_UNAVAILABLE", new { detail = Error, executed = false });
                if (!Digest(requestHash) || !Source(sourceTool) || context == null || !Digest(context.project_id))
                    return Response.Error("INVALID_EXECUTION_ADMISSION", new { executed = false });
                if (timeout < 5 || timeout > 900) return Response.Error("INVALID_EXECUTION_TIMEOUT");
                if (key != null && (string.IsNullOrWhiteSpace(key) || key.Length > 128)) return Response.Error("INVALID_EXECUTION_REQUEST_KEY");
                string keyHash = key == null ? null : Hash(key);
                var previous = keyHash == null ? null : journal.keys.FirstOrDefault(x => x.key_hash == keyHash);
                if (previous != null)
                {
                    if (previous.source_tool != sourceTool || previous.request_hash != requestHash)
                        return Response.Error("EXECUTION_REQUEST_KEY_CONFLICT", new { task_id = "execution:" + previous.job_id, executed = false });
                    existing = true; admitted = Copy(journal.jobs.FirstOrDefault(x => x.job_id == previous.job_id));
                    if (admitted == null) return Response.Error("EXECUTION_RECEIPT_EXPIRED", new { task_id = "execution:" + previous.job_id,
                        retry_safe = false, message = "The key remains protected; an expired key never starts another execution." });
                    return null;
                }
                if (journal.jobs.Any(x => !x.Terminal)) return Response.Error("EXECUTION_JOB_ACTIVE", new { task_id = "execution:" + ActiveId, executed = false });
                if (key != null && journal.keys.Count >= MaxKeys) return Response.Error("EXECUTION_KEY_CAPACITY", new { executed = false, limit = MaxKeys });
                try
                {
                    Maintain();
                    while (journal.jobs.Count >= MaxRecords)
                    {
                        var oldest = journal.jobs.OrderBy(x => x.created_ms).First();
                        DeletePayload(oldest); journal.jobs.Remove(oldest);
                    }
                    var job = new ExecutionJobRecord { job_id = Guid.NewGuid().ToString("N"), request_hash = requestHash,
                        key_hash = keyHash, source_tool = sourceTool, timeout_seconds = timeout, context = context,
                        created_ms = now(), expires_ms = now() + RetentionMs };
                    journal.jobs.Add(job);
                    if (key != null) journal.keys.Add(new ExecutionKeyRecord { key_hash = keyHash, request_hash = requestHash,
                        job_id = job.job_id, source_tool = sourceTool, instance_id = context.instance_id });
                    Save(); admitted = Copy(job); return null;
                }
                catch (Exception ex)
                {
                    Error = ex.GetType().Name;
                    var failed = journal.jobs.LastOrDefault(x => !x.Terminal && x.request_hash == requestHash && x.key_hash == keyHash);
                    if (failed != null)
                    {
                        failed.status = "failed"; failed.phase = "admission_persistence_failed";
                        failed.error_code = "EXECUTION_JOURNAL_UNAVAILABLE"; failed.executed = false; failed.finished_ms = now(); failed.revision++;
                    }
                    return Response.Error("EXECUTION_JOURNAL_UNAVAILABLE", new { executed = false, detail = Error,
                        task_id = failed == null ? null : "execution:" + failed.job_id, journal_available = false });
                }
            }
        }

        internal void Update(string id, Action<ExecutionJobRecord> update)
        {
            lock (gate)
            {
                var job = journal.jobs.Single(x => x.job_id == id);
                update(job); job.revision++;
                try { Save(); } catch (Exception ex) { Error = ex.GetType().Name; throw; }
            }
        }

        internal void FailInMemory(string id, ScriptExecutionTrace trace, Exception exception)
        {
            lock (gate)
            {
                Error = exception.GetType().Name;
                var job = journal.jobs.FirstOrDefault(x => x.job_id == id);
                if (job == null) return;
                job.status = "failed"; job.phase = "journal_unavailable"; job.result_success = false;
                job.error_code = "EXECUTION_JOURNAL_UNAVAILABLE"; job.exception_type = exception.GetType().FullName;
                job.executed = trace.Executed; job.execution_status = trace.Executed ? "returned" : "not_executed";
                job.diagnostic = OperationDiagnostic.Capture(trace.Phase, exception); job.finished_ms = now(); job.revision++;
                // Deliberately do not pretend this snapshot is durable. Admission remains closed.
            }
        }

        internal void Complete(string id, string json, ScriptExecutionTrace trace)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > ToolResultSerializer.MaxBytes) throw new InvalidDataException("Result exceeds storage limit");
            var payload = JObject.Parse(json);
            lock (gate)
            {
                var job = journal.jobs.Single(x => x.job_id == id);
                if (job.Terminal) return;
                job.executed = trace.Executed;
                job.recovery = trace.Recovery == null ? null : JObject.FromObject(trace.Recovery);
                job.diagnostic = trace.Diagnostic == null ? null : (JObject)trace.Diagnostic.DeepClone();
                job.exception_type = job.diagnostic?.Value<string>("exception_type");
                job.phase = "persisting"; job.revision++; Save();
                try
                {
                    Maintain(bytes.Length, id);
                    AtomicWrite(PayloadPath(id), json);
                    job.result_bytes = bytes.Length; job.result_sha256 = Hash(json);
                    job.result_success = payload.Value<bool?>("success") == true;
                    job.result_code = payload.Value<string>("code"); job.result_message = ToolResultSerializer.Short(payload.Value<string>("message"), 512);
                    job.serialization_status = job.result_code == "RESULT_SERIALIZATION_FAILED" || job.result_code == "RESULT_TOO_LARGE" ? "failed" : "completed";
                    job.status = job.cancel_requested && !trace.Executed ? "cancelled" : job.result_success ? "completed" : "failed";
                    job.error_code = job.result_code;
                    job.execution_status = trace.Executed ? "returned" : "not_executed";
                    job.phase = job.status; job.finished_ms = now(); job.revision++; Save();
                }
                catch (Exception ex)
                {
                    job.status = "failed"; job.phase = "result_persistence_failed"; job.error_code = "EXECUTION_RESULT_PERSISTENCE_FAILED";
                    job.result_success = false;
                    job.execution_status = trace.Executed ? "returned" : "not_executed";
                    job.exception_type = ex.GetType().FullName; job.finished_ms = now(); job.revision++;
                    try { Save(); } catch { Error = ex.GetType().Name; }
                }
            }
        }

        internal object Cancel(string id)
        {
            lock (gate)
            {
                var job = journal.jobs.FirstOrDefault(x => x.job_id == id);
                if (job == null) return Response.Error("EXECUTION_NOT_FOUND");
                if (job.Terminal) return null;
                if (job.phase == "executing" || job.phase == "serializing" || job.phase == "persisting")
                    return Response.Error("EXECUTION_NOT_INTERRUPTIBLE", new { task_id = "execution:" + id,
                        executed = job.executed, message = "A synchronous Unity call cannot be force-cancelled. Observe this execution; no rollback or stop was performed." });
                job.cancel_requested = true;
                if (job.phase == "queued") { job.status = "cancelled"; job.phase = "cancelled"; job.error_code = "EXECUTION_CANCELLED"; job.finished_ms = now(); }
                job.revision++; Save(); return null;
            }
        }

        internal object Cleanup(string id)
        {
            lock (gate)
            {
                var job = journal.jobs.FirstOrDefault(x => x.job_id == id);
                if (job == null) return Response.Error("EXECUTION_NOT_FOUND");
                if (!job.Terminal) return Response.Error("EXECUTION_STILL_RUNNING");
                try { DeletePayload(job); job.revision++; Save(); return null; }
                catch (Exception ex) { job.cleanup_pending = true; job.exception_type = ex.GetType().FullName; try { Save(); } catch { } return Response.Error("EXECUTION_CLEANUP_PENDING"); }
            }
        }

        // Explicit, separately confirmed removal of the retry tombstone. Cleanup alone never
        // removes it. This action is only for an operator who intentionally wants key reuse.
        internal object Forget(string id, bool confirm)
        {
            lock (gate)
            {
                var job = journal.jobs.FirstOrDefault(x => x.job_id == id);
                if (!confirm) return Response.Error("CONFIRM_FORGET_REQUIRED");
                if (job != null && !job.Terminal) return Response.Error("EXECUTION_STILL_RUNNING");
                var entries = journal.keys.Where(x => x.job_id == id).ToArray();
                if (entries.Length == 0) return Response.Error("EXECUTION_KEY_NOT_FOUND");
                foreach (var entry in entries) journal.keys.Remove(entry);
                Save(); return Response.Success("Idempotency protection explicitly removed. Reusing that key can execute again.", new { idempotency_protection_removed = true, task_id = "execution:" + id });
            }
        }

        internal JObject Read(string id, string key, string format = "summary", string path = null,
            int offset = 0, int limit = 100, bool diagnostics = false, bool originalResult = false)
        {
            lock (gate)
            {
                var job = Find(id, key);
                if (job == null)
                {
                    if (Error != null) return JObject.FromObject(Response.Error("EXECUTION_JOURNAL_UNAVAILABLE", new { retry_safe = false, journal_available = false }));
                    bool expired = key != null && journal.keys.Any(x => x.key_hash == Hash(key));
                    return JObject.FromObject(Response.Error(expired ? "EXECUTION_RECEIPT_EXPIRED" : "EXECUTION_NOT_FOUND", new { retry_safe = false }));
                }
                if (format != "summary" && format != "items" && format != "text") return JObject.FromObject(Response.Error("INVALID_RESULT_FORMAT"));
                if (offset < 0 || limit < 1 || limit > (format == "text" ? 16384 : 500)) return JObject.FromObject(Response.Error("INVALID_PAGE"));
                bool retained = job.result_sha256 != null && !job.result_deleted && now() < job.expires_ms;
                bool intactSize = false;
                try { var file = new FileInfo(PayloadPath(job.job_id)); intactSize = file.Exists && file.Length == job.result_bytes; } catch (IOException) { }
                bool available = retained && intactSize;
                var data = JObject.FromObject(job); data.Remove("key_hash"); data.Remove("request_hash"); data.Remove("diagnostic");
                data["budget_exceeded"] = (job.finished_ms > 0 ? job.finished_ms : now()) - job.created_ms > job.timeout_seconds * 1000L;
                data["cancel_supported"] = !job.Terminal && job.phase != "executing" && job.phase != "serializing" && job.phase != "persisting";
                data["result"] = new JObject { ["available"] = available, ["bytes"] = job.result_bytes,
                    ["sha256"] = job.result_sha256, ["inline_limit_bytes"] = InlineBytes, ["expires_at_ms"] = job.expires_ms,
                    ["storage"] = "project_library", ["integrity_status"] = retained && !intactSize ? "missing_or_changed" : available ? "hash_not_checked_this_read" : "unavailable",
                    ["raw_result_recoverable"] = job.serialization_status != "failed" && available };
                data["journal_available"] = Error == null;
                if (diagnostics && job.diagnostic != null) data["diagnostic"] = job.diagnostic.DeepClone();
                JObject payload = null;
                if (retained && !intactSize && format != "summary")
                    return JObject.FromObject(Response.Error("EXECUTION_RESULT_MISSING_OR_CHANGED", new { task_id = "execution:" + job.job_id, executed = job.executed, retry_safe = false }));
                if (available && (format != "summary" || job.result_bytes <= InlineBytes))
                {
                    try
                    {
                        var file = new FileInfo(PayloadPath(job.job_id));
                        if (!file.Exists || file.Length != job.result_bytes || file.Length > ToolResultSerializer.MaxBytes)
                            return JObject.FromObject(Response.Error("EXECUTION_RESULT_MISSING_OR_CHANGED", new { task_id = "execution:" + job.job_id, executed = job.executed, retry_safe = false }));
                        var text = File.ReadAllText(file.FullName);
                        if (Hash(text) != job.result_sha256) return JObject.FromObject(Response.Error("EXECUTION_RESULT_MISSING_OR_CHANGED", new { executed = job.executed, retry_safe = false }));
                        data["result"]["integrity_status"] = "sha256_verified";
                        if (format == "text")
                        {
                            if (path != null) return JObject.FromObject(Response.Error("TEXT_RESULT_PATH_UNSUPPORTED"));
                            if (offset > text.Length || offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1]))
                                return JObject.FromObject(Response.Error("INVALID_TEXT_OFFSET"));
                            int end = Math.Min(text.Length, offset + limit);
                            while (end > offset && (end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]) || Encoding.UTF8.GetByteCount(text.Substring(offset, end - offset)) > PageBytes)) end--;
                            if (end == offset && offset < text.Length) return JObject.FromObject(Response.Error("TEXT_PAGE_LIMIT_TOO_SMALL"));
                            data["result_page"] = new JObject { ["format"] = "text", ["offset"] = offset, ["next_offset"] = end,
                                ["total_characters"] = text.Length, ["offset_units"] = "UTF-16 code units; use next_offset", ["complete"] = end == text.Length,
                                ["text"] = text.Substring(offset, end - offset), ["sha256"] = job.result_sha256 };
                        }
                        else
                        {
                            payload = JObject.Parse(text);
                            if (format == "items") data["result_page"] = Page(payload, path, offset, limit, job.result_sha256);
                            else if (job.result_bytes <= InlineBytes) data["output"] = payload["data"]?.DeepClone();
                        }
                    }
                    catch (ArgumentException) { return JObject.FromObject(Response.Error("INVALID_RESULT_PATH_OR_OFFSET", new { executed = job.executed, retry_safe = false })); }
                    catch (Exception ex) { return JObject.FromObject(Response.Error("EXECUTION_RESULT_READ_FAILED", new { exception_type = ex.GetType().FullName, executed = job.executed, retry_safe = false })); }
                }
                if (format != "summary" && !available) return JObject.FromObject(Response.Error(job.Terminal ? "EXECUTION_RESULT_UNAVAILABLE" : "EXECUTION_RESULT_NOT_READY", new { task_id = "execution:" + job.job_id, executed = job.executed, retry_safe = false }));
                if (originalResult && payload != null && job.result_bytes <= InlineBytes)
                {
                    data.Remove("output");
                    var originalData = payload["data"] as JObject ?? new JObject();
                    originalData["execution"] = data; originalData["job_id"] = job.job_id; originalData["status"] = job.status; originalData["phase"] = job.phase;
                    payload["data"] = originalData;
                    if (!job.result_success && job.error_code != null && job.error_code != payload.Value<string>("code"))
                    { payload["success"] = false; payload["code"] = job.error_code; payload["error"] = job.error_code; }
                    return payload;
                }
                var receipt = JObject.FromObject(Response.Success("Execution task: " + job.status + ". Inspect result_success and execution_status; a status query does not replay or cancel it.", data));
                if (originalResult && job.Terminal) { receipt["success"] = job.result_success; if (!job.result_success) { receipt["code"] = job.error_code ?? "EXECUTION_FAILED"; receipt["error"] = receipt["code"].DeepClone(); } }
                return receipt;
            }
        }

        private static JToken Select(JToken root, string pointer)
        {
            if (string.IsNullOrEmpty(pointer)) return root;
            if (pointer.Length > 1024 || !pointer.StartsWith("/", StringComparison.Ordinal)) throw new ArgumentException("Use a JSON Pointer beginning with /.");
            var value = root;
            foreach (var part in pointer.Substring(1).Split('/'))
            {
                string key = part.Replace("~1", "/").Replace("~0", "~");
                if (value is JObject obj) value = obj[key];
                else if (value is JArray array && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count) value = array[index];
                else value = null;
                if (value == null) throw new ArgumentException("Result path not found.");
            }
            return value;
        }

        private static JObject Page(JObject payload, string path, int offset, int limit, string hash)
        {
            var selected = Select(payload, path ?? "/data/returnValue");
            int total = selected is JContainer container ? container.Count : 1;
            var items = selected is JArray array ? array.Skip(offset) : selected is JObject obj
                ? obj.Properties().Skip(offset).Select(x => (JToken)new JObject { ["name"] = x.Name, ["value"] = x.Value.DeepClone() }) : new[] { selected }.Skip(offset);
            if (offset > total) throw new ArgumentException("Offset exceeds item count.");
            var page = new JArray();
            int usedBytes = 2;
            foreach (var item in items.Take(limit))
            {
                int bytes = Encoding.UTF8.GetByteCount(item.ToString(Formatting.None));
                if (bytes + 2 > PageBytes)
                { if (page.Count == 0) return new JObject { ["error_code"] = "RESULT_ITEM_TOO_LARGE", ["hint"] = "Select a narrower result_path or use result_format=text.", ["offset"] = offset, ["next_offset"] = offset }; break; }
                if (usedBytes + bytes + (page.Count > 0 ? 1 : 0) > PageBytes) break;
                usedBytes += bytes + (page.Count > 0 ? 1 : 0);
                page.Add(item.DeepClone());
            }
            return new JObject { ["format"] = "items", ["path"] = path ?? "/data/returnValue", ["items"] = page,
                ["offset"] = offset, ["next_offset"] = offset + page.Count, ["total"] = total,
                ["complete"] = offset + page.Count == total, ["sha256"] = hash };
        }

        private void Maintain(int reserve = 0, string preserve = null)
        {
            foreach (var job in journal.jobs.Where(x => x.Terminal && x.job_id != preserve && now() >= x.expires_ms)) DeletePayload(job);
            long total = journal.jobs.Where(x => !x.result_deleted && x.result_sha256 != null).Sum(x => (long)x.result_bytes);
            foreach (var job in journal.jobs.Where(x => x.Terminal && x.job_id != preserve && !x.result_deleted && x.result_sha256 != null).OrderBy(x => x.finished_ms))
            { if (total + reserve <= MaxStoredBytes) break; total -= job.result_bytes; DeletePayload(job); }
            if (total + reserve > MaxStoredBytes) throw new IOException("Execution result quota exhausted.");
        }
        private void DeletePayload(ExecutionJobRecord job)
        {
            var path = PayloadPath(job.job_id);
            if (File.Exists(path))
            {
                if (job.result_sha256 == null) throw new IOException("Unclaimed result file preserved for review.");
                var info = new FileInfo(path);
                if (job.result_sha256 != null && (info.Length != job.result_bytes || info.Length > ToolResultSerializer.MaxBytes || Hash(File.ReadAllText(path)) != job.result_sha256))
                    throw new IOException("Changed cached result preserved; explicit operator review is required.");
                File.Delete(path);
            }
            job.result_deleted = true; job.cleanup_pending = false;
        }
        private void Save()
        {
            try { AtomicWrite(journalPath, JsonConvert.SerializeObject(journal)); }
            catch (Exception ex) { Error = ex.GetType().Name; throw; }
        }
        private static void AtomicWrite(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(text); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
