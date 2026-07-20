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
        public void GetPrompt_UnknownName_IsHandledGracefully()
        {
            var result = NewProvider().GetPrompt("no_such_prompt", new Dictionary<string, object>());
            Assert.That((string)result["description"], Does.Contain("not found"));
        }

        [Test]
        public void GetPrompt_MissingRequiredArg_ReturnsClearMessageNotPlaceholderBody()
        {
            // edit_prefab_safely declares prefab_path as required; omitting it must produce a clear
            // message, not a workflow body full of unfilled <prefab_path> placeholders.
            var text = MessageText(NewProvider().GetPrompt("edit_prefab_safely", new Dictionary<string, object>()));
            Assert.That(text, Does.Contain("missing required argument"));
            Assert.That(text, Does.Contain("prefab_path"));
            Assert.That(text, Does.Not.Contain("<prefab_path>"));
            Assert.That(text, Does.Not.Contain("set_prefab_property")); // workflow body was NOT emitted
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
            // Default constructor wires no resource provider → text message only, no embed, no throw.
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
