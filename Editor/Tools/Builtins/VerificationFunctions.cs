// Copyright (C) Funplay. Licensed under MIT.
using System.ComponentModel;
using Funplay.Editor.State;
using Funplay.Editor.Tools.Helpers;
namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Verification")]
    internal static class VerificationFunctions
    {
        [Description("Start/end a bounded verification scope in the actual authored Play scene. Optional background execution is explicit and compare-before-restored. Enroll input/recording/extraction via verification_id. End stops only owned jobs; no scene, business-data or dirty-state reset. Status is read-only through get_task(kind=verification). Full management capability; never implies application acceptance.")]
        public static object VerificationScope(
            [ToolParam("start or end", Required = false)] string action = "start",
            [ToolParam("Scope ID for end", Required = false)] string verification_id = null,
            [ToolParam("5..300 seconds before automatic owned cleanup", Required = false)] int timeout_seconds = 60,
            [ToolParam("Explicitly allow temporary background execution during this scope", Required = false)] bool allow_background = false,
            [ToolParam("Optional unique idempotency/recovery key, up to 128 characters", Required = false)] string request_key = null)
            => TaskStatusService.Attach("verification", action == "start" ? State.VerificationScope.Start(timeout_seconds, allow_background, request_key) :
                action == "end" ? State.VerificationScope.End(verification_id) : Response.Error("INVALID_SCOPE_ACTION"));
    }
}
