// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Funplay.Editor.State
{
    internal sealed class EditorOperation
    {
        public string operation_id, request_key, editor_session, target, status = "running", phase = "queued";
        public string created_at, updated_at, finished_at, error_code, message;
        public bool refresh_assets;
        public int timeout_seconds, step;
        public double created_seconds, deadline_seconds, settle_since;
        public List<string> compilation_errors = new List<string>();
        public List<EditorOperationEvent> history = new List<EditorOperationEvent>();
        [JsonIgnore] public bool Terminal => status != "running";
    }

    internal sealed class EditorOperationEvent
    {
        public string timestamp, phase, message;
    }

    internal sealed class EditorOperationSnapshot
    {
        public bool is_compiling, is_updating, is_playing, is_changing_play_mode, compilation_failed;
        public bool refresh_pending;
        public string refresh_error;
        public bool ready => !is_compiling && !is_updating && !is_changing_play_mode && !compilation_failed && !refresh_pending;
    }

    internal enum EditorOperationAction { None, ExitPlay, Refresh, EnterPlay }

    // A deterministic, Unity-independent state machine. Persist BEFORE returning an action to
    // the adapter. After a reload, inspect the requested effect; never replay a user code snippet.
    internal sealed class EditorOperationCoordinator
    {
        internal readonly List<EditorOperation> Operations;
        private readonly string session;
        private readonly Action persist;
        internal EditorOperation Active => Operations.LastOrDefault(x => !x.Terminal);

        internal static bool ValidJournal(List<EditorOperation> records) => records != null && records.Count <= 32 &&
            records.All(x => x != null && Guid.TryParseExact(x.operation_id, "N", out _) && !string.IsNullOrEmpty(x.editor_session) &&
                (x.target == "edit" || x.target == "play") && x.step >= 0 && x.step <= 3 &&
                x.timeout_seconds >= 5 && x.timeout_seconds <= 900 &&
                !double.IsNaN(x.deadline_seconds) && !double.IsInfinity(x.deadline_seconds) &&
                x.compilation_errors != null && x.history != null &&
                new[] { "running", "ready", "failed", "cancelled", "interrupted" }.Contains(x.status)) &&
            records.Select(x => x.operation_id).Distinct().Count() == records.Count && records.Count(x => !x.Terminal) <= 1;

        internal EditorOperationCoordinator(List<EditorOperation> operations, string session, Action persist, double now)
        {
            Operations = operations ?? new List<EditorOperation>();
            this.session = session;
            this.persist = persist;
            foreach (var op in Operations.Where(x => !x.Terminal && x.editor_session != session))
                Finish(op, "interrupted", "EDITOR_RESTARTED", "Editor session changed; no actions were replayed.", now);
        }

        internal EditorOperation Start(string target, bool refresh, int timeout, string key, double now)
        {
            if (target != "edit" && target != "play") throw new ArgumentException("target must be edit or play.");
            if (timeout < 5 || timeout > 900) throw new ArgumentException("timeout_seconds must be 5..900.");
            if (key != null && key.Length > 128) throw new ArgumentException("request_key must be at most 128 characters.");
            if (!string.IsNullOrWhiteSpace(key))
            {
                var previous = Operations.LastOrDefault(x => x.request_key == key);
                if (previous != null)
                {
                    if (previous.target != target || previous.refresh_assets != refresh || previous.timeout_seconds != timeout)
                        throw new InvalidOperationException("IDEMPOTENCY_CONFLICT");
                    return previous;
                }
            }
            if (Active != null) throw new InvalidOperationException("EDITOR_OPERATION_IN_PROGRESS");
            var op = new EditorOperation
            {
                operation_id = Guid.NewGuid().ToString("N"), request_key = key, editor_session = session,
                target = target, refresh_assets = refresh, timeout_seconds = timeout,
                created_at = Timestamp(now), created_seconds = now, deadline_seconds = now + timeout
            };
            // Retain 32 receipts, including the active one. IDs outside this window are explicitly unknown.
            while (Operations.Count >= 32) Operations.RemoveAt(0);
            Operations.Add(op);
            Change(op, "queued", "Preparation accepted; not yet ready.", now);
            return op;
        }

        internal EditorOperationAction Tick(EditorOperationSnapshot s, double now)
        {
            var op = Active;
            if (op == null) return EditorOperationAction.None;
            if (now >= op.deadline_seconds)
            {
                Finish(op, "failed", "OPERATION_TIMEOUT", "Deadline exceeded. Native import/compilation is not cancelled; inspect current_editor before retrying.", now);
                return EditorOperationAction.None;
            }
            if (now - op.created_seconds < .25) return EditorOperationAction.None; // Let the receipt leave the server.
            if (s.is_compiling || s.is_updating || s.is_changing_play_mode)
            {
                op.settle_since = 0;
                Change(op, s.is_compiling ? "compiling" : s.is_updating ? "importing" : s.is_playing ? "exiting_play" : "entering_play", null, now);
                return EditorOperationAction.None;
            }
            switch (op.step)
            {
                case 0:
                    op.step = 1;
                    if (s.is_playing && (op.refresh_assets || op.target == "edit"))
                    {
                        Change(op, "exiting_play", "Exit requested; waiting for Edit Mode readback.", now);
                        return EditorOperationAction.ExitPlay;
                    }
                    persist();
                    goto case 1;
                case 1:
                    if (s.is_playing && (op.refresh_assets || op.target == "edit")) return EditorOperationAction.None;
                    op.step = 2;
                    Change(op, op.refresh_assets ? "importing" : "verifying", null, now);
                    if (op.refresh_assets) return EditorOperationAction.Refresh;
                    goto case 2;
                case 2:
                    if (s.refresh_pending) return EditorOperationAction.None;
                    if (!string.IsNullOrEmpty(s.refresh_error))
                    {
                        Finish(op, "failed", "REFRESH_FAILED", s.refresh_error, now);
                        return EditorOperationAction.None;
                    }
                    if (s.compilation_failed || op.compilation_errors.Count > 0)
                    {
                        Finish(op, "failed", "COMPILATION_FAILED", "Resolve compiler errors before preparing the editor again.", now);
                        return EditorOperationAction.None;
                    }
                    if (op.settle_since == 0) op.settle_since = now;
                    if (now - op.settle_since < .75) return EditorOperationAction.None;
                    op.settle_since = 0;
                    op.step = 3;
                    if (op.target == "play" && !s.is_playing)
                    {
                        Change(op, "entering_play", "Play requested; waiting for Play Mode readback.", now);
                        return EditorOperationAction.EnterPlay;
                    }
                    persist();
                    goto case 3;
                case 3:
                    if (s.compilation_failed)
                    {
                        Finish(op, "failed", "COMPILATION_FAILED", "Compilation failed during mode transition.", now);
                        return EditorOperationAction.None;
                    }
                    if (s.is_playing != (op.target == "play")) { op.settle_since = 0; return EditorOperationAction.None; }
                    if (op.settle_since == 0) op.settle_since = now;
                    Change(op, "verifying", "Waiting for stable editor state.", now);
                    if (now - op.settle_since >= .75)
                        Finish(op, "ready", null, "Requested mode verified; imports and compilation are idle.", now);
                    return EditorOperationAction.None;
                default: throw new InvalidOperationException("Invalid operation step.");
            }
        }

        internal void Cancel(EditorOperation op, double now)
        {
            if (!op.Terminal) Finish(op, "cancelled", null, "Future steps cancelled. Already-started Unity work is not rolled back.", now);
        }
        internal void Reload(double now)
        {
            if (Active != null) { Active.settle_since = 0; Change(Active, "reloading", "Journal saved; poll the same operation_id after reconnection.", now); }
        }
        internal void Fail(string code, string message, double now)
        {
            if (Active != null) Finish(Active, "failed", code, message, now);
        }
        internal void CompilerErrors(IEnumerable<string> errors, double now)
        {
            if (Active == null) return;
            Active.compilation_errors.AddRange(errors.Take(Math.Max(0, 100 - Active.compilation_errors.Count)));
            Active.updated_at = Timestamp(now);
            persist();
        }
        private void Finish(EditorOperation op, string status, string code, string message, double now)
        {
            op.status = status; op.error_code = code; op.finished_at = Timestamp(now);
            Change(op, status, message, now);
        }
        private void Change(EditorOperation op, string phase, string message, double now)
        {
            if (op.phase == phase && op.history.Count > 0) return;
            op.phase = phase; op.message = message; op.updated_at = Timestamp(now);
            op.history.Add(new EditorOperationEvent { phase = phase, message = message, timestamp = op.updated_at });
            if (op.history.Count > 100) op.history.RemoveAt(0);
            persist();
        }
        private static string Timestamp(double seconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).ToString("O");
    }
}
