// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;

namespace Funplay.Editor.MCP.Server
{
    // Independently authored Funplay guidance, informed by the pinned official sources below.
    // No Unity Companion License skill prose, scripts or assets are redistributed here.
    internal static class ProjectSkillReferences
    {
        internal const string OfficialPluginRevision = "cf6b2da24e424b0a60d560a57f39f676cb6f79f3";
        private const string OfficialSource =
            "https://github.com/Unity-Technologies/unity-agent-plugin/tree/" + OfficialPluginRevision + "/skills/";

        private static readonly IReadOnlyDictionary<string, string> Workflow = new Dictionary<string, string>
        {
            ["references/unity-mcp-workflow/project-compatibility.md"] =
@"# Project Compatibility And Package Readiness

Read this for package installation, unresolved namespaces, version-dependent APIs or project setup. It does not authorize installing packages or changing project settings outside the user's task.

## Establish the actual environment

- Read `ProjectSettings/ProjectVersion.txt`, requested dependencies in `Packages/manifest.json`, and resolved dependencies in `Packages/packages-lock.json`. These describe different stages; a lock-file entry alone does not prove the assembly is loaded in the connected Editor.
- Query the active Unity session and use `find_project_types` when exposed to resolve the exact namespace and assembly before generating project-specific code. Check assembly-definition references, Editor-only boundaries and the installed package's public API. Gate Unity 6 or newer package examples against the actual project; Funplay also supports Unity 2022.3.
- Identify the relevant UI framework, render pipeline, input system and existing project services. A package recommendation is not a requirement to replace working project conventions. Report an unavailable type or unsupported version rather than reflecting into internal APIs or repeatedly guessing names.
- Scope editable asset searches to the intended `Assets` directories. Inspect all plausible matches instead of choosing the first GUID; package examples and read-only assets are not project-owned defaults.

## Package operations are asynchronous

1. Confirm the required package ID/version and intended add/remove/upgrade scope. Reuse installed compatible dependencies. Do not install Localization, TMP examples, a CLI or a render pipeline merely because a reference mentions it.
2. Prefer the exposed MCP package operation. If an authorized gap requires an Editor API, use `UnityEditor.PackageManager.Client` and let the Editor update loop process its request. Never busy-wait on `IsCompleted` on the main thread or treat the initial request handle as completion.
3. Observe the request's terminal result with a bounded deadline. After reload, recover available receipts and inspect the resolved package/version; a lost response is not permission to repeat a mutation. A `prepare_editor` request key does not make arbitrary `Client.Add` calls replay-safe.
4. Use `prepare_editor` / `get_task` to establish current readiness in the intended mode. Confirm the required types are loaded and check import/compilation errors before using them. Report requested, resolved and loaded state separately when they disagree.

Use the current Editor session rather than opening a second process on its project. Only an explicitly needed, separately owned headless bootstrap may launch the Editor directly: asynchronous UPM work must keep the process alive without `-quit`, yield to Editor updates, enforce a deadline and exit its own process after completion. Never call `EditorApplication.Exit` on the user's interactive session.

## Sources and adaptation

This is original Funplay workflow guidance, informed by Unity's official plugin 0.1.8-beta at the pinned revision:

- [Package management](__OFFICIAL__unity-package-management)
- [Editor search](__OFFICIAL__generate-editor-search-query)
".Replace("__OFFICIAL__", OfficialSource)
        };

        private static readonly IReadOnlyDictionary<string, string> Ui = new Dictionary<string, string>
        {
            ["references/unity-ui-composition/ui-frameworks.md"] =
@"# Choose The Existing UI Framework

Read this before applying the Canvas component guidance to an unfamiliar or mixed-framework project.

- Classify the target screen, not the whole repository: uGUI uses `Canvas`, `RectTransform` and `Graphic`; UI Toolkit uses `UIDocument`, UXML, USS and `VisualElement`; IMGUI uses `OnGUI` / `EditorGUI` / `GUILayout`. Runtime and Editor tooling can use different systems in the same project.
- Follow the target's existing framework and bindings. Do not convert a working screen, install a package, or wrap UI Toolkit in a new Canvas to make uGUI instructions fit. If a new screen's framework is genuinely undecided, explain the project-compatible choices and clarify a consequential choice.
- The component table, prefab-first authoring, layout audits and raycast advice in the main skill primarily describe uGUI. Do not claim that a uGUI audit covers UI Toolkit or IMGUI. Inspect the tool's supported scope; use scoped public Editor APIs through MCP for actual gaps.

## UI Toolkit

Reuse project UXML/USS, reusable visual-tree templates, PanelSettings, styles and data/event conventions. Author stable structure in those assets rather than rebuilding it procedurally; add runtime behavior only when the task requires it. UXML/USS/C# are source files and can use normal repository tools; Unity-owned PanelSettings `.asset` edits still require Unity APIs.

Check the installed Unity/package version before assuming support for runtime data binding, controls, style properties or UI Builder features. USS is not browser CSS: verify supported properties/selectors rather than importing web layout rules unchanged. UI Toolkit text uses the version's TextCore/TextSettings system, not automatically TMP font assets. Inspect flex layout, resolved styles, clipping, focus, picking and panel scaling in the live panel. CanvasScaler, Image.Type.Sliced, RectMask2D and GraphicRaycaster settings are not UI Toolkit fixes. Test the requested input and rendered states in the appropriate panel, not just UXML syntax.

## IMGUI and Editor UI

Preserve the existing EditorWindow/Inspector framework; IMGUI redraws its layout during GUI events and is not an authored mobile page prefab. Keep event handling and layout balanced, avoid asset writes on every repaint, and verify changes in the actual window. Use Undo/serialized properties for intentional edits. Do not introduce a runtime IMGUI interface merely to avoid building a requested uGUI page.

## Sources and adaptation

Original Funplay guidance informed by the official plugin 0.1.8-beta, with framework routing adapted to MCP and the existing project's conventions:

- [UI routing](__OFFICIAL__ui)
- [UI Toolkit](__OFFICIAL__ui-uitk)
- [IMGUI](__OFFICIAL__ui-imgui)
".Replace("__OFFICIAL__", OfficialSource),

            ["references/unity-ui-composition/sprite-importers.md"] =
@"# Sprite Importer Safety

Read this when changing nine-slice borders, pivots, slicing or shared Sprite imports. Setting an Image to Sliced alone is not a border repair.

## Identify the source and scope

- Inspect the Image's effective Sprite, its source asset and importer, Single/Multiple mode, packing/atlas membership, import scale and other consumers. An atlas texture is not the source to edit. Verify the exact sub-sprite identity, not just a duplicate display name.
- Derive border insets from the source artwork's non-stretchable corners and edges. Sprite Editor rectangles and borders use source-pixel coordinates; imported texture dimensions may be reduced by platform/max-size settings, and packed atlas coordinates may be rotated. Do not copy dimensions from a screenshot or the runtime packed texture into importer data without resolving this distinction.
- Preserve names, pivots, existing slice rectangles and identities unless the requested change needs them. Explain shared-asset effects before a change that would alter unrelated consumers; prefer an explicitly scoped variant when appropriate.

## Use the public importer API for the installed version

- For a verified Single Sprite TextureImporter, `spriteBorder` / supported importer properties may suffice. For Multiple sprites, layered source importers or slicing, inspect availability of the Sprite Editor Data Provider API in the installed 2D Sprite/importer version. Do not assume all importers support TextureImporter or silently convert their import mode.
- Where supported, initialize `SpriteDataProviderFactories` and the selected `ISpriteEditorDataProvider`, read its existing SpriteRects, and update only the intended entries. Preserve each surviving `spriteID`. Keep `ISpriteNameFileIdDataProvider` name/ID mappings consistent for supported versions when adding, removing or renaming slices; a border-only change must not generate new IDs or replace every mapping.
- Check the importer's permission for the actual edit, not merely that a provider exists. Where the installed API exposes `ISpriteFrameEditCapability`, require the matching border/pivot/rectangle/name or create/delete capability. A denied or required-but-missing capability stops the edit; do not override importer locks. Older versions need their documented supported edit path, not an assumed newer interface.
- Apply provider changes, reimport through its AssetImporter, then reacquire the imported Sprites. If the provider/API is absent, report the exact capability gap and stop that edit; do not patch `.meta`, use internal reflection, or rebuild slices as a fallback.

## Verify the saved result

Read the imported border, source identity and prefab/scene references again after reimport. Verify no missing or switched sprites, and resize the actual Sliced Image in the intended UI to check corners, edges and center. Restore any temporary validation size. Nonzero borders alone do not establish correct artwork, and an import success message does not prove reference preservation.

## Sources and adaptation

- [Official plugin 0.1.8-beta Sprite Editor guidance](__OFFICIAL__sprite-editor)
- [Unity Sprite Editor Data Provider API](https://docs.unity3d.com/Packages/com.unity.2d.sprite@1.0/manual/DataProvider.html) (use the manual matching the installed package)
".Replace("__OFFICIAL__", OfficialSource),

            ["references/unity-ui-composition/text-localization.md"] =
@"# TMP Fonts, Effects And Localization

Read the relevant section for font creation/repair, missing glyphs, text performance or requested localization. Ordinary UI work is not authorization to localize the project or replace legacy Text.

## Font assets and visual effects

- Follow the existing Text/TMP convention and font/material presets. Check TMP settings/resources and actual loaded APIs before font creation; if required resources are missing, locate the installed package's supported import asset and verify completion. Do not assume a menu command that opened a modal dialog completed an import, or import Examples & Extras as a routine prerequisite.
- Reproduce reference-visible TMP effects with component settings or a scoped material preset. Reuse the font atlas rather than duplicating font assets for styles. Check shader compatibility, SDF padding, fallback font weight/baselines and effect clipping at the target scale; do not mutate a shared material for a local change.
- Choose static/dynamic atlases, fallbacks or locale-specific font swaps from glyph coverage, the project's localization system, target devices and measured memory. Neither a universal CJK fallback ban nor mandatory font swapping is appropriate. Use licensed project fonts; system availability does not imply redistribution rights or reliable coverage on every device.
- For dynamic fonts inspect atlas growth, multi-atlas behavior and supported clear-on-build options. Evaluate autosizing and Canvas rebuild cost for frequently changing text before altering them. Do not prescribe fixed atlas sizes, font metrics, one Canvas per counter or OS-font modes across all Unity/TMP versions.
- When creating a TMP font asset through public APIs, persist any newly created material and atlas sub-assets as required by that version, without re-adding already persistent assets. Save and inspect `LoadAllAssetsAtPath`, material/texture links and a fresh load after reimport. An in-memory non-null material does not prove it was saved. Verify representative glyphs and effects after reopening the prefab.

## Localization coverage and bindings

1. Confirm the requested languages, existing localization solution and loaded package version. Do not replace project services or install Localization/Addressables automatically. Read the package-readiness guidance in the workflow skill if setup is required.
2. Inventory both authored legacy `Text` and `TMP_Text` across the requested scenes/prefabs, including inactive content. Separately inspect code-created strings, interpolated assignments, `SetText`, constants and project formatters; a component scan cannot find them all. Report scanned scope and unconverted sites with reasons.
3. Map table data by locale identifier, not enumeration/array order. Preserve stable keys, GUIDs, variables, pluralization and project-specific bindings. Never populate missing translations with the base language merely to make a completeness check pass; report intentional exceptions and unresolved gaps.
4. For Unity Localization, use public `LocalizeStringEvent` and persistent UnityEvent APIs when authored bindings are needed. Preserve unrelated listeners, avoid duplicates and read back target, method and call state. Use `EditorAndRuntime` only when authoring preview is intended; a runtime-only binding can be correct for runtime. Refresh the binding and verify the actual label changes after a save/reload. Do not edit private persistent-call fields or invoke internal localization plug-ins.
5. Check required key/locale pairs for missing or empty values and count what was examined. A zero-entry scan is inconclusive, not complete. For localized assets verify references and the existing Addressables build flow when applicable; avoid moving assets or rebuilding all content outside the requested scope.
6. Preview via the existing localization system, and test runtime switching when requested. Verify missing glyphs, fallback/style continuity, line breaks, clipping, long strings, RTL/shaping where relevant and reference fidelity in each requested locale. Preserve layout ownership rather than adding ContentSizeFitter to already driven children. Restore the original preview locale and editor state after verification.

## Sources and adaptation

Original Funplay guidance informed by the pinned official plugin 0.1.8-beta; its recommendations are filtered for project conventions, public APIs and Unity 2022.3 compatibility:

- [TMP optimization](__OFFICIAL__optimize-text-mesh-pro)
- [Localization](__OFFICIAL__localization)
- [TMP font properties](https://docs.unity3d.com/Packages/com.unity.textmeshpro@3.2/manual/FontAssetsProperties.html) (check the installed version)
".Replace("__OFFICIAL__", OfficialSource)
        };

        private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

        internal static IReadOnlyDictionary<string, string> GetForSkill(string skillId)
        {
            if (string.Equals(skillId, "unity-mcp-workflow", StringComparison.OrdinalIgnoreCase))
                return Workflow;
            if (string.Equals(skillId, "unity-ui-composition", StringComparison.OrdinalIgnoreCase))
                return Ui;
            return Empty;
        }
    }
}
