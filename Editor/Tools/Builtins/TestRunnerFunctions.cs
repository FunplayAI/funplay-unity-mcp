// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    /// <summary>
    /// Unity Test Runner integration with an async job pattern: run_tests starts a run and
    /// returns a job id immediately (test runs can take minutes and PlayMode runs trigger
    /// domain reloads, so a synchronous MCP call would time out); get_test_job polls for
    /// status/results. Job state lives in SessionState so it survives the domain reloads
    /// that PlayMode test runs cause; the results callback is re-registered on every domain
    /// load via [InitializeOnLoad].
    /// </summary>
    [ToolProvider("Testing")]
    internal static class TestRunnerFunctions
    {
        private const string ActiveJobKey = "Funplay.TestRunner.ActiveJob";

        [Description("Run Unity Test Runner tests (EditMode or PlayMode) asynchronously. Returns a job_id immediately; " +
                     "poll get_test_job for status and results. Only one test run can be active at a time (a Unity Test " +
                     "Runner limitation). PlayMode runs enter Play Mode and trigger domain reloads -- the job state survives " +
                     "them. Optional filters narrow the run to specific tests, categories, or assemblies.")]
        public static object RunTests(
            [ToolParam("Test mode: 'EditMode' or 'PlayMode'", Required = false)] string mode = "EditMode",
            [ToolParam("Comma-separated fully qualified test names to run (e.g. 'MyTests.LoginTest.CanLogin')", Required = false)] string test_names = null,
            [ToolParam("Comma-separated NUnit category names to run", Required = false)] string category_names = null,
            [ToolParam("Comma-separated test assembly names to run (e.g. 'MyGame.Tests')", Required = false)] string assembly_names = null)
        {
            TestMode testMode;
            switch ((mode ?? "EditMode").Trim().ToLowerInvariant())
            {
                case "editmode": testMode = TestMode.EditMode; break;
                case "playmode": testMode = TestMode.PlayMode; break;
                default:
                    return Response.Error("INVALID_MODE", new { mode, accepted = new[] { "EditMode", "PlayMode" } });
            }

            var active = LoadJob();
            if (active != null && active.Value<string>("status") == "running")
            {
                return Response.Error("TESTS_ALREADY_RUNNING", new
                {
                    job_id = active.Value<string>("jobId"),
                    hint = "Poll get_test_job, or cancel_test_run if the run is stuck."
                });
            }

            var filter = new Filter { testMode = testMode };
            if (!string.IsNullOrWhiteSpace(test_names)) filter.testNames = SplitList(test_names);
            if (!string.IsNullOrWhiteSpace(category_names)) filter.categoryNames = SplitList(category_names);
            if (!string.IsNullOrWhiteSpace(assembly_names)) filter.assemblyNames = SplitList(assembly_names);
            var hasFilters =
                (filter.testNames != null && filter.testNames.Length > 0) ||
                (filter.categoryNames != null && filter.categoryNames.Length > 0) ||
                (filter.assemblyNames != null && filter.assemblyNames.Length > 0);

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            string guid;
            try
            {
                guid = api.Execute(new ExecutionSettings(filter));
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }

            var now = FormatTimestamp(DateTime.Now);
            var job = new JObject
            {
                ["jobId"] = guid,
                ["status"] = "running",
                ["mode"] = testMode.ToString(),
                ["startedAt"] = now,
                ["lastActivityAt"] = now,
                ["testsCompleted"] = 0,
                ["hasFilters"] = hasFilters
            };
            SaveJob(job);

            return Response.Success(
                $"Test run started ({testMode}). Poll get_test_job with this job_id; PlayMode runs may take a while and reload the domain.",
                new { job_id = guid, mode = testMode.ToString() });
        }

        [Description("Get the status and results of a test run started by run_tests. While running, reports progress; " +
                     "when finished, reports pass/fail/skip counts and details for failed tests. If no test-runner " +
                     "callback has fired for a while, the response includes possiblyStuck=true with the current phase, " +
                     "current test (when known), and a diagnostic hint. Runner start/transition uses a 30-second threshold; " +
                     "a known in-progress test uses 120 seconds to avoid flagging ordinary long-running tests too early.")]
        [ReadOnlyTool]
        public static object GetTestJob(
            [ToolParam("Job id returned by run_tests. Omit to query the most recent run.", Required = false)] string job_id = null)
        {
            var job = LoadJob();
            if (job == null)
                return Response.Error("NO_TEST_JOB", new { hint = "Call run_tests first. Job state is cleared when the editor quits." });

            var storedId = job.Value<string>("jobId");
            if (!string.IsNullOrEmpty(job_id) && !string.Equals(job_id, storedId, StringComparison.Ordinal))
                return Response.Error("JOB_NOT_FOUND", new { job_id, activeJobId = storedId, hint = "Only the most recent run is tracked." });

            AnnotateIfPossiblyStuck(job);
            return Response.Success($"Test job {job.Value<string>("status")}.", job);
        }

        // Unity's Test Runner only supports one active run at a time, engine-wide, and this
        // package has no way to see runs started by other tools/sessions/processes against the
        // same Editor -- a concurrent caller (or a stale run left over from one) can silently
        // occupy the engine with no exception and no error surfaced here, leaving a run stuck at
        // "running" forever. Rather than reflect into Unity's private, version-fragile internal
        // run-tracking singleton to detect that directly, use a much simpler and more robust
        // signal we already own: if no ICallbacks activity (RunStarted/TestStarted/TestFinished)
        // has touched this job in a while, something has stopped progressing.
        internal const int RunnerStuckThresholdSeconds = 30;
        internal const int ActiveTestStuckThresholdSeconds = 120;

        internal static void AnnotateIfPossiblyStuck(JObject job, DateTime? now = null)
        {
            job?.Remove("possiblyStuck");
            job?.Remove("stuckPhase");
            job?.Remove("secondsSinceActivity");
            job?.Remove("stuckHint");
            if (job == null || job.Value<string>("status") != "running") return;

            var referenceTime = job.Value<string>("lastActivityAt") ?? job.Value<string>("startedAt");
            if (!TryParseTimestamp(referenceTime, out var reference))
                return;

            var currentTime = now ?? DateTime.Now;
            var currentTest = job.Value<string>("currentTest");
            var assessment = AssessStuckState(reference, currentTest, currentTime);
            if (assessment == null) return;

            job["possiblyStuck"] = true;
            job["stuckPhase"] = assessment.Phase;
            job["secondsSinceActivity"] = assessment.SecondsSinceActivity;
            job["stuckHint"] = assessment.Hint;
        }

        internal static TestRunnerStuckAssessment AssessStuckState(
            DateTime lastActivity,
            string currentTest,
            DateTime now)
        {
            var elapsedSeconds = Math.Max(0, (int)(now - lastActivity).TotalSeconds);
            var hasCurrentTest = !string.IsNullOrWhiteSpace(currentTest);
            var threshold = hasCurrentTest ? ActiveTestStuckThresholdSeconds : RunnerStuckThresholdSeconds;
            if (elapsedSeconds < threshold)
                return null;

            var phase = hasCurrentTest ? "test_execution" : "runner_start_or_transition";
            var hint = hasCurrentTest
                ? $"Test '{currentTest}' has produced no completion callback for {elapsedSeconds}s. It may be a legitimate long-running test, so inspect the Editor and Console before cancelling. If its expected duration is shorter, cancel and retry once the Editor is idle."
                : $"No test-runner callback has fired for {elapsedSeconds}s before or between tests. Unity supports one active run engine-wide; another caller or a stale run may be occupying it. Check get_editor_state and get_console_logs, then cancel and retry only after the Editor is idle.";
            return new TestRunnerStuckAssessment(phase, elapsedSeconds, hint);
        }

        internal static string FormatTimestamp(DateTime value)
        {
            return value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryParseTimestamp(string value, out DateTime timestamp)
        {
            return DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out timestamp);
        }

        [Description("Cancel a running test run. Use when a run appears stuck (e.g. a PlayMode run that never finishes).")]
        public static object CancelTestRun(
            [ToolParam("Job id returned by run_tests. Omit to cancel the most recent run.", Required = false)] string job_id = null)
        {
            var job = LoadJob();
            if (job == null)
                return Response.Error("NO_TEST_JOB");

            var guid = string.IsNullOrEmpty(job_id) ? job.Value<string>("jobId") : job_id;

            // CancelTestRun only exists in com.unity.test-framework 1.3+. Resolve it by reflection
            // so this file still compiles against the 1.1.x that Unity 2022.3 bundles by default,
            // and degrade with a clear error there instead of failing the whole package.
            var cancelMethod = typeof(TestRunnerApi).GetMethod("CancelTestRun",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (cancelMethod == null)
                return Response.Error("CANCEL_NOT_SUPPORTED", new
                {
                    hint = "TestRunnerApi.CancelTestRun requires com.unity.test-framework 1.3 or newer. " +
                           "Upgrade the Test Framework package, or wait for the run to finish."
                });

            bool cancelled;
            try
            {
                cancelled = (bool)cancelMethod.Invoke(null, new object[] { guid });
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }

            if (cancelled && job.Value<string>("jobId") == guid)
            {
                job["status"] = "cancelled";
                job.Remove("currentTest");
                job.Remove("currentTestStartedAt");
                SaveJob(job);
            }

            return cancelled
                ? Response.Success($"Test run {guid} cancelled.")
                : (object)Response.Error("CANCEL_FAILED", new { job_id = guid, hint = "The run may have already finished." });
        }

        // -------- Job persistence (survives domain reloads, cleared on editor quit) --------

        internal static JObject LoadJob()
        {
            var raw = SessionState.GetString(ActiveJobKey, null);
            if (string.IsNullOrEmpty(raw)) return null;
            try { return JObject.Parse(raw); }
            catch { return null; }
        }

        internal static void SaveJob(JObject job)
        {
            SessionState.SetString(ActiveJobKey, job.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static string[] SplitList(string csv)
        {
            var parts = csv.Split(',');
            var list = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var trimmed = p.Trim();
                if (trimmed.Length > 0) list.Add(trimmed);
            }
            return list.ToArray();
        }

        internal sealed class TestRunnerStuckAssessment
        {
            public TestRunnerStuckAssessment(string phase, int secondsSinceActivity, string hint)
            {
                Phase = phase;
                SecondsSinceActivity = secondsSinceActivity;
                Hint = hint;
            }

            public string Phase { get; }
            public int SecondsSinceActivity { get; }
            public string Hint { get; }
        }
    }

    /// <summary>
    /// Global Test Runner callback that keeps the active job's SessionState entry up to date.
    /// Re-registered on every domain load so PlayMode test runs (which reload the domain
    /// mid-run) still report completion. Unity only allows one test run at a time, so a
    /// single active-job slot is sufficient.
    /// </summary>
    [InitializeOnLoad]
    internal static class TestRunnerJobTracker
    {
        // Instance-based RegisterCallbacks (available since test-framework 1.1, which Unity 2022.3
        // bundles by default) instead of the static RegisterTestCallback added in 1.3+. Callbacks
        // are held in the test framework's global holder, so registering on any instance is
        // equivalent; the instance is kept alive in a static field for safety.
        private static readonly TestRunnerApi Api;

        static TestRunnerJobTracker()
        {
            Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            Api.hideFlags = HideFlags.HideAndDontSave;
            Api.RegisterCallbacks(new Callbacks());
        }

        private sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                var job = TestRunnerFunctions.LoadJob();
                if (job == null || job.Value<string>("status") != "running") return;
                if (job.Value<bool?>("hasFilters") == true)
                {
                    // Unity's callback tree can include unfiltered tests even when ExecutionSettings
                    // applies test/category/assembly filters, so avoid exposing a misleading total.
                    job.Remove("totalTests");
                }
                else
                {
                    job["totalTests"] = CountLeafTests(testsToRun);
                }
                job["lastActivityAt"] = TestRunnerFunctions.FormatTimestamp(DateTime.Now);
                job.Remove("currentTest");
                job.Remove("currentTestStartedAt");
                TestRunnerFunctions.SaveJob(job);
            }

            public void TestStarted(ITestAdaptor test)
            {
                if (test == null || test.IsSuite) return;
                var job = TestRunnerFunctions.LoadJob();
                if (job == null || job.Value<string>("status") != "running") return;
                var now = TestRunnerFunctions.FormatTimestamp(DateTime.Now);
                job["lastActivityAt"] = now;
                job["currentTestStartedAt"] = now;
                job["currentTest"] = test.FullName;
                TestRunnerFunctions.SaveJob(job);
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test.IsSuite) return;
                var job = TestRunnerFunctions.LoadJob();
                if (job == null || job.Value<string>("status") != "running") return;
                job["testsCompleted"] = (job.Value<int?>("testsCompleted") ?? 0) + 1;
                job["lastActivityAt"] = TestRunnerFunctions.FormatTimestamp(DateTime.Now);
                job.Remove("currentTest");
                job.Remove("currentTestStartedAt");
                TestRunnerFunctions.SaveJob(job);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                var job = TestRunnerFunctions.LoadJob();
                if (job == null || job.Value<string>("status") != "running") return;

                var failures = new JArray();
                CollectFailures(result, failures, maxFailures: 20);

                job["status"] = "finished";
                job["finishedAt"] = TestRunnerFunctions.FormatTimestamp(DateTime.Now);
                job["lastActivityAt"] = job["finishedAt"];
                job.Remove("currentTest");
                job.Remove("currentTestStartedAt");
                job["resultState"] = result.ResultState;
                job["passCount"] = result.PassCount;
                job["failCount"] = result.FailCount;
                job["skipCount"] = result.SkipCount;
                job["inconclusiveCount"] = result.InconclusiveCount;
                var finalTotal = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
                job["testsCompleted"] = finalTotal;
                job["totalTests"] = finalTotal;
                job["durationSeconds"] = Math.Round(result.Duration, 2);
                job["failures"] = failures;
                TestRunnerFunctions.SaveJob(job);
            }

            private static int CountLeafTests(ITestAdaptor test)
            {
                if (!test.IsSuite) return 1;
                int count = 0;
                foreach (var child in test.Children)
                    count += CountLeafTests(child);
                return count;
            }

            private static void CollectFailures(ITestResultAdaptor result, JArray failures, int maxFailures)
            {
                if (failures.Count >= maxFailures) return;

                if (!result.Test.IsSuite)
                {
                    if (result.TestStatus == TestStatus.Failed)
                    {
                        failures.Add(new JObject
                        {
                            ["test"] = result.FullName,
                            ["message"] = Truncate(result.Message, 500),
                            ["stackTrace"] = Truncate(result.StackTrace, 500)
                        });
                    }
                    return;
                }

                if (result.Children == null) return;
                foreach (var child in result.Children)
                {
                    CollectFailures(child, failures, maxFailures);
                    if (failures.Count >= maxFailures) return;
                }
            }

            private static string Truncate(string s, int max)
            {
                if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
                return s.Substring(0, max) + $"... (+{s.Length - max} chars)";
            }
        }
    }
}
