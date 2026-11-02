// Copyright (C) Funplay. Licensed under MIT.
using System.Collections.Generic;

namespace Funplay.Editor.MCP.Server
{
    internal static class MCPWaitFailure
    {
        internal static MCPResponse Create(MCPRequest request, string layer, int budgetSeconds, bool timeout)
        {
            var data = new Dictionary<string, object> { ["error_code"] = timeout ? "RESPONSE_WAIT_TIMEOUT" : "RESPONSE_WAIT_CANCELLED",
                ["layer"] = layer, ["wait_budget_seconds"] = budgetSeconds, ["underlying_operation_cancelled"] = false,
                ["execution_outcome"] = "unknown", ["retry_safe"] = false,
                ["hint"] = "Only response waiting ended. Recover the original task or inspect actual project state; do not replay a write automatically." };
            if (request?.Method == "tools/call" && request.Params != null &&
                request.Params.TryGetValue("arguments", out var raw) && raw is Dictionary<string, object> args)
            {
                var recovery = new Dictionary<string, object> { ["tool"] = "get_task" };
                if (args.TryGetValue("task_id", out var id) && id is string task && task.Length <= 160) recovery["task_id"] = task;
                if (request.Params.TryGetValue("name", out var name) && (name as string == "execute_code" || name as string == "replay_execute_code"))
                {
                    recovery["kind"] = "execution";
                    if (args.TryGetValue("request_key", out var key) && key is string text && text.Length <= 128) recovery["request_key"] = text;
                }
                if (recovery.Count > 1) data["recovery"] = recovery;
            }
            return new MCPResponse { Id = request?.Id, Error = new MCPError { Code = -32000,
                Message = timeout ? "Request timeout" : "Request cancelled", Data = data } };
        }
    }
}
