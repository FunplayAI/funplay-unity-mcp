// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.State;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class ExecutionDiagnosticsTests
    {
        [Test] public void OptInDiagnosticsNeverCollectRawPathsMessagesSourceOrArguments()
        {
            Exception failure;
            try { throw new InvalidOperationException("SECRET /Users/private/token sensitive code"); }
            catch (Exception error) { failure = error; }
            var diagnostic = OperationDiagnostic.Capture("executing", failure);
            Assert.AreEqual(typeof(InvalidOperationException).FullName, (string)diagnostic["exception_type"]);
            Assert.IsTrue((bool)diagnostic["stack_is_sanitized"]); Assert.LessOrEqual(((JArray)diagnostic["stack_methods"]).Count, 12);
            foreach (var text in new[] { "SECRET", "/Users/private", "sensitive code", ".cs:" }) StringAssert.DoesNotContain(text, diagnostic.ToString());
        }
        [TestCase(true)][TestCase(false)] public void ResponseWaitFailuresNeverClaimToHaveStoppedOrRolledBackExecution(bool timeout)
        {
            var request = new MCPRequest { Id = 42, Method = "tools/call", Params = new Dictionary<string, object> {
                ["name"] = "execute_code", ["arguments"] = new Dictionary<string, object> { ["code"] = "SECRET_CODE", ["request_key"] = "owned-key" } } };
            var failure = MCPWaitFailure.Create(request, "test_layer", 60, timeout);
            var data = JObject.FromObject(failure.Error.Data);
            Assert.IsFalse((bool)data["underlying_operation_cancelled"]); Assert.IsFalse((bool)data["retry_safe"]);
            Assert.AreEqual("unknown", (string)data["execution_outcome"]); Assert.AreEqual("owned-key", (string)data["recovery"]["request_key"]);
            StringAssert.DoesNotContain("SECRET_CODE", data.ToString());
        }
        [Test] public void StartAndQueryExecutionReceiptsUseTheSameRevision()
        {
            var state = new JObject { ["job_id"] = Guid.NewGuid().ToString("N"), ["status"] = "completed", ["phase"] = "completed", ["execution_status"] = "returned", ["serialization_status"] = "completed", ["result_success"] = true };
            var query = TaskStatusService.Observe("execution", new JObject { ["success"] = true, ["data"] = state.DeepClone() });
            var start = TaskStatusService.Observe("execution", new JObject { ["success"] = true, ["data"] = new JObject { ["job_id"] = state["job_id"].DeepClone(), ["execution"] = state.DeepClone() } });
            Assert.AreEqual((string)query["data"]["task"]["revision"], (string)start["data"]["task"]["revision"]);
        }
    }
}
