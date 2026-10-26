// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Funplay.Editor.MCP.Server;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tools.Helpers
{
    // Text is read ONLY as a conservative reference inventory. All authored changes go through
    // SerializedObject/PrefabUtility. Recovery replaces an exact byte backup, not hand-edited YAML.
    internal static class SerializedReferenceTransaction
    {
        private static readonly Regex Document = new Regex(@"^--- !u!\d+ &(-?\d+)", RegexOptions.Multiline);
        private static readonly Regex Reference = new Regex(@"\{\s*fileID:\s*(-?\d+)\s*(?:,\s*guid:\s*([0-9a-fA-F]{32})\s*,\s*type:\s*(\d+)\s*)?\}");
        internal static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static string HashFile(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        internal static Dictionary<string, string> Inventory(string yaml, string selfGuid = null)
        {
            if (!yaml.StartsWith("%YAML", StringComparison.Ordinal)) throw new InvalidDataException("Text-serialized Unity asset required; do not change project serialization mode automatically.");
            var docs = Document.Matches(yaml).Cast<Match>().ToArray();
            if (docs.Length == 0) throw new InvalidDataException("No serialized document headers.");
            var result = new Dictionary<string, string>();
            for (int d = 0; d < docs.Length; d++)
            {
                string body = yaml.Substring(docs[d].Index, (d + 1 < docs.Length ? docs[d + 1].Index : yaml.Length) - docs[d].Index);
                var refs = Reference.Matches(body).Cast<Match>().ToArray();
                if (Regex.Matches(body, @"\bfileID\s*:").Count != refs.Length)
                    throw new InvalidDataException("Unsupported reference encoding; cannot prove untouched references survive.");
                for (int r = 0; r < refs.Length; r++)
                {
                    var match = refs[r]; var guid = match.Groups[2].Value.ToLowerInvariant();
                    if (selfGuid != null && guid == selfGuid) guid = "$self";
                    // Retaining document identity, slot ordinal and preceding property/list prefix
                    // rejects reorderings too: false rejection is safer than an unproven save.
                    int line = body.LastIndexOf('\n', match.Index); var prefix = body.Substring(line + 1, match.Index - line - 1).Trim();
                    result.Add(docs[d].Groups[1].Value + "#" + r + ":" + prefix,
                        match.Groups[1].Value + ":" + guid + ":" + match.Groups[3].Value);
                }
            }
            return result;
        }
        internal static bool Preserved(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, string changedSlot, string newReference)
            => before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == (pair.Key == changedSlot ? newReference : pair.Value));
        internal static object Patch(string path, long localId, string property, string expectedHash, string expectedGuid, long expectedFileId, string replacementPath, long replacementLocalId)
        {
            string temp = "Assets/__FunplayReferencePreflight_" + Guid.NewGuid().ToString("N") + Path.GetExtension(path ?? "");
            var receipt = JObject.FromObject(PatchCore(path, localId, property, expectedHash, expectedGuid, expectedFileId, replacementPath, replacementLocalId, temp));
            bool cleanup = true; string cleanupError = null;
            try { if (File.Exists(temp) || File.Exists(temp + ".meta")) cleanup = AssetDatabase.DeleteAsset(temp); }
            catch (Exception ex) { cleanup = false; cleanupError = ex.Message; }
            if (!cleanup)
            {
                if (!(receipt["data"] is JObject)) receipt["data"] = new JObject();
                receipt["data"]["transaction_succeeded"] = receipt.Value<bool>("success");
                receipt["data"]["cleanup_pending"] = true; receipt["data"]["preflight_path"] = temp;
                receipt["data"]["cleanup_error"] = cleanupError; receipt["success"] = false; receipt["code"] = "REFERENCE_PREFLIGHT_CLEANUP_PENDING";
            }
            return receipt;
        }
        private static object PatchCore(string path, long localId, string property, string expectedHash, string expectedGuid, long expectedFileId, string replacementPath, long replacementLocalId, string temp)
        {
            string backup = null, originalHash = null; bool liveTouched = false;
            try
            {
                if (EditorApplication.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating) return Response.Error("STABLE_EDIT_MODE_REQUIRED");
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains("..") || path.Contains('\\') ||
                    (Path.GetExtension(path) != ".prefab" && Path.GetExtension(path) != ".asset") || !File.Exists(path)) return Response.Error("INVALID_TRANSACTION_ASSET");
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0 || !AssetDatabase.IsOpenForEdit(path)) return Response.Error("ASSET_NOT_WRITABLE");
                if (new FileInfo(path).Length > 16 * 1024 * 1024) return Response.Error("REFERENCE_TRANSACTION_ASSET_TOO_LARGE");
                if (PrefabStageUtility.GetCurrentPrefabStage()?.assetPath == path) return Response.Error("PREFAB_STAGE_OPEN");
                var all = Targets(path); if (all.Any(EditorUtility.IsDirty)) return Response.Error("ASSET_HAS_UNSAVED_CHANGES");
                var target = Resolve(all, localId); if (target == null) return Response.Error("SERIALIZED_TARGET_NOT_FOUND");
                var so = new SerializedObject(target); var field = so.FindProperty(property);
                if (field == null || field.propertyType != SerializedPropertyType.ObjectReference) return Response.Error("OBJECT_REFERENCE_PROPERTY_REQUIRED");
                Object replacement = null;
                if (replacementPath != null)
                {
                    if ((!replacementPath.StartsWith("Assets/", StringComparison.Ordinal) && !replacementPath.StartsWith("Packages/", StringComparison.Ordinal)) || replacementPath.Contains("..") || replacementPath.Contains('\\')) return Response.Error("INVALID_REPLACEMENT_ASSET");
                    replacement = Resolve(Targets(replacementPath), replacementLocalId);
                    if (replacement == null) return Response.Error("REPLACEMENT_SUBASSET_NOT_FOUND");
                }
                var bytes = File.ReadAllBytes(path); originalHash = Hash(bytes);
                if (originalHash != expectedHash) return Response.Error("ASSET_PRECONDITION_FAILED", new { actual_sha256 = originalHash });
                string self = AssetDatabase.AssetPathToGUID(path);
                var before = Inventory(Encoding.UTF8.GetString(bytes), self);
                string oldGuid = (expectedGuid ?? "").ToLowerInvariant(); if (oldGuid == self) oldGuid = "$self";
                string oldPrefix = expectedFileId + ":" + oldGuid + ":";
                var slots = before.Where(x => x.Key.StartsWith(localId + "#", StringComparison.Ordinal) && x.Value.StartsWith(oldPrefix, StringComparison.Ordinal)).ToArray();
                if (slots.Length != 1) return Response.Error("REFERENCE_SLOT_AMBIGUOUS", new { matches = slots.Length, hint = "A unique expected old PPtr within the selected serialized document is required; no save was attempted." });
                string newRef = "0::";
                if (replacement != null)
                {
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(replacement, out string guid, out long id)) return Response.Error("REPLACEMENT_NOT_PERSISTENT");
                    newRef = id + ":" + (guid == self ? "$self" : guid) + ":";
                }
                string slot = slots[0].Key;
                if (!AssetDatabase.CopyAsset(path, temp)) return Response.Error("REFERENCE_PREFLIGHT_COPY_FAILED");
                AssetDatabase.ImportAsset(temp, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var copyBaseline = Inventory(File.ReadAllText(temp), AssetDatabase.AssetPathToGUID(temp));
                if (!Preserved(before, copyBaseline, null, null)) return Response.Error("REFERENCE_COPY_NOT_EQUIVALENT", new { live_asset_untouched = true });
                Apply(temp, localId, property, replacement);
                var trial = Inventory(File.ReadAllText(temp), AssetDatabase.AssetPathToGUID(temp));
                // Derive type from Unity's actual save, never a guessed importer mapping.
                if (!trial.TryGetValue(slot, out var actualNew) || !actualNew.StartsWith(newRef, StringComparison.Ordinal) ||
                    !Preserved(before, trial, slot, actualNew)) return Response.Error("REFERENCE_PREFLIGHT_FAILED", new { live_asset_untouched = true, hint = "Untouched GUID/fileID slots changed, disappeared or reordered. Do not save the live graph." });
                if (HashFile(path) != originalHash || Targets(path).Any(EditorUtility.IsDirty)) return Response.Error("ASSET_CHANGED_DURING_PREFLIGHT");
                var backupDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "FunplayMcp", "ReferenceBackups");
                Directory.CreateDirectory(backupDir); backup = Path.Combine(backupDir, Guid.NewGuid().ToString("N") + ".bytes");
                File.WriteAllBytes(backup, bytes); liveTouched = true;
                Apply(path, localId, property, replacement);
                var after = Inventory(File.ReadAllText(path), self);
                if (!Preserved(before, after, slot, actualNew)) throw new InvalidDataException("LIVE_REFERENCE_COMPARISON_FAILED");
                return Response.Success("Serialized reference transaction verified after synchronous reimport.", new {
                    asset_path = path, local_file_id = localId, property, before_sha256 = originalHash,
                    after_sha256 = HashFile(path), protected_reference_slots = before.Count - 1,
                    old_reference = slots[0].Value, new_reference = actualNew, backup_path = backup, rollback_needed = false,
                    context = MCPProjectContext.Current,
                    limits = "Exact PPtr slot preservation only; serialization callback business/network side effects are not reversible. Unsupported reference encodings/reorderings are rejected."
                });
            }
            catch (Exception ex)
            {
                bool restored = false; string restoreError = null;
                if (liveTouched && backup != null)
                {
                    try
                    {
                        FileUtil.ReplaceFile(backup, path);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        restored = HashFile(path) == originalHash;
                        if (!restored) restoreError = "Import callbacks changed the restored bytes; retained backup requires review.";
                    } catch (Exception restore) { restoreError = restore.Message; }
                }
                return Response.Error("REFERENCE_TRANSACTION_FAILED", new { detail = ex.Message, live_asset_touched = liveTouched,
                    rollback_verified = restored, cleanup_pending = liveTouched && !restored, backup_path = backup, restore_error = restoreError });
            }
        }
        private static Object[] Targets(string path)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return root == null ? assets : assets.Concat(root.GetComponentsInChildren<Component>(true).Where(c => c != null).Cast<Object>())
                .Concat(root.GetComponentsInChildren<Transform>(true).Select(t => (Object)t.gameObject)).Distinct().ToArray();
        }
        private static Object Resolve(IEnumerable<Object> objects, long id) => objects.FirstOrDefault(x => x != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(x, out string _, out long actual) && actual == id);
        private static void Apply(string path, long id, string property, Object value)
        {
            var target = Resolve(Targets(path), id); if (target == null) throw new InvalidDataException("Target changed during preflight.");
            var so = new SerializedObject(target); var field = so.FindProperty(property);
            if (field == null || field.propertyType != SerializedPropertyType.ObjectReference) throw new InvalidDataException("Property changed during preflight.");
            field.objectReferenceValue = value; so.ApplyModifiedPropertiesWithoutUndo();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) { PrefabUtility.SavePrefabAsset(prefab, out bool success); if (!success) throw new IOException("Prefab save failed."); }
            else AssetDatabase.SaveAssetIfDirty(target);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var persisted = new SerializedObject(Resolve(Targets(path), id)).FindProperty(property);
            if (persisted == null || persisted.objectReferenceValue != value) throw new InvalidDataException("Persisted reference differs from the requested value.");
        }
    }
}
