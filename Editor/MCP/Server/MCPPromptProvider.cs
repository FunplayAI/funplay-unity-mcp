// Copyright (C) Funplay. Licensed under MIT.

using System.Collections.Generic;
using System.Linq;
using Funplay.Editor.Settings;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// MCP prompts: parameterized workflow templates a client can select. Unlike a canned
    /// one-liner, each prompt declares real `arguments` and, in GetPrompt, expands into an
    /// ordered sequence of ACTUAL tool calls with the caller's arguments interpolated plus a
    /// Guidance section on the pitfalls that workflow hits. (MCP prompt `arguments` are not
    /// JSON Schema — just [{name, description, required}] with string values.)
    /// </summary>
    internal class MCPPromptProvider
    {
        private readonly string _projectName;
        private readonly string _projectPath;
        private readonly MCPResourceProvider _resourceProvider;

        // Declarative spec for a built-in prompt: name, the arguments it declares, and the live
        // resource it embeds. Single source of truth for ListPrompts (names/args), GetPrompt
        // (required-argument enforcement + which resource to embed), and the reserved-name set.
        // Each prompt's bespoke body/description TEXT is still built per-name in GetPrompt.
        private sealed class PromptSpec
        {
            public string Name;
            public string ListDescription;
            public Dictionary<string, object>[] Args;
            public string EmbedResourceUri; // null = embed nothing
        }

        private static readonly PromptSpec[] BuiltInSpecs =
        {
            new PromptSpec
            {
                Name = "edit_prefab_safely",
                ListDescription = "Change a prefab's contents without corrupting unrelated fields (single-field vs structural paths).",
                Args = new[]
                {
                    Arg("prefab_path", "Path to the .prefab asset (e.g. 'Assets/Prefabs/UIFoo.prefab').", true),
                    Arg("change_description", "What you intend to change (used to pick the single-field vs structural path).", false)
                }
            },
            new PromptSpec
            {
                Name = "verify_compilation",
                ListDescription = "Confirm scripts compile cleanly after external edits (Play Mode + domain-reload aware).",
                Args = new[] { Arg("touched_paths", "Comma-separated .cs/.asmdef paths you edited (context only).", false) },
                EmbedResourceUri = "unity://errors/compilation"
            },
            new PromptSpec
            {
                Name = "enter_play_and_recover",
                ListDescription = "Enter Play Mode and survive the domain reload before validating a runtime change.",
                Args = new[] { Arg("validation_goal", "What you want to observe/verify in Play Mode.", false) }
            },
            new PromptSpec
            {
                Name = "wire_serialized_references",
                ListDescription = "Fill missing serialized Object references on a component, then confirm the wired state.",
                Args = new[]
                {
                    Arg("target", "GameObject identifier (name, hierarchy path, or instanceId).", true),
                    Arg("component_type", "Component type name whose references you are wiring (e.g. 'Image').", false)
                },
                EmbedResourceUri = "unity://selection/current"
            },
            new PromptSpec
            {
                Name = "create_playable_prototype",
                ListDescription = "Build a minimal playable Unity prototype from a short idea and validate it in Play Mode.",
                Args = new[] { Arg("idea", "One-line description of the prototype to build.", true) },
                EmbedResourceUri = "unity://scene/active"
            }
        };

        // Reserved names derived from the specs: a project mcp-prompts/*.md file cannot shadow them.
        private static readonly HashSet<string> BuiltInNames =
            new HashSet<string>(BuiltInSpecs.Select(s => s.Name), System.StringComparer.Ordinal);

        private List<ProjectPromptLoader.ProjectPrompt> _projectPrompts;

        public MCPPromptProvider(string projectName, string projectPath, MCPResourceProvider resourceProvider = null)
        {
            _projectName = string.IsNullOrEmpty(projectName) ? "Unity Project" : projectName;
            _projectPath = string.IsNullOrEmpty(projectPath) ? "Unknown" : projectPath;
            _resourceProvider = resourceProvider;
        }

        // Project-authored prompts from <projectRoot>/mcp-prompts/*.md, loaded once per provider
        // instance. NOTE: editing a .md file does NOT trigger a Unity recompile, so changes are
        // picked up only after a domain reload (any script recompile rebuilds this provider) or a
        // server restart — not on the .md save itself. Parse/skip warnings are surfaced to the log
        // so a malformed file that silently fails to appear is diagnosable.
        private List<ProjectPromptLoader.ProjectPrompt> ProjectPrompts()
        {
            if (_projectPrompts == null)
            {
                _projectPrompts = ProjectPromptLoader.Load(_projectPath, out var warnings);
                foreach (var w in warnings)
                    PluginDebugLogger.Log("[Funplay MCP] mcp-prompts warning: " + w);
            }
            return _projectPrompts;
        }

        public List<Dictionary<string, object>> ListPrompts()
        {
            var prompts = BuiltInSpecs
                .Select(s => CreatePrompt(s.Name, s.ListDescription, s.Args))
                .ToList();

            // Append project-authored prompts (mcp-prompts/*.md); reserved built-in names win.
            foreach (var p in ProjectPrompts())
            {
                if (BuiltInNames.Contains(p.Name))
                    continue;
                prompts.Add(CreatePrompt(p.Name, p.Description, p.Arguments.ToArray()));
            }

            return prompts;
        }

        public Dictionary<string, object> GetPrompt(string name, Dictionary<string, object> arguments)
        {
            var spec = BuiltInSpecs.FirstOrDefault(s => s.Name == name);

            // Enforce declared-required arguments for built-in prompts: a missing one yields a clear
            // message instead of a workflow body full of unfilled <placeholder> tokens.
            if (spec != null)
            {
                var missing = spec.Args
                    .Where(a => (bool)a["required"] && ArgValue(arguments, (string)a["name"], null) == null)
                    .Select(a => (string)a["name"])
                    .ToList();
                if (missing.Count > 0)
                    return TextPrompt($"Prompt '{name}' is missing required argument(s): {string.Join(", ", missing)}.");
            }

            string description;
            string body;

            switch (name)
            {
                case "edit_prefab_safely":
                {
                    var path = ArgValue(arguments, "prefab_path", "<prefab_path>");
                    var change = ArgValue(arguments, "change_description", null);
                    description = $"Apply the intended change to prefab '{path}' without collateral corruption.";
                    body =
$@"Goal: {(change != null ? $"apply \"{change}\" to" : "edit")} prefab '{path}' while changing ONLY the fields you intend.

Single field (the common case):
1. set_prefab_property(prefab_path='{path}', game_object_path=<'/'-separated child path, empty = root>, component=<type>, property=<field>, value=<json>).
   Several fields on one component: set_prefab_properties with a JSON object.
   This edits the asset in place (no prefab stage), so only your field changes.

Structural edit (add/reparent components, multiple objects):
1. open_prefab_stage('{path}')
2. mutate the stage contents via set_component_property(ies) / add_component / execute_code
3. save_prefab_stage, then close_prefab_stage

Guidance:
- Never hand-edit the .prefab as text while the Editor is open — it overwrites from its in-memory copy.
- Do NOT use open_prefab_stage + save_prefab_stage for a single-field edit on a prefab with layout groups / ContentSizeFitter / TMP auto-size / Spine: the wholesale re-serialize freezes recomputed RectTransform/font values and can zero Spine skeletonDataAsset. save_prefab_stage warns when it detects these — prefer set_prefab_property.
- After any stage save, git-diff the .prefab to confirm only intended fields changed.";
                    break;
                }

                case "verify_compilation":
                {
                    var paths = ArgValue(arguments, "touched_paths", null);
                    description = "Recompile and confirm no errors after editing scripts outside Unity.";
                    body =
$@"Goal: confirm scripts compile cleanly{(paths != null ? $" after editing: {paths}" : " after external edits")}.

1. If in Play Mode, exit_play_mode first — request_recompile is rejected in Play Mode.
2. request_recompile
3. wait_for_compilation
4. get_compilation_errors — if errors remain, patch the smallest safe regions and repeat from step 2.
5. get_console_logs(group_duplicates=true) to catch load-time/initialization errors the compile check does not surface.

Guidance: a domain reload can interrupt the request — if a call is interrupted, poll get_reload_recovery_status for the real outcome before assuming failure.";
                    break;
                }

                case "enter_play_and_recover":
                {
                    var goal = ArgValue(arguments, "validation_goal", null);
                    description = "Enter Play Mode safely and validate a runtime change.";
                    body =
$@"Goal: reach a stable Play Mode to validate{(goal != null ? $": {goal}" : " a runtime change")}.

1. If already playing, exit_play_mode first for a clean start.
2. enter_play_mode
3. The HTTP server briefly drops during the domain reload — poll get_reload_recovery_status until it reports ready BEFORE issuing the next tool call.
4. Run the validation (inspect objects, read get_console_logs(group_duplicates=true)), then exit_play_mode when done.

Guidance: treat any result between enter_play_mode and a confirmed recovery as unknown — confirm via get_reload_recovery_status plus a readback, don't assume the tool that got interrupted failed or succeeded.";
                    break;
                }

                case "wire_serialized_references":
                {
                    var target = ArgValue(arguments, "target", "<target>");
                    var comp = ArgValue(arguments, "component_type", null);
                    description = $"Fill missing serialized references on '{target}'.";
                    body =
$@"Goal: fill missing serialized Object references on '{target}'{(comp != null ? $" ({comp})" : "")}.

1. Resolve the object: find_game_objects('{target}') or get_selection; note its instanceId.
2. get_component_properties(component_instance_id=<id>) to see which reference fields are null.
3. set_component_property / set_component_properties, passing each reference as {{""fileID"": <instanceId>}} or {{""assetPath"": ""Assets/...""}}.
4. The success response echoes the post-write value ('newValue'/'applied') — confirm each wired field is now non-null.

Guidance: treat a user-supplied name as a hint, not a path; inspect before mutating. Carry the returned instanceId into follow-up calls (find_method=by_id) instead of re-resolving by name.";
                    break;
                }

                case "create_playable_prototype":
                {
                    var idea = ArgValue(arguments, "idea", "<idea>");
                    description = $"Build a minimal playable prototype: {idea}.";
                    body =
$@"Goal: build a minimal playable prototype from: {idea}.

1. Build the environment (create_primitive / create_game_object) and ensure a camera exists.
2. Add input + control: create_script for behaviour, then request_recompile and get_compilation_errors before using the new types.
3. Add minimal helper UI if needed (create_canvas / create_text / create_button).
4. enter_play_and_recover to validate, then iterate.

Guidance: keep the scene minimal; save only the assets you intentionally create and read them back to confirm.";
                    break;
                }

                default:
                {
                    // Not a built-in (built-in names match the cases above), so any name here is a
                    // candidate project-authored prompt (mcp-prompts/*.md).
                    var proj = ProjectPrompts().FirstOrDefault(p => p.Name == name);
                    if (proj != null)
                    {
                        description = proj.Description;
                        body = proj.BuildText(arguments);
                    }
                    else
                    {
                        description = "Prompt not found: " + name;
                        body = description;
                    }
                    break;
                }
            }

            var header = $"Unity project: {_projectName} ({_projectPath}).\n\n";

            var messages = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = header + body
                    }
                }
            };

            // Embed the live read-only resource this prompt's spec declares, so the model sees the
            // current state inline instead of spending a round-trip to fetch it. Declared once in
            // BuiltInSpecs (not a second switch), so it can't drift from the prompt definition.
            // Best-effort: silently skipped when no resource provider is wired or the read fails.
            if (spec != null && !string.IsNullOrEmpty(spec.EmbedResourceUri))
                TryAppendResource(messages, spec.EmbedResourceUri);

            return new Dictionary<string, object>
            {
                ["description"] = description,
                ["messages"] = messages
            };
        }

        // A minimal single-text-message prompt result (used for missing-required-argument and
        // not-found responses), matching the shape of a normal prompts/get result.
        private static Dictionary<string, object> TextPrompt(string message)
        {
            return new Dictionary<string, object>
            {
                ["description"] = message,
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = message
                        }
                    }
                }
            };
        }

        // Read a resource and append it as an embedded-resource message. Any failure is swallowed:
        // the prompt's workflow text already tells the model how to fetch the state itself, so a
        // missing embed must never break prompts/get.
        private void TryAppendResource(List<object> messages, string uri)
        {
            if (_resourceProvider == null)
                return;

            try
            {
                var read = _resourceProvider.ReadResource(uri);
                if (read == null || !(read.TryGetValue("contents", out var c) && c is List<object> contents) || contents.Count == 0)
                    return;
                if (!(contents[0] is Dictionary<string, object> first))
                    return;

                messages.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new Dictionary<string, object>
                    {
                        ["type"] = "resource",
                        ["resource"] = new Dictionary<string, object>
                        {
                            ["uri"] = first.TryGetValue("uri", out var u) ? u : uri,
                            ["mimeType"] = first.TryGetValue("mimeType", out var m) ? m : "text/plain",
                            ["text"] = first.TryGetValue("text", out var t) ? t : string.Empty
                        }
                    }
                });
            }
            catch
            {
                // best-effort embed; ignore
            }
        }

        private static Dictionary<string, object> CreatePrompt(
            string name, string description, params Dictionary<string, object>[] args)
        {
            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["description"] = description,
                ["arguments"] = new List<object>(args)
            };
        }

        private static Dictionary<string, object> Arg(string name, string description, bool required)
        {
            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["description"] = description,
                ["required"] = required
            };
        }

        // MCP prompt argument values are always strings; return null when absent/blank so callers
        // can interpolate a conditional clause instead of an empty fragment.
        private static string ArgValue(Dictionary<string, object> arguments, string key, string fallback)
        {
            if (arguments != null && arguments.TryGetValue(key, out var v) && v != null)
            {
                var s = v.ToString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return fallback;
        }
    }
}
