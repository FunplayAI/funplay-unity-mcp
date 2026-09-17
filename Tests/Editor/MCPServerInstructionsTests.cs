// Copyright (C) Funplay. Licensed under MIT.

using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    /// <summary>
    /// The server-level `instructions` text is injected into every MCP client's context at
    /// initialize, so it must stay non-empty and keep pinning the cross-cutting disciplines a
    /// fresh client would otherwise get wrong. These assertions guard against it being emptied
    /// or losing a load-bearing convention.
    /// </summary>
    public sealed class MCPServerInstructionsTests
    {
        [Test]
        public void Text_IsNonEmpty()
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(MCPServerInstructions.Text));
            // Guard against an accidentally truncated string; the guidance is inherently multi-line.
            Assert.Greater(MCPServerInstructions.Text.Length, 200);
        }

        [Test]
        public void Text_IncludesSharedUiAutomationGuidanceOnce()
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(MCPServerInstructions.UiAutomationGuidance));
            var first = MCPServerInstructions.Text.IndexOf(
                MCPServerInstructions.UiAutomationGuidance, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(first, 0);
            Assert.AreEqual(-1, MCPServerInstructions.Text.IndexOf(
                MCPServerInstructions.UiAutomationGuidance,
                first + MCPServerInstructions.UiAutomationGuidance.Length, System.StringComparison.Ordinal));
        }

        [TestCase("success")]                 // structured envelope convention
        [TestCase("code")]                    // branch-on-code convention
        [TestCase("set_prefab_property")]     // safe prefab edit path
        [TestCase("prepare_editor")]          // durable preparation
        [TestCase("get_task")]                // shared bounded status waiting
        [TestCase("current_editor.ready")]    // current readiness, not only historical success
        [TestCase("request_key")]             // recover a lost response without replay
        [TestCase("find_method=by_id")]       // instanceId reuse
        [TestCase("group_duplicates")]        // console log ergonomics
        public void Text_MentionsCoreDiscipline(string phrase)
        {
            Assert.That(MCPServerInstructions.Text, Does.Contain(phrase));
        }

        [Test]
        public void Text_DoesNotPromiseUnsafePrefabOrTransportBehavior()
        {
            Assert.That(MCPServerInstructions.Text, Does.Not.Contain("zero Spine references"));
            Assert.That(MCPServerInstructions.Text, Does.Not.Contain("drop the connection"));
            Assert.That(MCPServerInstructions.Text, Does.Contain("persisted readback"));
            Assert.That(MCPServerInstructions.Text, Does.Contain("automatically fall back"));
        }
    }
}
