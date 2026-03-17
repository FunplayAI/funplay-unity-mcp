// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.Reflection;
using GameBooom.Editor.Api.Models;

namespace GameBooom.Editor.Tools
{
    /// <summary>
    /// Builds LLM tool definitions from reflected method metadata.
    /// Generates JSON Schema for parameters using [ToolParam] attributes.
    /// </summary>
    internal static class ToolSchemaBuilder
    {
        public static List<ToolDefinition> BuildAll()
        {
            var definitions = new List<ToolDefinition>();

            // Add reflection-discovered tools (skip disabled ones)
            foreach (var kvp in ToolRegistry.MethodCache)
            {
                if (!ToolRegistry.IsEnabled(kvp.Key))
                    continue;

                var def = BuildFromMethod(kvp.Key, kvp.Value);
                if (def != null)
                    definitions.Add(def);
            }

            // Add manually registered tools (skip disabled ones)
            foreach (var kvp in ToolRegistry.ManualTools)
            {
                if (!ToolRegistry.IsEnabled(kvp.Key))
                    continue;

                definitions.Add(kvp.Value.Definition);
            }

            return definitions;
        }

        public static ToolDefinition BuildFromMethod(string functionName, MethodInfo method)
        {
            var description = GetMethodDescription(method);
            var parameters = BuildParametersSchema(method);

            return new ToolDefinition
            {
                type = "function",
                function = new ToolFunctionDef
                {
                    name = functionName,
                    description = description,
                    parameters = parameters
                }
            };
        }

        private static string GetMethodDescription(MethodInfo method)
        {
            // Check for DescriptionAttribute first
            var descAttr = method.GetCustomAttribute<DescriptionAttribute>();
            if (descAttr != null)
                return descAttr.Description;

            // Generate from method name
            var name = method.Name;
            var provider = method.DeclaringType?.GetCustomAttribute<ToolProviderAttribute>();
            var category = provider?.Category ?? "";

            return $"{(string.IsNullOrEmpty(category) ? "" : category + ": ")}{InsertSpaces(name)}";
        }

        private static ToolParametersDef BuildParametersSchema(MethodInfo method)
        {
            var schema = new ToolParametersDef();

            var parameters = method.GetParameters();
            foreach (var param in parameters)
            {
                var toolParamAttr = param.GetCustomAttribute<ToolParamAttribute>();

                var propDef = new ToolPropertyDef
                {
                    type = GetJsonSchemaType(param.ParameterType),
                    description = toolParamAttr?.Description ?? InsertSpaces(param.Name)
                };

                // Add enum values for enum types
                if (param.ParameterType.IsEnum)
                {
                    propDef.@enum = new List<string>(Enum.GetNames(param.ParameterType));
                }

                var snakeName = ToolRegistry.ToSnakeCase(param.Name);
                schema.properties[snakeName] = propDef;

                // Determine if required
                bool isRequired = toolParamAttr?.Required ?? !param.HasDefaultValue;
                if (isRequired)
                {
                    schema.required.Add(snakeName);
                }
            }

            return schema;
        }

        private static string GetJsonSchemaType(Type type)
        {
            // Handle nullable types
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
                type = underlying;

            if (type == typeof(string)) return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
            if (type == typeof(bool)) return "boolean";
            if (type.IsEnum) return "string";
            if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
                return "array";

            return "string"; // Default: treat as string (will be parsed by the function)
        }

        private static string InsertSpaces(string pascalCase)
        {
            if (string.IsNullOrEmpty(pascalCase)) return pascalCase;

            var result = new System.Text.StringBuilder();
            for (int i = 0; i < pascalCase.Length; i++)
            {
                if (i > 0 && char.IsUpper(pascalCase[i]) && char.IsLower(pascalCase[i - 1]))
                    result.Append(' ');
                result.Append(pascalCase[i]);
            }
            return result.ToString();
        }
    }
}
