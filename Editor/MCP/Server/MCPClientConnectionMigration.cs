// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>Read-only inspection of saved client entries, not evidence of a live connection.</summary>
    internal static class MCPClientConnectionMigration
    {
        internal static bool NeedsReconfiguration(
            string content,
            FunplayMCPClientConfigPanel.MCPConfigTarget target,
            string projectScopeKey,
            string currentServerKey,
            string recordedServerKey)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            var keys = new[] { recordedServerKey, currentServerKey, FunplayMCPServerKey.LegacyKey };
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            if (target.IsDeepSeekHarness)
            {
                foreach (var key in keys)
                {
                    if (!string.IsNullOrEmpty(key) &&
                        FunplayDeepSeekHarnessPatch.TryGetManagedBlockUrl(content, key, out var url))
                        urls[key] = url;
                }
            }
            else if (target.IsToml)
            {
                foreach (var key in keys)
                {
                    if (!FunplayMCPClientConfigPanel.TryFindTomlSection(content, key, out var start, out var end))
                        continue;

                    // Only an active URL assignment in this exact section, never a comment or
                    // the next server's URL. Support both TOML quote styles for read-only checks.
                    var match = Regex.Match(content.Substring(start, end - start),
                        "(?m)^[ \\t]*url[ \\t]*=[ \\t]*(?:\"([^\"\\r\\n]*)\"|'([^'\\r\\n]*)')[ \\t]*(?:#.*)?\\r?$");
                    urls[key] = match.Success
                        ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
                        : null;
                }
            }
            else
            {
                var root = JObject.Parse(content);
                var rootKey = string.IsNullOrEmpty(target.RootKey) ? "mcpServers" : target.RootKey;
                var globalServers = root[rootKey] as JObject;
                var projectServers = target.UseProjectScope && !string.IsNullOrEmpty(projectScopeKey)
                    ? (root["projects"] as JObject)?[projectScopeKey] as JObject
                    : null;
                projectServers = projectServers?[rootKey] as JObject;

                foreach (var key in keys)
                {
                    if (string.IsNullOrEmpty(key))
                        continue;

                    // Project entries take precedence; other projects' sections are never inspected.
                    var entry = projectServers?[key] ?? globalServers?[key];
                    var field = string.IsNullOrEmpty(target.UrlFieldName) ? "url" : target.UrlFieldName;
                    if (entry is JObject map)
                        urls[key] = map[field]?.Type == JTokenType.String ? (string)map[field] : null;
                }
            }

            // A project-owned/current entry takes precedence over the old shared name. Once
            // reconfigured, unrelated legacy entries must not leave this project's notice stuck on.
            foreach (var key in keys)
                if (!string.IsNullOrEmpty(key) && urls.TryGetValue(key, out var url))
                    return IsLegacyEndpoint(url);

            return false;
        }

        internal static bool IsLegacyEndpoint(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                !uri.IsLoopback)
                return false;

            return uri.AbsolutePath == "/" || uri.AbsolutePath == "/mcp" || uri.AbsolutePath == "/mcp/";
        }
    }
}
