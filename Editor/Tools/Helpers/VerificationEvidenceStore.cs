// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Funplay.Editor.MCP.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Helpers
{
    internal static class VerificationEvidenceStore
    {
        private const int Schema = 1;
        private static string DirectoryPath => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "FunplayMcp", "VerificationEvidence");
        internal static JObject Fingerprint(string pathsJson, string configuration)
        {
            var paths = JArray.Parse(pathsJson);
            if (paths.Count < 1 || paths.Count > 200 || paths.Any(t => t.Type != JTokenType.String)) throw new ArgumentException("Use 1..200 project-relative file paths.");
            var config = string.IsNullOrEmpty(configuration) ? new JObject() : JObject.Parse(configuration);
            if (config.ToString(Formatting.None).Length > 32768) throw new ArgumentException("Configuration exceeds 32 KiB.");
            var inputs = new JArray();
            foreach (var path in paths.Values<string>().Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                if (path == null || path.Contains("..") || path.Contains('\\') || (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("ProjectSettings/", StringComparison.Ordinal) && path != "Packages/manifest.json" && path != "Packages/packages-lock.json"))
                    throw new ArgumentException("Only explicit Assets/ProjectSettings files or package manifests are supported.");
                if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Missing, oversized or linked input: " + path);
                inputs.Add(new JObject { ["path"] = path, ["sha256"] = SerializedReferenceTransaction.Hash(File.ReadAllBytes(path)),
                    ["meta_sha256"] = File.Exists(path + ".meta") ? SerializedReferenceTransaction.Hash(File.ReadAllBytes(path + ".meta")) : null,
                    ["dependencies"] = path.StartsWith("Assets/", StringComparison.Ordinal) ? AssetDatabase.GetAssetDependencyHash(path).ToString() : null });
            }
            return new JObject { ["schema_version"] = Schema, ["project_id"] = MCPProjectContext.Current.project_id,
                ["unity_version"] = Application.unityVersion, ["configuration"] = Canonical(config), ["inputs"] = inputs };
        }
        internal static JToken Canonical(JToken value) => value is JObject obj ? new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value)))) :
            value is JArray array ? new JArray(array.Select(Canonical)) : value.DeepClone();
        internal static string KeyFor(JObject fingerprint) => SerializedReferenceTransaction.Hash(Encoding.UTF8.GetBytes(fingerprint.ToString(Formatting.None)));
        internal static object Save(string paths, string configuration, string checkpoint)
        {
            try
            {
                var fingerprint = Fingerprint(paths, configuration);
                var plan = JObject.Parse(checkpoint);
                if (plan.ToString(Formatting.None).Length > 32768) return Response.Error("CHECKPOINT_TOO_LARGE");
                var inventory = new JArray();
                foreach (var input in fingerprint["inputs"])
                {
                    string path = (string)input["path"]; string error = null; int? refs = null, unresolved = null, missingScripts = null;
                    if (new[] { ".prefab", ".asset", ".unity" }.Contains(Path.GetExtension(path)))
                    {
                        try
                        {
                            var raw = SerializedReferenceTransaction.Inventory(File.ReadAllText(path)); refs = raw.Count;
                            unresolved = raw.Values.Count(v => { var parts = v.Split(':'); return parts[1].Length == 32 && string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(parts[1])); });
                            // Missing scripts are a separate structural count, never equated with
                            // unresolved PPtrs. Saved scenes are not opened to obtain this count.
                            var root = path.EndsWith(".prefab", StringComparison.Ordinal) ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
                            if (root != null) missingScripts = root.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
                        } catch (Exception ex) { error = ex.Message; }
                    }
                    inventory.Add(new JObject { ["path"] = path, ["reference_slots"] = refs, ["unresolved_guid_slots"] = unresolved, ["missing_scripts"] = missingScripts,
                        ["inventory_error"] = error, ["note"] = "Unresolved GUID count does not detect missing subasset local IDs; null counts mean not examined, not zero." });
                }
                // An import callback or concurrent source writer must not turn a cached inventory
                // into evidence for bytes we never examined.
                if (!JToken.DeepEquals(fingerprint, Fingerprint(paths, configuration))) return Response.Error("EVIDENCE_INPUT_CHANGED");
                string key = KeyFor(fingerprint);
                var receipt = new JObject { ["fingerprint"] = fingerprint, ["inventory"] = inventory, ["checkpoint"] = plan,
                    ["checkpoint_provenance"] = "Caller-authored continuation notes, not independently verified runtime acceptance.",
                    ["observed_at"] = DateTime.UtcNow.ToString("O"), ["context"] = JObject.FromObject(MCPProjectContext.Current) };
                System.IO.Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(Path.Combine(DirectoryPath, key + ".json"), receipt.ToString(Formatting.Indented));
                return Response.Success("Static evidence/checkpoint saved. Runtime observations are never reused as fresh state.", new { evidence_id = key, evidence = receipt });
            }
            catch (Exception ex) { return Response.Error("EVIDENCE_SAVE_FAILED", new { detail = ex.Message }); }
        }
        internal static object Read(string id, string configuration)
        {
            try
            {
                if (id == null || id.Length != 64 || id.Any(c => !"0123456789abcdef".Contains(c))) return Response.Error("INVALID_EVIDENCE_ID");
                string path = Path.Combine(DirectoryPath, id + ".json"); if (!File.Exists(path)) return Response.Error("EVIDENCE_NOT_FOUND");
                if (new FileInfo(path).Length > 2 * 1024 * 1024) return Response.Error("INVALID_EVIDENCE_FILE");
                var receipt = JObject.Parse(File.ReadAllText(path)); var old = (JObject)receipt["fingerprint"];
                if (old == null || KeyFor(old) != id) return Response.Error("EVIDENCE_FINGERPRINT_CORRUPT");
                var paths = new JArray(old["inputs"].Select(x => x["path"]));
                var current = Fingerprint(paths.ToString(Formatting.None), configuration ?? old["configuration"].ToString(Formatting.None));
                bool valid = JToken.DeepEquals(old, current);
                return Response.Success("Cached static evidence revalidated against current files/import dependencies; runtime is not cached.", new {
                    evidence_id = id, cache_valid = valid, invalidated = !valid, evidence = receipt,
                    current_context = MCPProjectContext.Current, requires_fresh_runtime_read = true,
                    note = "Pass current task configuration explicitly when it changed. Saved checkpoint claims are historical; recapture frames, input and application predicates before accepting current runtime behavior."
                });
            }
            catch (Exception ex) { return Response.Error("EVIDENCE_READ_FAILED", new { detail = ex.Message, cache_valid = false }); }
        }
    }
}
