// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameBooom.Editor.Api.Models;
using UnityEngine;

namespace GameBooom.Editor.Tools
{
    /// <summary>
    /// Scans assemblies for classes marked with [ToolProvider]
    /// and discovers all public static methods as tool functions.
    /// Blocked tools (e.g. code execution, input simulation) are filtered out.
    /// Also supports manual tool registration for external plugins.
    /// </summary>
    internal static class ToolRegistry
    {
        private static readonly object _lock = new object();
        private static volatile List<Type> _providerTypes;
        private static volatile Dictionary<string, MethodInfo> _methodCache;

        /// <summary>
        /// Manually registered tools from external plugins.
        /// Key = snake_case tool name.
        /// </summary>
        private static readonly Dictionary<string, ManualToolEntry> _manualTools =
            new Dictionary<string, ManualToolEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tools that have been explicitly disabled at runtime.
        /// </summary>
        private static readonly HashSet<string> _disabledTools =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> BlockedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "execute_code", "evaluate_expression",
            "simulate_key_press", "simulate_mouse_click",
            "simulate_key_combo", "simulate_mouse_drag"
        };

        /// <summary>
        /// Represents a manually registered tool function.
        /// </summary>
        internal class ManualToolEntry
        {
            public string Name;
            public ToolDefinition Definition;
            public Func<Dictionary<string, string>, string> Handler;
        }

        public static IReadOnlyList<Type> ProviderTypes
        {
            get
            {
                if (_providerTypes == null)
                    lock (_lock) { if (_providerTypes == null) ScanAssemblies(); }
                return _providerTypes;
            }
        }

        public static IReadOnlyDictionary<string, MethodInfo> MethodCache
        {
            get
            {
                if (_methodCache == null)
                    lock (_lock) { if (_methodCache == null) ScanAssemblies(); }
                return _methodCache;
            }
        }

        public static void ScanAssemblies()
        {
            _providerTypes = new List<Type>();
            _methodCache = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    if (assembly.IsDynamic) continue;

                    try
                    {
                        foreach (var type in assembly.GetTypes())
                        {
                            if (type.GetCustomAttribute<ToolProviderAttribute>() == null)
                                continue;

                            _providerTypes.Add(type);

                            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                            foreach (var method in methods)
                            {
                                var snakeName = ToSnakeCase(method.Name);

                                if (BlockedTools.Contains(snakeName))
                                    continue;

                                if (!_methodCache.ContainsKey(snakeName))
                                {
                                    _methodCache[snakeName] = method;
                                }
                                else
                                {
                                    Debug.LogWarning($"[GameBooom] Duplicate tool function name: {snakeName}");
                                }
                            }
                        }
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // Skip assemblies that can't be loaded
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameBooom] Error scanning assemblies for tool functions: {ex.Message}");
            }
        }

        public static MethodInfo GetMethod(string snakeCaseName)
        {
            if (_methodCache == null) ScanAssemblies();
            _methodCache.TryGetValue(snakeCaseName, out var method);
            return method;
        }

        public static bool IsReadOnly(MethodInfo method)
        {
            return method.GetCustomAttribute<ReadOnlyToolAttribute>() != null;
        }

        public static bool IsSceneEditing(MethodInfo method)
        {
            return method.GetCustomAttribute<SceneEditingToolAttribute>() != null;
        }

        // --- Public Registration API ---

        /// <summary>
        /// Manually register a tool function. External plugins can use this
        /// to add tools without using the [ToolProvider] attribute.
        /// </summary>
        /// <param name="name">Snake_case tool name (e.g. "my_custom_tool")</param>
        /// <param name="definition">Tool definition with JSON schema</param>
        /// <param name="handler">Function that receives parameters and returns a result string</param>
        public static void Register(string name, ToolDefinition definition,
            Func<Dictionary<string, string>, string> handler)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (BlockedTools.Contains(name))
            {
                Debug.LogWarning($"[GameBooom] Cannot register blocked tool: {name}");
                return;
            }

            lock (_lock)
            {
                _manualTools[name] = new ManualToolEntry
                {
                    Name = name,
                    Definition = definition,
                    Handler = handler
                };
            }

            Debug.Log($"[GameBooom] Registered manual tool: {name}");
        }

        /// <summary>
        /// Unregister a manually registered tool.
        /// </summary>
        public static void Unregister(string name)
        {
            lock (_lock)
            {
                _manualTools.Remove(name);
            }
        }

        /// <summary>
        /// Enable or disable a tool at runtime. Disabled tools are excluded
        /// from tool definitions sent to the LLM.
        /// </summary>
        public static void SetEnabled(string name, bool enabled)
        {
            lock (_lock)
            {
                if (enabled)
                    _disabledTools.Remove(name);
                else
                    _disabledTools.Add(name);
            }
        }

        /// <summary>
        /// Check if a tool is currently enabled.
        /// </summary>
        public static bool IsEnabled(string name)
        {
            return !_disabledTools.Contains(name) && !BlockedTools.Contains(name);
        }

        /// <summary>
        /// Get all registered manual tools (for use by ToolSchemaBuilder and FunctionInvokerController).
        /// </summary>
        public static IReadOnlyDictionary<string, ManualToolEntry> ManualTools => _manualTools;

        /// <summary>
        /// Get the set of disabled tool names.
        /// </summary>
        public static IReadOnlyCollection<string> DisabledTools => _disabledTools;

        // --- Utility ---

        public static string ToSnakeCase(string pascalCase)
        {
            if (string.IsNullOrEmpty(pascalCase)) return pascalCase;

            var chars = new List<char>();
            for (int i = 0; i < pascalCase.Length; i++)
            {
                var c = pascalCase[i];
                if (char.IsUpper(c) && i > 0)
                {
                    // Add underscore before uppercase if previous char is lowercase
                    // or if next char is lowercase (handles "XMLParser" -> "xml_parser")
                    bool prevIsLower = char.IsLower(pascalCase[i - 1]);
                    bool nextIsLower = i + 1 < pascalCase.Length && char.IsLower(pascalCase[i + 1]);
                    if (prevIsLower || nextIsLower)
                        chars.Add('_');
                }
                chars.Add(char.ToLowerInvariant(c));
            }
            return new string(chars.ToArray());
        }

        public static string ToPascalCase(string snakeCase)
        {
            if (string.IsNullOrEmpty(snakeCase)) return snakeCase;

            var parts = snakeCase.Split('_');
            var result = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                if (part.Length > 0)
                {
                    result.Append(char.ToUpperInvariant(part[0]));
                    if (part.Length > 1)
                        result.Append(part.Substring(1));
                }
            }
            return result.ToString();
        }

        /// <summary>
        /// Invalidate cache, forcing re-scan on next access.
        /// </summary>
        public static void InvalidateCache()
        {
            _providerTypes = null;
            _methodCache = null;
        }
    }
}
