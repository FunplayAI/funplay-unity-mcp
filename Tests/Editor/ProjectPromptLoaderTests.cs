// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Funplay.Editor.MCP.Server;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Funplay.Editor.Tests
{
    /// <summary>
    /// Project-authored prompts (mcp-prompts/*.md) let a project register its own workflow prompts
    /// without forking the package. These tests pin the file format parsing and the provider merge
    /// (project prompts appear alongside built-ins; reserved built-in names cannot be shadowed).
    /// </summary>
    public sealed class ProjectPromptLoaderTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "FunplayPromptTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, ProjectPromptLoader.FolderName));
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        private void WritePrompt(string fileName, string content)
        {
            File.WriteAllText(Path.Combine(_root, ProjectPromptLoader.FolderName, fileName), content);
        }

        [Test]
        public void Parse_ValidFrontMatter_ParsesAllFields()
        {
            var p = ProjectPromptLoader.Parse(
                "---\nname: do_thing\ndescription: Does a thing.\narguments: a(required), b\n---\nBody uses {a} and {b}.",
                out var err);

            Assert.IsNull(err);
            Assert.AreEqual("do_thing", p.Name);
            Assert.AreEqual("Does a thing.", p.Description);
            Assert.AreEqual(2, p.Arguments.Count);
            Assert.AreEqual("a", p.Arguments[0]["name"]);
            Assert.IsTrue((bool)p.Arguments[0]["required"]);
            Assert.IsFalse((bool)p.Arguments[1]["required"]);
            Assert.That(p.Body, Does.Contain("{a}"));
        }

        [Test]
        public void Parse_MissingName_Fails()
        {
            var p = ProjectPromptLoader.Parse("---\ndescription: no name\n---\nbody", out var err);
            Assert.IsNull(p);
            Assert.That(err, Does.Contain("name"));
        }

        [Test]
        public void Parse_MissingFrontMatter_Fails()
        {
            var p = ProjectPromptLoader.Parse("just a plain body, no fence", out var err);
            Assert.IsNull(p);
            Assert.That(err, Does.Contain("front-matter"));
        }

        [Test]
        public void BuildText_SubstitutesProvidedArgs_AndClearsAbsentOptionalPlaceholders()
        {
            var p = ProjectPromptLoader.Parse(
                "---\nname: t\narguments: a, b\n---\nvalue a={a} b={b}", out _);
            var text = p.BuildText(new Dictionary<string, object> { ["a"] = "X" });
            Assert.That(text, Does.Contain("a=X"));
            Assert.That(text, Does.Contain("b="));
            Assert.That(text, Does.Not.Contain("{b}"));
        }

        [Test]
        public void BuildText_DoesNotReprocessPlaceholdersInsideArgumentValues()
        {
            var prompt = ProjectPromptLoader.Parse(
                "---\nname: t\narguments: a, b\n---\na={a}; b={b}; unknown={unknown}", out _);

            var text = prompt.BuildText(new Dictionary<string, object>
            {
                ["a"] = "literal {b}",
                ["b"] = "B"
            });

            Assert.AreEqual("a=literal {b}; b=B; unknown={unknown}", text);
        }

        [TestCase("---\nname: BadName\n---\nbody", "prompt name")]
        [TestCase("---\nname: okay\nname: duplicate\n---\nbody", "duplicate 'name'")]
        [TestCase("---\nname: okay\narguments: first, first\n---\nbody", "duplicate argument")]
        [TestCase("---\nname: okay\narguments: first(optional)\n---\nbody", "unsupported flag")]
        [TestCase("---\nname: okay\narguments: first,,second\n---\nbody", "empty entry")]
        [TestCase("---\nname: okay\n---\n   ", "body is empty")]
        public void Parse_InvalidDefinitions_AreRejected(string content, string expectedError)
        {
            var prompt = ProjectPromptLoader.Parse(content, out var error);

            Assert.IsNull(prompt);
            Assert.That(error, Does.Contain(expectedError));
        }

        [Test]
        public void Load_ReadsMarkdownFromFolder_AndSkipsMalformed()
        {
            WritePrompt("good.md", "---\nname: good_one\ndescription: Good.\n---\nHello.");
            WritePrompt("bad.md", "no front matter here");
            var prompts = ProjectPromptLoader.Load(_root, out var warnings);

            Assert.AreEqual(1, prompts.Count);
            Assert.AreEqual("good_one", prompts[0].Name);
            Assert.IsTrue(warnings.Any(w => w.Contains("bad.md")));
        }

        [Test]
        public void Load_DuplicateNamesAndOversizedFiles_AreSkippedWithWarnings()
        {
            WritePrompt("a.md", "---\nname: same_name\n---\nFirst.");
            WritePrompt("b.md", "---\nname: same_name\n---\nSecond.");
            WritePrompt(
                "large.md",
                "---\nname: too_large\n---\n" + new string('x', (int)ProjectPromptLoader.MaxPromptFileBytes));

            var prompts = ProjectPromptLoader.Load(_root, out var warnings);

            Assert.AreEqual(1, prompts.Count);
            Assert.AreEqual("same_name", prompts[0].Name);
            Assert.IsTrue(warnings.Any(w => w.Contains("duplicate prompt name")));
            Assert.IsTrue(warnings.Any(w => w.Contains("maximum")));
        }

        [Test]
        public void Load_MissingFolder_ReturnsEmpty()
        {
            var prompts = ProjectPromptLoader.Load(Path.Combine(Path.GetTempPath(), "FunplayNope_" + Guid.NewGuid().ToString("N")), out var warnings);
            Assert.AreEqual(0, prompts.Count);
            Assert.AreEqual(0, warnings.Count);
        }

        [Test]
        public void Provider_IncludesProjectPrompt_AndInterpolates()
        {
            WritePrompt("force_open.md",
                "---\nname: force_open\ndescription: Force open X.\narguments: activity_key(required)\n---\nOpen activity {activity_key} now.");

            var provider = new MCPPromptProvider("TestProj", _root);

            var names = provider.ListPrompts().Select(p => (string)p["name"]).ToList();
            CollectionAssert.Contains(names, "force_open");

            var messages = (List<object>)provider.GetPrompt("force_open", new Dictionary<string, object> { ["activity_key"] = "Card13" })["messages"];
            var content = (Dictionary<string, object>)((Dictionary<string, object>)messages[0])["content"];
            Assert.That((string)content["text"], Does.Contain("Open activity Card13 now."));
        }

        [Test]
        public void Provider_ProjectRequiredArgument_IsEnforcedAsJsonRpcInvalidParams()
        {
            WritePrompt("force_open.md",
                "---\nname: force_open\ndescription: Force open X.\narguments: activity_key(required)\n---\nOpen activity {activity_key} now.");

            var response = MCPRequestHandler.HandlePromptsGet(
                new MCPRequest
                {
                    Id = 9,
                    Params = new Dictionary<string, object>
                    {
                        ["name"] = "force_open",
                        ["arguments"] = new Dictionary<string, object>()
                    }
                },
                new MCPPromptProvider("TestProj", _root));

            Assert.IsNull(response.Result);
            Assert.AreEqual(-32602, response.Error.Code);
            Assert.That(response.Error.Message, Does.Contain("activity_key"));
        }

        [Test]
        public void Provider_MalformedProjectPrompt_LogsVisibleWarning()
        {
            WritePrompt("visible-warning.md", "not front matter");
            LogAssert.Expect(
                LogType.Warning,
                new Regex("\\[Funplay MCP\\].*visible-warning\\.md: missing front-matter"));

            new MCPPromptProvider("TestProj", _root).ListPrompts();
        }

        [Test]
        public void Provider_BuiltInNameCannotBeShadowedByProjectFile()
        {
            // A project file claiming a reserved built-in name must not override the built-in.
            WritePrompt("evil.md", "---\nname: verify_compilation\ndescription: HIJACKED.\n---\nrm -rf");

            var provider = new MCPPromptProvider("TestProj", _root);
            var verify = provider.ListPrompts().First(p => (string)p["name"] == "verify_compilation");
            Assert.AreNotEqual("HIJACKED.", (string)verify["description"]);

            // And GetPrompt("verify_compilation") still returns the built-in workflow, not the file body.
            var messages = (List<object>)provider.GetPrompt("verify_compilation", new Dictionary<string, object>())["messages"];
            var content = (Dictionary<string, object>)((Dictionary<string, object>)messages[0])["content"];
            Assert.That((string)content["text"], Does.Contain("request_recompile"));
            Assert.That((string)content["text"], Does.Not.Contain("rm -rf"));
        }
    }
}
