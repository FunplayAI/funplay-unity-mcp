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
        private readonly Action<string, string> replaceFile;
        private ExecutionJournal journal;
        private bool journalLoaded;
        private string persistedHash, attemptedHash, legacyText, legacyHash, failedLoadHash;
        internal JObject JournalDiagnostic { get; private set; }
        internal string LegacyBackup { get; private set; }
        internal string Error { get; private set; }

        internal ExecutionJobStore(string root, Func<long> clock = null, Action<string, string> replace = null)
        {
            this.root = Path.GetFullPath(root); journalPath = Path.Combine(this.root, "journal.json");
            now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            replaceFile = replace;
            try
            {
                Directory.CreateDirectory(this.root);
                journal = File.Exists(journalPath) ? ReadJournal() : new ExecutionJournal();
                journalLoaded = true;
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
            catch (Exception ex) { if (!journalLoaded) journal = new ExecutionJournal(); RecordFailure("load_journal", ex); }
        }

        internal static string Hash(string value)
        { return HashBytes(Encoding.UTF8.GetBytes(value ?? "")); }
        internal static string HashBytes(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static bool Id(string id) => Guid.TryParseExact(id, "N", out _);
        private static bool Digest(string value) => value?.Length == 64 && value.All(c => "0123456789abcdef".Contains(c));
        private static bool Source(string value) => value == "execute_code" || value == "replay_execute_code";
        private string PayloadPath(string id)
        { if (!Id(id)) throw new ArgumentException("INVALID_EXECUTION_ID"); return Path.Combine(root, id + ".result.json"); }

        private ExecutionJournal ReadJournal()
        {
            var bytes = ExecutionAtomicFile.Read(journalPath);
            if (!journalLoaded && failedLoadHash == null) failedLoadHash = HashBytes(bytes);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var token = ExecutionJson.Parse(text.TrimStart('\ufeff'));
            if (token["schema_version"]?.Type != JTokenType.Integer || token.Value<int>("schema_version") != 1)
                throw new InvalidDataException("Unsupported execution journal schema.");
            bool wrapped = NormalizeList(token, "jobs", "ExecutionJobRecord") | NormalizeList(token, "keys", "ExecutionKeyRecord");
            var value = token.ToObject<ExecutionJournal>(ExecutionJson.Serializer());
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
            foreach (var key in value.keys)
            {
                var job = value.jobs.FirstOrDefault(x => x.job_id == key.job_id);
                if (job != null && (job.key_hash != key.key_hash || job.request_hash != key.request_hash || job.source_tool != key.source_tool || job.context.instance_id != key.instance_id))
                    throw new InvalidDataException("Inconsistent execution key; nothing was replayed.");
            }
            persistedHash = HashBytes(bytes);
            if (wrapped) { legacyText = text; legacyHash = persistedHash; }
            return value;
        }

        private static bool NormalizeList(JObject root, string name, string element)
        {
            if (root[name] is JArray) return false;
            if (!(root[name] is JObject wrapper) || wrapper.Properties().Any(x => x.Name != "$type" && x.Name != "$values") ||
                !(wrapper["$values"] is JArray values) || wrapper["$type"]?.Type != JTokenType.String)
                throw new InvalidDataException("Invalid execution list container.");
            string type = wrapper.Value<string>("$type");
            if (type.Length > 512 || !type.StartsWith("System.Collections.Generic.List`1[[Funplay.Editor.State." + element + ", Funplay.Editor", StringComparison.Ordinal))
                throw new InvalidDataException("Unrecognized execution list container.");
            // Only these two known list envelopes are unwrapped. Never resolve $type, follow
            // $ref or recursively reinterpret opaque diagnostic/recovery/business JSON.
            root[name] = values.DeepClone(); return true;
        }

        private static ExecutionJobRecord Copy(ExecutionJobRecord record) => record == null ? null :
            ExecutionJson.Copy(record);
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
                if (Error != null) return Unavailable(false);
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
                    RecordFailure("admit_journal", ex);
                    var failed = journal.jobs.LastOrDefault(x => !x.Terminal && x.request_hash == requestHash && x.key_hash == keyHash);
                    if (failed != null)
                    {
                        failed.status = "failed"; failed.phase = "admission_persistence_failed";
                        failed.error_code = "EXECUTION_JOURNAL_UNAVAILABLE"; failed.executed = false; failed.finished_ms = now(); failed.revision++;
                    }
                    return Unavailable(false, failed?.job_id);
                }
            }
        }

        internal void Update(string id, Action<ExecutionJobRecord> update)
        {
            lock (gate)
            {
                var job = journal.jobs.Single(x => x.job_id == id);
                update(job); job.revision++;
                Save();
            }
        }

        internal void FailInMemory(string id, ScriptExecutionTrace trace, Exception exception)
        {
            lock (gate)
            {
                RecordFailure("journal_unavailable", exception);
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
                job.recovery = trace.Recovery == null ? null : ExecutionJson.Object(trace.Recovery);
                job.diagnostic = trace.Diagnostic == null ? null : (JObject)trace.Diagnostic.DeepClone();
                job.exception_type = job.diagnostic?.Value<string>("exception_type");
                job.phase = "persisting"; job.revision++; Save();
                try
                {
                    Maintain(bytes.Length, id);
                    ExecutionAtomicFile.Write(PayloadPath(id), json, "result", job.result_sha256, replaceFile);
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
                    job.diagnostic = OperationDiagnostic.Capture("persist_result", ex);
                    try { Save(); } catch { /* Save retains the journal diagnostic and closes admission. */ }
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
                    if (Error != null) return Unavailable(null, diagnostics: diagnostics);
                    bool expired = key != null && journal.keys.Any(x => x.key_hash == Hash(key));
                    return ExecutionJson.Object(Response.Error(expired ? "EXECUTION_RECEIPT_EXPIRED" : "EXECUTION_NOT_FOUND", new { retry_safe = false }));
                }
                if (format != "summary" && format != "items" && format != "text") return ExecutionJson.Object(Response.Error("INVALID_RESULT_FORMAT"));
                if (offset < 0 || limit < 1 || limit > (format == "text" ? 16384 : 500)) return ExecutionJson.Object(Response.Error("INVALID_PAGE"));
                bool retained = job.result_sha256 != null && !job.result_deleted && now() < job.expires_ms;
                bool intactSize = false;
                try { var file = new FileInfo(PayloadPath(job.job_id)); intactSize = file.Exists && file.Length == job.result_bytes; } catch (IOException) { }
                bool available = retained && intactSize;
                var data = ExecutionJson.Object(job); data.Remove("key_hash"); data.Remove("request_hash"); data.Remove("diagnostic");
                data["budget_exceeded"] = (job.finished_ms > 0 ? job.finished_ms : now()) - job.created_ms > job.timeout_seconds * 1000L;
                data["cancel_supported"] = !job.Terminal && job.phase != "executing" && job.phase != "serializing" && job.phase != "persisting";
                data["result"] = new JObject { ["available"] = available, ["bytes"] = job.result_bytes,
                    ["sha256"] = job.result_sha256, ["inline_limit_bytes"] = InlineBytes, ["expires_at_ms"] = job.expires_ms,
                    ["storage"] = "project_library", ["integrity_status"] = retained && !intactSize ? "missing_or_changed" : available ? "hash_not_checked_this_read" : "unavailable",
                    ["raw_result_recoverable"] = job.serialization_status != "failed" && available };
                data["journal_available"] = Error == null;
                data["journal_format"] = legacyText == null ? "plain_v1" : "legacy_list_wrappers_v1";
                if (LegacyBackup != null) data["legacy_journal_backup"] = LegacyBackup;
                if (Error != null) data["journal_diagnostic"] = Diagnostic(diagnostics);
                if (diagnostics && job.diagnostic != null) data["diagnostic"] = job.diagnostic.DeepClone();
                JObject payload = null;
                if (retained && !intactSize && format != "summary")
                    return ExecutionJson.Object(Response.Error("EXECUTION_RESULT_MISSING_OR_CHANGED", new { task_id = "execution:" + job.job_id, executed = job.executed, retry_safe = false }));
                if (available && (format != "summary" || job.result_bytes <= InlineBytes))
                {
                    try
                    {
                        var file = new FileInfo(PayloadPath(job.job_id));
                        if (!file.Exists || file.Length != job.result_bytes || file.Length > ToolResultSerializer.MaxBytes)
                            return ExecutionJson.Object(Response.Error("EXECUTION_RESULT_MISSING_OR_CHANGED", new { task_id = "execution:" + job.job_id, executed = job.executed, retry_safe = false }));
                        var text = File.ReadAllText(file.FullName);
                        if (Hash(text) != job.result_sha256) return ExecutionJson.Object(Response.Error("EXECUTION_RESULT_MISSING_OR_CHANGED", new { executed = job.executed, retry_safe = false }));
                        data["result"]["integrity_status"] = "sha256_verified";
                        if (format == "text")
                        {
                            if (path != null) return ExecutionJson.Object(Response.Error("TEXT_RESULT_PATH_UNSUPPORTED"));
                            if (offset > text.Length || offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1]))
                                return ExecutionJson.Object(Response.Error("INVALID_TEXT_OFFSET"));
                            int end = Math.Min(text.Length, offset + limit);
                            while (end > offset && (end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]) || Encoding.UTF8.GetByteCount(text.Substring(offset, end - offset)) > PageBytes)) end--;
                            if (end == offset && offset < text.Length) return ExecutionJson.Object(Response.Error("TEXT_PAGE_LIMIT_TOO_SMALL"));
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
                    catch (ArgumentException) { return ExecutionJson.Object(Response.Error("INVALID_RESULT_PATH_OR_OFFSET", new { executed = job.executed, retry_safe = false })); }
                    catch (Exception ex) { return ExecutionJson.Object(Response.Error("EXECUTION_RESULT_READ_FAILED", new { exception_type = ex.GetType().FullName, executed = job.executed, retry_safe = false })); }
                }
                if (format != "summary" && !available) return ExecutionJson.Object(Response.Error(job.Terminal ? "EXECUTION_RESULT_UNAVAILABLE" : "EXECUTION_RESULT_NOT_READY", new { task_id = "execution:" + job.job_id, executed = job.executed, retry_safe = false }));
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
                var receipt = ExecutionJson.Object(Response.Success("Execution task: " + job.status + ". Inspect result_success and execution_status; a status query does not replay or cancel it.", data));
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
            try
            {
                ExecutionAtomicFile.Check(journalPath, persistedHash);
                if (legacyText != null)
                {
                    // The validated source is retained byte-for-byte before its first canonical
                    // save. No result file or idempotency record is deleted during migration.
                    string backupName = "journal.legacy-list-wrappers." + legacyHash + ".json";
                    string backup = Path.Combine(root, backupName);
                    if (File.Exists(backup)) ExecutionAtomicFile.Check(backup, legacyHash);
                    else ExecutionAtomicFile.Write(backup, legacyText, "legacy_journal_backup", null);
                    LegacyBackup = backupName;
                }
                var text = ExecutionJson.Serialize(journal); attemptedHash = Hash(text);
                ExecutionAtomicFile.Write(journalPath, text, "journal", persistedHash, replaceFile);
                persistedHash = attemptedHash; attemptedHash = null; legacyText = null; legacyHash = null;
            }
            catch (Exception ex) { RecordFailure("save_journal", ex); throw; }
        }

        private void RecordFailure(string stage, Exception exception)
        {
            var cause = exception is ExecutionStorageException storage ? storage.InnerException : exception;
            Error = cause.GetType().Name;
            JournalDiagnostic = OperationDiagnostic.Capture(stage, exception);
            JournalDiagnostic["failure_code"] = cause is JsonException ? "JOURNAL_JSON_INVALID" :
                cause is DecoderFallbackException ? "JOURNAL_ENCODING_INVALID" :
                cause is InvalidDataException ? "JOURNAL_FORMAT_OR_INTEGRITY_INVALID" :
                cause is UnauthorizedAccessException ? "JOURNAL_ACCESS_DENIED" : "JOURNAL_STORAGE_FAILED";
        }
        private JObject Diagnostic(bool full)
        {
            var copy = JournalDiagnostic == null ? new JObject() : (JObject)JournalDiagnostic.DeepClone();
            if (!full) copy.Remove("stack_methods");
            return copy;
        }
        private JObject Unavailable(bool? executed, string id = null, bool diagnostics = false) => ExecutionJson.Object(
            Response.Error("EXECUTION_JOURNAL_UNAVAILABLE", new { detail = Error, executed,
                task_id = id == null ? null : "execution:" + id, journal_available = false, retry_safe = false,
                journal_diagnostic = Diagnostic(diagnostics), recovery = new { tool = "execute_code", action = "recover_journal", requires_profile = "full",
                    hint = "After resolving the reported storage problem, explicitly recover_journal or restart the Editor. This preserves keys and never replays commands. Do not delete the journal or change project JSON defaults." } }));

        internal object RecoverJournal()
        {
            lock (gate)
            {
                if (journal.jobs.Any(x => !x.Terminal)) return Response.Error("EXECUTION_STILL_RUNNING");
                if (Error == null) return Response.Success("Execution journal is already available; no commands were replayed.", new { journal_available = true, commands_replayed = 0 });
                try
                {
                    if (!journalLoaded)
                    {
                        // A missing unreadable journal is not an empty history. Never create a
                        // replacement that silently drops unknown original retry protection.
                        if (failedLoadHash != null) ExecutionAtomicFile.Check(journalPath, failedLoadHash);
                        journal = ReadJournal(); journalLoaded = true;
                        foreach (var job in journal.jobs.Where(x => !x.Terminal))
                        {
                            bool returned = job.execution_status == "returned";
                            bool unknown = returned || job.phase == "executing" || job.phase == "serializing" || job.phase == "persisting";
                            job.status = unknown ? "outcome_unknown" : "interrupted";
                            job.executed = returned ? job.executed : unknown ? (bool?)null : false;
                            job.execution_status = returned ? "returned" : unknown ? "outcome_unknown" : "not_executed";
                            job.error_code = "EXECUTION_INTERRUPTED"; job.phase = "recovered_without_replay"; job.finished_ms = now(); job.revision++;
                        }
                    }
                    else
                    {
                        string actual = File.Exists(journalPath) ? HashBytes(ExecutionAtomicFile.Read(journalPath)) : null;
                        if (actual != persistedHash && (attemptedHash == null || actual != attemptedHash))
                            throw new InvalidDataException("Journal changed externally; no recovery write was made.");
                        persistedHash = actual; // Also recognizes a replacement that committed before an I/O error.
                    }
                    Save(); ExecutionAtomicFile.Check(journalPath, persistedHash);
                    Error = null; JournalDiagnostic = null;
                    return Response.Success("Execution journal verified and persisted. History/keys were retained; no commands were replayed.",
                        new { journal_available = true, records = journal.jobs.Count, protected_keys = journal.keys.Count,
                            commands_replayed = 0, legacy_journal_backup = LegacyBackup });
                }
                catch (Exception ex) { RecordFailure("recover_journal", ex); return Unavailable(null); }
            }
        }
    }
}
