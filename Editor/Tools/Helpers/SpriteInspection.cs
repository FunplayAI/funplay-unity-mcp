// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Funplay.Editor.Tools.Helpers
{
    internal static class SpriteInspection
    {
        internal static object Describe(Sprite sprite)
        {
            if (sprite == null) return null;
            var path = AssetDatabase.GetAssetPath(sprite);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long localId);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            var rect = sprite.rect;
            return new
            {
                name = sprite.name, asset_path = path, asset_guid = guid, local_file_id = localId,
                instance_id = ObjectIdHelper.GetSerializableId(sprite),
                border = Border(sprite.border), has_border = sprite.border != Vector4.zero,
                rect = new { x = rect.x, y = rect.y, width = rect.width, height = rect.height },
                pixels_per_unit = sprite.pixelsPerUnit, packed = sprite.packed,
                packing_mode = sprite.packed ? sprite.packingMode.ToString() : null,
                packing_rotation = sprite.packed ? sprite.packingRotation.ToString() : null,
                texture = sprite.texture != null ? new { name = sprite.texture.name, asset_path = AssetDatabase.GetAssetPath(sprite.texture), width = sprite.texture.width, height = sprite.texture.height } : null,
                importer = DescribeImporter(importer),
                value_source = "loaded_sprite_and_importer", persisted = false,
                note = "Border belongs to the source Sprite, not the packed atlas texture. Loaded values may include unsaved importer changes; this read does not reimport/save."
            };
        }
        internal static object DescribeImporter(TextureImporter importer)
        {
            if (importer == null) return null;
            // Sub-sprite identity comes from imported Sprite local IDs; never match duplicate names silently.
#pragma warning disable 0618
            var slices = importer.spriteImportMode == SpriteImportMode.Multiple ? importer.spritesheet.Select(x => new
            {
                name = x.name, border = Border(x.border), rect = new { x = x.rect.x, y = x.rect.y, width = x.rect.width, height = x.rect.height }
            }).ToArray() : null;
#pragma warning restore 0618
            return new
            {
                asset_path = importer.assetPath, texture_type = importer.textureType.ToString(), sprite_mode = importer.spriteImportMode.ToString(),
                single_sprite_border = importer.spriteImportMode == SpriteImportMode.Single ? Border(importer.spriteBorder) : null,
                sub_sprites = slices, pixels_per_unit = importer.spritePixelsPerUnit, is_readable = importer.isReadable,
                dirty = EditorUtility.IsDirty(importer), texture_compression = importer.textureCompression.ToString()
            };
        }
        internal static object DescribeImage(Image image) => new
        {
            image_instance_id = ObjectIdHelper.GetSerializableId(image), object_instance_id = ObjectIdHelper.GetSerializableId(image.gameObject),
            object_path = ObjectsHelper.GetGameObjectPath(image.gameObject),
            asset_path = EditorUtility.IsPersistent(image) ? AssetDatabase.GetAssetPath(image) : image.gameObject.scene.path,
            image_type = image.type.ToString(), preserve_aspect = image.preserveAspect, pixels_per_unit_multiplier = image.pixelsPerUnitMultiplier,
            source_sprite = Describe(image.sprite), effective_sprite = Describe(image.overrideSprite),
            uses_override = image.overrideSprite != image.sprite,
            sliced_without_border = image.type == Image.Type.Sliced && image.overrideSprite != null && image.overrideSprite.border == Vector4.zero,
            value_source = EditorUtility.IsPersistent(image) ? "loaded_asset" : "live_scene", persisted = false
        };
        internal static object Border(Vector4 border) => new { left = border.x, bottom = border.y, right = border.z, top = border.w };

        internal static object Dependents(IEnumerable<string> sourcePaths, int scanLimit)
        {
            var sources = new HashSet<string>(sourcePaths.Where(x => !string.IsNullOrEmpty(x)), StringComparer.Ordinal);
            var candidates = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })
                .Concat(AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
                .Concat(AssetDatabase.FindAssets("t:SpriteAtlas", new[] { "Assets" }))
                .Distinct().Select(AssetDatabase.GUIDToAssetPath).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var matches = new List<object>(); int scanned = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            foreach (var path in candidates)
            {
                if (scanned >= scanLimit || timer.ElapsedMilliseconds >= 2000) break;
                scanned++;
                var dependencies = AssetDatabase.GetDependencies(path, true).Where(sources.Contains).ToArray();
                if (dependencies.Length > 0) matches.Add(new { asset_path = path, source_paths = dependencies });
            }
            return new { complete = scanned == candidates.Length, scanned_assets = scanned, candidate_assets = candidates.Length,
                assets = matches, scope = "Assets prefabs, scenes and SpriteAtlases; recursive dependency references, not proven runtime usage" };
        }
    }
}
