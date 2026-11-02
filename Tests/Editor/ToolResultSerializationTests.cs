// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections;
using System.Collections.Generic;
using Funplay.Editor.Tools;
using Funplay.Editor.Tools.Builtins;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Funplay.Editor.Tests
{
    public sealed class ToolResultSerializationTests
    {
        private sealed class Row { public Vector3 value; }
        private sealed class Cycle { public Cycle next; public override string ToString() => throw new Exception("Never stringify a failed result."); }

        [Test] public void UnityValuesRemainStructuralInsideArraysDictionariesAndAnonymousObjects()
        {
            var result = JObject.Parse(FunctionInvokerController.SerializeResult(Response.Success("ok", new {
                rows = new[] { new Row { value = Vector3.zero }, new Row { value = new Vector3(1, 2, 3) } },
                map = new Dictionary<string, object> { ["color"] = new Color(.1f, .2f, .3f, .4f), ["q"] = Quaternion.identity },
                scalar = 17, nil = (object)null
            })));
            Assert.IsTrue((bool)result["success"]);
            Assert.AreEqual(3, (int)result["data"]["rows"][1]["value"]["z"]);
            Assert.AreEqual(3, ((JObject)result["data"]["rows"][0]["value"]).Count);
            Assert.AreEqual(.4f, (float)result["data"]["map"]["color"]["a"], .0001f);
            Assert.AreEqual(1, (int)result["data"]["map"]["q"]["w"]);
            Assert.AreEqual(JTokenType.Null, result["data"]["nil"].Type);
        }

        [Test] public void ExtendedValueTypesHaveFiniteExplicitContracts()
        {
            foreach (var value in new object[] { Vector2.one, Vector4.one, Vector2Int.one, Vector3Int.one,
                new Color32(1, 2, 3, 255), new Rect(1, 2, 3, 4), new RectInt(1, 2, 3, 4),
                new Bounds(Vector3.one, Vector3.one), new BoundsInt(Vector3Int.zero, Vector3Int.one),
                new Ray(Vector3.zero, Vector3.forward), new Ray2D(Vector2.zero, Vector2.up),
                new Plane(Vector3.up, 2), new Pose(Vector3.one, Quaternion.identity), Matrix4x4.identity })
            {
                var result = JObject.Parse(FunctionInvokerController.SerializeResult(Response.Success("ok", value)));
                Assert.IsTrue((bool)result["success"], value.GetType().Name);
                Assert.Less(result.ToString().Length, 1500);
            }
        }

        [Test] public void CyclesFailExplicitlyWithoutCallingToStringOrClaimingAnUnexecutedCommand()
        {
            var value = new Cycle(); value.next = value;
            var trace = new ScriptExecutionTrace { Executed = true, Recovery = new { modified = new[] { "12" } } };
            var result = JObject.Parse(FunctionInvokerController.SerializeResult(
                ExecutionResultEnvelope.Wrap(Response.Success("ran", new { returnValue = value }), trace), "execute_code"));
            Assert.IsFalse((bool)result["success"]);
            Assert.AreEqual("RESULT_SERIALIZATION_FAILED", (string)result["code"]);
            Assert.IsTrue((bool)result["data"]["executed"]);
            Assert.IsFalse((bool)result["data"]["retry_safe"]);
            Assert.AreEqual(trace.Id, (string)result["data"]["execution_id"]);
            Assert.AreEqual("12", (string)result["data"]["recovery"]["modified"][0]);
        }

        [Test] public void UnknownToolExecutionIsNotFalselyReportedAsNotExecuted()
        {
            var value = new Cycle(); value.next = value;
            var result = JObject.Parse(FunctionInvokerController.SerializeResult(value, "custom"));
            Assert.AreEqual(JTokenType.Null, result["data"]["executed"].Type);
            Assert.IsFalse((bool)result["data"]["retry_safe"]);
        }

        [TestCase(float.NaN)][TestCase(float.PositiveInfinity)][TestCase(float.NegativeInfinity)]
        public void NonFiniteUnityNumbersFailInsteadOfProducingInvalidJson(float number)
        {
            var result = JObject.Parse(FunctionInvokerController.SerializeResult(Response.Success("ok", new Vector3(number, 0, 0))));
            Assert.AreEqual("RESULT_SERIALIZATION_FAILED", (string)result["code"]);
        }

        [Test] public void DeepAndOversizedGraphsFailWithABoundedReceipt()
        {
            object value = "leaf";
            for (int i = 0; i < 100; i++) value = new[] { value };
            var deep = JObject.Parse(ToolResultSerializer.Serialize(value));
            Assert.AreEqual("RESULT_SERIALIZATION_FAILED", (string)deep["code"]);
            var huge = ToolResultSerializer.Serialize(new { text = new string('界', ToolResultSerializer.MaxBytes / 3 + 1) });
            Assert.AreEqual("RESULT_TOO_LARGE", (string)JObject.Parse(huge)["code"]);
            Assert.Less(huge.Length, 2000);
        }
        [Test] public void NullableAndJTokenNonFiniteValuesCannotBypassTheFiniteWriter()
        {
            foreach (var value in new object[] { new { scalar = (double?)double.NaN }, new JObject { ["bad"] = new JValue(double.PositiveInfinity) },
                new JRaw("NaN"), new JRaw("{\"bypass\":Infinity}"), JValue.CreateUndefined(), new JConstructor("Date", 1), JValue.CreateComment("not-json") })
                Assert.AreEqual("RESULT_SERIALIZATION_FAILED", (string)JObject.Parse(FunctionInvokerController.SerializeResult(Response.Success("bad", value)))["code"], value.GetType().FullName);
        }

        [Test] public void UnityObjectsReturnIdentityNotTheirSceneOrAssetGraphs()
        {
            var go = new GameObject("SerializerReference");
            try
            {
                var result = JObject.Parse(ToolResultSerializer.Serialize(go));
                Assert.AreEqual(ObjectIdHelper.GetSerializableId(go), (string)result["object_id"]);
                Assert.AreEqual("UnityEngine.GameObject", (string)result["type"]);
                Assert.AreEqual(3, result.Count);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [UnityTest] public IEnumerator ExecuteCodeNestedVectorReachesTheRealInvokerResponse()
        {
            var call = new FunctionCall { FunctionName = "execute_code", Parameters = new Dictionary<string, string> {
                ["skip_refresh"] = "true", ["code"] = "public class ReturnVectors : IFunplayCommand { public void Execute(ExecutionContext ctx) { ctx.ReturnValue = new { rows = new[] { new { value = UnityEngine.Vector3.zero } } }; } }" } };
            var task = new FunctionInvokerController().InvokeAsync(call);
            while (!task.IsCompleted) yield return null;
            var result = JObject.Parse(task.GetAwaiter().GetResult());
            if (result["data"]?["job_id"] is JValue job)
            {
                var finish = ExecutionTestCompletion.Finish(System.Threading.Tasks.Task.FromResult((object)result));
                while (!finish.IsCompleted) yield return null;
                result = (JObject)finish.GetAwaiter().GetResult();
            }
            Assert.IsTrue((bool)result["success"], result.ToString());
            Assert.IsTrue((bool)result["executed"]);
            Assert.AreEqual(0, (int)result["data"]["returnValue"]["rows"][0]["value"]["z"]);
        }
    }
}
