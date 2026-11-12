// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Services;
using Funplay.Editor.Settings;
using Funplay.Editor.State;
using Funplay.Editor.Threading;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tests
{
    public sealed class ExecutionJobIntegrationTests
    {
        private readonly List<string> owned = new List<string>();
        private string folder, settingsRoot;
        [UnityTearDown] public IEnumerator Cleanup()
        {
            foreach (var id in owned)
            {
                var job = ExecutionJobService.Store.Find(id);
                if (job != null && !job.Terminal) ExecutionJobService.Store.Cancel(id);
            }
            double deadline = EditorApplication.timeSinceStartup + 20;
            while (owned.Any(id => ExecutionJobService.Store.Find(id)?.Terminal == false) && EditorApplication.timeSinceStartup < deadline) yield return null;
            foreach (var id in owned) if (ExecutionJobService.Store.Find(id)?.Terminal == true) ExecutionJobService.Store.Cleanup(id);
            owned.Clear();
            if (folder != null) AssetDatabase.DeleteAsset(folder); folder = null;
            if (settingsRoot != null && Directory.Exists(settingsRoot)) Directory.Delete(settingsRoot, true); settingsRoot = null;
        }
        private string Track(object receipt)
        {
            var json = receipt as JObject ?? JObject.FromObject(receipt);
            Assert.IsTrue((bool)json["success"], json.ToString());
            string id = (string)json["data"]["job_id"]; Assert.IsNotNull(id); owned.Add(id); return id;
        }
        private static string Code(string counter, string value = "42") =>
            "public class OwnedExecution : IFunplayCommand { public void Execute(ExecutionContext ctx) { UnityEditor.SessionState.SetInt(\"" + counter + "\", UnityEditor.SessionState.GetInt(\"" + counter + "\", 0) + 1); ctx.ReturnValue = " + value + "; } }";

        [UnityTest] public IEnumerator ReceiptsPrecedeExecutionAndRepeatedKeysDoNotReplayTheWrite()
        {
            string key = Guid.NewGuid().ToString("N"), counter = "OwnedExecutionCounter_" + key;
            var code = Code(counter);
            var first = ScriptExecutionFunctions.ExecuteCode(code, skip_refresh: true, request_key: key, wait_seconds: 0);
            Assert.IsTrue(first.IsCompleted); string id = Track(first.Result);
            Assert.AreEqual(0, SessionState.GetInt(counter, 0), "Admission must not run user code inline.");
            var repeated = ScriptExecutionFunctions.ExecuteCode(code, skip_refresh: true, request_key: key, wait_seconds: 0);
            Assert.AreEqual(id, (string)((JObject)repeated.Result)["data"]["job_id"]);
            var finish = ExecutionTestCompletion.Finish(first); while (!finish.IsCompleted) yield return null;
            Assert.IsTrue((bool)((JObject)finish.Result)["success"], finish.Result.ToString());
            Assert.AreEqual(1, SessionState.GetInt(counter, 0));
            var third = ScriptExecutionFunctions.ExecuteCode(code, skip_refresh: true, request_key: key, wait_seconds: 0);
            Assert.AreEqual(id, (string)((JObject)third.Result)["data"]["job_id"]);
            var conflict = JObject.FromObject(ScriptExecutionFunctions.ExecuteCode(Code(counter, "43"), skip_refresh: true, request_key: key, wait_seconds: 0).Result);
            Assert.AreEqual("EXECUTION_REQUEST_KEY_CONFLICT", (string)conflict["code"]); Assert.AreEqual(1, SessionState.GetInt(counter, 0));
            SessionState.EraseInt(counter);
        }

        [UnityTest] public IEnumerator SerializationFailureKeepsActualExecutionAndRecoveryInsteadOfInvitingRetry()
        {
            string key = Guid.NewGuid().ToString("N"), counter = "OwnedSerializationCounter_" + key;
            string code = "public class Cycle { public Cycle self; public override string ToString() { throw new System.Exception(\"Must not stringify\"); } } " +
                Code(counter, "MakeCycle()") .Replace("public void Execute", "private static Cycle MakeCycle() { var c = new Cycle(); c.self = c; return c; } public void Execute");
            var task = ScriptExecutionFunctions.ExecuteCode(code, skip_refresh: true, request_key: key, wait_seconds: 0);
            string id = Track(task.Result); var finish = ExecutionTestCompletion.Finish(task); while (!finish.IsCompleted) yield return null;
            var result = (JObject)finish.Result;
            Assert.AreEqual("RESULT_SERIALIZATION_FAILED", (string)result["code"]);
            Assert.IsTrue((bool)result["data"]["executed"]); Assert.IsFalse((bool)result["data"]["retry_safe"]);
            Assert.AreEqual(id, (string)result["data"]["execution_id"]); Assert.AreEqual(1, SessionState.GetInt(counter, 0));
            var read = TaskStatusService.Query(id, "execution", key, null, 0, 100);
            Assert.AreEqual("failed", (string)read["data"]["status"]);
            Assert.AreEqual("failed", (string)read["data"]["serialization_status"]); Assert.IsTrue((bool)read["success"]);
            Assert.AreEqual(1, SessionState.GetInt(counter, 0)); SessionState.EraseInt(counter);
        }

        [UnityTest] public IEnumerator QueuedCancellationNeverRunsTheSnippet()
        {
            string key = Guid.NewGuid().ToString("N"), counter = "OwnedCancelledCounter_" + key;
            var task = ScriptExecutionFunctions.ExecuteCode(Code(counter), skip_refresh: true, request_key: key, wait_seconds: 0);
            string id = Track(task.Result);
            var result = JObject.FromObject(ScriptExecutionFunctions.ExecuteCode(action: "cancel", task_id: "execution:" + id).Result);
            Assert.AreEqual("cancelled", (string)result["data"]["status"]); yield return null;
            Assert.AreEqual(0, SessionState.GetInt(counter, 0)); Assert.IsFalse(ExecutionJobService.Store.Find(id).executed.Value);
            SessionState.EraseInt(counter);
        }

        [UnityTest] public IEnumerator CachedTaskReadsWorkWhileTheActualEditorThreadIsBlocked()
        {
            var helper = new NeverQueueThread(); var settings = Settings();
            var bridge = new MCPExecutionBridge(helper, settings, new StateController(), new FunctionInvokerController(), null);
            string key = Guid.NewGuid().ToString("N");
            var start = bridge.ExecuteToolAsync("execute_code", new Dictionary<string, object> { ["code"] =
                "public class BlockingOwnedRead { public static string Run() { System.Threading.Thread.Sleep(3000); return \"returned\"; } }",
                ["skip_refresh"] = true, ["request_key"] = key, ["wait_seconds"] = 0 }, default, MCPProjectContext.Current.project_id);
            string id = Track(JObject.Parse(start.Result));
            var observation = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (ExecutionJobService.Store.Find(id)?.phase != "executing" && DateTime.UtcNow < deadline) await Task.Delay(10);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var read = JObject.Parse(await bridge.ExecuteToolAsync("get_task", new Dictionary<string, object> {
                    ["task_id"] = "execution:" + id, ["wait_seconds"] = 0 }, default, MCPProjectContext.Current.project_id));
                long milliseconds = watch.ElapsedMilliseconds;
                var cancel = JObject.Parse(await bridge.ExecuteToolAsync("execute_code", new Dictionary<string, object> {
                    ["action"] = "cancel", ["task_id"] = "execution:" + id }, default, MCPProjectContext.Current.project_id));
                return new { read, milliseconds, cancel };
            });
            while (!observation.IsCompleted) yield return null;
            var observed = observation.GetAwaiter().GetResult();
            Assert.Less(observed.milliseconds, 1000, "Cached observation queued behind synchronous Unity code.");
            Assert.AreEqual("running", (string)observed.read["data"]["status"], observed.read.ToString());
            Assert.AreEqual("EXECUTION_NOT_INTERRUPTIBLE", (string)observed.cancel["code"]);
            Assert.AreEqual(0, helper.NativeQueueCalls);
            var finish = ExecutionTestCompletion.Finish(Task.FromResult((object)JObject.Parse(start.Result))); while (!finish.IsCompleted) yield return null;
            Assert.IsTrue((bool)((JObject)finish.Result)["success"]);
        }

        [Test] public void FastPathsEnforceIdentityExposureAndReadWriteBoundariesBeforeAdmission()
        {
            var helper = new NeverQueueThread(); var settings = Settings();
            var bridge = new MCPExecutionBridge(helper, settings, new StateController(), new FunctionInvokerController(), null);
            var args = new Dictionary<string, object> { ["code"] = "return \"no\";", ["wait_seconds"] = 0 };
            Assert.AreEqual("PROJECT_CONTEXT_REQUIRED", (string)JObject.Parse(bridge.ExecuteToolAsync("execute_code", args, default).Result)["code"]);
            Assert.AreEqual("PROJECT_CONTEXT_MISMATCH", (string)JObject.Parse(bridge.ExecuteToolAsync("execute_code", args, default, new string('b', 64)).Result)["code"]);
            var forget = new Dictionary<string, object> { ["action"] = "forget", ["task_id"] = "execution:" + Guid.NewGuid().ToString("N"), ["confirm_forget"] = true };
            Assert.AreEqual("EXECUTION_FORGET_REQUIRES_FULL", (string)JObject.Parse(bridge.ExecuteToolAsync("execute_code", forget, default, MCPProjectContext.Current.project_id).Result)["code"]);
            var recovery = new Dictionary<string, object> { ["action"] = "recover_journal" };
            Assert.AreEqual("EXECUTION_JOURNAL_RECOVERY_REQUIRES_FULL", (string)JObject.Parse(bridge.ExecuteToolAsync("execute_code", recovery, default, MCPProjectContext.Current.project_id).Result)["code"]);
            settings.MCPCoreTools = new[] { "get_task" };
            Assert.AreEqual("TOOL_NOT_EXPOSED", (string)JObject.Parse(bridge.ExecuteToolAsync("execute_code", args, default, MCPProjectContext.Current.project_id).Result)["code"]);
            settings.MCPToolExportProfile = "full"; settings.MCPFullTools = new[] { "get_task" };
            Assert.AreEqual("TOOL_NOT_EXPOSED", (string)JObject.Parse(bridge.ExecuteToolAsync("execute_code", recovery, default, MCPProjectContext.Current.project_id).Result)["code"]);
            settings.MCPFullTools = new[] { "get_task", "execute_code" };
            Assert.AreEqual("PROJECT_CONTEXT_REQUIRED", (string)JObject.Parse(bridge.ExecuteToolAsync("execute_code", recovery, default).Result)["code"]);
            recovery["task_id"] = "execution:" + Guid.NewGuid().ToString("N");
            Assert.AreEqual("INVALID_EXECUTION_CONTROL_ARGUMENTS", (string)JObject.Parse(bridge.ExecuteToolAsync("execute_code", recovery, default, MCPProjectContext.Current.project_id).Result)["code"]);
            Assert.AreEqual(0, helper.NativeQueueCalls);
        }

        [UnityTest] public IEnumerator BoundedStartWaitReturnsAShortTerminalResultAndTransportCancellationDoesNotCancelTheJob()
        {
            string counter = "OwnedWaitCounter_" + Guid.NewGuid().ToString("N");
            var task = ScriptExecutionFunctions.ExecuteCode(Code(counter), skip_refresh: true, request_key: Guid.NewGuid().ToString("N"), wait_seconds: 30);
            while (!task.IsCompleted) yield return null;
            var result = (JObject)task.Result; string id = Track(result);
            Assert.AreEqual("completed", (string)result["data"]["task"]["wait_reason"]);
            Assert.IsTrue((bool)result["data"]["task"]["wait_complete"]); Assert.AreEqual(42, (int)result["data"]["returnValue"]);
            var record = ExecutionJobService.Store.Find(id); Assert.Greater(record.compile_ms, 0); Assert.Greater(record.persist_ms, 0);
            SessionState.EraseInt(counter);

            var cts = new CancellationTokenSource(); string key = Guid.NewGuid().ToString("N");
            var pending = ExecutionJobService.StartAsync(new ExecutionOptions { Code = Code(counter), Key = key, SkipRefresh = true, WaitSeconds = 30 }, cts.Token);
            string pendingId = ExecutionJobService.Store.Find(null, key).job_id; owned.Add(pendingId); cts.Cancel();
            while (!pending.IsCompleted) yield return null;
            Assert.IsTrue(pending.IsCanceled); Assert.IsFalse(ExecutionJobService.Store.Find(pendingId).cancel_requested);
            while (!ExecutionJobService.Store.Find(pendingId).Terminal) yield return null;
            Assert.AreEqual(1, SessionState.GetInt(counter, 0)); SessionState.EraseInt(counter); cts.Dispose();
        }

        [UnityTest] public IEnumerator MultiMegabytePrefabSaveHasARecoverableIdAndDoesNotRepeatTheSave()
        {
            string name = "FunplayExecutionFixture_" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            string path = folder + "/Large.prefab", key = Guid.NewGuid().ToString("N"), counter = "OwnedPrefabSave_" + key;
            var preview = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Large", typeof(RectTransform)); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, preview);
            try
            {
                for (int i = 0; i < 1025; i++)
                {
                    var child = new GameObject("Node" + i, typeof(RectTransform)); child.transform.SetParent(root.transform);
                    if (i < 105) { var text = child.AddComponent<Text>(); text.text = new string('x', 40000); }
                }
                Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(root, path));
            }
            finally { Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(preview); }
            Assert.Greater(new FileInfo(path).Length, 4 * 1024 * 1024);
            string before = ExecutionJobStore.Hash(File.ReadAllText(path));
            string code = "public class SaveOwnedPrefab : IFunplayCommand { public void Execute(ExecutionContext ctx) { " +
                "var root = UnityEditor.PrefabUtility.LoadPrefabContents(\"" + path + "\"); try { " +
                "root.transform.GetChild(0).localScale = new UnityEngine.Vector3(2,3,4); UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, \"" + path + "\"); " +
                "UnityEditor.SessionState.SetInt(\"" + counter + "\", UnityEditor.SessionState.GetInt(\"" + counter + "\",0)+1); ctx.ReturnValue = new { nodes = root.GetComponentsInChildren<UnityEngine.Transform>(true).Length }; " +
                "} finally { UnityEditor.PrefabUtility.UnloadPrefabContents(root); } } }";
            var start = ScriptExecutionFunctions.ExecuteCode(code, skip_refresh: true, request_key: key, wait_seconds: 0);
            string id = Track(start.Result); var finish = ExecutionTestCompletion.Finish(start); while (!finish.IsCompleted) yield return null;
            var result = (JObject)finish.Result; Assert.IsTrue((bool)result["success"], result.ToString()); Assert.AreEqual(1026, (int)result["data"]["returnValue"]["nodes"]);
            string after = ExecutionJobStore.Hash(File.ReadAllText(path)); Assert.AreNotEqual(before, after);
            var repeated = ScriptExecutionFunctions.ExecuteCode(code, skip_refresh: true, request_key: key, wait_seconds: 0);
            Assert.AreEqual(id, (string)((JObject)repeated.Result)["data"]["job_id"]); Assert.AreEqual(1, SessionState.GetInt(counter, 0));
            Assert.AreEqual(after, ExecutionJobStore.Hash(File.ReadAllText(path))); SessionState.EraseInt(counter);
        }

        private SettingsController Settings()
        {
            settingsRoot = Path.Combine(Path.GetTempPath(), "FunplayExecutionSettings_" + Guid.NewGuid().ToString("N"));
            return new SettingsController(new Paths(settingsRoot));
        }
        private sealed class Paths : IApplicationPaths
        {
            internal Paths(string root) { ProjectPath = root; }
            public string ProjectPath { get; }
            public string AssetsPath => Path.Combine(ProjectPath, "Assets"); public string TempPath => Path.Combine(ProjectPath, "Temp");
            public string DataPath => AssetsPath; public string PersistentDataPath => Path.Combine(ProjectPath, "Persistent");
        }
        private sealed class NeverQueueThread : IEditorThreadHelper
        {
            internal int NativeQueueCalls;
            public bool IsMainThread => true;
            public Task ExecuteOnEditorThreadAsync(Action action) => Task.CompletedTask; // A deferred UI log is not needed for this fixture.
            public Task<T> ExecuteOnEditorThreadAsync<T>(Func<T> func) { NativeQueueCalls++; throw new Exception("Unexpected native Editor queue"); }
            public Task<T> ExecuteAsyncOnEditorThreadAsync<T>(Func<Task<T>> func) { NativeQueueCalls++; throw new Exception("Unexpected native Editor queue"); }
            public void Dispose() { }
        }
    }
}
