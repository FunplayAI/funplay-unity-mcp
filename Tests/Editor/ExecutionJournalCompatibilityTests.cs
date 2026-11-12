// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using System.Linq;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    // Unity's EditMode runner executes these synchronous cases on the Editor thread.
    public sealed class ExecutionJournalCompatibilityTests
    {
        private string root;
        private Func<JsonSerializerSettings> previousDefaults;
        private readonly ProjectExecutionContext context = new ProjectExecutionContext {
            project_id = new string('a', 64), instance_id = "owned-compatibility-session", editor_epoch = "epoch", project_path = "/owned/fixture" };
        [SetUp] public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "FunplayJournalCompatibility_" + Guid.NewGuid().ToString("N"));
            previousDefaults = JsonConvert.DefaultSettings;
        }
        [TearDown] public void TearDown()
        {
            JsonConvert.DefaultSettings = previousDefaults;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        private ExecutionJobRecord Complete(ExecutionJobStore store, string key = "retained-key")
        {
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), key, "execute_code", 300, context, out var job, out _));
            var trace = new ScriptExecutionTrace { Id = job.job_id, Executed = true };
            store.Complete(job.job_id, ToolResultSerializer.Serialize(ExecutionResultEnvelope.Wrap(Response.Success("done", new { returnValue = 42 }), trace)), trace);
            return job;
        }
        [TestCase(TypeNameHandling.Arrays)][TestCase(TypeNameHandling.All)][TestCase(TypeNameHandling.Objects)][TestCase(TypeNameHandling.Auto)]
        public void JournalSaveAndReloadIgnoreGlobalArrayTypeMetadata(TypeNameHandling types)
        {
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings { TypeNameHandling = types };
            var store = new ExecutionJobStore(root); var first = Complete(store);
            Assert.IsInstanceOf<JArray>(JObject.Parse(File.ReadAllText(Path.Combine(root, "journal.json")))["jobs"]);
            store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            Assert.AreEqual(first.job_id, store.Find(null, "retained-key").job_id);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "retained-key", "execute_code", 300, context, out var repeated, out bool existing));
            Assert.IsTrue(existing); Assert.AreEqual(first.job_id, repeated.job_id);
        }
        [Test] public void ExistingWrappedListsLoadWithoutEnablingTypeInstantiationOrLosingKeys()
        {
            var store = new ExecutionJobStore(root); var first = Complete(store);
            string path = Path.Combine(root, "journal.json"); var journal = JObject.Parse(File.ReadAllText(path));
            foreach (string name in new[] { "jobs", "keys" }) journal[name] = new JObject {
                ["$type"] = "System.Collections.Generic.List`1[[Funplay.Editor.State." + (name == "jobs" ? "ExecutionJobRecord" : "ExecutionKeyRecord") + ", Funplay.Editor]], mscorlib",
                ["$values"] = journal[name].DeepClone() };
            File.WriteAllText(path, journal.ToString(Formatting.None));
            store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            Assert.AreEqual(first.job_id, store.Find(null, "retained-key").job_id);
            Assert.AreEqual(42, (int)store.Read(first.job_id, null)["data"]["output"]["returnValue"]);
        }
        [Test] public void JournalNeverInvokesTheProjectDefaultSettingsDelegate()
        {
            JsonConvert.DefaultSettings = () => throw new InvalidOperationException("Project defaults must not be consulted");
            var store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            var first = Complete(store);
            store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            Assert.AreEqual(first.job_id, store.Find(null, "retained-key").job_id);
            Assert.AreEqual(42, (int)store.Read(first.job_id, null)["data"]["output"]["returnValue"]);
            Assert.IsTrue((bool)ExecutionJson.Object(store.RecoverJournal())["success"]);
        }
        private string WrapJournal()
        {
            string path = Path.Combine(root, "journal.json"); var token = JObject.Parse(File.ReadAllText(path));
            foreach (string name in new[] { "jobs", "keys" }) token[name] = new JObject {
                ["$type"] = "System.Collections.Generic.List`1[[Funplay.Editor.State." + (name == "jobs" ? "ExecutionJobRecord" : "ExecutionKeyRecord") + ", Funplay.Editor]], mscorlib",
                ["$values"] = token[name].DeepClone() };
            string text = token.ToString(Formatting.None); File.WriteAllText(path, text); return text;
        }
        [Test] public void LegacyMigrationRetainsExactBackupResultsAndEveryIdempotencyKey()
        {
            var store = new ExecutionJobStore(root); var first = Complete(store);
            string resultPath = Path.Combine(root, first.job_id + ".result.json"), result = File.ReadAllText(resultPath);
            string original = WrapJournal(), path = Path.Combine(root, "journal.json");
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Arrays };
            store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            Assert.AreEqual(original, File.ReadAllText(path), "Reading a terminal legacy journal must not rewrite it.");
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("new"), "fresh", "execute_code", 300, context, out var fresh, out bool existing));
            Assert.IsFalse(existing); Assert.IsNotNull(store.LegacyBackup);
            Assert.AreEqual(original, File.ReadAllText(Path.Combine(root, store.LegacyBackup)));
            Assert.AreEqual(result, File.ReadAllText(resultPath));
            var current = JObject.Parse(File.ReadAllText(path)); Assert.IsInstanceOf<JArray>(current["jobs"]); Assert.AreEqual(2, current["keys"].Count());
            Assert.IsNull(store.Cancel(fresh.job_id)); store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "retained-key", "execute_code", 300, context, out var repeated, out existing));
            Assert.IsTrue(existing); Assert.AreEqual(first.job_id, repeated.job_id);
            Assert.AreEqual("EXECUTION_REQUEST_KEY_CONFLICT", (string)ExecutionJson.Object(store.Admit(ExecutionJobStore.Hash("changed"), "retained-key", "execute_code", 300, context, out _, out _))["code"]);
        }
        [Test] public void InterruptedLegacyExecutionIsNotReplayedAndOpaqueRecoveryJsonIsNotReinterpreted()
        {
            var store = new ExecutionJobStore(root);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "retained-key", "execute_code", 300, context, out var first, out _));
            var opaque = new JObject { ["$type"] = "Untrusted.OpaqueData, NotLoaded", ["$values"] = new JArray("do not reinterpret") };
            store.Update(first.job_id, x => { x.status = "running"; x.phase = "executing"; x.executed = null; x.recovery = opaque; });
            string original = WrapJournal(); store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            var recovered = store.Find(null, "retained-key"); Assert.AreEqual("outcome_unknown", recovered.status); Assert.IsNull(recovered.executed);
            Assert.IsTrue(JToken.DeepEquals(opaque, recovered.recovery));
            Assert.AreEqual(original, File.ReadAllText(Path.Combine(root, store.LegacyBackup)));
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("same"), "retained-key", "execute_code", 300, context, out var repeat, out bool existing));
            Assert.IsTrue(existing); Assert.AreEqual(first.job_id, repeat.job_id);
        }
        [TestCase("values-not-array")][TestCase("unknown-wrapper-field")][TestCase("foreign-type")]
        [TestCase("missing-keys")][TestCase("duplicate-jobs")][TestCase("key-mismatch")][TestCase("unsupported-schema")]
        public void AmbiguousOrInvalidJournalsArePreservedAndRecoveryFailsClosed(string problem)
        {
            var store = new ExecutionJobStore(root); Complete(store); WrapJournal();
            string path = Path.Combine(root, "journal.json"); var token = JObject.Parse(File.ReadAllText(path));
            switch (problem)
            {
                case "values-not-array": token["jobs"]["$values"] = new JObject(); break;
                case "unknown-wrapper-field": token["jobs"]["$ref"] = "other"; break;
                case "foreign-type": token["jobs"]["$type"] = "System.Diagnostics.Process, System"; break;
                case "missing-keys": token.Remove("keys"); break;
                case "key-mismatch": token["keys"]["$values"][0]["key_hash"] = new string('b', 64); break;
                case "unsupported-schema": token["schema_version"] = 2; break;
            }
            string text = token.ToString(Formatting.None);
            if (problem == "duplicate-jobs") text = text.Insert(text.Length - 1, ",\"jobs\":[]");
            File.WriteAllText(path, text);
            store = new ExecutionJobStore(root); Assert.IsNotNull(store.Error);
            Assert.IsFalse((bool)ExecutionJson.Object(store.RecoverJournal())["success"]);
            Assert.AreEqual(text, File.ReadAllText(path)); Assert.AreEqual(0, Directory.GetFiles(root, "journal.legacy-list-wrappers.*.json").Length);
            Assert.AreEqual("EXECUTION_JOURNAL_UNAVAILABLE", (string)ExecutionJson.Object(store.Admit(ExecutionJobStore.Hash("next"), "next", "execute_code", 300, context, out _, out _))["code"]);
        }
        [Test] public void RequestSignaturesAndErrorEnvelopesDoNotDependOnProjectDefaults()
        {
            var options = new ExecutionOptions { Code = "owned fixture", Key = "stable-key", PreferredNamespaces = "UnityEngine" };
            JsonConvert.DefaultSettings = null; string signature = options.Signature;
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All,
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(), DefaultValueHandling = DefaultValueHandling.Ignore };
            Assert.AreEqual(signature, options.Signature);
            var cycle = new Cycle(); cycle.Self = cycle;
            var trace = new ScriptExecutionTrace { Executed = true, Recovery = new { modified = new[] { "owned" } } };
            var error = JObject.Parse(ToolResultSerializer.Serialize(ExecutionResultEnvelope.Wrap(Response.Success("executed", cycle), trace)));
            Assert.AreEqual("RESULT_SERIALIZATION_FAILED", (string)error["code"]);
            Assert.IsFalse((bool)error["success"]); Assert.IsInstanceOf<JArray>(error["data"]["recovery"]["modified"]);
        }
        private sealed class Cycle { public Cycle Self; }
        [Test] public void LegacyBackupPreservesTheOriginalUtf8BomAndBytes()
        {
            var store = new ExecutionJobStore(root); Complete(store); string wrapped = WrapJournal();
            string path = Path.Combine(root, "journal.json");
            var bytes = new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(wrapped)).ToArray();
            File.WriteAllBytes(path, bytes); store = new ExecutionJobStore(root); Assert.IsNull(store.Error);
            Assert.IsNull(store.Admit(ExecutionJobStore.Hash("new"), "new-bom", "execute_code", 300, context, out _, out _));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(root, store.LegacyBackup)));
        }
    }
}
