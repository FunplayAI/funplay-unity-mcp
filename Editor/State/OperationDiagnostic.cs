// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.State
{
    // File paths, exception messages, argument values and source snippets are deliberately not
    // collected. Diagnostics are opt-in and contain bounded method identities, never a raw stack.
    internal static class OperationDiagnostic
    {
        internal static JObject Capture(string stage, Exception error)
        {
            var storage = error as ExecutionStorageException;
            if (storage != null) error = storage.InnerException;
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            var frames = new JArray();
            foreach (var frame in new StackTrace(error, false).GetFrames() ?? Array.Empty<StackFrame>())
            {
                if (frames.Count >= 12) break;
                var method = frame.GetMethod();
                string name = method?.DeclaringType?.FullName + "." + method?.Name;
                frames.Add(name.Length > 240 ? name.Substring(0, 240) : name);
            }
            string exceptionType = error.GetType().FullName ?? error.GetType().Name;
            var result = new JObject { ["stage"] = stage, ["exception_type"] = exceptionType.Length > 256 ? exceptionType.Substring(0, 256) : exceptionType,
                ["managed_runtime"] = Environment.Version.ToString(), ["stack_methods"] = frames,
                ["stack_is_sanitized"] = true, ["automatic_retry"] = false };
            if (error is System.IO.IOException || error is UnauthorizedAccessException)
            {
                result["hresult"] = error.HResult; result["hresult_hex"] = "0x" + unchecked((uint)error.HResult).ToString("X8");
                result["native_error_code"] = (error.HResult & unchecked((int)0xffff0000)) == unchecked((int)0x80070000)
                    ? (JToken)new JValue(error.HResult & 0xffff) : JValue.CreateNull();
            }
            if (storage != null)
            { result["io_operation"] = storage.Operation; result["file_role"] = storage.Role; result["io_attempts"] = storage.Attempts; }
            return result;
        }
    }
}
