// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Funplay.Editor.State
{
    internal sealed class ExecutionOptions
    {
        internal string Code, Key, PreferredNamespaces, SourceTool = "execute_code";
        internal bool Safety, Strict, InjectNamespaces, SkipRefresh;
        internal int WaitSeconds = 2, TimeoutSeconds = 300;
        internal Func<bool> StillAllowed = () => true;
        internal string Signature => ExecutionJobStore.Hash(JsonConvert.SerializeObject(new {
            code = ExecutionJobStore.Hash(Code), Key, PreferredNamespaces, SourceTool, Safety, Strict, InjectNamespaces, SkipRefresh, TimeoutSeconds,
            MCPProjectContext.Current.project_id, MCPProjectContext.Current.instance_id }));
    }
    internal sealed class ExecutionAbortedException : Exception
    {
        internal readonly string Code;
        internal ExecutionAbortedException(string code) : base(code) { Code = code; }
    }

    [InitializeOnLoad] internal static class ExecutionJobService
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, ExecutionOptions> Pending = new Dictionary<string, ExecutionOptions>();
        internal static readonly ExecutionJobStore Store;
        internal static readonly AsyncLocal<Func<bool>> InvocationAccess = new AsyncLocal<Func<bool>>();
        private static bool running;
        private static readonly int MainThread;
        static ExecutionJobService()
        {
            MainThread = Thread.CurrentThread.ManagedThreadId;
            Store = new ExecutionJobStore(Path.Combine(MCPProjectContext.Current.project_path, "Library/FunplayMcp/Executions"));
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Interrupt;
            EditorApplication.quitting += Interrupt;
        }
        internal static void Initialize() { }
        internal static bool IsExecutionRead(IDictionary<string, object> arguments) =>
            arguments.TryGetValue("kind", out var kind) && kind as string == "execution" ||
            arguments.TryGetValue("task_id", out var id) && (id as string)?.StartsWith("execution:", StringComparison.Ordinal) == true;
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        internal static async Task<object> StartAsync(ExecutionOptions options, CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(options.Code)) return Response.Error("CODE_REQUIRED", new { executed = false });
            if (options.Code.Length > 256 * 1024) return Response.Error("EXECUTION_CODE_TOO_LARGE", new { executed = false });
            var invalid = TaskStatusService.ValidateWait(options.WaitSeconds); if (invalid != null) return invalid;
            if (!options.StillAllowed()) return Response.Error("TOOL_NOT_EXPOSED", new { executed = false });
            object error;
            ExecutionJobRecord job; bool existing;
            lock (Gate)
            {
                error = Store.Admit(options.Signature, options.Key, options.SourceTool, options.TimeoutSeconds,
                    MCPProjectContext.Current, out job, out existing);
                if (error == null && !existing) Pending.Add(job.job_id, options);
            }
            if (error != null) return error;
            var watch = Stopwatch.StartNew();
            // The runner is queued, never executed inline by admission. Waiting uses wall time
            // and immutable stored receipts on a pool continuation, not a blocked Editor thread.
            while (options.WaitSeconds > 0 && watch.Elapsed.TotalSeconds < options.WaitSeconds)
            {
                cancellation.ThrowIfCancellationRequested();
                var state = Store.Find(job.job_id);
                if (state == null || state.Terminal) break;
                await Task.Delay(Math.Min(50, Math.Max(1, options.WaitSeconds * 1000 - (int)watch.ElapsedMilliseconds)), cancellation).ConfigureAwait(false);
            }
            var result = Store.Read(job.job_id, null, originalResult: true);
            var annotated = TaskStatusService.Observe("execution", result);
            if (annotated["data"]?["task"] is JObject task)
            { task["wait_reason"] = task.Value<bool>("wait_complete") ? "completed" : options.WaitSeconds == 0 ? "immediate" : "timeout"; task["waited_ms"] = watch.ElapsedMilliseconds; }
            return annotated;
        }

        internal static JObject Query(string id, string key, string after, int offset, int limit,
            string format, string path, bool diagnostics, Func<string, bool> allowed)
        {
            if (id?.StartsWith("execution:", StringComparison.Ordinal) == true) id = id.Substring(10);
            string kind = "execution";
            var invalid = TaskStatusService.Resolve(ref id, ref kind, key);
            if (invalid != null) return JObject.FromObject(invalid);
            if (after != null && (after.Length != 64 || after.Any(c => !"0123456789abcdef".Contains(c))))
                return JObject.FromObject(Response.Error("INVALID_TASK_REVISION"));
            var owner = Store.Owner(id, key);
            if (!TaskStatusService.CanRead("execution", allowed)) return JObject.FromObject(Response.Error("TASK_KIND_NOT_EXPOSED"));
            if (owner != null && !allowed(owner)) return JObject.FromObject(Response.Error("TASK_KIND_NOT_EXPOSED"));
            var found = Store.Find(id, key);
            if (found != null && key != null && found.key_hash != ExecutionJobStore.Hash(key))
                return JObject.FromObject(Response.Error("EXECUTION_TASK_KEY_MISMATCH"));
            if (found != null && found.context.project_id != MCPProjectContext.Current.project_id)
                return JObject.FromObject(Response.Error("PROJECT_CONTEXT_MISMATCH"));
            return TaskStatusService.Observe("execution", Store.Read(id, key, format, path, offset, limit, diagnostics), after);
        }

        internal static object Control(string action, string id, bool confirmForget, Func<string, bool> allowed)
        {
            if (id?.StartsWith("execution:", StringComparison.Ordinal) == true) id = id.Substring(10);
            if (!Guid.TryParseExact(id, "N", out _)) return Response.Error("INVALID_EXECUTION_ID");
            var owner = Store.Owner(id);
            if (owner != null && !allowed(owner)) return Response.Error("TASK_KIND_NOT_EXPOSED");
            var job = Store.Find(id);
            if (job != null && job.context.project_id != MCPProjectContext.Current.project_id) return Response.Error("PROJECT_CONTEXT_MISMATCH");
            object error;
            try
            {
                switch (action)
                {
                    case "cancel": error = Store.Cancel(id); break;
                    case "cleanup": error = Store.Cleanup(id); break;
                    case "forget": return Store.Forget(id, confirmForget);
                    default: return Response.Error("INVALID_EXECUTION_ACTION");
                }
            }
            catch (Exception ex) { return Response.Error("EXECUTION_CONTROL_FAILED", new { exception_type = ex.GetType().FullName }); }
            return error ?? (object)TaskStatusService.Observe("execution", Store.Read(id, null));
        }

        private static void Tick()
        {
            if (running) return;
            KeyValuePair<string, ExecutionOptions> next;
            lock (Gate)
            {
                if (Pending.Count == 0) return;
                next = Pending.First(); Pending.Remove(next.Key);
            }
            var job = Store.Find(next.Key);
            if (job == null || job.Terminal) return;
            running = true;
            _ = RunAsync(job, next.Value);
        }

        private static async Task RunAsync(ExecutionJobRecord job, ExecutionOptions options)
        {
            var trace = new ScriptExecutionTrace { Id = job.job_id };
            var phaseClock = Stopwatch.StartNew(); string phase = "queued";
            trace.OnPhase = next =>
            {
                var current = Store.Find(job.job_id);
                if (current == null || current.Terminal) throw new ExecutionAbortedException("EXECUTION_INTERRUPTED");
                if (!trace.Executed)
                {
                    if (current.cancel_requested) throw new ExecutionAbortedException("EXECUTION_CANCELLED");
                    if (Now > job.created_ms + job.timeout_seconds * 1000L) throw new ExecutionAbortedException("EXECUTION_PREPARATION_TIMEOUT");
                    if (!options.StillAllowed()) throw new ExecutionAbortedException("TOOL_NOT_EXPOSED");
                }
                double elapsed = phaseClock.Elapsed.TotalMilliseconds;
                Store.Update(job.job_id, record =>
                {
                    if (phase == "refreshing") record.refresh_ms += elapsed;
                    if (phase == "compiling") record.compile_ms += elapsed;
                    if (phase == "executing") record.execute_ms += elapsed;
                    record.status = "running"; record.phase = next;
                    record.execution_status = next == "executing" ? "running" : trace.Executed ? "returned" : "not_executed";
                    record.executed = next == "executing" ? (bool?)null : trace.Executed;
                });
                phase = next; phaseClock.Restart();
            };
            try
            {
                if (Thread.CurrentThread.ManagedThreadId != MainThread) throw new InvalidOperationException("Unity code must run on the Editor thread.");
                Store.Update(job.job_id, record => { record.started_ms = Now; record.queue_ms = Now - record.created_ms; });
                trace.SetPhase("validation");
                var raw = await ScriptExecutionFunctions.ExecuteCodeImmediate(options.Code, options.Safety,
                    options.SkipRefresh, options.PreferredNamespaces, trace, options.Strict, options.InjectNamespaces);
                if (!trace.Executed && Store.Find(job.job_id)?.cancel_requested == true) raw = Response.Error("EXECUTION_CANCELLED");
                trace.SetPhase("serializing");
                var serializationClock = Stopwatch.StartNew();
                var json = ToolResultSerializer.Serialize(ExecutionResultEnvelope.Wrap(raw, trace), options.SourceTool);
                Store.Update(job.job_id, record => { record.serialize_ms = serializationClock.Elapsed.TotalMilliseconds; });
                var persistenceClock = Stopwatch.StartNew();
                Store.Complete(job.job_id, json, trace);
                Store.Update(job.job_id, record => record.persist_ms = persistenceClock.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                trace.Diagnostic = OperationDiagnostic.Capture(trace.Phase, ex);
                try
                {
                    var failure = ex is ExecutionAbortedException aborted ? aborted.Code : "EXECUTION_RUNNER_FAILED";
                    var raw = Response.Error(failure, new { executed = trace.Executed, exception_type = ex.GetType().FullName });
                    Store.Complete(job.job_id, ToolResultSerializer.Serialize(ExecutionResultEnvelope.Wrap(raw, trace), options.SourceTool), trace);
                }
                catch (Exception storageError) { Store.FailInMemory(job.job_id, trace, storageError); }
            }
            finally { running = false; }
        }

        private static void Interrupt()
        {
            lock (Gate) Pending.Clear();
            var id = Store.ActiveId;
            if (id == null) return;
            try
            {
                Store.Update(id, record =>
                {
                    bool returned = record.execution_status == "returned";
                    bool unknown = !returned && (record.phase == "executing" || record.phase == "serializing" || record.phase == "persisting");
                    record.status = unknown || returned ? "outcome_unknown" : "interrupted";
                    record.executed = returned ? record.executed : unknown ? (bool?)null : false;
                    record.execution_status = returned ? "returned" : unknown ? "outcome_unknown" : "not_executed";
                    record.phase = "interrupted_without_replay"; record.error_code = "EXECUTION_INTERRUPTED"; record.finished_ms = Now;
                });
            }
            catch { }
        }
    }
}
