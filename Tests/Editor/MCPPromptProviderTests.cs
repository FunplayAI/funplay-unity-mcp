// Copyright (C) Funplay. Licensed under MIT.

using System.Collections.Generic;
using System.Linq;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    /// <summary>
    /// Prompts must be real parameterized workflows: declared arguments, and a GetPrompt body that
    /// interpolates the caller's arguments into an ordered sequence of ACTUAL tool names (not a
    /// canned restatement of the description). These tests pin that contract.
    /// </summary>
    public sealed class MCPPromptProviderTests
    {
        private static MCPPromptProvider NewProvider() => new MCPPromptProvider("TestProj", "/tmp/proj");

        [Test]
        public void ListPrompts_ExposesExpectedPromptsWithArguments()
        {
            var prompts = NewProvider().ListPrompts();
            var names = prompts.Select(p => (string)p["name"]).ToList();

            CollectionAssert.AreEquivalent(
                new[] { "edit_prefab_safely", "verify_compilation", "enter_play_and_recover", "wire_serialized_references", "create_playable_prototype" },
                names);

            // Every prompt declares a non-null arguments list; the required ones are flagged.
            foreach (var p in prompts)
                Assert.IsNotNull(p["arguments"] as List<object>, $"{p["name"]} missing arguments list");

            Assert.IsTrue(RequiredArg(prompts, "edit_prefab_safely", "prefab_path"));
            Assert.IsTrue(RequiredArg(prompts, "wire_serialized_references", "target"));
            Assert.IsTrue(RequiredArg(prompts, "create_playable_prototype", "idea"));
        }

        [Test]
        public void GetPrompt_InterpolatesArgumentsAndNamesRealTools()
        {
            var text = MessageText(NewProvider().GetPrompt("edit_prefab_safely", new Dictionary<string, object>
            {
                ["prefab_path"] = "Assets/Prefabs/UIThing.prefab",
                ["change_description"] = "set title localization id"
            }));

            Assert.That(text, Does.Contain("Assets/Prefabs/UIThing.prefab"));   // arg interpolated
            Assert.That(text, Does.Contain("set title localization id"));       // arg interpolated
            Assert.That(text, Does.Contain("set_prefab_property"));             // real tool
            Assert.That(text, Does.Contain("open_prefab_stage"));               // structural path tool
        }

        [Test]
        public void GetPrompt_VerifyCompilation_HasPlayModeAndRecompileSequence()
        {
            var text = MessageText(NewProvider().GetPrompt("verify_compilation", new Dictionary<string, object>()));
            Assert.That(text, Does.Contain("exit_play_mode"));
            Assert.That(text, Does.Contain("request_recompile"));
            Assert.That(text, Does.Contain("wait_for_compilation"));
            Assert.That(text, Does.Contain("get_compilation_errors"));
        }

        [Test]
        public void GetPrompt_OmitsOptionalArgClauseWhenAbsent()
        {
            // With no validation_goal, the body must not contain a dangling "<validation_goal>" placeholder.
            var text = MessageText(NewProvider().GetPrompt("enter_play_and_recover", new Dictionary<string, object>()));
            Assert.That(text, Does.Contain("enter_play_mode"));
            Assert.That(text, Does.Contain("get_reload_recovery_status"));
            Assert.That(text, Does.Not.Contain("<validation_goal>"));
        }

        [Test]
        public void TryGetPrompt_UnknownName_ReturnsInvalidParamsDetail()
        {
            var ok = NewProvider().TryGetPrompt(
                "no_such_prompt",
                new Dictionary<string, object>(),
                out var result,
                out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(result);
            Assert.That(error.Message, Does.Contain("not found"));
        }

        [Test]
        public void TryGetPrompt_MissingRequiredArg_ReturnsClearErrorWithoutWorkflow()
        {
            var ok = NewProvider().TryGetPrompt(
                "edit_prefab_safely",
                new Dictionary<string, object>(),
                out var result,
                out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(result);
            Assert.That(error.Message, Does.Contain("missing required argument"));
            Assert.That(error.Message, Does.Contain("prefab_path"));
        }

        [Test]
        public void TryGetPrompt_RejectsUnknownAndNonStringArguments()
        {
            var provider = NewProvider();

            Assert.IsFalse(provider.TryGetPrompt(
                "verify_compilation",
                new Dictionary<string, object> { ["unexpected"] = "x" },
                out _,
                out var unknownError));
            Assert.That(unknownError.Message, Does.Contain("unknown argument"));

            Assert.IsFalse(provider.TryGetPrompt(
                "verify_compilation",
                new Dictionary<string, object> { ["touched_paths"] = 42 },
                out _,
                out var typeError));
            Assert.That(typeError.Message, Does.Contain("must be strings"));
        }

        [Test]
        public void GetPrompt_WireReferences_ResolvesComponentInstanceIdBeforeMutation()
        {
            var text = MessageText(NewProvider().GetPrompt("wire_serialized_references", new Dictionary<string, object>
            {
                ["target"] = "Canvas/Hud"
            }));

            Assert.That(text, Does.Contain("list_components"));
            Assert.That(text, Does.Contain("GameObject instanceId"));
            Assert.That(text, Does.Contain("component instanceId"));
            Assert.That(text, Does.Contain("Do not pass the GameObject ID as a component ID"));
        }

        [Test]
        public void GetPrompt_CreatePrototype_UsesOnlyExposedPlayRecoverySteps()
        {
            var text = MessageText(NewProvider().GetPrompt("create_playable_prototype", new Dictionary<string, object>
            {
                ["idea"] = "move a cube"
            }));

            Assert.That(text, Does.Contain("request_recompile"));
            Assert.That(text, Does.Contain("wait_for_compilation"));
            Assert.That(text, Does.Contain("enter_play_mode"));
            Assert.That(text, Does.Contain("get_reload_recovery_status"));
            Assert.That(text, Does.Contain("exit_play_mode"));
            Assert.That(text, Does.Not.Contain("enter_play_and_recover"));
        }

        [TestCase(null, "Invalid params: 'name' is required")]
        [TestCase("no_such_prompt", "not found")]
        [TestCase("edit_prefab_safely", "missing required argument")]
        public void HandlePromptsGet_InvalidPromptRequest_ReturnsJsonRpcInvalidParams(
            string promptName,
            string expectedMessage)
        {
            var parameters = new Dictionary<string, object>();
            if (promptName != null)
                parameters["name"] = promptName;

            var response = MCPRequestHandler.HandlePromptsGet(
                new MCPRequest { Id = 7, Params = parameters },
                NewProvider());

            Assert.IsNull(response.Result);
            Assert.IsNotNull(response.Error);
            Assert.AreEqual(-32602, response.Error.Code);
            Assert.That(response.Error.Message, Does.Contain(expectedMessage));
        }

        [Test]
        public void HandlePromptsGet_NonObjectArguments_ReturnsJsonRpcInvalidParams()
        {
            var response = MCPRequestHandler.HandlePromptsGet(
                new MCPRequest
                {
                    Id = 8,
                    Params = new Dictionary<string, object>
                    {
                        ["name"] = "verify_compilation",
                        ["arguments"] = "not-an-object"
                    }
                },
                NewProvider());

            Assert.IsNull(response.Result);
            Assert.AreEqual(-32602, response.Error.Code);
            Assert.That(response.Error.Message, Does.Contain("must be an object"));
        }

        [Test]
        public void HandlePromptsGet_ErrorData_SerializesAsJsonObject()
        {
            var response = MCPRequestHandler.HandlePromptsGet(
                new MCPRequest
                {
                    Id = 10,
                    Params = new Dictionary<string, object> { ["name"] = "no_such_prompt" }
                },
                NewProvider());

            Assert.IsInstanceOf<Dictionary<string, object>>(response.Error.Data);
            var serialized = SimpleJsonHelper.Serialize(response.Error.Data);
            Assert.That(serialized, Does.StartWith("{"));
            var parsed = SimpleJsonHelper.Deserialize(serialized) as Dictionary<string, object>;
            Assert.IsNotNull(parsed);
            Assert.AreEqual("no_such_prompt", parsed["prompt"]);
        }

        [Test]
        public void GetPrompt_EmbedsLiveResourceWhenProviderWired()
        {
            var res = new MCPResourceProvider(null, null, null);
            try
            {
                var provider = new MCPPromptProvider("TestProj", "/tmp/proj", res);
                var messages = (List<object>)provider.GetPrompt("verify_compilation", new Dictionary<string, object>())["messages"];

                Assert.GreaterOrEqual(messages.Count, 2, "verify_compilation should append the compilation-errors resource");
                var embedded = (Dictionary<string, object>)messages[1];
                var content = (Dictionary<string, object>)embedded["content"];
                Assert.AreEqual("resource", content["type"]);
                var resource = (Dictionary<string, object>)content["resource"];
                Assert.AreEqual("unity://errors/compilation", resource["uri"]);
            }
            finally
            {
                res.Dispose();
            }
        }

        [Test]
        public void GetPrompt_NoEmbedWhenProviderMissing()
        {
            // Default constructor wires no resource provider: text message only, no embed, no throw.
            var messages = (List<object>)NewProvider().GetPrompt("verify_compilation", new Dictionary<string, object>())["messages"];
            Assert.AreEqual(1, messages.Count);
        }

        private static bool RequiredArg(List<Dictionary<string, object>> prompts, string prompt, string argName)
        {
            var p = prompts.First(x => (string)x["name"] == prompt);
            var args = (List<object>)p["arguments"];
            var arg = args.Cast<Dictionary<string, object>>().First(a => (string)a["name"] == argName);
            return (bool)arg["required"];
        }

        private static string MessageText(Dictionary<string, object> promptResult)
        {
            var messages = (List<object>)promptResult["messages"];
            var first = (Dictionary<string, object>)messages[0];
            var content = (Dictionary<string, object>)first["content"];
            return (string)content["text"];
        }
    }
}
