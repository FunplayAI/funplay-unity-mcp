// Copyright (C) Funplay. Licensed under MIT.

using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor
{
    /// <summary>
    /// Covers <see cref="FunplayMCPClientConfigPanel.ContainsJsonComment"/>: the guard that stops the
    /// client config panel rewriting a config file whose contents its strict-JSON writer cannot
    /// represent. OpenCode's <c>opencode.json</c> and VS Code's <c>mcp.json</c> are both read with real
    /// JSONC parsers, so comments there are legal -- and rewriting such a file from a strict-JSON parse
    /// would silently drop every key past the first comment.
    /// </summary>
    public sealed class JsonConfigRewriteSafetyTests
    {
        [Test]
        public void DetectsALineComment()
        {
            Assert.IsTrue(FunplayMCPClientConfigPanel.ContainsJsonComment(
                "{\n  // my provider\n  \"mcp\": {}\n}"));
        }

        [Test]
        public void DetectsABlockComment()
        {
            Assert.IsTrue(FunplayMCPClientConfigPanel.ContainsJsonComment(
                "{\n  /* keep this */\n  \"mcp\": {}\n}"));
        }

        [Test]
        public void AcceptsPlainJson()
        {
            Assert.IsFalse(FunplayMCPClientConfigPanel.ContainsJsonComment(
                "{\"mcp\":{\"funplay-x\":{\"url\":\"http://127.0.0.1:8675/\"}}}"));
        }

        [Test]
        public void DoesNotMistakeSlashesInsideStringsForComments()
        {
            // A url is the single most common value in these files, and every one of them carries "//".
            Assert.IsFalse(FunplayMCPClientConfigPanel.ContainsJsonComment(
                "{\"url\":\"https://example.com/a\",\"note\":\"/* not a comment */\"}"));
        }

        [Test]
        public void DoesNotTreatAnEscapedQuoteAsClosingTheString()
        {
            // An escaped quote mid-string used to end the string early, turning the rest of a legitimate
            // value into "outside a string" and any "//" in it into a false positive.
            Assert.IsFalse(FunplayMCPClientConfigPanel.ContainsJsonComment(
                "{\"note\":\"a \\\" quote then https://x/y\"}"));
        }
    }
}
