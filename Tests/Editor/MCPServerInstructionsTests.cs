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
            // Guard against an accidentally truncated string — the guidance is inherently multi-line.
            Assert.Greater(MCPServerInstructions.Text.Length, 200);
        }

        [TestCase("success")]                 // structured envelope convention
        [TestCase("code")]                    // branch-on-code convention
        [TestCase("set_prefab_property")]     // safe prefab edit path
        [TestCase("request_recompile")]       // recompile discipline
        [TestCase("exit_play_mode")]          // play-mode guard before recompile
        [TestCase("get_reload_recovery_status")] // domain-reload poll
        [TestCase("find_method=by_id")]       // instanceId reuse
        [TestCase("group_duplicates")]        // console log ergonomics
        public void Text_MentionsCoreDiscipline(string phrase)
        {
            Assert.That(MCPServerInstructions.Text, Does.Contain(phrase));
        }
    }
}
