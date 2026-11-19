// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.MCP.Server
{
    // Source ranges keep comments, number spelling and unrelated configuration byte-for-byte.
    // Deliberately accepts only JSON plus (when enabled) comments and trailing commas, not JS.
    internal sealed class MCPConfigJson
    {
        internal const int MaximumLength = 8 * 1024 * 1024;
        private readonly string source;
        private readonly bool allowComments;
        private readonly Node root;
        private int offset;
        private static readonly Regex Literal = new Regex(
            @"\G(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)",
            RegexOptions.CultureInvariant);

        private sealed class Node
        {
            internal int Start, End;
            internal JToken Value;
            internal List<Property> Properties;
        }

        private sealed class Property
        {
            internal string Key;
            internal int Start, Comma = -1;
            internal Node Node;
        }

        private sealed class Edit
        {
            internal int Start, End, Order;
            internal string Text;
        }

        internal MCPConfigJson(string text, bool jsonc = false)
        {
            source = text ?? string.Empty;
            allowComments = jsonc;
            if (source.Length > MaximumLength) Fail("configuration exceeds 8 MiB characters");
            if (source.Length > 0 && source[0] == '\uFEFF') offset++;
            Trivia();
            if (offset < source.Length) root = ReadValue(0);
            Trivia();
            if (offset != source.Length) Fail("unexpected content after the root value");
            if (root != null && !(root.Value is JObject)) Fail("configuration root must be an object");
        }

        internal JObject Object => root == null ? new JObject() : (JObject)root.Value.DeepClone();

        internal string Update(JObject desired)
        {
            if (desired == null) throw new ArgumentNullException(nameof(desired));
            var newline = source.Contains("\r\n") ? "\r\n" : "\n";
            var match = Regex.Match(source, "\\r?\\n([ \\t]+)\"");
            var indent = match.Success ? match.Groups[1].Value : "  ";
            var edits = new List<Edit>();
            if (root == null)
                return source + (source.Length > 0 && !source.EndsWith("\n") ? newline : "") +
                       Serialize(desired, indent, newline, "") + newline;
            Visit(root, desired, edits, indent, newline);
            var result = source;
            foreach (var edit in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End)
                         .ThenByDescending(e => e.Order))
                result = result.Substring(0, edit.Start) + edit.Text + result.Substring(edit.End);
            if (!JToken.DeepEquals(new MCPConfigJson(result, allowComments).Object, desired))
                throw new InvalidOperationException("Configuration edit could not be verified; file was left unchanged.");
            return result;
        }

        private void Visit(Node node, JToken next, List<Edit> edits, string indent, string newline)
        {
            if (JToken.DeepEquals(node.Value, next)) return;
            if (node.Properties == null || !(next is JObject map))
            {
                AddEdit(edits, node.Start, node.End, Serialize(next, indent, newline, LineIndent(node.Start)));
                return;
            }

            var retained = new List<Property>();
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.Properties)
            {
                existing.Add(property.Key);
                if (map.TryGetValue(property.Key, StringComparison.Ordinal, out var value))
                {
                    retained.Add(property);
                    Visit(property.Node, value, edits, indent, newline);
                }
                else
                {
                    AddEdit(edits, property.Start, property.Node.End, "");
                    if (property.Comma >= 0) AddEdit(edits, property.Comma, property.Comma + 1, "");
                }
            }

            var added = map.Properties().Where(p => !existing.Contains(p.Name)).ToArray();
            var last = retained.LastOrDefault();
            if (added.Length == 0)
            {
                // Removing the last property must not introduce a trailing comma in strict JSON.
                if (!allowComments && last != null && last.Comma >= 0)
                    AddEdit(edits, last.Comma, last.Comma + 1, "");
                return;
            }
            if (last != null && last.Comma < 0) AddEdit(edits, last.Node.End, last.Node.End, ",");
            var prefix = LineIndent(node.Start) + indent;
            var closing = node.End - 1;
            var lineStart = source.LastIndexOf('\n', Math.Max(0, closing - 1)) + 1;
            var ownLine = source.Substring(lineStart, closing - lineStart).All(c => c == ' ' || c == '\t');
            var trailing = allowComments && node.Properties.Count > 0 && node.Properties.Last().Comma >= 0;
            var fields = string.Join("," + newline, added.Select(p => prefix + Quote(p.Name) + ": " +
                Serialize(p.Value, indent, newline, prefix)));
            var insertion = (ownLine ? "" : newline) + fields + (trailing ? "," : "") + newline +
                            (ownLine ? "" : LineIndent(node.Start));
            AddEdit(edits, ownLine ? lineStart : closing, ownLine ? lineStart : closing, insertion);
        }

        private static void AddEdit(List<Edit> edits, int start, int end, string text)
        {
            edits.Add(new Edit { Start = start, End = end, Text = text, Order = edits.Count });
        }

        private string LineIndent(int position)
        {
            var start = position == 0 ? 0 : source.LastIndexOf('\n', position - 1) + 1;
            var end = start;
            while (end < source.Length && (source[end] == ' ' || source[end] == '\t')) end++;
            return source.Substring(start, end - start);
        }

        private static string Serialize(JToken token, string indent, string newline, string prefix)
        {
            var builder = new StringBuilder();
            using (var text = new StringWriter(builder, CultureInfo.InvariantCulture))
            using (var writer = new JsonTextWriter(text)
            {
                Formatting = Formatting.Indented,
                IndentChar = indent[0], Indentation = indent.Length
            }) token.WriteTo(writer);
            return builder.ToString().Replace("\r\n", "\n").Replace("\n", newline + prefix);
        }

        private static string Quote(string value) => new JValue(value).ToString(Formatting.None);

        private Node ReadValue(int depth)
        {
            if (depth > 128) Fail("configuration nesting is too deep");
            Trivia();
            var start = offset;
            if (offset >= source.Length) Fail("expected a value");
            if (source[offset] == '"') return ReadString();
            var character = source[offset];
            if (character != '{' && character != '[')
            {
                var literal = Literal.Match(source, offset);
                if (!literal.Success) Fail("expected a JSON value");
                offset += literal.Length;
                return new Node { Start = start, End = offset, Value = ParseScalar(literal.Value) };
            }

            var isObject = character == '{';
            var close = isObject ? '}' : ']';
            JToken container = isObject ? (JToken)new JObject() : new JArray();
            var properties = isObject ? new List<Property>() : null;
            offset++;
            Trivia();
            while (offset >= source.Length || source[offset] != close)
            {
                Node key = null;
                if (isObject)
                {
                    if (offset >= source.Length || source[offset] != '"') Fail("expected an object key");
                    key = ReadString();
                    if (((JObject)container).Property((string)key.Value, StringComparison.Ordinal) != null)
                        Fail("duplicate object key");
                    Trivia();
                    if (offset >= source.Length || source[offset++] != ':') Fail("expected a colon");
                }
                var child = ReadValue(depth + 1);
                Property property = null;
                if (isObject)
                {
                    ((JObject)container).Add((string)key.Value, child.Value);
                    property = new Property { Key = (string)key.Value, Start = key.Start, Node = child };
                    properties.Add(property);
                }
                else ((JArray)container).Add(child.Value);
                Trivia();
                if (offset < source.Length && source[offset] == close) break;
                if (offset >= source.Length || source[offset] != ',') Fail("expected a comma or closing delimiter");
                if (property != null) property.Comma = offset;
                offset++;
                Trivia();
                if (!allowComments && offset < source.Length && source[offset] == close) Fail("trailing comma");
            }
            offset++;
            return new Node { Start = start, End = offset, Value = container, Properties = properties };
        }

        private Node ReadString()
        {
            var start = offset++;
            while (offset < source.Length)
            {
                var character = source[offset++];
                if (character == '"')
                    return new Node { Start = start, End = offset,
                        Value = ParseScalar(source.Substring(start, offset - start)) };
                if (character < 0x20) Fail("unescaped control character");
                if (character != '\\') continue;
                if (offset >= source.Length) Fail("unterminated string escape");
                var escape = source[offset++];
                if (escape == 'u')
                {
                    for (var i = 0; i < 4; i++)
                        if (offset >= source.Length || !Uri.IsHexDigit(source[offset++])) Fail("invalid Unicode escape");
                }
                else if ("\"\\/bfnrt".IndexOf(escape) < 0) Fail("invalid string escape");
            }
            Fail("unterminated string");
            return null;
        }

        private void Trivia()
        {
            while (offset < source.Length)
            {
                var c = source[offset];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { offset++; continue; }
                if (c != '/' || offset + 1 >= source.Length ||
                    (source[offset + 1] != '/' && source[offset + 1] != '*')) return;
                if (!allowComments) Fail("comments are not supported by this client");
                if (source[offset + 1] == '/')
                {
                    offset += 2;
                    while (offset < source.Length && source[offset] != '\r' && source[offset] != '\n') offset++;
                }
                else
                {
                    var end = source.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                    if (end < 0) Fail("unterminated comment");
                    offset = end + 2;
                }
            }
        }

        private static JToken ParseScalar(string value)
        {
            using (var text = new StringReader(value))
            using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
                return JToken.ReadFrom(reader);
        }

        private void Fail(string reason) => throw new InvalidOperationException(
            $"Invalid client JSON{(allowComments ? "C" : "")} at offset {offset}: {reason}. File was left unchanged.");
    }
}
