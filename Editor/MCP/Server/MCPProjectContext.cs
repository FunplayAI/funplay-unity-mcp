// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
{
    [Serializable]
    internal sealed class ProjectExecutionContext
    {
        public string project_id, instance_id, editor_epoch, project_path;
        public string expected_context => project_id + ":" + instance_id;
    }

    [InitializeOnLoad]
    internal static class MCPProjectContext
    {
        internal const string ExpectedArgument = "expected_context";
        private const string SessionKey = "Funplay.ExecutionTrust.EditorInstance";
        // Created on the Editor thread at load. Immutable identity is safe to read on transports.
        internal static readonly ProjectExecutionContext Current;
        // The lightweight JSON-RPC serializer handles dictionaries/lists, not arbitrary DTOs.
        // Keep native protocol metadata structural (tool envelopes use Json.NET separately).
        internal static Dictionary<string, object> Metadata => new Dictionary<string, object>
        {
            ["project_id"] = Current.project_id, ["instance_id"] = Current.instance_id,
            ["editor_epoch"] = Current.editor_epoch, ["project_path"] = Current.project_path,
            ["expected_context"] = Current.expected_context
        };
        static MCPProjectContext()
        {
            var instance = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(instance))
            {
                instance = Guid.NewGuid().ToString("N");
                SessionState.SetString(SessionKey, instance);
            }
            var path = Path.GetDirectoryName(Application.dataPath);
            Current = new ProjectExecutionContext { project_id = FunplayProjectIdentity.FromProjectPath(path),
                instance_id = instance, editor_epoch = Guid.NewGuid().ToString("N"), project_path = path };
        }

        internal static string Endpoint(int port, string projectId) =>
            "http://127.0.0.1:" + port + "/mcp/project/" + projectId + "/";

        internal static bool TryRoute(string target, out string projectId)
        {
            projectId = null;
            if (string.IsNullOrEmpty(target)) return false;
            var path = target.Split('?')[0];
            if (path == "/" || path == "/mcp" || path == "/mcp/") return true;
            const string prefix = "/mcp/project/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var id = path.Substring(prefix.Length).TrimEnd('/');
            if (id.Length != 64) return false;
            foreach (var c in id) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            projectId = id;
            return true;
        }

        internal static string Validate(ProjectExecutionContext actual, string routeProject, string expected, bool mutating)
        {
            if (!string.IsNullOrEmpty(routeProject) && routeProject != actual.project_id) return "PROJECT_CONTEXT_MISMATCH";
            if (!string.IsNullOrEmpty(expected))
            {
                var parts = expected.Split(':');
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) return "INVALID_EXPECTED_CONTEXT";
                if (parts[0] != actual.project_id) return "PROJECT_CONTEXT_MISMATCH";
                if (parts[1] != actual.instance_id) return "EDITOR_INSTANCE_CHANGED";
            }
            if (mutating && string.IsNullOrEmpty(routeProject) && string.IsNullOrEmpty(expected)) return "PROJECT_CONTEXT_REQUIRED";
            return null;
        }

        internal static string Stamp(string result)
        {
            if (string.IsNullOrEmpty(result) || result[0] != '{') return result;
            try
            {
                var json = JObject.Parse(result);
                if (json["success"] == null) return result;
                json["context"] = JObject.FromObject(Current);
                return json.ToString(Formatting.None);
            }
            catch (JsonException) { return result; }
        }
    }
}
