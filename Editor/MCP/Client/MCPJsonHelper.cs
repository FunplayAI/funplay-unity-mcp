// Copyright (C) GameBooom. Licensed under GPLv3.

using System.Collections.Generic;
using GameBooom.Editor.Api;
using GameBooom.Editor.Api.Models;

namespace GameBooom.Editor.MCP.Client
{
    /// <summary>
    /// Parses MCP JSON-RPC responses for tools/list and tools/call results.
    /// </summary>
    internal static class MCPJsonHelper
    {
        public static List<ToolDefinition> ParseToolsList(string json)
        {
            var definitions = new List<ToolDefinition>();
            if (string.IsNullOrEmpty(json)) return definitions;

            var root = JsonParse.ParseJsonObject(json, 0, out _);
            if (root == null) return definitions;

            if (!root.ContainsKey("result") || !(root["result"] is Dictionary<string, object> result))
                return definitions;

            if (!result.ContainsKey("tools") || !(result["tools"] is List<object> tools))
                return definitions;

            foreach (var toolObj in tools)
            {
                if (!(toolObj is Dictionary<string, object> tool)) continue;

                var name = tool.ContainsKey("name") ? tool["name"] as string : null;
                var description = tool.ContainsKey("description") ? tool["description"] as string : null;
                if (string.IsNullOrEmpty(name)) continue;

                var def = new ToolDefinition
                {
                    type = "function",
                    function = new ToolFunctionDef
                    {
                        name = name,
                        description = description ?? "",
                        parameters = new ToolParametersDef()
                    }
                };

                if (tool.ContainsKey("inputSchema") && tool["inputSchema"] is Dictionary<string, object> schema)
                    def.function.parameters = ConvertInputSchema(schema);

                definitions.Add(def);
            }

            return definitions;
        }

        public static string ExtractCallResult(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            var root = JsonParse.ParseJsonObject(json, 0, out _);
            if (root == null) return null;

            if (!root.ContainsKey("result") || !(root["result"] is Dictionary<string, object> result))
            {
                if (root.ContainsKey("error") && root["error"] is Dictionary<string, object> error)
                {
                    var message = error.ContainsKey("message") ? error["message"] as string : "Unknown MCP error";
                    return $"Error: {message}";
                }
                return null;
            }

            if (!result.ContainsKey("content") || !(result["content"] is List<object> content))
                return null;

            var texts = new System.Text.StringBuilder();
            foreach (var item in content)
            {
                if (!(item is Dictionary<string, object> contentItem)) continue;
                var type = contentItem.ContainsKey("type") ? contentItem["type"] as string : null;
                if (type == "text" && contentItem.ContainsKey("text"))
                {
                    if (texts.Length > 0) texts.Append("\n");
                    texts.Append(contentItem["text"] as string ?? "");
                }
            }

            return texts.Length > 0 ? texts.ToString() : null;
        }

        private static ToolParametersDef ConvertInputSchema(Dictionary<string, object> schema)
        {
            var paramsDef = new ToolParametersDef();

            if (schema.ContainsKey("properties") && schema["properties"] is Dictionary<string, object> props)
            {
                foreach (var kvp in props)
                {
                    var propDef = new ToolPropertyDef { type = "string" };

                    if (kvp.Value is Dictionary<string, object> propSchema)
                    {
                        if (propSchema.ContainsKey("type"))
                            propDef.type = propSchema["type"] as string ?? "string";
                        if (propSchema.ContainsKey("description"))
                            propDef.description = propSchema["description"] as string;
                        if (propSchema.ContainsKey("default"))
                            propDef.@default = propSchema["default"]?.ToString();
                        if (propSchema.ContainsKey("enum") && propSchema["enum"] is List<object> enumValues)
                        {
                            propDef.@enum = new List<string>();
                            foreach (var ev in enumValues)
                                propDef.@enum.Add(ev?.ToString() ?? "");
                        }
                    }

                    paramsDef.properties[kvp.Key] = propDef;
                }
            }

            if (schema.ContainsKey("required") && schema["required"] is List<object> required)
            {
                foreach (var r in required)
                {
                    var reqStr = r as string;
                    if (!string.IsNullOrEmpty(reqStr))
                        paramsDef.required.Add(reqStr);
                }
            }

            return paramsDef;
        }
    }
}
