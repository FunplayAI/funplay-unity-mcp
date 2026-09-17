// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.Linq;
using Funplay.Editor.DI;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Settings;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Inspection")]
    internal static class InspectionFunctions
    {
        [Description("Find project and Unity type full names/assemblies, including non-Component helpers. Exact short-name matches explicitly report ambiguity. No instantiation or code execution; use assembly-qualified names when multiple assemblies define the same type.")]
        [ReadOnlyTool]
        public static object FindProjectTypes([ToolParam("Full/short name or substring", Required = false)] string query = "",
            [ToolParam("Exact name match", Required = false)] bool exact = false,
            [ToolParam("Exact assembly name", Required = false)] string assembly = null,
            [ToolParam("any, component or unity_object", Required = false)] string kind = "any",
            [ToolParam("Zero-based offset", Required = false)] int offset = 0,
            [ToolParam("Page size 1..200", Required = false)] int limit = 50)
        {
            if (offset < 0 || limit < 1 || limit > 200 || !new[] { "any", "component", "unity_object" }.Contains(kind)) return Response.Error("INVALID_TYPE_QUERY");
            var matches = ProjectTypeCatalog.Types.Where(t =>
                (string.IsNullOrEmpty(assembly) || t.Assembly.GetName().Name == assembly) &&
                (kind != "component" || typeof(Component).IsAssignableFrom(t)) &&
                (kind != "unity_object" || typeof(UnityEngine.Object).IsAssignableFrom(t)) &&
                (exact ? string.Equals(t.Name, query, StringComparison.OrdinalIgnoreCase) || string.Equals(t.FullName, query, StringComparison.OrdinalIgnoreCase) || t.AssemblyQualifiedName == query
                       : t.FullName.IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            return Response.Success("Loaded type catalogue.", new { total = matches.Length, ambiguous = exact && matches.Length > 1,
                items = matches.Skip(offset).Take(limit).Select(ProjectTypeCatalog.Describe).ToArray(), next_offset = offset + limit < matches.Length ? (int?)(offset + limit) : null });
        }

        [Description("Batch inspect Image → effective Sprite → source TextureImporter, including actual borders, Single/Multiple metadata, packed status and sub-asset GUID/local IDs. selectors is a JSON array: {image_id:123}, {asset_path:'Assets/x.png', sprite_name?:'...', local_file_id?:123}, or {prefab_path:'Assets/UI.prefab'}. Partial failures are explicit. Does not save/reimport or assign borders.")]
        [ReadOnlyTool]
        public static object InspectUiSprites([ToolParam("JSON selector array, 1..100 entries")] string selectors,
            [ToolParam("Bounded prefab/scene/atlas reverse dependency scan", Required = false)] bool include_dependents = false,
            [ToolParam("Dependent scan limit, 1..5000", Required = false)] int dependent_scan_limit = 1000)
        {
            JArray inputs;
            try { inputs = JArray.Parse(selectors); }
            catch (Exception ex) { return Response.Error("INVALID_SELECTORS", new { detail = ex.Message }); }
            if (inputs.Count < 1 || inputs.Count > 100 || dependent_scan_limit < 1 || dependent_scan_limit > 5000) return Response.Error("INVALID_INSPECTION_LIMIT");
            var results = new List<object>(); var sources = new HashSet<string>(); int success = 0, totalItems = 0;
            foreach (var token in inputs)
            {
                try
                {
                    if (!(token is JObject selector)) throw new ArgumentException("Each selector must be an object.");
                    if (selector.Properties().Any(x => !new[] { "image_id", "asset_path", "prefab_path", "sprite_name", "local_file_id" }.Contains(x.Name)))
                        throw new ArgumentException("Unknown selector property.");
                    if (new[] { "image_id", "asset_path", "prefab_path" }.Count(x => selector[x] != null) != 1) throw new ArgumentException("Specify exactly one image_id, asset_path or prefab_path.");
                    var images = new List<Image>(); var sprites = new List<Sprite>();
                    if (selector["image_id"] != null)
                    {
                        var obj = ObjectIdHelper.ToObject(selector["image_id"].ToString());
                        if (obj is Image image) images.Add(image);
                        else if (obj is GameObject go) images.AddRange(go.GetComponents<Image>());
                        if (images.Count != 1) throw new ArgumentException("image_id must resolve to exactly one Image or GameObject containing one.");
                    }
                    else if (selector["prefab_path"] != null)
                    {
                        var path = (string)selector["prefab_path"]; UIAuditService.ValidateAssetPath(path);
                        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("prefab_path must end in .prefab.");
                        var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (root == null) throw new ArgumentException("Prefab not found.");
                        images.AddRange(root.GetComponentsInChildren<Image>(true).Take(501));
                        if (images.Count > 500) throw new ArgumentException("Prefab contains more than 500 Images; narrow to explicit Image IDs.");
                    }
                    else
                    {
                        var path = (string)selector["asset_path"]; UIAuditService.ValidateAssetPath(path);
                        sprites.AddRange(AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>());
                        if (selector["sprite_name"] != null) sprites = sprites.Where(x => x.name == (string)selector["sprite_name"]).ToList();
                        if (selector["local_file_id"] != null) sprites = sprites.Where(x => {
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(x, out string _, out long id); return id == (long)selector["local_file_id"];
                        }).ToList();
                        if (sprites.Count == 0) throw new ArgumentException("No imported Sprite matches the selector.");
                        if (sprites.Count > 500) throw new ArgumentException("More than 500 sub-sprites; use a local_file_id selector.");
                        if (selector["sprite_name"] != null && sprites.Count > 1 && selector["local_file_id"] == null)
                        {
                            results.Add(new { selector, success = false, code = "AMBIGUOUS_SPRITE_NAME", candidates = sprites.Select(SpriteInspection.Describe).ToArray() }); continue;
                        }
                    }
                    if (totalItems + images.Count + sprites.Count > 500) throw new ArgumentException("Batch exceeds 500 Image/Sprite observations; split selectors or choose local_file_id.");
                    totalItems += images.Count + sprites.Count;
                    foreach (var image in images) { if (image.sprite != null) sources.Add(AssetDatabase.GetAssetPath(image.sprite)); if (image.overrideSprite != null) sources.Add(AssetDatabase.GetAssetPath(image.overrideSprite)); }
                    foreach (var sprite in sprites) sources.Add(AssetDatabase.GetAssetPath(sprite));
                    results.Add(new { selector, success = true, images = images.Select(SpriteInspection.DescribeImage).ToArray(), sprites = sprites.Select(SpriteInspection.Describe).ToArray() }); success++;
                }
                catch (Exception ex) { results.Add(new { selector = token, success = false, code = "SPRITE_INSPECTION_FAILED", detail = ex.Message }); }
            }
            return Response.Success("Sprite inspection completed with per-selector results.", new
            {
                success_count = success, failure_count = inputs.Count - success, results,
                dependents = include_dependents ? SpriteInspection.Dependents(sources, dependent_scan_limit) : null
            });
        }

        [Description("Discover implemented tools versus tools currently exposed by the active Core/Full configuration, including disabled tools. This is read-only; it never enables tools. Prefer structured tools for supported tasks; execute_code is a fallback for project-specific gaps.")]
        [ReadOnlyTool]
        public static object GetToolCapabilities([ToolParam("Optional name substring", Required = false)] string query = null)
        {
            var settings = RootScopeServices.Services?.GetService(typeof(ISettingsController)) as ISettingsController;
            var profile = MCPToolExportPolicy.Parse(settings?.MCPToolExportProfile);
            var names = ToolRegistry.MethodCache.Keys.Concat(ToolRegistry.ManualTools.Keys).Distinct().OrderBy(x => x).Where(x => string.IsNullOrEmpty(query) || x.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            return Response.Success("Tool implementation and exposure are separate capabilities.", new
            {
                profile = MCPToolExportPolicy.ToSettingValue(profile),
                core_customized = settings?.MCPCoreToolsConfigured ?? false,
                full_customized = settings?.MCPFullToolsConfigured ?? false,
                tools = names.Select(name => new
                {
                    name, implemented = true, enabled = ToolRegistry.IsEnabled(name),
                    exposed = ToolRegistry.IsEnabled(name) && MCPToolExportPolicy.IsToolAllowed(name, profile, settings?.MCPCoreToolsConfigured ?? false, settings?.MCPCoreTools, settings?.MCPFullToolsConfigured ?? false, settings?.MCPFullTools),
                    read_only = ToolRegistry.GetMethod(name) != null && ToolRegistry.IsReadOnly(ToolRegistry.GetMethod(name)),
                    role = name == "execute_code" ? "fallback" : "structured"
                }).ToArray(),
                hint = "If implemented but unexposed, adjust MCP Server tool exposure deliberately; do not assume the plugin lacks the capability."
            });
        }
    }
}
