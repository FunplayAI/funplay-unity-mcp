// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.MCP.Server
{
    internal enum MCPToolCallStatus
    {
        Success,
        Interrupted,
        Error
    }

    internal struct MCPLogEntry
    {
        public DateTime Timestamp;
        public string ToolName;
        public MCPToolCallStatus Status;
        public string ResultSummary;
        public string DisplayResult;
        public bool IsJsonResult;
        public string ImageDataUri;
    }

    internal class MCPInteractionLog
    {
        private const string ImageDataUriPrefix = "data:image/png;base64,";
        private const int MaxSummaryCharacters = 200;
        private const int MaxJsonParseCharacters = 64 * 1024;
        private const int MaxRenderDepth = 8;
        internal const int MaxDisplayResultCharacters = 4000;
        private const string TruncatedSuffix = "\n... (truncated)";
        private const string EmptyValue = "—";

        private readonly MCPLogEntry[] _buffer;
        private int _head;
        private int _count;
        private readonly object _lock = new object();

        public event Action<MCPLogEntry> OnEntryAdded;

        public MCPInteractionLog(int capacity = 200)
        {
            _buffer = new MCPLogEntry[capacity];
        }

        public void Add(string toolName, MCPToolCallStatus status, string resultSummary)
        {
            var imageDataUri = resultSummary != null && resultSummary.StartsWith(ImageDataUriPrefix, StringComparison.Ordinal)
                ? resultSummary
                : null;
            var compactSummary = imageDataUri != null
                ? "Screenshot captured successfully."
                : CreateCompactSummary(resultSummary);
            var displayResult = compactSummary;
            var isJsonResult = false;

            if (imageDataUri == null && TryFormatJsonForDisplay(resultSummary, out var formattedJson))
            {
                displayResult = TruncateDisplayResult(formattedJson);
                isJsonResult = true;
            }

            var entry = new MCPLogEntry
            {
                Timestamp = DateTime.Now,
                ToolName = toolName,
                Status = status,
                ResultSummary = compactSummary,
                DisplayResult = displayResult,
                IsJsonResult = isJsonResult,
                ImageDataUri = imageDataUri
            };

            lock (_lock)
            {
                _buffer[_head] = entry;
                _head = (_head + 1) % _buffer.Length;
                if (_count < _buffer.Length) _count++;
            }

            OnEntryAdded?.Invoke(entry);
        }

        private static string CreateCompactSummary(string resultSummary)
        {
            if (string.IsNullOrEmpty(resultSummary))
                return "";

            return resultSummary.Length > MaxSummaryCharacters
                ? resultSummary.Substring(0, MaxSummaryCharacters - 3) + "..."
                : resultSummary;
        }

        /// <summary>
        /// Converts a JSON result into a compact human-readable activity description. Standard
        /// Funplay response envelopes hide transport fields such as <c>success</c> and promote the
        /// message plus data fields; arbitrary objects and arrays become indented labels and
        /// numbered items. The Recent Activity panel is a status view, not a JSON inspector.
        /// </summary>
        internal static bool TryFormatJsonForDisplay(string resultSummary, out string formattedJson)
        {
            formattedJson = null;
            if (string.IsNullOrWhiteSpace(resultSummary) || resultSummary.Length > MaxJsonParseCharacters)
                return false;

            var trimmed = resultSummary.Trim();
            if (trimmed.Length < 2)
                return false;

            var looksLikeObject = trimmed[0] == '{' && trimmed[trimmed.Length - 1] == '}';
            var looksLikeArray = trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']';
            if (!looksLikeObject && !looksLikeArray)
                return false;

            try
            {
                var token = JToken.Parse(trimmed);
                var builder = new StringBuilder(Math.Min(resultSummary.Length, MaxDisplayResultCharacters));

                if (token is JObject obj && IsResponseEnvelope(obj))
                    AppendResponseEnvelope(builder, obj, 0);
                else
                    AppendToken(builder, token, 0);

                formattedJson = builder.ToString().Trim();
                return formattedJson.Length > 0;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static string TruncateDisplayResult(string displayResult)
        {
            if (displayResult.Length <= MaxDisplayResultCharacters)
                return displayResult;

            var maxBodyLength = MaxDisplayResultCharacters - TruncatedSuffix.Length;
            var cutoff = displayResult.LastIndexOf('\n', maxBodyLength);
            if (cutoff < maxBodyLength / 2)
                cutoff = maxBodyLength;

            return displayResult.Substring(0, cutoff).TrimEnd() + TruncatedSuffix;
        }

        private static bool IsResponseEnvelope(JObject obj)
        {
            return obj?["success"]?.Type == JTokenType.Boolean &&
                   (obj.Property("message") != null ||
                    obj.Property("data") != null ||
                    obj.Property("code") != null ||
                    obj.Property("error") != null);
        }

        private static void AppendResponseEnvelope(StringBuilder builder, JObject obj, int depth)
        {
            var message = GetStringValue(obj["message"]);
            var code = GetStringValue(obj["code"]);
            var error = GetStringValue(obj["error"]);
            var success = obj["success"]?.Value<bool?>();
            var hasData = HasDisplayValue(obj["data"]);
            var hasExtraFields = false;
            foreach (var property in obj.Properties())
            {
                if (!IsEnvelopeField(property.Name))
                {
                    hasExtraFields = true;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(message))
                AppendMultiline(builder, depth, message.Trim());

            var hasErrorDetails = success == false &&
                                  ((!string.IsNullOrWhiteSpace(code) && !SameText(code, message)) ||
                                   (!string.IsNullOrWhiteSpace(error) &&
                                    !SameText(error, code) &&
                                    !SameText(error, message)));
            if (builder.Length > 0 && (hasErrorDetails || hasData || hasExtraFields))
                builder.AppendLine();

            if (hasErrorDetails)
            {
                if (!string.IsNullOrWhiteSpace(code) && !SameText(code, message))
                    AppendScalarProperty(builder, depth, "Code", code);
                if (!string.IsNullOrWhiteSpace(error) &&
                    !SameText(error, code) &&
                    !SameText(error, message))
                {
                    AppendScalarProperty(builder, depth, "Error", error);
                }
            }

            if (hasData)
            {
                if (obj["data"] is JObject dataObject)
                    AppendObject(builder, dataObject, depth);
                else
                    AppendProperty(builder, "Data", obj["data"], depth);
            }

            foreach (var property in obj.Properties())
            {
                if (!IsEnvelopeField(property.Name))
                    AppendProperty(builder, property.Name, property.Value, depth);
            }

            if (builder.Length == 0)
                AppendLine(builder, depth, success == true ? "Completed successfully." : "No details available.");
        }

        private static bool IsEnvelopeField(string name)
        {
            return string.Equals(name, "success", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "message", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "data", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "code", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "error", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasDisplayValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return false;
            if (token is JObject obj)
                return obj.HasValues;
            if (token is JArray array)
                return array.Count > 0;
            if (token.Type == JTokenType.String)
                return !string.IsNullOrWhiteSpace(token.Value<string>());
            return true;
        }

        private static void AppendToken(StringBuilder builder, JToken token, int depth)
        {
            if (depth >= MaxRenderDepth)
            {
                AppendLine(builder, depth, "…");
                return;
            }

            switch (token)
            {
                case JObject obj:
                    if (IsResponseEnvelope(obj))
                        AppendResponseEnvelope(builder, obj, depth);
                    else
                        AppendObject(builder, obj, depth);
                    break;
                case JArray array:
                    AppendArray(builder, array, depth);
                    break;
                default:
                    AppendMultiline(builder, depth, FormatScalar(token));
                    break;
            }
        }

        private static void AppendObject(StringBuilder builder, JObject obj, int depth)
        {
            if (!obj.HasValues)
            {
                AppendLine(builder, depth, EmptyValue);
                return;
            }

            foreach (var property in obj.Properties())
                AppendProperty(builder, property.Name, property.Value, depth);
        }

        private static void AppendArray(StringBuilder builder, JArray array, int depth)
        {
            if (array.Count == 0)
            {
                AppendLine(builder, depth, EmptyValue);
                return;
            }

            for (var i = 0; i < array.Count; i++)
            {
                var item = array[i];
                if (item is JObject || item is JArray || TryParseNestedJsonString(item, out _))
                {
                    AppendLine(builder, depth, $"{i + 1}.");
                    if (TryParseNestedJsonString(item, out var nested))
                        AppendToken(builder, nested, depth + 1);
                    else
                        AppendToken(builder, item, depth + 1);
                }
                else
                {
                    AppendScalarProperty(builder, depth, $"{i + 1}.", FormatScalar(item), separator: " ");
                }
            }
        }

        private static void AppendProperty(StringBuilder builder, string name, JToken value, int depth)
        {
            var label = HumanizeKey(name);
            if (TryParseNestedJsonString(value, out var nested))
            {
                AppendLine(builder, depth, label + ":");
                AppendToken(builder, nested, depth + 1);
                return;
            }

            if (value is JObject obj)
            {
                if (!obj.HasValues)
                {
                    AppendScalarProperty(builder, depth, label, EmptyValue);
                    return;
                }

                AppendLine(builder, depth, label + ":");
                AppendObject(builder, obj, depth + 1);
                return;
            }

            if (value is JArray array)
            {
                if (array.Count == 0)
                {
                    AppendScalarProperty(builder, depth, label, EmptyValue);
                    return;
                }

                AppendLine(builder, depth, label + ":");
                AppendArray(builder, array, depth + 1);
                return;
            }

            AppendScalarProperty(builder, depth, label, FormatScalar(value));
        }

        private static bool TryParseNestedJsonString(JToken token, out JToken nested)
        {
            nested = null;
            if (token?.Type != JTokenType.String)
                return false;

            var value = token.Value<string>();
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxJsonParseCharacters)
                return false;

            var trimmed = value.Trim();
            if (trimmed.Length < 2)
                return false;

            var looksLikeObject = trimmed[0] == '{' && trimmed[trimmed.Length - 1] == '}';
            var looksLikeArray = trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']';
            if (!looksLikeObject && !looksLikeArray)
                return false;

            try
            {
                nested = JToken.Parse(trimmed);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static void AppendScalarProperty(
            StringBuilder builder,
            int depth,
            string label,
            string value,
            string separator = ": ")
        {
            var normalized = NormalizeLineEndings(value);
            var lines = normalized.Split('\n');
            AppendLine(builder, depth, label + separator + (lines.Length > 0 ? lines[0] : EmptyValue));
            for (var i = 1; i < lines.Length; i++)
                AppendLine(builder, depth + 1, lines[i]);
        }

        private static void AppendMultiline(StringBuilder builder, int depth, string value)
        {
            var lines = NormalizeLineEndings(value).Split('\n');
            foreach (var line in lines)
                AppendLine(builder, depth, line);
        }

        private static void AppendLine(StringBuilder builder, int depth, string value)
        {
            builder.Append(' ', Math.Max(0, depth) * 2);
            builder.AppendLine(string.IsNullOrEmpty(value) ? EmptyValue : value);
        }

        private static string FormatScalar(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return EmptyValue;

            if (token.Type == JTokenType.String)
            {
                var value = token.Value<string>();
                return string.IsNullOrEmpty(value) ? EmptyValue : value;
            }

            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>() ? "Yes" : "No";

            if (token is JValue scalar && scalar.Value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);

            return token.ToString(Formatting.None);
        }

        private static string GetStringValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return null;
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
        }

        private static string NormalizeLineEndings(string value)
        {
            return (value ?? EmptyValue).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static bool SameText(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string HumanizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "Value";

            var words = new List<string>();
            var current = new StringBuilder();
            for (var i = 0; i < key.Length; i++)
            {
                var character = key[i];
                if (character == '_' || character == '-' || char.IsWhiteSpace(character))
                {
                    FlushWord(words, current);
                    continue;
                }

                var previous = i > 0 ? key[i - 1] : '\0';
                var next = i + 1 < key.Length ? key[i + 1] : '\0';
                var startsWord = current.Length > 0 && char.IsUpper(character) &&
                                 (char.IsLower(previous) || char.IsDigit(previous) ||
                                  (char.IsUpper(previous) && char.IsLower(next)));
                if (startsWord)
                    FlushWord(words, current);

                current.Append(character);
            }
            FlushWord(words, current);

            for (var i = 0; i < words.Count; i++)
            {
                var lower = words[i].ToLowerInvariant();
                if (IsAcronym(lower))
                {
                    words[i] = lower.ToUpperInvariant();
                }
                else
                {
                    words[i] = lower;
                    if (i == 0 && words[i].Length > 0)
                        words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
                }
            }

            return string.Join(" ", words);
        }

        private static void FlushWord(List<string> words, StringBuilder current)
        {
            if (current.Length == 0)
                return;
            words.Add(current.ToString());
            current.Clear();
        }

        private static bool IsAcronym(string word)
        {
            switch (word)
            {
                case "api":
                case "cpu":
                case "gpu":
                case "gui":
                case "http":
                case "https":
                case "id":
                case "json":
                case "mcp":
                case "png":
                case "ui":
                case "uri":
                case "url":
                    return true;
                default:
                    return false;
            }
        }

        public List<MCPLogEntry> GetEntries()
        {
            lock (_lock)
            {
                var result = new List<MCPLogEntry>(_count);
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - 1 - i + _buffer.Length) % _buffer.Length;
                    result.Add(_buffer[idx]);
                }
                return result;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _count = 0;
            }
        }
    }
}
