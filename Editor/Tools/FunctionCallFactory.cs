// Copyright (C) GameBooom. Licensed under GPLv3.
using System.Collections.Generic;
using GameBooom.Editor.Api.Models;

namespace GameBooom.Editor.Tools
{
    /// <summary>
    /// Converts LLM tool_call responses into FunctionCall objects.
    /// Parses JSON arguments into Dictionary&lt;string, string&gt;.
    /// </summary>
    internal static class FunctionCallFactory
    {
        public static List<FunctionCall> CreateFromToolCalls(List<ToolCallDto> toolCalls)
        {
            var result = new List<FunctionCall>();
            if (toolCalls == null) return result;

            foreach (var tc in toolCalls)
            {
                if (tc.function == null) continue;

                var fc = new FunctionCall
                {
                    Id = System.Guid.NewGuid().ToString(),
                    ToolCallId = tc.id,
                    FunctionName = tc.function.name,
                    RawArguments = tc.function.arguments,
                    Parameters = ParseArguments(tc.function.arguments)
                };

                // Check if this is a known local tool
                var method = ToolRegistry.GetMethod(fc.FunctionName);
                if (method != null)
                {
                    fc.IsReadOnly = ToolRegistry.IsReadOnly(method);
                }

                result.Add(fc);
            }

            return result;
        }

        /// <summary>
        /// Parses JSON arguments string into a flat Dictionary&lt;string, string&gt;.
        /// Handles nested objects by converting them back to JSON strings.
        /// </summary>
        private static Dictionary<string, string> ParseArguments(string jsonArgs)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(jsonArgs)) return result;

            try
            {
                // Simple JSON object parser for flat key-value pairs
                jsonArgs = jsonArgs.Trim();
                if (!jsonArgs.StartsWith("{") || !jsonArgs.EndsWith("}"))
                    return result;

                var content = jsonArgs.Substring(1, jsonArgs.Length - 2).Trim();
                if (string.IsNullOrEmpty(content)) return result;

                int pos = 0;
                while (pos < content.Length)
                {
                    // Skip whitespace
                    SkipWhitespace(content, ref pos);
                    if (pos >= content.Length) break;

                    // Parse key
                    var key = ParseJsonString(content, ref pos);
                    if (key == null) break;

                    SkipWhitespace(content, ref pos);
                    if (pos >= content.Length || content[pos] != ':') break;
                    pos++; // skip ':'
                    SkipWhitespace(content, ref pos);

                    // Parse value
                    var value = ParseJsonValue(content, ref pos);

                    result[key] = value ?? "";

                    SkipWhitespace(content, ref pos);
                    if (pos < content.Length && content[pos] == ',')
                        pos++;
                }
            }
            catch
            {
                // If parsing fails, store raw args
                result["_raw"] = jsonArgs;
            }

            return result;
        }

        private static string ParseJsonString(string json, ref int pos)
        {
            if (pos >= json.Length || json[pos] != '"') return null;
            pos++; // skip opening quote

            var sb = new System.Text.StringBuilder();
            while (pos < json.Length)
            {
                var c = json[pos];
                if (c == '\\' && pos + 1 < json.Length)
                {
                    pos++;
                    var esc = json[pos];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(esc); break;
                    }
                }
                else if (c == '"')
                {
                    pos++; // skip closing quote
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
                pos++;
            }
            return sb.ToString();
        }

        private static string ParseJsonValue(string json, ref int pos)
        {
            if (pos >= json.Length) return null;

            var c = json[pos];

            // String
            if (c == '"')
                return ParseJsonString(json, ref pos);

            // Object or Array - capture the entire block as string
            if (c == '{' || c == '[')
            {
                char open = c;
                char close = c == '{' ? '}' : ']';
                int depth = 1;
                int start = pos;
                pos++;
                bool inString = false;

                while (pos < json.Length && depth > 0)
                {
                    var ch = json[pos];
                    if (inString)
                    {
                        if (ch == '\\') pos++; // skip escaped char
                        else if (ch == '"') inString = false;
                    }
                    else
                    {
                        if (ch == '"') inString = true;
                        else if (ch == open) depth++;
                        else if (ch == close) depth--;
                    }
                    pos++;
                }

                return json.Substring(start, pos - start);
            }

            // Number, boolean, null
            int valueStart = pos;
            while (pos < json.Length && json[pos] != ',' && json[pos] != '}' && json[pos] != ']'
                   && !char.IsWhiteSpace(json[pos]))
            {
                pos++;
            }

            var raw = json.Substring(valueStart, pos - valueStart);
            // Strip quotes from true/false/null
            if (raw == "true") return "true";
            if (raw == "false") return "false";
            if (raw == "null") return "";
            return raw;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos]))
                pos++;
        }
    }
}
