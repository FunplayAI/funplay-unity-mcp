// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.MCP.Server
{
    internal enum MCPActivityDisplayLineKind
    {
        Message,
        Section,
        Property,
        NumberedItem,
        Continuation,
        Spacer,
        Truncated
    }

    internal sealed class MCPActivityDisplayLine
    {
        public MCPActivityDisplayLine(
            MCPActivityDisplayLineKind kind,
            int depth,
            string label,
            string value)
        {
            Kind = kind;
            Depth = depth;
            Label = label ?? "";
            Value = value ?? "";
        }

        public MCPActivityDisplayLineKind Kind { get; }
        public int Depth { get; }
        public string Label { get; }
        public string Value { get; }
    }

    internal sealed class FunplayMCPRecentActivityPanel : IDisposable
    {
        private static readonly Color StructuredBackgroundColor = new Color(0.13f, 0.13f, 0.13f);
        private static readonly Color MessageColor = new Color(0.84f, 0.84f, 0.84f);
        private static readonly Color PropertyColor = new Color(0.42f, 0.70f, 0.92f);
        private static readonly Color ValueColor = new Color(0.72f, 0.72f, 0.72f);
        private static readonly Color PositiveValueColor = new Color(0.38f, 0.78f, 0.48f);
        private static readonly Color NegativeValueColor = new Color(0.94f, 0.43f, 0.43f);
        private static readonly Color PendingValueColor = new Color(0.95f, 0.68f, 0.25f);
        private static readonly Color MutedValueColor = new Color(0.56f, 0.56f, 0.56f);

        private readonly MCPServerService _server;
        private readonly List<Texture2D> _previewTextures = new List<Texture2D>();
        private ScrollView _scrollView;

        public FunplayMCPRecentActivityPanel(MCPServerService server)
        {
            _server = server;
        }

        public void AddTo(VisualElement parent)
        {
            ClearPreviewTextures();

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginTop = 12;
            header.style.marginBottom = 4;

            var label = new Label("Recent Activity");
            label.style.fontSize = 12;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = new Color(0.75f, 0.75f, 0.75f);
            label.style.flexGrow = 1;
            header.Add(label);

            var clearButton = new Button(() =>
            {
                _server.InteractionLog.Clear();
                ClearPreviewTextures();
                _scrollView?.contentContainer.Clear();
            });
            clearButton.text = "Clear";
            clearButton.style.height = 20;
            clearButton.style.width = 50;
            header.Add(clearButton);

            parent.Add(header);

            _scrollView = new ScrollView(ScrollViewMode.Vertical);
            _scrollView.style.flexGrow = 1;
            _scrollView.style.backgroundColor = new Color(0.14f, 0.14f, 0.14f);
            _scrollView.style.borderTopLeftRadius = 4;
            _scrollView.style.borderTopRightRadius = 4;
            _scrollView.style.borderBottomLeftRadius = 4;
            _scrollView.style.borderBottomRightRadius = 4;
            _scrollView.style.paddingLeft = 6;
            _scrollView.style.paddingRight = 6;
            _scrollView.style.paddingTop = 4;
            _scrollView.style.paddingBottom = 4;
            parent.Add(_scrollView);

            var entries = _server.InteractionLog.GetEntries();
            for (int i = entries.Count - 1; i >= 0; i--)
                AddRow(entries[i]);
        }

        public void OnEntryAdded(MCPLogEntry entry)
        {
            EditorApplication.delayCall += () =>
            {
                if (_scrollView == null)
                    return;

                AddRow(entry);
                EditorApplication.delayCall += () =>
                {
                    if (_scrollView != null)
                        _scrollView.scrollOffset = new Vector2(0, float.MaxValue);
                };
            };
        }

        public void Dispose()
        {
            ClearPreviewTextures();
            _scrollView = null;
        }

        private void AddRow(MCPLogEntry entry)
        {
            var badgeText = GetBadgeText(entry.Status);
            var accentColor = GetAccentColor(entry.Status);

            var card = new VisualElement();
            card.style.backgroundColor = new Color(0.19f, 0.19f, 0.19f);
            card.style.borderTopLeftRadius = 4;
            card.style.borderTopRightRadius = 4;
            card.style.borderBottomLeftRadius = 4;
            card.style.borderBottomRightRadius = 4;
            card.style.borderLeftWidth = 3;
            card.style.borderLeftColor = accentColor;
            card.style.paddingLeft = 8;
            card.style.paddingRight = 8;
            card.style.paddingTop = 5;
            card.style.paddingBottom = 5;
            card.style.marginBottom = 3;

            var topRow = new VisualElement();
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.alignItems = Align.Center;

            var timeLabel = new Label(entry.Timestamp.ToString("HH:mm:ss"));
            timeLabel.style.fontSize = 10;
            timeLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            timeLabel.style.marginRight = 6;
            timeLabel.style.minWidth = 48;
            topRow.Add(timeLabel);

            var toolLabel = new Label(entry.ToolName);
            toolLabel.style.fontSize = 12;
            toolLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            toolLabel.style.color = new Color(0.88f, 0.88f, 0.88f);
            toolLabel.style.flexGrow = 1;
            topRow.Add(toolLabel);

            var badge = new Label(badgeText);
            badge.style.fontSize = 9;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.color = Color.white;
            badge.style.backgroundColor = accentColor;
            badge.style.borderTopLeftRadius = 3;
            badge.style.borderTopRightRadius = 3;
            badge.style.borderBottomLeftRadius = 3;
            badge.style.borderBottomRightRadius = 3;
            badge.style.paddingLeft = 5;
            badge.style.paddingRight = 5;
            badge.style.paddingTop = 1;
            badge.style.paddingBottom = 1;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            topRow.Add(badge);

            card.Add(topRow);

            var isStructuredResult = entry.IsJsonResult;
            var displayResult = string.IsNullOrEmpty(entry.DisplayResult)
                ? entry.ResultSummary
                : entry.DisplayResult;
            // Entries created before the structured renderer was loaded only carry their compact
            // summary. Re-parse small, complete JSON summaries at render time so reopening the
            // panel upgrades those legacy rows instead of preserving raw protocol JSON forever.
            if (!isStructuredResult &&
                MCPInteractionLog.TryFormatJsonForDisplay(entry.ResultSummary, out var legacyDisplayResult))
            {
                displayResult = MCPInteractionLog.TruncateDisplayResult(legacyDisplayResult);
                isStructuredResult = true;
            }

            if (!string.IsNullOrEmpty(displayResult))
            {
                if (isStructuredResult)
                {
                    card.Add(CreateStructuredResult(displayResult));
                }
                else
                {
                    var summaryLabel = CreateWrappedLabel(displayResult, new Color(0.6f, 0.6f, 0.6f));
                    summaryLabel.style.fontSize = 11;
                    summaryLabel.style.marginTop = 3;
                    card.Add(summaryLabel);
                }
            }

            if (!string.IsNullOrEmpty(entry.ImageDataUri) &&
                TryCreateImagePreview(entry.ImageDataUri, out var preview))
            {
                card.Add(preview);
            }

            _scrollView?.contentContainer.Add(card);
        }

        private static VisualElement CreateStructuredResult(string displayResult)
        {
            var container = new VisualElement();
            container.style.backgroundColor = StructuredBackgroundColor;
            container.style.marginTop = 5;
            container.style.paddingLeft = 7;
            container.style.paddingRight = 7;
            container.style.paddingTop = 5;
            container.style.paddingBottom = 5;
            container.style.borderTopLeftRadius = 3;
            container.style.borderTopRightRadius = 3;
            container.style.borderBottomLeftRadius = 3;
            container.style.borderBottomRightRadius = 3;

            foreach (var line in ParseStructuredDisplay(displayResult))
                AddStructuredLine(container, line);

            return container;
        }

        private static void AddStructuredLine(VisualElement container, MCPActivityDisplayLine line)
        {
            if (line.Kind == MCPActivityDisplayLineKind.Spacer)
            {
                var spacer = new VisualElement();
                spacer.style.height = 5;
                container.Add(spacer);
                return;
            }

            var indent = Math.Max(0, line.Depth) * 12;
            switch (line.Kind)
            {
                case MCPActivityDisplayLineKind.Message:
                {
                    var message = CreateWrappedLabel(line.Value, MessageColor);
                    message.style.unityFontStyleAndWeight = FontStyle.Bold;
                    message.style.marginLeft = indent;
                    message.style.marginBottom = 1;
                    container.Add(message);
                    break;
                }
                case MCPActivityDisplayLineKind.Section:
                {
                    var section = CreateWrappedLabel(line.Label, PropertyColor);
                    section.style.unityFontStyleAndWeight = FontStyle.Bold;
                    section.style.marginLeft = indent;
                    section.style.marginTop = 2;
                    section.style.marginBottom = 1;
                    container.Add(section);
                    break;
                }
                case MCPActivityDisplayLineKind.Property:
                    container.Add(CreateKeyValueRow(line.Label + ":", line.Value, indent));
                    break;
                case MCPActivityDisplayLineKind.NumberedItem:
                    container.Add(CreateKeyValueRow(line.Label, line.Value, indent));
                    break;
                case MCPActivityDisplayLineKind.Truncated:
                {
                    var truncated = CreateWrappedLabel(line.Value, PendingValueColor);
                    truncated.style.unityFontStyleAndWeight = FontStyle.Italic;
                    truncated.style.marginLeft = indent;
                    truncated.style.marginTop = 2;
                    container.Add(truncated);
                    break;
                }
                default:
                {
                    var continuation = CreateWrappedLabel(line.Value, ValueColor);
                    continuation.style.marginLeft = indent;
                    container.Add(continuation);
                    break;
                }
            }
        }

        private static VisualElement CreateKeyValueRow(string keyText, string valueText, int indent)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.FlexStart;
            row.style.marginLeft = indent;
            row.style.marginBottom = 1;

            var key = CreateWrappedLabel(keyText, PropertyColor);
            key.style.unityFontStyleAndWeight = FontStyle.Bold;
            key.style.flexShrink = 0;
            key.style.maxWidth = Length.Percent(46);
            key.style.marginRight = 6;
            row.Add(key);

            if (!string.IsNullOrEmpty(valueText))
            {
                var value = CreateWrappedLabel(valueText, GetValueColor(valueText));
                value.style.flexGrow = 1;
                value.style.flexShrink = 1;
                value.style.flexBasis = 0;
                value.style.minWidth = 0;
                row.Add(value);
            }

            return row;
        }

        private static Label CreateWrappedLabel(string text, Color color)
        {
            var label = new Label(text ?? "");
            label.enableRichText = false;
            label.style.fontSize = 11;
            label.style.color = color;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.overflow = Overflow.Hidden;
            return label;
        }

        private static Color GetValueColor(string value)
        {
            var normalized = (value ?? "").Trim().TrimEnd('.', '!', ':').ToLowerInvariant();
            switch (normalized)
            {
                case "yes":
                case "true":
                case "ok":
                case "success":
                case "succeeded":
                case "passed":
                case "complete":
                case "completed":
                case "finished":
                case "ready":
                    return PositiveValueColor;
                case "no":
                case "false":
                case "error":
                case "failed":
                case "failure":
                    return NegativeValueColor;
                case "running":
                case "pending":
                case "interrupted":
                case "in progress":
                    return PendingValueColor;
                case "—":
                    return MutedValueColor;
                default:
                    return ValueColor;
            }
        }

        internal static List<MCPActivityDisplayLine> ParseStructuredDisplay(string displayResult)
        {
            var parsed = new List<MCPActivityDisplayLine>();
            if (string.IsNullOrEmpty(displayResult))
                return parsed;

            var normalized = displayResult.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            var leadingMessageEnd = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    leadingMessageEnd = i;
                    break;
                }
            }

            var hasContent = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    parsed.Add(new MCPActivityDisplayLine(MCPActivityDisplayLineKind.Spacer, 0, "", ""));
                    continue;
                }

                var spaceCount = 0;
                while (spaceCount < rawLine.Length && rawLine[spaceCount] == ' ')
                    spaceCount++;

                var depth = spaceCount / 2;
                var text = rawLine.Substring(spaceCount).TrimEnd();
                if (string.Equals(text, "... (truncated)", StringComparison.Ordinal))
                {
                    parsed.Add(new MCPActivityDisplayLine(
                        MCPActivityDisplayLineKind.Truncated, depth, "", text));
                }
                else if (leadingMessageEnd > 0 && i < leadingMessageEnd)
                {
                    parsed.Add(new MCPActivityDisplayLine(
                        MCPActivityDisplayLineKind.Message, depth, "", text));
                }
                else if (TryParseNumberedItem(text, out var number, out var numberedValue))
                {
                    parsed.Add(new MCPActivityDisplayLine(
                        MCPActivityDisplayLineKind.NumberedItem, depth, number, numberedValue));
                }
                else if (text.EndsWith(":", StringComparison.Ordinal))
                {
                    parsed.Add(new MCPActivityDisplayLine(
                        MCPActivityDisplayLineKind.Section, depth, text.Substring(0, text.Length - 1), ""));
                }
                else
                {
                    var separator = text.IndexOf(": ", StringComparison.Ordinal);
                    if (separator > 0)
                    {
                        parsed.Add(new MCPActivityDisplayLine(
                            MCPActivityDisplayLineKind.Property,
                            depth,
                            text.Substring(0, separator),
                            text.Substring(separator + 2)));
                    }
                    else
                    {
                        parsed.Add(new MCPActivityDisplayLine(
                            hasContent ? MCPActivityDisplayLineKind.Continuation : MCPActivityDisplayLineKind.Message,
                            depth,
                            "",
                            text));
                    }
                }

                hasContent = true;
            }

            return parsed;
        }

        private static bool TryParseNumberedItem(string text, out string number, out string value)
        {
            number = null;
            value = null;
            if (string.IsNullOrEmpty(text))
                return false;

            var digitCount = 0;
            while (digitCount < text.Length && char.IsDigit(text[digitCount]))
                digitCount++;

            if (digitCount == 0 || digitCount >= text.Length || text[digitCount] != '.')
                return false;
            if (digitCount + 1 < text.Length && text[digitCount + 1] != ' ')
                return false;

            number = text.Substring(0, digitCount + 1);
            value = digitCount + 1 == text.Length
                ? ""
                : text.Substring(digitCount + 2);
            return true;
        }

        internal static string GetBadgeText(MCPToolCallStatus status)
        {
            switch (status)
            {
                case MCPToolCallStatus.Success:
                    return "OK";
                case MCPToolCallStatus.Interrupted:
                    return "INT";
                default:
                    return "ERR";
            }
        }

        private static Color GetAccentColor(MCPToolCallStatus status)
        {
            switch (status)
            {
                case MCPToolCallStatus.Success:
                    return new Color(0.3f, 0.75f, 0.4f);
                case MCPToolCallStatus.Interrupted:
                    return new Color(0.95f, 0.68f, 0.25f);
                default:
                    return new Color(0.9f, 0.35f, 0.35f);
            }
        }

        private bool TryCreateImagePreview(string imageDataUri, out Image preview)
        {
            preview = null;
            const string prefix = "data:image/png;base64,";
            if (string.IsNullOrEmpty(imageDataUri) || !imageDataUri.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            try
            {
                var bytes = Convert.FromBase64String(imageDataUri.Substring(prefix.Length));
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    return false;
                }

                _previewTextures.Add(texture);

                preview = new Image
                {
                    image = texture,
                    scaleMode = ScaleMode.ScaleToFit
                };
                preview.style.height = 150;
                preview.style.marginTop = 6;
                preview.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f);
                preview.style.borderTopLeftRadius = 3;
                preview.style.borderTopRightRadius = 3;
                preview.style.borderBottomLeftRadius = 3;
                preview.style.borderBottomRightRadius = 3;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ClearPreviewTextures()
        {
            foreach (var texture in _previewTextures)
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }

            _previewTextures.Clear();
        }
    }
}
