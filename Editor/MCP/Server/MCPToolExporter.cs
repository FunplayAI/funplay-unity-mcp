// Copyright (C) GameBooom. Licensed under GPLv3.

using System.Collections.Generic;
using GameBooom.Editor.Tools;
using UnityEngine;

namespace GameBooom.Editor.MCP.Server
{
    /// <summary>
    /// Exports GameBooom tool definitions to MCP tool schema format.
    /// </summary>
    internal class MCPToolExporter
    {
        public List<Dictionary<string, object>> ExportTools()
        {
            var mcpTools = new List<Dictionary<string, object>>();
            var tools = ToolSchemaBuilder.BuildAll();

            Debug.Log($"[GameBooom MCP Server] Exporting {tools.Count} tools");

            foreach (var tool in tools)
            {
                var mcpTool = new Dictionary<string, object>
                {
                    ["name"] = tool.function.name,
                    ["description"] = tool.function.description,
                    ["inputSchema"] = ConvertParametersToJsonSchema(tool.function.parameters)
                };
                mcpTools.Add(mcpTool);
            }

            return mcpTools;
        }

        private Dictionary<string, object> ConvertParametersToJsonSchema(
            GameBooom.Editor.Api.Models.ToolParametersDef parameters)
        {
            var properties = new Dictionary<string, object>();

            foreach (var prop in parameters.properties)
            {
                var propertySchema = new Dictionary<string, object>
                {
                    ["type"] = prop.Value.type,
                    ["description"] = prop.Value.description
                };

                if (prop.Value.@enum != null && prop.Value.@enum.Count > 0)
                    propertySchema["enum"] = prop.Value.@enum;

                if (!string.IsNullOrEmpty(prop.Value.@default))
                    propertySchema["default"] = prop.Value.@default;

                properties[prop.Key] = propertySchema;
            }

            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties
            };

            if (parameters.required != null && parameters.required.Count > 0)
                schema["required"] = parameters.required;

            return schema;
        }
    }
}
