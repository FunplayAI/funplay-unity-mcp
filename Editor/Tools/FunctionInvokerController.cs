// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Funplay.Editor.Tools
{
    /// <summary>
    /// Invokes tool functions via reflection.
    /// Handles snake_case to PascalCase parameter mapping and type conversion.
    /// </summary>
    internal class FunctionInvokerController
    {
        public string Invoke(FunctionCall functionCall)
        {
            return InvokeAsync(functionCall).GetAwaiter().GetResult();
        }

        public async Task<string> InvokeAsync(FunctionCall functionCall)
        {
            if (functionCall == null)
                return ToolResultFormatter.Error("NULL_FUNCTION_CALL");
            if (string.IsNullOrWhiteSpace(functionCall.FunctionName))
                return ToolResultFormatter.Error("FUNCTION_NAME_REQUIRED");

            // Check manually registered tools first
            if (ToolRegistry.ManualTools.TryGetValue(functionCall.FunctionName, out var manualTool))
            {
                try
                {
                    var missingParameter = FindMissingManualParameter(manualTool, functionCall.Parameters);
                    if (missingParameter != null)
                    {
                        return ToolResultFormatter.Error("MISSING_PARAM",
                            new { param = missingParameter, expected = "a required value" });
                    }

                    return WrapLegacyStringResult(manualTool.Handler(functionCall.Parameters));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Funplay] Manual tool '{functionCall.FunctionName}' failed: {ex.Message}");
                    return ToolResultFormatter.Error("MANUAL_TOOL_FAILED",
                        new { tool = functionCall.FunctionName, message = ex.Message });
                }
            }

            var method = ToolRegistry.GetMethod(functionCall.FunctionName);
            if (method == null)
                return ToolResultFormatter.Error("UNKNOWN_FUNCTION",
                    new { function = functionCall.FunctionName });

            try
            {
                var args = BuildArguments(method, functionCall.Parameters);
                var result = method.Invoke(null, args);
                return await NormalizeResultAsync(result);
            }
            catch (ToolArgumentException pex)
            {
                return ToolResultFormatter.Error(pex.Code,
                    new { param = pex.ParamName, provided = pex.Provided, expected = pex.Expected });
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                Debug.LogError($"[Funplay] Function '{functionCall.FunctionName}' failed: {inner.Message}\n{inner.StackTrace}");
                return ToolResultFormatter.Error("FUNCTION_FAILED",
                    new { function = functionCall.FunctionName, message = inner.Message });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Funplay] Function invoke error for '{functionCall.FunctionName}': {ex.Message}");
                return ToolResultFormatter.Error("FUNCTION_INVOKE_ERROR",
                    new { function = functionCall.FunctionName, message = ex.Message });
            }
        }

        private static async Task<string> NormalizeResultAsync(object result)
        {
            if (result is Task task)
            {
                await task;

                var resultProperty = task.GetType().GetProperty("Result");
                if (resultProperty == null)
                    return SerializeResult(null);

                var taskResult = resultProperty.GetValue(task);
                return SerializeResult(taskResult);
            }

            return SerializeResult(result);
        }

        // Tools may return either a plain string (backward-compatible behavior) or a structured
        // object (e.g. via Response.Success/Error) which we JSON-encode for the client.
        private static string SerializeResult(object value)
        {
            if (value == null)
                return JsonConvert.SerializeObject(Response.Success("OK"));
            if (value is string s)
                return WrapLegacyStringResult(s);
            try
            {
                return JsonConvert.SerializeObject(value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Funplay] Failed to serialize tool result: {ex.Message}");
                return JsonConvert.SerializeObject(Response.Success(value.ToString() ?? "OK"));
            }
        }

        // Legacy tools return a bare human-readable string on success and only switch to a
        // structured {success:false,...} JSON on failure, so callers can't uniformly branch on a
        // `success` field. Wrap a bare success string in the standard envelope so EVERY tool
        // response is parseable as {success, message}. Two things pass through untouched:
        //   (a) image data URIs ("data:...") -- MCPRequestHandler renders these as image content;
        //   (b) strings that are ALREADY a {success:...} envelope (ToolResultFormatter.Error, or a
        //       tool that hand-built one via JsonConvert) -- so we never double-wrap.
        internal static string WrapLegacyStringResult(string s)
        {
            if (s == null)
                return JsonConvert.SerializeObject(Response.Success("OK"));
            if (s.StartsWith("data:", StringComparison.Ordinal))
                return s;
            if (IsEnvelopeJson(s))
                return s;
            return JsonConvert.SerializeObject(Response.Success(s));
        }

        private static bool IsEnvelopeJson(string s)
        {
            var trimmed = s.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                return false;
            try
            {
                var obj = JObject.Parse(s);
                return obj.TryGetValue("success", out var token) && token.Type == JTokenType.Boolean;
            }
            catch
            {
                return false;
            }
        }

        private static string FindMissingManualParameter(
            ToolRegistry.ManualToolEntry manualTool,
            Dictionary<string, string> parameters)
        {
            var required = manualTool.Definition?.function?.parameters?.required;
            if (required == null)
                return null;

            foreach (var name in required)
            {
                if (parameters == null || !parameters.TryGetValue(name, out var value) || value == null)
                    return name;
            }

            return null;
        }

        private object[] BuildArguments(MethodInfo method, Dictionary<string, string> parameters)
        {
            var methodParams = method.GetParameters();
            var args = new object[methodParams.Length];

            for (int i = 0; i < methodParams.Length; i++)
            {
                var param = methodParams[i];
                var snakeName = ToolRegistry.ToSnakeCase(param.Name);

                string value = null;
                // Try snake_case name first, then original name
                if (parameters != null)
                {
                    if (!parameters.TryGetValue(snakeName, out value))
                        parameters.TryGetValue(param.Name, out value);
                }

                if (value != null)
                {
                    try
                    {
                        args[i] = ConvertValue(value, param.ParameterType);
                    }
                    catch (Exception ex)
                    {
                        throw new ToolArgumentException(snakeName, value, DescribeExpectedFormat(param.ParameterType), ex);
                    }
                }
                else if (param.HasDefaultValue)
                {
                    args[i] = param.DefaultValue;
                }
                else
                {
                    var toolParam = param.GetCustomAttribute<ToolParamAttribute>();
                    var isRequired = toolParam?.Required ?? true;
                    if (isRequired)
                    {
                        throw new ToolArgumentException(
                            "MISSING_PARAM",
                            snakeName,
                            null,
                            DescribeExpectedFormat(param.ParameterType));
                    }

                    args[i] = GetDefaultForType(param.ParameterType);
                }
            }

            return args;
        }

        private object ConvertValue(string value, Type targetType)
        {
            if (targetType == typeof(string))
                return value;

            // Handle nullable: empty or "null" -> null; otherwise unwrap and parse the underlying.
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {
                if (string.IsNullOrEmpty(value) || value == "null")
                    return null;
                targetType = underlying;
            }

            // NOTE: parse failures -- INCLUDING an explicit empty string for a non-string type --
            // are intentionally NOT swallowed here. They propagate to BuildArguments, which
            // rethrows them as a ToolArgumentException so the caller gets an INVALID_PARAM error
            // instead of a silent default(T) / zero-vector / false / zeroth-enum wrong write.
            if (targetType == typeof(int))
                return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(long))
                return long.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float))
                return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(double))
                return double.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool))
            {
                if (value == "true" || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (value == "false" || value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase))
                    return false;
                throw new FormatException($"'{value}' is not a boolean (expected true/false/1/0/yes/no)");
            }
            if (targetType.IsEnum)
            {
                // Enum.Parse accepts unmapped NUMERIC strings (e.g. "999" -> (T)999) without
                // throwing, so validate the result is a defined member.
                var parsed = Enum.Parse(targetType, value, ignoreCase: true);
                if (!Enum.IsDefined(targetType, parsed))
                    throw new FormatException($"'{value}' is not a defined {targetType.Name} value (one of [{string.Join(", ", Enum.GetNames(targetType))}])");
                return parsed;
            }

            // Vector3: "x,y,z" or "(x,y,z)"
            if (targetType == typeof(Vector3))
                return ParseVector3(value);
            // Vector2
            if (targetType == typeof(Vector2))
                return ParseVector2(value);
            // Color: "r,g,b,a" or "r,g,b" or "#hex"
            if (targetType == typeof(Color))
                return ParseColor(value);

            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        private Vector3 ParseVector3(string value)
        {
            value = value.Trim('(', ')', ' ');
            var parts = value.Split(',');
            if (parts.Length != 3)
                throw new FormatException($"expected 3 comma-separated numbers 'x,y,z', got {parts.Length}");
            return new Vector3(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture));
        }

        private Vector2 ParseVector2(string value)
        {
            value = value.Trim('(', ')', ' ');
            var parts = value.Split(',');
            if (parts.Length != 2)
                throw new FormatException($"expected 2 comma-separated numbers 'x,y', got {parts.Length}");
            return new Vector2(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));
        }

        private Color ParseColor(string value)
        {
            value = value.Trim();
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out var c))
                    return c;
                throw new FormatException($"'{value}' is not a valid #hex color");
            }

            value = value.Trim('(', ')', ' ');
            var parts = value.Split(',');
            if (parts.Length < 3 || parts.Length > 4)
                throw new FormatException($"expected 'r,g,b' or 'r,g,b,a' or '#hex', got {parts.Length} components");
            float r = float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            float g = float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            float b = float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
            float a = parts.Length >= 4 ? float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) : 1f;
            return new Color(r, g, b, a);
        }

        private object GetDefaultForType(Type type)
        {
            if (type.IsValueType)
                return Activator.CreateInstance(type);
            return null;
        }

        // Human-readable expected-format hint for an INVALID_PARAM error, so the caller knows
        // what shape the value should have taken.
        private static string DescribeExpectedFormat(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            if (t == typeof(int) || t == typeof(long)) return "an integer";
            if (t == typeof(float) || t == typeof(double)) return "a number";
            if (t == typeof(bool)) return "a boolean ('true'/'false'/'1'/'0'/'yes')";
            if (t == typeof(Vector3)) return "a Vector3 'x,y,z'";
            if (t == typeof(Vector2)) return "a Vector2 'x,y'";
            if (t == typeof(Color)) return "a Color 'r,g,b[,a]' or '#hex'";
            if (t.IsEnum) return $"one of [{string.Join(", ", Enum.GetNames(t))}]";
            return t.Name;
        }
    }

    // Thrown when a tool argument string cannot be converted to the parameter's type. Carries
    // enough context for the invoker to return a structured INVALID_PARAM error.
    internal sealed class ToolArgumentException : Exception
    {
        public string Code { get; }
        public string ParamName { get; }
        public string Provided { get; }
        public string Expected { get; }

        public ToolArgumentException(string paramName, string provided, string expected, Exception inner)
            : this("INVALID_PARAM", paramName, provided, expected, inner)
        {
        }

        public ToolArgumentException(string code, string paramName, string provided, string expected, Exception inner = null)
            : base($"Parameter '{paramName}' could not be parsed as {expected}.", inner)
        {
            Code = code;
            ParamName = paramName;
            Provided = provided;
            Expected = expected;
        }
    }
}
