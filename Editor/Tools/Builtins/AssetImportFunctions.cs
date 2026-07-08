// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("AssetImport")]
    internal static class AssetImportFunctions
    {
        // The default (non-platform-specific) texture platform key.
        private const string DefaultTexturePlatform = "DefaultTexturePlatform";

        [Description("Read the import settings of an asset via its AssetImporter. " +
                     "Understands TextureImporter (textureType, maxTextureSize, textureCompression, filterMode, wrapMode, " +
                     "sRGBTexture, mipmapEnabled, isReadable, alphaSource + the DefaultTexturePlatform override format/maxTextureSize/overridden), " +
                     "AudioImporter (defaultSampleSettings loadType/compressionFormat/quality/sampleRateSetting, forceToMono, preloadAudioData, loadInBackground), " +
                     "and ModelImporter (globalScale, importBlendShapes, importCameras, importLights, meshCompression, isReadable, animationType, materialImportMode). " +
                     "Any other importer type is reported by type name with a 'no typed reader' note.")]
        [ReadOnlyTool]
        public static object GetAssetImportSettings(
            [ToolParam("Project-relative asset path, e.g. 'Assets/Art/icon.png'")] string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    return Response.Error("PATH_REQUIRED", new { hint = "Pass a project-relative asset path, e.g. 'Assets/Art/icon.png'." });

                var importer = AssetImporter.GetAtPath(path);
                if (importer == null)
                    return Response.Error("IMPORTER_NOT_FOUND",
                        new { path, hint = "No AssetImporter at that path. Verify it is a project-relative path to an imported asset." });

                switch (importer)
                {
                    case TextureImporter ti:
                    {
                        var def = ti.GetPlatformTextureSettings(DefaultTexturePlatform);
                        return Response.Success($"Import settings for '{path}' (TextureImporter).", new
                        {
                            path,
                            importerType = "TextureImporter",
                            textureType = ti.textureType.ToString(),
                            maxTextureSize = ti.maxTextureSize,
                            textureCompression = ti.textureCompression.ToString(),
                            filterMode = ti.filterMode.ToString(),
                            wrapMode = ti.wrapMode.ToString(),
                            sRGBTexture = ti.sRGBTexture,
                            mipmapEnabled = ti.mipmapEnabled,
                            isReadable = ti.isReadable,
                            alphaSource = ti.alphaSource.ToString(),
                            defaultPlatform = new
                            {
                                format = def.format.ToString(),
                                maxTextureSize = def.maxTextureSize,
                                overridden = def.overridden
                            }
                        });
                    }
                    case AudioImporter ai:
                    {
                        // preloadAudioData was moved onto AudioImporterSampleSettings (the top-level
                        // AudioImporter.preloadAudioData property is Obsolete(error) in this Unity version).
                        var ss = ai.defaultSampleSettings;
                        return Response.Success($"Import settings for '{path}' (AudioImporter).", new
                        {
                            path,
                            importerType = "AudioImporter",
                            defaultSampleSettings = new
                            {
                                loadType = ss.loadType.ToString(),
                                compressionFormat = ss.compressionFormat.ToString(),
                                quality = ss.quality,
                                sampleRateSetting = ss.sampleRateSetting.ToString()
                            },
                            forceToMono = ai.forceToMono,
                            preloadAudioData = ss.preloadAudioData,
                            loadInBackground = ai.loadInBackground
                        });
                    }
                    case ModelImporter mi:
                    {
                        return Response.Success($"Import settings for '{path}' (ModelImporter).", new
                        {
                            path,
                            importerType = "ModelImporter",
                            globalScale = mi.globalScale,
                            importBlendShapes = mi.importBlendShapes,
                            importCameras = mi.importCameras,
                            importLights = mi.importLights,
                            meshCompression = mi.meshCompression.ToString(),
                            isReadable = mi.isReadable,
                            animationType = mi.animationType.ToString(),
                            materialImportMode = mi.materialImportMode.ToString()
                        });
                    }
                    default:
                    {
                        var typeName = importer.GetType().Name;
                        return Response.Success($"Import settings for '{path}' ({typeName}).", new
                        {
                            path,
                            importerType = typeName,
                            note = "no typed reader"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return Response.Error("TOOL_EXCEPTION", new { path, message = ex.Message });
            }
        }

        [Description("Apply import settings to an asset. settings_json is a JSON object of field->value using the same field " +
                     "names get_asset_import_settings returns for the resolved importer type. " +
                     "TextureImporter: maxTextureSize, textureCompression(enum name), filterMode, wrapMode, sRGBTexture, mipmapEnabled, isReadable. " +
                     "AudioImporter: forceToMono, preloadAudioData, loadInBackground, loadType(enum name), compressionFormat(enum name), quality. " +
                     "ModelImporter: globalScale, isReadable, meshCompression(enum name), importBlendShapes. " +
                     "Only provided+recognized fields are applied, then SaveAndReimport() runs; unknown or unparseable fields go to 'skipped' " +
                     "(the call does not fail). Returns each applied field read back FROM the importer after reimport so postprocessor overrides are visible.")]
        [SceneEditingTool]
        public static object SetAssetImportSettings(
            [ToolParam("Project-relative asset path, e.g. 'Assets/Art/icon.png'")] string path,
            [ToolParam("JSON object of field->value to apply (same field names get_asset_import_settings returns for this importer type)")] string settings_json)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    return Response.Error("PATH_REQUIRED", new { hint = "Pass a project-relative asset path, e.g. 'Assets/Art/icon.png'." });

                var importer = AssetImporter.GetAtPath(path);
                if (importer == null)
                    return Response.Error("IMPORTER_NOT_FOUND",
                        new { path, hint = "No AssetImporter at that path. Verify it is a project-relative path to an imported asset." });

                JObject obj;
                try
                {
                    obj = JObject.Parse(settings_json ?? string.Empty);
                }
                catch (Exception ex)
                {
                    return Response.Error("INVALID_JSON",
                        new { settings_json, detail = ex.Message, hint = "settings_json must be a JSON object of field->value, e.g. {\"maxTextureSize\":1024}." });
                }

                var applied = new List<string>();
                var skipped = new List<string>();

                switch (importer)
                {
                    case TextureImporter ti:
                        ApplyTexture(ti, obj, applied, skipped);
                        break;
                    case AudioImporter ai:
                        ApplyAudio(ai, obj, applied, skipped);
                        break;
                    case ModelImporter mi:
                        ApplyModel(mi, obj, applied, skipped);
                        break;
                    default:
                        foreach (var p in obj.Properties())
                            skipped.Add(p.Name);
                        return Response.Error("UNSUPPORTED_IMPORTER",
                            new { path, importerType = importer.GetType().Name, skipped,
                                  hint = "Only TextureImporter, AudioImporter and ModelImporter are writable by this tool." });
                }

                if (applied.Count == 0)
                    return Response.Success($"No fields applied for '{path}' (nothing recognized).", new
                    {
                        path,
                        importerType = importer.GetType().Name,
                        applied = new Dictionary<string, object>(),
                        skipped
                    });

                importer.SaveAndReimport();

                // Re-fetch after reimport so any postprocessor that overrode our values is reflected in the read-back.
                var fresh = AssetImporter.GetAtPath(path) ?? importer;
                var readBack = new Dictionary<string, object>();
                foreach (var f in applied)
                    readBack[f] = ReadImporterField(fresh, f);

                return Response.Success($"Applied {applied.Count} field(s) to '{path}' ({fresh.GetType().Name}).", new
                {
                    path,
                    importerType = fresh.GetType().Name,
                    applied = readBack,
                    skipped
                });
            }
            catch (Exception ex)
            {
                return Response.Error("TOOL_EXCEPTION", new { path, message = ex.Message });
            }
        }

        // -------- Per-type apply --------

        private static void ApplyTexture(TextureImporter ti, JObject obj, List<string> applied, List<string> skipped)
        {
            foreach (var p in obj.Properties())
            {
                var name = p.Name;
                var token = p.Value;
                switch (name)
                {
                    case "maxTextureSize":
                        if (TryGetInt(token, out var mts)) { ti.maxTextureSize = mts; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "textureCompression":
                        if (TryParseEnum<TextureImporterCompression>(token, out var tc)) { ti.textureCompression = tc; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "filterMode":
                        if (TryParseEnum<FilterMode>(token, out var fm)) { ti.filterMode = fm; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "wrapMode":
                        if (TryParseEnum<TextureWrapMode>(token, out var wm)) { ti.wrapMode = wm; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "sRGBTexture":
                        if (TryGetBool(token, out var srgb)) { ti.sRGBTexture = srgb; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "mipmapEnabled":
                        if (TryGetBool(token, out var mip)) { ti.mipmapEnabled = mip; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "isReadable":
                        if (TryGetBool(token, out var rd)) { ti.isReadable = rd; applied.Add(name); } else skipped.Add(name);
                        break;
                    default:
                        skipped.Add(name);
                        break;
                }
            }
        }

        private static void ApplyAudio(AudioImporter ai, JObject obj, List<string> applied, List<string> skipped)
        {
            var ss = ai.defaultSampleSettings;
            bool ssDirty = false;
            foreach (var p in obj.Properties())
            {
                var name = p.Name;
                var token = p.Value;
                switch (name)
                {
                    case "forceToMono":
                        if (TryGetBool(token, out var ftm)) { ai.forceToMono = ftm; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "loadInBackground":
                        if (TryGetBool(token, out var lib)) { ai.loadInBackground = lib; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "preloadAudioData":
                        // Lives on the sample settings (top-level property is Obsolete(error) in this Unity version).
                        if (TryGetBool(token, out var pad)) { ss.preloadAudioData = pad; ssDirty = true; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "loadType":
                        if (TryParseEnum<AudioClipLoadType>(token, out var lt)) { ss.loadType = lt; ssDirty = true; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "compressionFormat":
                        if (TryParseEnum<AudioCompressionFormat>(token, out var cf)) { ss.compressionFormat = cf; ssDirty = true; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "quality":
                        if (TryGetFloat(token, out var q)) { ss.quality = q; ssDirty = true; applied.Add(name); } else skipped.Add(name);
                        break;
                    default:
                        skipped.Add(name);
                        break;
                }
            }
            if (ssDirty)
                ai.defaultSampleSettings = ss;
        }

        private static void ApplyModel(ModelImporter mi, JObject obj, List<string> applied, List<string> skipped)
        {
            foreach (var p in obj.Properties())
            {
                var name = p.Name;
                var token = p.Value;
                switch (name)
                {
                    case "globalScale":
                        if (TryGetFloat(token, out var gs)) { mi.globalScale = gs; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "isReadable":
                        if (TryGetBool(token, out var rd)) { mi.isReadable = rd; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "meshCompression":
                        if (TryParseEnum<ModelImporterMeshCompression>(token, out var mc)) { mi.meshCompression = mc; applied.Add(name); } else skipped.Add(name);
                        break;
                    case "importBlendShapes":
                        if (TryGetBool(token, out var ibs)) { mi.importBlendShapes = ibs; applied.Add(name); } else skipped.Add(name);
                        break;
                    default:
                        skipped.Add(name);
                        break;
                }
            }
        }

        // -------- Read-back (after SaveAndReimport) --------

        // Returns the current value of a single applied field, read from the (re-fetched) importer.
        private static object ReadImporterField(AssetImporter importer, string field)
        {
            switch (importer)
            {
                case TextureImporter ti:
                    switch (field)
                    {
                        case "maxTextureSize": return ti.maxTextureSize;
                        case "textureCompression": return ti.textureCompression.ToString();
                        case "filterMode": return ti.filterMode.ToString();
                        case "wrapMode": return ti.wrapMode.ToString();
                        case "sRGBTexture": return ti.sRGBTexture;
                        case "mipmapEnabled": return ti.mipmapEnabled;
                        case "isReadable": return ti.isReadable;
                    }
                    break;
                case AudioImporter ai:
                    switch (field)
                    {
                        case "forceToMono": return ai.forceToMono;
                        case "loadInBackground": return ai.loadInBackground;
                        case "preloadAudioData": return ai.defaultSampleSettings.preloadAudioData;
                        case "loadType": return ai.defaultSampleSettings.loadType.ToString();
                        case "compressionFormat": return ai.defaultSampleSettings.compressionFormat.ToString();
                        case "quality": return ai.defaultSampleSettings.quality;
                    }
                    break;
                case ModelImporter mi:
                    switch (field)
                    {
                        case "globalScale": return mi.globalScale;
                        case "isReadable": return mi.isReadable;
                        case "meshCompression": return mi.meshCompression.ToString();
                        case "importBlendShapes": return mi.importBlendShapes;
                    }
                    break;
            }
            return null;
        }

        // -------- JToken coercion helpers --------

        private static bool TryGetInt(JToken t, out int v)
        {
            v = 0;
            try
            {
                if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) { v = t.Value<int>(); return true; }
                if (t.Type == JTokenType.String && int.TryParse(t.Value<string>(), out var pv)) { v = pv; return true; }
            }
            catch { }
            return false;
        }

        private static bool TryGetBool(JToken t, out bool v)
        {
            v = false;
            try
            {
                if (t.Type == JTokenType.Boolean) { v = t.Value<bool>(); return true; }
                if (t.Type == JTokenType.Integer) { v = t.Value<int>() != 0; return true; }
                if (t.Type == JTokenType.String && bool.TryParse(t.Value<string>(), out var pv)) { v = pv; return true; }
            }
            catch { }
            return false;
        }

        private static bool TryGetFloat(JToken t, out float v)
        {
            v = 0f;
            try
            {
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) { v = t.Value<float>(); return true; }
                if (t.Type == JTokenType.String &&
                    float.TryParse(t.Value<string>(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pv)) { v = pv; return true; }
            }
            catch { }
            return false;
        }

        private static bool TryParseEnum<TEnum>(JToken t, out TEnum v) where TEnum : struct
        {
            v = default;
            try
            {
                if (t.Type == JTokenType.String)
                {
                    // Reject numeric strings that don't correspond to a defined member.
                    return Enum.TryParse(t.Value<string>(), true, out v) && Enum.IsDefined(typeof(TEnum), v);
                }
                if (t.Type == JTokenType.Integer)
                {
                    int iv = t.Value<int>();
                    if (Enum.IsDefined(typeof(TEnum), iv)) { v = (TEnum)(object)iv; return true; }
                }
            }
            catch { }
            return false;
        }
    }
}
