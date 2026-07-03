// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    /// <summary>
    /// Read, write, and create ScriptableObject assets through the same SerializedObject
    /// machinery used for components — picks up [SerializeField] private fields,
    /// supports Object references, returns per-field success.
    /// </summary>
    [ToolProvider("ScriptableObject")]
    internal static class ScriptableObjectFunctions
    {
        [Description("Create a new ScriptableObject asset of the given type at the given path. " +
                     "The type must derive from ScriptableObject and must not be abstract. " +
                     "Follow with set_scriptable_object_properties to fill in fields.")]
        public static object CreateScriptableObject(
            [ToolParam("ScriptableObject-derived type name (e.g. 'MyGameConfig' or full 'MyGame.Configs.MyGameConfig')")] string type_name,
            [ToolParam("Asset path to create (e.g. 'Assets/Configs/NewConfig.asset')")] string asset_path)
        {
            if (string.IsNullOrEmpty(type_name))
                return Response.Error("TYPE_NAME_REQUIRED");
            if (string.IsNullOrEmpty(asset_path) || !asset_path.StartsWith("Assets/") || !asset_path.EndsWith(".asset"))
                return Response.Error("INVALID_ASSET_PATH", new { asset_path, hint = "Must start with 'Assets/' and end with '.asset'." });
            if (asset_path.Contains("..") || asset_path.Contains("\\"))
                return Response.Error("INVALID_ASSET_PATH", new { asset_path, hint = "Use a normalized Unity asset path under Assets/ without '..' or backslashes." });

            var type = ResolveScriptableObjectType(type_name);
            if (type == null)
                return Response.Error("TYPE_NOT_FOUND", new { type_name, hint = "Type must derive from ScriptableObject and be loaded." });
            if (type.IsAbstract)
                return Response.Error("TYPE_IS_ABSTRACT", new { type = type.FullName });

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(asset_path) != null)
                return Response.Error("ASSET_ALREADY_EXISTS", new { asset_path });

            var dir = System.IO.Path.GetDirectoryName(asset_path);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                return Response.Error("FOLDER_NOT_FOUND", new { folder = dir, hint = "Create the folder first in the Project window or with an existing file/folder workflow." });

            var instance = ScriptableObject.CreateInstance(type);
            if (instance == null)
                return Response.Error("CREATE_INSTANCE_FAILED", new { type = type.FullName });

            AssetDatabase.CreateAsset(instance, asset_path);
            AssetDatabase.SaveAssets();

            return Response.Success($"Created {type.Name} asset at {asset_path}.", new
            {
                assetPath = asset_path,
                type = type.FullName,
                instanceId = ObjectIdHelper.GetSerializableId(instance)
            });
        }

        [Description("Get all serialized properties of a ScriptableObject asset, including [SerializeField] private fields. " +
                     "Works on any .asset file whose main object derives from ScriptableObject.")]
        [ReadOnlyTool]
        public static object GetScriptableObject(
            [ToolParam("Asset path (e.g. 'Assets/Configs/GameConfig.asset')")] string asset_path)
        {
            var resolved = LoadScriptableObject(asset_path);
            if (resolved.Error != null) return resolved.Error;

            var props = ComponentSerializer.ReadProperties(resolved.Asset);
            return Response.Success($"{props.Count} properties on {resolved.Asset.GetType().Name}.", new
            {
                assetPath = asset_path,
                type = resolved.Asset.GetType().FullName,
                instanceId = ObjectIdHelper.GetSerializableId(resolved.Asset),
                properties = props
            });
        }

        [Description("Set one or more serialized properties on a ScriptableObject asset and save it. " +
                     "Pass `properties` as a JSON object: {\"maxHealth\": 100, \"displayName\": \"Boss\", \"icon\": {\"assetPath\": \"Assets/...\"}}. " +
                     "Returns per-field success so partial failures are diagnosable. Changes are Undo-able and persisted to disk.")]
        public static object SetScriptableObjectProperties(
            [ToolParam("Asset path (e.g. 'Assets/Configs/GameConfig.asset')")] string asset_path,
            [ToolParam("JSON object of property→value pairs")] string properties)
        {
            if (string.IsNullOrWhiteSpace(properties))
                return Response.Error("PROPERTIES_REQUIRED");

            var resolved = LoadScriptableObject(asset_path);
            if (resolved.Error != null) return resolved.Error;

            JObject jobj;
            try { jobj = JObject.Parse(properties); }
            catch (Exception ex) { return Response.Error("INVALID_PROPERTIES_JSON", new { message = ex.Message }); }

            var results = ComponentSerializer.WriteProperties(resolved.Asset, jobj,
                $"Set properties on {resolved.Asset.GetType().Name}");

            AssetDatabase.SaveAssetIfDirty(resolved.Asset);

            int success = results.Count(r => r.Success);
            int fail = results.Count - success;
            return Response.Success(
                $"Applied {success} of {results.Count} field(s) on {resolved.Asset.GetType().Name}, asset saved.",
                new
                {
                    assetPath = asset_path,
                    successCount = success,
                    failCount = fail,
                    fields = results
                });
        }

        // -------- Helpers --------

        private struct ResolvedScriptableObject
        {
            public ScriptableObject Asset;
            public object Error;
        }

        private static ResolvedScriptableObject LoadScriptableObject(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return new ResolvedScriptableObject { Error = Response.Error("ASSET_PATH_REQUIRED") };

            var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj == null)
                return new ResolvedScriptableObject { Error = Response.Error("ASSET_NOT_FOUND", new { asset_path = assetPath }) };

            if (!(obj is ScriptableObject so))
                return new ResolvedScriptableObject
                {
                    Error = Response.Error("NOT_A_SCRIPTABLE_OBJECT",
                        new { asset_path = assetPath, actualType = obj.GetType().FullName })
                };

            return new ResolvedScriptableObject { Asset = so };
        }

        private static Type ResolveScriptableObjectType(string typeName)
        {
            var type = TypeResolver.Resolve(typeName);
            if (type != null && typeof(ScriptableObject).IsAssignableFrom(type))
                return type;

            // Fallback: scan loaded assemblies for a ScriptableObject-derived type with a matching short name
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if ((t.Name == typeName || t.FullName == typeName) &&
                        typeof(ScriptableObject).IsAssignableFrom(t))
                        return t;
                }
            }
            return null;
        }
    }
}
