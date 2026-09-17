// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.State;
using Funplay.Editor.Threading;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.MCP.Server
{
    // Bounded MCP response waiting happens AFTER the initial invocation/pending-function receipt.
    // It never holds the Editor thread, replays a start, cancels a job, or performs cleanup.
    internal static class MCPTaskWaiter
    {
        private static readonly string[] Waitable = { "get_task", "prepare_editor", "audit_ui", "start_ui_preview_session",
            "end_ui_preview_session", "extract_recording_frames", "record_game_view", "run_tests" };

        internal static int QueryBudget(Dictionary<string, object> arguments)
            => arguments.TryGetValue("wait_seconds", out var value) && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var seconds)
                ? Math.Max(0, Math.Min(30, seconds)) : 20;

        internal static async Task<string> InitialReadAsync(Task<string> read, int budgetMilliseconds, CancellationToken cancellation)
        {
            if (read.IsCompleted) return await read.ConfigureAwait(false);
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                var expiry = Task.Delay(Math.Max(1, budgetMilliseconds), budget.Token);
                var finished = await Task.WhenAny(read, expiry).ConfigureAwait(false);
                budget.Cancel();
                cancellation.ThrowIfCancellationRequested();
                if (finished == read || read.IsCompleted) return await read.ConfigureAwait(false);
                _ = read.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                return JsonConvert.SerializeObject(Response.Error("EDITOR_STATUS_UNAVAILABLE", new
                {
                    retry_after_ms = 5000,
                    hint = "The initial read could not reach the Editor within the wait budget. Retry status with the same task ID/key; no task outcome was assumed and nothing was replayed."
                }));
            }
        }

        internal static async Task<string> CompleteAsync(string tool, Dictionary<string, object> arguments,
            string initial, IEditorThreadHelper thread, CancellationToken cancellation, double? remainingWaitSeconds = null)
        {
            if (!Waitable.Contains(tool)) return initial;
            var response = JObject.Parse(initial);
            var task = response["data"]?["task"];
            if (task == null || response.Value<bool?>("success") != true) return initial;
            int seconds = arguments.TryGetValue("wait_seconds", out var supplied) ? Convert.ToInt32(supplied, CultureInfo.InvariantCulture) : tool == "get_task" ? 20 : 2;
            // Inputs were validated before the side effect. Keep a defensive transport bound.
            seconds = Math.Max(0, Math.Min(30, seconds));
            string action = arguments.TryGetValue("action", out var a) ? Convert.ToString(a)?.Trim().ToLowerInvariant() : "start";
            bool keyMissing = !arguments.TryGetValue("request_key", out var key) || string.IsNullOrWhiteSpace(Convert.ToString(key));
            bool receiptFirst = tool == "prepare_editor" && keyMissing || tool == "start_ui_preview_session" && keyMissing ||
                tool == "run_tests" || tool == "record_game_view" && action == "start";
            // Recording must return before the caller performs interactions. Tests and starts
            // without a recovery key retain immediate receipt delivery across possible reloads.
            if (receiptFirst) { task["wait_reason"] = "receipt_first"; return response.ToString(Formatting.None); }
            string id = (string)task["task_id"];
            string after = arguments.TryGetValue("after_revision", out var r) ? r as string : null;
            int offset = arguments.TryGetValue("offset", out var o) ? Convert.ToInt32(o, CultureInfo.InvariantCulture) : 0;
            int limit = arguments.TryGetValue("limit", out var l) ? Convert.ToInt32(l, CultureInfo.InvariantCulture) : 100;
            var clock = Stopwatch.StartNew();
            var final = await WaitAsync(response, Math.Min(seconds, remainingWaitSeconds ?? seconds), after, async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var read = thread.ExecuteOnEditorThreadAsync(() =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return TaskStatusService.Query(id, null, null, after, offset, limit);
                });
                var cancelled = new TaskCompletionSource<bool>();
                using (cancellation.Register(() => cancelled.TrySetCanceled()))
                {
                    await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    return await read.ConfigureAwait(false);
                }
            }, () => clock.Elapsed.TotalSeconds, (ms, ct) => Task.Delay(ms, ct), cancellation).ConfigureAwait(false);
            return final.ToString(Formatting.None);
        }

        internal static async Task<JObject> WaitAsync(JObject initial, double seconds, string after,
            Func<Task<JObject>> read, Func<double> now, Func<int, CancellationToken, Task> delay, CancellationToken cancellation)
        {
            double start = now(), deadline = start + Math.Max(0, Math.Min(30, seconds));
            var result = initial;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var task = result["data"]?["task"];
                if (result.Value<bool?>("success") != true || task == null) return result;
                bool complete = task.Value<bool>("wait_complete");
                bool changed = after != null && (string)task["revision"] != after;
                string reason = complete ? "completed" : changed ? "changed" : seconds <= 0 ? "immediate" : now() >= deadline ? "timeout" : null;
                if (reason != null)
                {
                    result = (JObject)result.DeepClone();
                    result["data"]["task"]["wait_reason"] = reason;
                    result["data"]["task"]["waited_ms"] = (int)Math.Max(0, (now() - start) * 1000);
                    return result;
                }
                await delay(Math.Max(1, Math.Min(250, (int)((deadline - now()) * 1000))), cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var pending = read();
                if (!pending.IsCompleted)
                {
                    using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        var expiry = delay(Math.Max(1, (int)Math.Ceiling((deadline - now()) * 1000)), budget.Token);
                        var finished = await Task.WhenAny(pending, expiry).ConfigureAwait(false);
                        budget.Cancel();
                        cancellation.ThrowIfCancellationRequested();
                        if (finished != pending && !pending.IsCompleted)
                        {
                            // Unity may be compiling or a modal dialog may block its queue. Return
                            // the last observed receipt honestly instead of exceeding the wait budget.
                            _ = pending.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                            result = (JObject)result.DeepClone();
                            result["data"]["task"]["wait_reason"] = "read_timeout";
                            result["data"]["task"]["waited_ms"] = (int)Math.Max(0, (now() - start) * 1000);
                            result["data"]["task"]["poll_after_ms"] = Math.Max(5000, result["data"]["task"].Value<int>("poll_after_ms"));
                            TaskStatusService.AlignPollingHint(result);
                            return result;
                        }
                    }
                }
                result = await pending.ConfigureAwait(false);
            }
        }
    }
}
