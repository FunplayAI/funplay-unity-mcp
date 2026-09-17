// Copyright (C) Funplay. Licensed under MIT.
using System;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("UI")]
    internal static class UIConfigurationFunctions
    {
        [Description("Read project UI templates/defaults and bounded counts of existing Text/TMP fonts and input modules. Ties/incomplete scans are unresolved, never silently guessed. New projects prefer TMP. Does not install packages or change settings.")]
        [ReadOnlyTool]
        public static object GetUiDefaults([ToolParam("Prefab scan cap, 1..2000", Required = false)] int prefab_scan_limit = 200)
        {
            try
            {
                return Response.Success("Project UI authoring defaults.", new {
                    configuration_path = ProjectUIDefaults.ConfigurationPath, configuration = ProjectUIDefaults.Load(),
                    conventions = ProjectUIDefaults.Probe(prefab_scan_limit),
                    tmp_component_available = ProjectUIDefaults.TmpType != null,
                    input_system_component_available = ProjectUIDefaults.NewInputType != null,
                    precedence = "Explicit arguments > configured prefab/defaults > verified existing conventions > TMP for an empty project. Templates retain their component types and authored layout."
                });
            }
            catch (Exception ex) { return Response.Error("UI_DEFAULTS_INVALID", new { detail = ex.Message }); }
        }

        [Description("Replace project-scoped UI defaults using validated JSON schema_version=1; text_component auto/tmp/legacy, font_asset/font_material, input_module auto/input_system/legacy, canvas/button/text template objects {asset_path,text_path}. All fields validated before atomic save. No scene changes or dependency installation.")]
        public static object ConfigureUiDefaults([ToolParam("Full project UI configuration JSON")] string configuration)
        {
            try
            {
                var parsed = ProjectUIDefaults.Parse(configuration);
                ProjectUIDefaults.Save(parsed);
                return Response.Success("Saved project UI defaults.", new { persisted = true, path = ProjectUIDefaults.ConfigurationPath, configuration = ProjectUIDefaults.Load() });
            }
            catch (Exception ex) { return Response.Error("UI_DEFAULTS_NOT_SAVED", new { detail = ex.Message }); }
        }

        [Description("Author a canvas/button/text in Edit Mode using a configured or explicit UI prefab, or project-aware primitives when no template is configured. Preserve prefab references, typography and layout unless explicitly overridden. Save the resulting scene/prefab deliberately. Requires TMP resources when TMP is selected; never substitutes legacy Text silently.")]
        [SceneEditingTool]
        public static object CreateProjectUi(
            [ToolParam("canvas, button or text")] string kind,
            [ToolParam("New object name")] string name,
            [ToolParam("Label content; omitted preserves the template label", Required = false)] string text = null,
            [ToolParam("Unique parent name/path/instance ID; canvas can be a root", Required = false)] string parent = null,
            [ToolParam("Optional prefab asset path", Required = false)] string template_path = null,
            [ToolParam("Optional label path relative to prefab root", Required = false)] string text_path = null,
            [ToolParam("Optional anchored x,y", Required = false)] string position = null,
            [ToolParam("Optional width,height", Required = false)] string size = null,
            [ToolParam("Optional anchor preset", Required = false)] string anchor = null,
            [ToolParam("Optional pivot x,y", Required = false)] string pivot = null,
            [ToolParam("auto, tmp or legacy", Required = false)] string text_component = null,
            [ToolParam("Optional font asset", Required = false)] string font_asset = null,
            [ToolParam("Optional shared font material preset", Required = false)] string font_material = null,
            [ToolParam("Optional font size", Required = false)] string font_size = null,
            [ToolParam("Optional Canvas render mode", Required = false)] string render_mode = null,
            [ToolParam("auto, input_system or legacy", Required = false)] string input_module = null)
            => UICreationService.Create(kind, name, text, parent, position, size, anchor, pivot, font_size, render_mode, text_component, template_path, text_path, font_asset, font_material, input_module);
    }
}
