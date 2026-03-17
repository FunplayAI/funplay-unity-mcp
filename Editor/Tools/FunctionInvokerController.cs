// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace GameBooom.Editor.Tools
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
                return "Error: null function call";

            // Check manually registered tools first
            if (ToolRegistry.ManualTools.TryGetValue(functionCall.FunctionName, out var manualTool))
            {
                try
                {
                    return manualTool.Handler(functionCall.Parameters) ?? "OK";
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[GameBooom] Manual tool '{functionCall.FunctionName}' failed: {ex.Message}");
                    return $"Error: {ex.Message}";
                }
            }

            var method = ToolRegistry.GetMethod(functionCall.FunctionName);
            if (method == null)
                return $"Error: Unknown function '{functionCall.FunctionName}'";

            try
            {
                var args = BuildArguments(method, functionCall.Parameters);
                var result = method.Invoke(null, args);
                return await NormalizeResultAsync(result);
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                Debug.LogError($"[GameBooom] Function '{functionCall.FunctionName}' failed: {inner.Message}\n{inner.StackTrace}");
                return $"Error: {inner.Message}";
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom] Function invoke error for '{functionCall.FunctionName}': {ex.Message}");
                return $"Error: {ex.Message}";
            }
        }

        private static async Task<string> NormalizeResultAsync(object result)
        {
            if (result is Task<string> stringTask)
            {
                return await stringTask;
            }

            if (result is Task task)
            {
                await task;

                var resultProperty = task.GetType().GetProperty("Result");
                return resultProperty?.GetValue(task)?.ToString() ?? "OK";
            }

            return result?.ToString() ?? "OK";
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
                    args[i] = ConvertValue(value, param.ParameterType);
                }
                else if (param.HasDefaultValue)
                {
                    args[i] = param.DefaultValue;
                }
                else
                {
                    args[i] = GetDefaultForType(param.ParameterType);
                }
            }

            return args;
        }

        private object ConvertValue(string value, Type targetType)
        {
            if (targetType == typeof(string))
                return value;

            if (string.IsNullOrEmpty(value))
                return GetDefaultForType(targetType);

            // Handle nullable
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {
                if (string.IsNullOrEmpty(value) || value == "null")
                    return null;
                targetType = underlying;
            }

            try
            {
                if (targetType == typeof(int))
                    return int.Parse(value, CultureInfo.InvariantCulture);
                if (targetType == typeof(long))
                    return long.Parse(value, CultureInfo.InvariantCulture);
                if (targetType == typeof(float))
                    return float.Parse(value, CultureInfo.InvariantCulture);
                if (targetType == typeof(double))
                    return double.Parse(value, CultureInfo.InvariantCulture);
                if (targetType == typeof(bool))
                    return value == "true" || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                if (targetType.IsEnum)
                    return Enum.Parse(targetType, value, ignoreCase: true);

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
            catch
            {
                return GetDefaultForType(targetType);
            }
        }

        private Vector3 ParseVector3(string value)
        {
            value = value.Trim('(', ')', ' ');
            var parts = value.Split(',');
            if (parts.Length >= 3)
            {
                return new Vector3(
                    float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                    float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                    float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture));
            }
            return Vector3.zero;
        }

        private Vector2 ParseVector2(string value)
        {
            value = value.Trim('(', ')', ' ');
            var parts = value.Split(',');
            if (parts.Length >= 2)
            {
                return new Vector2(
                    float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                    float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));
            }
            return Vector2.zero;
        }

        private Color ParseColor(string value)
        {
            value = value.Trim();
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out var c))
                    return c;
            }

            value = value.Trim('(', ')', ' ');
            var parts = value.Split(',');
            if (parts.Length >= 3)
            {
                float r = float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                float g = float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                float b = float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
                float a = parts.Length >= 4 ? float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) : 1f;
                return new Color(r, g, b, a);
            }

            return Color.white;
        }

        private object GetDefaultForType(Type type)
        {
            if (type.IsValueType)
                return Activator.CreateInstance(type);
            return null;
        }
    }
}
