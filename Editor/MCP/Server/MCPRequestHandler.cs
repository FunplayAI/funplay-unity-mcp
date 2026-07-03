// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Funplay.Editor.Settings;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Handles MCP protocol requests (initialize, tools/list, tools/call, etc.)
    /// </summary>
    internal class MCPRequestHandler
    {
        private readonly MCPToolExporter _toolExporter;
        private readonly MCPExecutionBridge _executionBridge;
        private readonly MCPResourceProvider _resourceProvider;
        private readonly MCPPromptProvider _promptProvider;
        private readonly string _serverName;
        private readonly string _serverVersion;
        private readonly string _projectIdentity;

        public MCPRequestHandler(
            MCPToolExporter toolExporter,
            MCPExecutionBridge executionBridge,
            MCPResourceProvider resourceProvider,
            MCPPromptProvider promptProvider,
            string serverName,
            string serverVersion,
            string projectIdentity)
        {
            _toolExporter = toolExporter ?? throw new ArgumentNullException(nameof(toolExporter));
            _executionBridge = executionBridge ?? throw new ArgumentNullException(nameof(executionBridge));
            _resourceProvider = resourceProvider ?? throw new ArgumentNullException(nameof(resourceProvider));
            _promptProvider = promptProvider ?? throw new ArgumentNullException(nameof(promptProvider));
            _serverName = string.IsNullOrWhiteSpace(serverName) ? "Funplay MCP Server" : serverName;
            _serverVersion = string.IsNullOrWhiteSpace(serverVersion) ? "0.0.0" : serverVersion;
            _projectIdentity = projectIdentity ?? string.Empty;
        }

        public async Task<MCPResponse> HandleRequestAsync(MCPRequest request, CancellationToken ct)
        {
            try
            {
                if (request == null)
                    return CreateErrorResponse(null, -32600, "Invalid Request");

                if (request.JsonRpc != "2.0")
                    return CreateErrorResponse(request.Id, -32600, "Invalid Request: jsonrpc must be '2.0'");

                if (ShouldLogRequest(request.Method))
                    PluginDebugLogger.Log($"[Funplay MCP Server] Handling request: {request.Method}");

                return request.Method switch
                {
                    "initialize" => HandleInitialize(request),
                    "notifications/initialized" => null,
                    "notifications/cancelled" => null,
                    "tools/list" => HandleToolsList(request),
                    "tools/call" => await HandleToolsCallAsync(request, ct),
                    "prompts/list" => HandlePromptsList(request),
                    "prompts/get" => HandlePromptsGet(request),
                    "resources/list" => HandleResourcesList(request),
                    "resources/read" => HandleResourcesRead(request),
                    "resources/templates/list" => HandleResourceTemplatesList(request),
                    _ when request.Method != null && request.Method.StartsWith("notifications/") => null,
                    _ => CreateErrorResponse(request.Id, -32601, $"Method not found: {request.Method}")
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error handling request: {ex.Message}\n{ex.StackTrace}");
                return CreateErrorResponse(request?.Id, -32603, $"Internal error: {ex.Message}");
            }
        }

        private MCPResponse HandleInitialize(MCPRequest request)
        {
            var result = new Dictionary<string, object>
            {
                ["protocolVersion"] = "2024-11-05",
                ["serverInfo"] = new Dictionary<string, object>
                {
                    ["name"] = _serverName,
                    ["version"] = _serverVersion
                },
                ["funplay"] = new Dictionary<string, object>
                {
                    ["projectIdentity"] = _projectIdentity,
                    ["projectIdentityVersion"] = FunplayProjectIdentity.IdentityVersion
                },
                ["capabilities"] = new Dictionary<string, object>
                {
                    // listChanged: the server piggybacks notifications/tools/list_changed
                    // onto the next POST response (SSE) after the exposed tool set changes.
                    ["tools"] = new Dictionary<string, object> { ["listChanged"] = true },
                    ["resources"] = new Dictionary<string, object>(),
                    ["prompts"] = new Dictionary<string, object>()
                }
            };

            PluginDebugLogger.Log("[Funplay MCP Server] Initialized successfully");
            return new MCPResponse { Id = request.Id, Result = result };
        }

        private MCPResponse HandleToolsList(MCPRequest request)
        {
            var tools = _toolExporter.ExportTools();
            PluginDebugLogger.Log($"[Funplay MCP Server] Returning {tools.Count} tools");

            return new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object> { ["tools"] = tools }
            };
        }

        private async Task<MCPResponse> HandleToolsCallAsync(MCPRequest request, CancellationToken ct)
        {
            try
            {
                if (!request.Params.TryGetValue("name", out var nameObj) || !(nameObj is string toolName))
                    return CreateErrorResponse(request.Id, -32602, "Invalid params: 'name' is required");

                var arguments = request.Params.ContainsKey("arguments") && request.Params["arguments"] is Dictionary<string, object> args
                    ? args
                    : new Dictionary<string, object>();

                PluginDebugLogger.Log($"[Funplay MCP Server] Calling tool: {toolName}");
                var result = await _executionBridge.ExecuteToolAsync(toolName, arguments, ct);

                return new MCPResponse
                {
                    Id = request.Id,
                    Result = new Dictionary<string, object>
                    {
                        ["content"] = BuildContentFromResult(result)
                    }
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay MCP Server] Error executing tool: {ex.Message}");
                return CreateErrorResponse(request.Id, -32603, $"Tool execution failed: {ex.Message}");
            }
        }

        private MCPResponse HandlePromptsList(MCPRequest request)
        {
            return new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object>
                {
                    ["prompts"] = _promptProvider.ListPrompts()
                }
            };
        }

        private MCPResponse HandlePromptsGet(MCPRequest request)
        {
            if (request.Params == null ||
                !request.Params.TryGetValue("name", out var nameObj) ||
                !(nameObj is string promptName) ||
                string.IsNullOrWhiteSpace(promptName))
            {
                return CreateErrorResponse(request.Id, -32602, "Invalid params: 'name' is required");
            }

            var arguments = request.Params.ContainsKey("arguments") && request.Params["arguments"] is Dictionary<string, object> args
                ? args
                : new Dictionary<string, object>();

            return new MCPResponse
            {
                Id = request.Id,
                Result = _promptProvider.GetPrompt(promptName, arguments)
            };
        }

        private MCPResponse HandleResourcesList(MCPRequest request)
        {
            return new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object>
                {
                    ["resources"] = _resourceProvider.ListResources()
                }
            };
        }

        private MCPResponse HandleResourcesRead(MCPRequest request)
        {
            if (request.Params == null ||
                !request.Params.TryGetValue("uri", out var uriObj) ||
                !(uriObj is string uri) ||
                string.IsNullOrWhiteSpace(uri))
            {
                return CreateErrorResponse(request.Id, -32602, "Invalid params: 'uri' is required");
            }

            return new MCPResponse
            {
                Id = request.Id,
                Result = _resourceProvider.ReadResource(uri)
            };
        }

        private MCPResponse HandleResourceTemplatesList(MCPRequest request)
        {
            return new MCPResponse
            {
                Id = request.Id,
                Result = new Dictionary<string, object>
                {
                    ["resourceTemplates"] = _resourceProvider.ListResourceTemplates()
                }
            };
        }

        private const string ImageDataUriPrefix = "data:image/png;base64,";

        private List<Dictionary<string, object>> BuildContentFromResult(string result)
        {
            var content = new List<Dictionary<string, object>>();

            if (result != null && result.StartsWith(ImageDataUriPrefix))
            {
                var base64Data = result.Substring(ImageDataUriPrefix.Length);
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "image", ["data"] = base64Data, ["mimeType"] = "image/png"
                });
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "text", ["text"] = "Screenshot captured successfully."
                });
            }
            else
            {
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "text", ["text"] = result
                });
            }

            return content;
        }

        private MCPResponse CreateErrorResponse(object requestId, int code, string message)
        {
            return new MCPResponse
            {
                Id = requestId,
                Error = new MCPError { Code = code, Message = message }
            };
        }

        private static bool ShouldLogRequest(string method)
        {
            switch (method)
            {
                case null:
                case "initialize":
                case "notifications/initialized":
                case "notifications/cancelled":
                case "resources/list":
                case "resources/read":
                case "resources/templates/list":
                case "tools/list":
                case "prompts/list":
                    return false;
                default:
                    return !method.StartsWith("notifications/", StringComparison.Ordinal);
            }
        }
    }
}
