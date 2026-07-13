// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using Funplay.Editor.Api.Models;
using Funplay.Editor.Tools;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class FunctionInvokerControllerTests
    {
        [Test]
        public void Invoke_RejectsMalformedTypedParameter()
        {
            var result = new FunctionInvokerController().Invoke(new FunctionCall
            {
                FunctionName = "get_hierarchy",
                Parameters = new Dictionary<string, string> { ["depth"] = "not-a-number" }
            });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"INVALID_PARAM\"", result);
            StringAssert.Contains("\"param\":\"depth\"", result);
        }

        [Test]
        public void Invoke_RejectsMissingRequiredParameter()
        {
            var result = new FunctionInvokerController().Invoke(new FunctionCall
            {
                FunctionName = "simulate_mouse_click",
                Parameters = new Dictionary<string, string> { ["y"] = "12" }
            });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"MISSING_PARAM\"", result);
            StringAssert.Contains("\"param\":\"x\"", result);
        }

        [Test]
        public void Invoke_WrapsLegacyStringSuccess()
        {
            var result = new FunctionInvokerController().Invoke(new FunctionCall
            {
                FunctionName = "get_hierarchy"
            });

            StringAssert.Contains("\"success\":true", result);
            StringAssert.Contains("\"message\":", result);
        }

        [Test]
        public void Invoke_RejectsMalformedVectorBeforeChangingSceneObjects()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var existing = new GameObject("InvokerVectorTarget_" + suffix);
            existing.transform.position = new Vector3(4f, 5f, 6f);

            try
            {
                var invoker = new FunctionInvokerController();
                var setResult = invoker.Invoke(new FunctionCall
                {
                    FunctionName = "set_transform",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = existing.name,
                        ["position"] = "1,2"
                    }
                });

                StringAssert.Contains("\"code\":\"INVALID_PARAM\"", setResult);
                Assert.AreEqual(new Vector3(4f, 5f, 6f), existing.transform.position);

                var createName = "InvokerInvalidPrimitive_" + suffix;
                var createResult = invoker.Invoke(new FunctionCall
                {
                    FunctionName = "create_primitive",
                    Parameters = new Dictionary<string, string>
                    {
                        ["primitive_type"] = "Cube",
                        ["name"] = createName,
                        ["position"] = "1,2"
                    }
                });

                StringAssert.Contains("\"code\":\"INVALID_PARAM\"", createResult);
                Assert.IsNull(GameObject.Find(createName));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }
        }

        [Test]
        public void WrapLegacyStringResult_PreservesImagesAndExistingEnvelopes()
        {
            const string image = "data:image/png;base64,AA==";
            const string envelope = "{\"success\":false,\"code\":\"EXPECTED\"}";

            Assert.AreEqual(image, FunctionInvokerController.WrapLegacyStringResult(image));
            Assert.AreEqual(envelope, FunctionInvokerController.WrapLegacyStringResult(envelope));
        }

        [Test]
        public void Invoke_ValidatesAndWrapsManualToolResults()
        {
            var toolName = "test_manual_" + Guid.NewGuid().ToString("N");
            var definition = new ToolDefinition
            {
                function = new ToolFunctionDef
                {
                    name = toolName,
                    description = "Test manual tool",
                    parameters = new ToolParametersDef
                    {
                        required = new List<string> { "value" }
                    }
                }
            };

            ToolRegistry.Register(toolName, definition, parameters => "manual:" + parameters["value"]);
            try
            {
                var invoker = new FunctionInvokerController();
                var missing = invoker.Invoke(new FunctionCall { FunctionName = toolName });
                StringAssert.Contains("\"code\":\"MISSING_PARAM\"", missing);

                var success = invoker.Invoke(new FunctionCall
                {
                    FunctionName = toolName,
                    Parameters = new Dictionary<string, string> { ["value"] = "ok" }
                });
                StringAssert.Contains("\"success\":true", success);
                StringAssert.Contains("manual:ok", success);
            }
            finally
            {
                ToolRegistry.Unregister(toolName);
            }
        }
    }
}
