// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Settings;
using Funplay.Editor.State;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Funplay.Editor.Tests
{
    public sealed class ExecutionTrustTests
    {
        [Test] public void WrongProjectAndStaleInstanceFailBeforeWritesButLegacyReadsRemainPossible()
        {
            var current = new ProjectExecutionContext { project_id = new string('a', 64), instance_id = "session", editor_epoch = "epoch" };
            Assert.AreEqual("PROJECT_CONTEXT_MISMATCH", MCPProjectContext.Validate(current, new string('b', 64), null, true));
            Assert.AreEqual("PROJECT_CONTEXT_REQUIRED", MCPProjectContext.Validate(current, null, null, true));
            Assert.IsNull(MCPProjectContext.Validate(current, null, null, false));
            Assert.IsNull(MCPProjectContext.Validate(current, current.project_id, null, true));
            Assert.IsNull(MCPProjectContext.Validate(current, null, current.expected_context, true));
            Assert.AreEqual("EDITOR_INSTANCE_CHANGED", MCPProjectContext.Validate(current, current.project_id, current.project_id + ":old", true));
            Assert.AreEqual("PROJECT_CONTEXT_MISMATCH", MCPProjectContext.Validate(current, null, new string('b', 64) + ":session", false));
            Assert.AreEqual("INVALID_EXPECTED_CONTEXT", MCPProjectContext.Validate(current, null, "malformed", true));
        }
        [Test] public void RoutingCannotBeSpoofedWithQueryOrEncodedPath()
        {
            string id = new string('a', 64);
            Assert.IsTrue(MCPProjectContext.TryRoute("/mcp/project/" + id + "/?q=ignored", out var found)); Assert.AreEqual(id, found);
            foreach (var bad in new[] { "/mcp/project/" + id + "/child", "/mcp/project/%61", "/something", "/mcp/project/../", "/mcp/project/" + id.ToUpperInvariant() })
                Assert.IsFalse(MCPProjectContext.TryRoute(bad, out _), bad);
            Assert.IsTrue(MCPProjectContext.TryRoute("/", out found)); Assert.IsNull(found);
            Assert.That(MCPProjectContext.Endpoint(1234, id), Does.EndWith("/mcp/project/" + id + "/"));
        }
        [Test] public void EvidenceStampPreservesEnvelopeAndDoesNotCorruptNativeImage()
        {
            string result = MCPProjectContext.Stamp("{\"success\":true,\"data\":{\"sentinel\":42}}");
            var json = JObject.Parse(result);
            Assert.AreEqual(42, (int)json["data"]["sentinel"]);
            Assert.AreEqual(MCPProjectContext.Current.project_id, (string)json["context"]["project_id"]);
            Assert.AreEqual("data:image/png;base64,abc", MCPProjectContext.Stamp("data:image/png;base64,abc"));
        }
        [Test] public void NativeProtocolSerializerKeepsContextStructural()
        {
            var wire = JObject.Parse(SimpleJsonHelper.Serialize(new Dictionary<string, object> {
                ["funplay"] = new Dictionary<string, object> { ["context"] = MCPProjectContext.Metadata },
                ["_meta"] = new Dictionary<string, object> { ["context"] = MCPProjectContext.Metadata }
            }));
            Assert.AreEqual(MCPProjectContext.Current.project_path, (string)wire["funplay"]["context"]["project_path"]);
            Assert.AreEqual(MCPProjectContext.Current.expected_context, (string)wire["_meta"]["context"]["expected_context"]);
            Assert.AreEqual(Newtonsoft.Json.Linq.JTokenType.Object, wire["funplay"]["context"].Type);
        }
        [Test] public void ExportedGuardIsOptionalForMutationsAndDoesNotInflateReadSchemas()
        {
            var tools = JArray.FromObject(new MCPToolExporter(null).ExportTools());
#if ENABLE_INPUT_SYSTEM
            Assert.AreEqual(40, tools.Count);
#else
            // simulate_key_press / simulate_key_combo only compile with the Input System backend.
            Assert.AreEqual(38, tools.Count);
#endif
            var write = tools.Single(t => (string)t["name"] == "set_component_property");
            Assert.AreEqual("string", (string)write["inputSchema"]["properties"]["expected_context"]["type"]);
            Assert.IsFalse(write["inputSchema"]["required"].Values<string>().Contains("expected_context"));
            var read = tools.Single(t => (string)t["name"] == "get_editor_state");
            Assert.IsNull(read["inputSchema"]["properties"]["expected_context"]);
        }
        [Test] public void ClockClassificationDoesNotConfuseEncodedFramesOrScaledTimeWithUpdates()
        {
            var a = new PlayerClockSample { frame = 100, wall_seconds = 1, time_scale = 1 };
            var b = new PlayerClockSample { frame = 101, wall_seconds = 2, time_scale = 0 };
            Assert.AreEqual("running_scaled_time_paused", PlayerLoopWitness.Classify(true, false, true, a, b, 2.2));
            Assert.AreEqual("stalled", PlayerLoopWitness.Classify(true, false, true, a, b, 4));
            Assert.AreEqual("stalled", PlayerLoopWitness.Classify(true, false, true, a, a, 4), "Even a single frozen sample must eventually stall.");
            Assert.AreEqual("editor_paused", PlayerLoopWitness.Classify(true, true, true, a, b, 4));
            Assert.AreEqual("observer_unavailable", PlayerLoopWitness.Classify(true, false, false, a, b, 4));
            Assert.AreEqual("insufficient_samples", PlayerLoopWitness.Classify(true, false, true, null, null, 4));
        }
        [Test] public void FrameCountersDoNotWakeEditorTaskRevision()
        {
            object Receipt(int frame) => Response.Success("ready", new { operation = new { operation_id = Guid.Empty.ToString("N"), status = "running" },
                current_editor = new { ready = false, player = new { frame, last_frame_age_seconds = frame / 3.0 } } });
            string Revision(int frame) => (string)TaskStatusService.Describe("editor", Receipt(frame))["data"]["task"]["revision"];
            Assert.AreEqual(Revision(1), Revision(2));
        }
        [Test] public void RecoveryHintsRespectExactExposureEvenWhenNothingIsAllowed()
        {
            var core = MCPRecoveryGuidance.Describe(name => name == "get_task" || name == "get_editor_state");
            Assert.That(core, Does.Contain("get_task")); Assert.That(core, Does.Not.Contain("get_reload_recovery_status"));
            var none = MCPRecoveryGuidance.Describe(_ => false);
            Assert.That(none, Does.Contain("No recovery reader")); Assert.That(none, Does.Not.Contain("get_task"));
        }
        [TestCase("input", "simulate_mouse_click")][TestCase("verification", "verification_scope")]
        public void NewTaskReadersCannotBypassCustomExposure(string kind, string owner)
        {
            Assert.IsFalse(TaskStatusService.CanRead(kind, name => name == "get_task"));
            Assert.IsTrue(TaskStatusService.CanRead(kind, name => name == owner));
        }
        [Test] public void ManagementStaysFullAndInputIsNotMisclassifiedReadOnly()
        {
            Assert.Greater(ToolRegistry.MethodCache.Count, 0);
            Assert.AreEqual(40, MCPToolExportPolicy.DefaultCoreTools.Count);
            foreach (var name in new[] { "verification_scope", "patch_serialized_reference", "inspect_render_bindings", "save_verification_evidence", "read_verification_evidence" })
            {
                Assert.IsNotNull(ToolRegistry.GetMethod(name), name);
                Assert.IsFalse(MCPToolExportPolicy.DefaultCoreTools.Contains(name), name);
                Assert.IsTrue(MCPToolExportPolicy.IsToolAllowed(name, MCPToolExportProfile.Full, false, null, false, null));
            }
            foreach (var name in new[] { "simulate_mouse_click", "simulate_key_press", "simulate_key_combo" })
                if (ToolRegistry.GetMethod(name) != null) Assert.IsFalse(ToolRegistry.IsReadOnly(ToolRegistry.GetMethod(name)), name);
            Assert.IsTrue(ToolRegistry.IsReadOnly(ToolRegistry.GetMethod("read_verification_evidence")));
        }
        [TestCase(false, true, true, true)][TestCase(false, true, false, false)][TestCase(true, true, true, false)]
        public void CleanupRestoresOnlyAnOwnedUnchangedSetting(bool before, bool assigned, bool actual, bool restore)
            => Assert.AreEqual(restore, VerificationScope.ShouldRestore(before, assigned, actual));
        [Test] public void ReferenceInventoryProtectsAll167UntouchedSlotsIncludingUnresolvedOnes()
        {
            string Header = "%YAML 1.1\n--- !u!114 &123\nMonoBehaviour:\n  entries:\n";
            var lines = Enumerable.Range(0, 168).Select(i => "  - {fileID: 11400000, guid: " + i.ToString("x32") + ", type: 2}\n").ToArray();
            var before = SerializedReferenceTransaction.Inventory(Header + string.Concat(lines));
            var afterLines = (string[])lines.Clone(); afterLines[80] = "  - {fileID: 11400000, guid: ffffffffffffffffffffffffffffffff, type: 2}\n";
            var after = SerializedReferenceTransaction.Inventory(Header + string.Concat(afterLines));
            string slot = before.Keys.ElementAt(80);
            Assert.IsTrue(SerializedReferenceTransaction.Preserved(before, after, slot, after[slot]));
            afterLines[81] = "  - {fileID: 0}\n";
            Assert.IsFalse(SerializedReferenceTransaction.Preserved(before, SerializedReferenceTransaction.Inventory(Header + string.Concat(afterLines)), slot, after[slot]));
            Assert.Catch(() => SerializedReferenceTransaction.Inventory("binary"));
            Assert.Catch(() => SerializedReferenceTransaction.Inventory(Header + "  entries: {fileID: abc}\n"));
        }
        [Test] public void RenderInspectionDoesNotInstantiateSharedMaterials()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var renderer = go.GetComponent<Renderer>(); var shared = renderer.sharedMaterial;
                int count = Resources.FindObjectsOfTypeAll<Material>().Length;
                var receipt = JObject.FromObject(RenderInspectionFunctions.InspectRenderBindings(ObjectIdHelper.GetSerializableId(go)));
                Assert.IsTrue((bool)receipt["success"], receipt.ToString());
                Assert.AreEqual(shared, renderer.sharedMaterial); Assert.AreEqual(count, Resources.FindObjectsOfTypeAll<Material>().Length);
                Assert.IsNull((bool?)receipt["data"]["rendered_correctly"]);
                Assert.AreEqual(1, ((JArray)receipt["data"]["bindings"]).Count);
            } finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        [Test] public void StaticCheckpointReuseInvalidatesChangedFileAndConfiguration()
        {
            string path = "Assets/__FunplayTrustEvidence_" + Guid.NewGuid().ToString("N") + ".asset";
            var asset = ScriptableObject.CreateInstance<ReferenceTransactionFixture>();
            try
            {
                AssetDatabase.CreateAsset(asset, path);
                var saved = JObject.FromObject(VerificationEvidenceStore.Save(new JArray(path).ToString(), "{\"b\":2,\"a\":1}", "{\"next\":\"test current runtime\"}"));
                Assert.IsTrue((bool)saved["success"], saved.ToString()); string id = (string)saved["data"]["evidence_id"];
                Assert.IsTrue((bool)JObject.FromObject(VerificationEvidenceStore.Read(id, "{\"a\":1,\"b\":2}"))["data"]["cache_valid"]);
                Assert.IsFalse((bool)JObject.FromObject(VerificationEvidenceStore.Read(id, "{\"a\":3}"))["data"]["cache_valid"]);
                asset.label = "changed"; EditorUtility.SetDirty(asset); AssetDatabase.SaveAssetIfDirty(asset);
                Assert.IsFalse((bool)JObject.FromObject(VerificationEvidenceStore.Read(id, null))["data"]["cache_valid"]);
            } finally { AssetDatabase.DeleteAsset(path); }
        }
        [Test] public void NativeModalExceptionRetainsConsequentialChoiceBoundaries()
        {
            Assert.That(MCPServerInstructions.UiAutomationGuidance, Does.Contain("blocking native Unity modal"));
            Assert.That(MCPServerInstructions.UiAutomationGuidance, Does.Contain("save, discard, overwrite, install, authorize"));
            Assert.That(MCPServerInstructions.UiAutomationGuidance, Does.Contain("recheck project identity"));
        }
        [Test] public void SelectivePersistentReferenceChangeUsesPreconditionsAndPreservesOtherReferences()
        {
            string prefix = "Assets/__FunplayTrustTxn_" + Guid.NewGuid().ToString("N");
            string path = prefix + ".asset", aPath = prefix + "_a.asset", bPath = prefix + "_b.asset";
            try
            {
                var a = ScriptableObject.CreateInstance<ReferenceTransactionFixture>(); AssetDatabase.CreateAsset(a, aPath);
                var b = ScriptableObject.CreateInstance<ReferenceTransactionFixture>(); AssetDatabase.CreateAsset(b, bPath);
                var target = ScriptableObject.CreateInstance<ReferenceTransactionFixture>(); target.reference = a; target.other = b; AssetDatabase.CreateAsset(target, path);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out string _, out long targetId);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out string aGuid, out long aId);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(b, out string _, out long bId);
                string hash = SerializedReferenceTransaction.Hash(File.ReadAllBytes(path));
                var rejected = JObject.FromObject(SerializedReferenceTransaction.Patch(path, targetId, "reference", "wrong", aGuid, aId, bPath, bId));
                Assert.AreEqual("ASSET_PRECONDITION_FAILED", (string)rejected["code"]); Assert.AreEqual(hash, SerializedReferenceTransaction.Hash(File.ReadAllBytes(path)));
                var result = JObject.FromObject(SerializedReferenceTransaction.Patch(path, targetId, "reference", hash, aGuid, aId, bPath, bId));
                Assert.IsTrue((bool)result["success"], result.ToString());
                var loaded = AssetDatabase.LoadAssetAtPath<ReferenceTransactionFixture>(path);
                Assert.AreEqual(b, loaded.reference); Assert.AreEqual(b, loaded.other);
                Assert.Greater((int)result["data"]["protected_reference_slots"], 0);
            } finally { AssetDatabase.DeleteAsset(path); AssetDatabase.DeleteAsset(aPath); AssetDatabase.DeleteAsset(bPath); }
        }
        [Test] public void UnresolvedSubassetReferencesAreEitherPreservedOrRejectedWithoutLiveSave()
        {
            string prefix = "Assets/__FunplayTrustUnresolved_" + Guid.NewGuid().ToString("N");
            string path = prefix + ".asset", resources = prefix + "_resources.asset";
            try
            {
                var container = ScriptableObject.CreateInstance<ReferenceTransactionFixture>(); AssetDatabase.CreateAsset(container, resources);
                var entries = Enumerable.Range(0, 168).Select(i => { var item = ScriptableObject.CreateInstance<ReferenceTransactionFixture>(); item.name = "Entry" + i; AssetDatabase.AddObjectToAsset(item, container); return item; }).ToArray();
                EditorUtility.SetDirty(container); AssetDatabase.SaveAssetIfDirty(container);
                var target = ScriptableObject.CreateInstance<ReferenceTransactionFixture>(); target.entries = entries.Cast<UnityEngine.Object>().ToArray(); AssetDatabase.CreateAsset(target, path);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out string _, out long targetId);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(entries[0], out string guid, out long oldId);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(entries[1], out string _, out long nextId);
                for (int i = 42; i < entries.Length; i++) UnityEngine.Object.DestroyImmediate(entries[i], true);
                EditorUtility.SetDirty(container); AssetDatabase.SaveAssetIfDirty(container);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var bytes = File.ReadAllBytes(path); string hash = SerializedReferenceTransaction.Hash(bytes);
                var original = SerializedReferenceTransaction.Inventory(System.Text.Encoding.UTF8.GetString(bytes));
                var receipt = JObject.FromObject(SerializedReferenceTransaction.Patch(path, targetId, "entries.Array.data[0]", hash, guid, oldId, resources, nextId));
                if ((bool)receipt["success"])
                {
                    var after = SerializedReferenceTransaction.Inventory(File.ReadAllText(path));
                    var changed = original.Keys.Where(k => original[k] != after[k]).ToArray();
                    Assert.AreEqual(1, changed.Length); Assert.IsTrue(SerializedReferenceTransaction.Preserved(original, after, changed[0], after[changed[0]]));
                    Assert.GreaterOrEqual((int)receipt["data"]["protected_reference_slots"], 167);
                }
                else Assert.AreEqual(hash, SerializedReferenceTransaction.Hash(File.ReadAllBytes(path)), receipt.ToString());
            }
            finally { AssetDatabase.DeleteAsset(path); AssetDatabase.DeleteAsset(resources); }
        }
        [Test] public void ExternalDecoderRequiresOptInAndRejectsArbitraryCommands()
        {
            Assert.IsNull(InstalledVideoDecoder.Resolve("/bin/sh"));
            Assert.IsNull(InstalledVideoDecoder.Resolve("relative/ffmpeg"));
            Assert.That(InstalledVideoDecoder.Quote("a b\"c\\"), Does.StartWith("\""));
            Assert.Throws<ArgumentException>(() => InstalledVideoDecoder.Quote("bad\nargument"));
            var discovery = JObject.FromObject(RecordingEvidenceService.DecoderAvailability());
            Assert.IsTrue((bool)discovery["external_requires_explicit_opt_in"]);
        }
        [UnityTest] public IEnumerator OwnedPlayScopeObservesRealEdgesAndRestoresBackgroundWithoutChangingScene()
        {
            if (!Environment.GetCommandLineArgs().Contains("-funplayOwnedTestSession")) Assert.Ignore("Requires an explicitly owned test session.");
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
            yield return new EnterPlayMode();
            var availability = JObject.FromObject(InputFrameJob.Availability());
            if (availability.Value<bool?>("available") != true) { yield return new ExitPlayMode(); Assert.Ignore("Dynamic new Input System backend required for frame-edge check."); }
#if ENABLE_INPUT_SYSTEM
            var inputSettings = new ContinuousDragTests.OwnedInputSettings();
#endif
            bool background = Application.runInBackground;
            var lease = JObject.FromObject(VerificationScope.Start(15, true, "owned-frame-check"));
            Assert.IsTrue((bool)lease["success"], lease.ToString()); string id = (string)lease["data"]["job_id"];
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            try
            {
                var again = JObject.FromObject(VerificationScope.Start(15, true, "owned-frame-check"));
                Assert.AreEqual(id, (string)again["data"]["job_id"]);
                Assert.AreEqual(id, (string)JObject.FromObject(VerificationScope.Read(null, "owned-frame-check"))["data"]["job_id"]);
                var deadline = EditorApplication.timeSinceStartup + 10;
                while (PlayerLoopWitness.Latest == null) { Assert.Less(EditorApplication.timeSinceStartup, deadline); yield return null; }
                var input = JObject.FromObject(InputFrameJob.Start(new Vector2(20, 20), "left", null, 2, id));
                Assert.IsTrue((bool)input["success"], input.ToString()); string inputId = (string)input["data"]["job_id"];
                while ((string)JObject.FromObject(InputFrameJob.Read(inputId))["data"]["status"] == "running")
                { Assert.Less(EditorApplication.timeSinceStartup, deadline); yield return null; }
                var receipt = JObject.FromObject(InputFrameJob.Read(inputId));
                Assert.AreEqual("completed", (string)receipt["data"]["status"], receipt.ToString());
                foreach (var edge in new[] { "down_observed", "held_observed", "up_observed" }) Assert.IsTrue((bool)receipt["data"][edge], edge);
                Assert.AreEqual("LEGACY_POLLING_INJECTION_UNSUPPORTED", (string)JObject.FromObject(InputInteractionFunctions.SimulateMouseClick(0, 0, input_route: "legacy_polling"))["code"]);
                var read = JObject.FromObject(VerificationScope.Read(id)); Assert.IsTrue(Application.runInBackground, "Read must not restore owned settings.");
                Assert.IsTrue((bool)read["data"]["scene_unchanged"]);
            }
            finally
            {
                VerificationScope.End(id);
#if ENABLE_INPUT_SYSTEM
                inputSettings.Dispose();
#endif
            }
            Assert.AreEqual(background, Application.runInBackground); Assert.AreEqual(scene, UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
            yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator RestoreOwnedPlayStateAfterFailedAssertion()
        {
            if (Environment.GetCommandLineArgs().Contains("-funplayOwnedTestSession") && EditorApplication.isPlaying) yield return new ExitPlayMode();
        }
    }
}
