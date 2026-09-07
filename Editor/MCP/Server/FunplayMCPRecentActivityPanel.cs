// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using Funplay.Editor.Settings;
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

        private readonly MCPInteractionLog _interactionLog;
        private readonly ISettingsController _settingsController;
        private readonly Action<Action> _defer;
        private readonly List<RowExpandState> _rows = new List<RowExpandState>();
        private ScrollView _scrollView;
        private RowExpandState _autoExpandedRow;
        private int _generation;
        private bool _expandAllByDefault;

        // Tracks one row's expand/collapse so the "latest entry auto-expands" behavior can
        // later collapse it again once a newer entry arrives - but only if the user never
        // touched it themselves in the meantime (manual choices always win).
        private sealed class RowExpandState
        {
            public VisualElement Card;
            public bool ManuallyToggled;
            public Action<bool> ApplyExpanded;
            public Action ReleaseDetails;
        }

        public FunplayMCPRecentActivityPanel(MCPServerService server, ISettingsController settingsController)
            : this(server.InteractionLog, settingsController: settingsController)
        {
        }

        internal FunplayMCPRecentActivityPanel(
            MCPInteractionLog interactionLog,
            Action<Action> defer = null,
            ISettingsController settingsController = null)
        {
            _interactionLog = interactionLog ?? throw new ArgumentNullException(nameof(interactionLog));
            _defer = defer ?? (callback => EditorApplication.delayCall += () => callback());
            _settingsController = settingsController;
            _expandAllByDefault = _settingsController?.MCPRecentActivityExpandedByDefault ?? true;
            if (_settingsController != null)
                _settingsController.OnSettingsChanged += RefreshExpansionDefaults;
        }

        public void AddTo(VisualElement parent)
        {
            ClearRows();

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
                _interactionLog.Clear();
                ClearRows();
            });
            clearButton.name = "recent-activity-clear";
            clearButton.text = "Clear";
            clearButton.style.height = 20;
            clearButton.style.width = 50;
            header.Add(clearButton);

            parent.Add(header);

            _scrollView = new ScrollView(ScrollViewMode.Vertical);
            _scrollView.name = "recent-activity-scroll";
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

            _autoExpandedRow = null;
            // GetEntries() returns newest-first (index 0 = most recent); this loop walks it
            // back-to-front so the oldest row is added (and rendered) first, newest last/bottom.
            var entries = _interactionLog.GetEntries();
            for (int i = entries.Count - 1; i >= 0; i--)
                AddRow(entries[i], isLatest: i == 0);
        }

        public void OnEntryAdded(MCPLogEntry entry)
        {
            var generation = _generation;
            _defer(() =>
            {
                // Clear/rebuild/dispose invalidates already-queued entries and scroll requests.
                if (_scrollView == null || generation != _generation)
                    return;

                AddRow(entry, isLatest: true);
                _defer(() =>
                {
                    if (_scrollView != null && generation == _generation)
                        _scrollView.scrollOffset = new Vector2(0, float.MaxValue);
                });
            });
        }

        public void Dispose()
        {
            if (_settingsController != null)
                _settingsController.OnSettingsChanged -= RefreshExpansionDefaults;
            ClearRows();
            _scrollView = null;
        }

        private void RefreshExpansionDefaults()
        {
            var expandAll = _settingsController.MCPRecentActivityExpandedByDefault;
            if (expandAll == _expandAllByDefault)
                return;

            _expandAllByDefault = expandAll;
            // Apply the preference live without rebuilding rows or discarding manual choices.
            foreach (var row in _rows)
            {
                if (!row.ManuallyToggled)
                    row.ApplyExpanded?.Invoke(expandAll || row == _autoExpandedRow);
            }
        }

        private void AddRow(MCPLogEntry entry, bool isLatest = false)
        {
            var badgeText = GetBadgeText(entry.Status);
            var accentColor = GetAccentColor(entry.Status);

            var card = new VisualElement();
            card.name = "recent-activity-row";
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
            topRow.name = "recent-activity-header";
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.alignItems = Align.Center;

            var expandArrow = new Label("▸");
            expandArrow.style.fontSize = 10;
            expandArrow.style.color = new Color(0.5f, 0.5f, 0.5f);
            expandArrow.style.marginRight = 4;
            expandArrow.style.width = 10;
            topRow.Add(expandArrow);

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

            var detailsContainer = new VisualElement { name = "recent-activity-details" };
            var hasDetails = !string.IsNullOrEmpty(displayResult) || !string.IsNullOrEmpty(entry.ImageDataUri);
            var rowState = new RowExpandState { Card = card };

            if (isLatest)
            {
                // A new entry became the latest: collapse whichever row was auto-expanded for
                // that reason before, unless the user has since touched it themselves -
                // manual choices are never overridden by the "latest" auto-expand behavior.
                if (!_expandAllByDefault && _autoExpandedRow != null && !_autoExpandedRow.ManuallyToggled)
                    _autoExpandedRow.ApplyExpanded(false);
                _autoExpandedRow = null;
            }

            if (hasDetails)
            {
                // DisplayResult is readable text with a larger, bounded display budget. The
                // compact ResultSummary is raw protocol text capped at 200 chars for resources.
                var collapsedSummary = CreateCollapsedSummaryLabel(displayResult);
                card.Add(collapsedSummary);

                card.Add(detailsContainer);

                // Native Editor tooltip: show the same formatted detail text the expanded view
                // renders, everywhere in the card. Plain `card.tooltip` is not enough: an ellipsis-
                // truncated Label (the collapsed summary line) supplies its own default tooltip
                // (its raw, unformatted text) at the moment it is hovered, which pre-empts an
                // ancestor's tooltip and made the header and the summary line disagree. Intercept
                // the TooltipEvent during the trickle-down (capture) phase instead, before it
                // reaches any such child, so this one formatted value always wins.
                card.RegisterCallback<TooltipEvent>(evt =>
                {
                    evt.tooltip = displayResult;
                    evt.rect = card.worldBound;
                    evt.StopImmediatePropagation();
                }, TrickleDown.TrickleDown);

                var expanded = false;
                Texture2D previewTexture = null;
                rowState.ReleaseDetails = () =>
                {
                    detailsContainer.Clear();
                    if (previewTexture != null)
                        UnityEngine.Object.DestroyImmediate(previewTexture);
                    previewTexture = null;
                };
                rowState.ApplyExpanded = value =>
                {
                    if (value && !expanded)
                    {
                        if (!string.IsNullOrEmpty(displayResult))
                        {
                            if (isStructuredResult)
                                detailsContainer.Add(CreateStructuredResult(displayResult));
                            else
                            {
                                var text = CreateWrappedLabel(displayResult, new Color(0.6f, 0.6f, 0.6f));
                                text.style.fontSize = 11;
                                text.style.marginTop = 3;
                                detailsContainer.Add(text);
                            }
                        }

                        if (TryCreateImagePreview(entry.ImageDataUri, out var preview))
                        {
                            previewTexture = preview.image as Texture2D;
                            detailsContainer.Add(preview);
                        }
                    }
                    else if (!value && expanded)
                    {
                        // Hiding the subtree alone retains its Labels and decoded GPU texture.
                        // Recreate on demand so collapsed history stays lightweight.
                        rowState.ReleaseDetails();
                    }
                    expanded = value;
                    expandArrow.text = expanded ? "▾" : "▸";
                    detailsContainer.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                    collapsedSummary.style.display = expanded ? DisplayStyle.None : DisplayStyle.Flex;
                };

                EventCallback<ClickEvent> toggle = evt =>
                {
                    if (evt.button != 0)
                        return;
                    rowState.ManuallyToggled = true;
                    rowState.ApplyExpanded(!expanded);
                };
                // Both the header row and the collapsed summary line must open the card -
                // they are siblings, not nested, so each needs its own click registration.
                topRow.RegisterCallback(toggle);
                collapsedSummary.RegisterCallback(toggle);

                if (isLatest)
                    _autoExpandedRow = rowState;

                rowState.ApplyExpanded(_expandAllByDefault || isLatest);
            }
            else
            {
                expandArrow.style.display = DisplayStyle.None;
            }

            _rows.Add(rowState);
            _scrollView?.contentContainer.Add(card);
            // The log is a ring buffer; mirror its bound instead of retaining evicted UI rows.
            while (_rows.Count > _interactionLog.Capacity)
            {
                var oldest = _rows[0];
                oldest.ReleaseDetails?.Invoke();
                oldest.Card.RemoveFromHierarchy();
                if (_autoExpandedRow == oldest)
                    _autoExpandedRow = null;
                _rows.RemoveAt(0);
            }
        }

        private static Label CreateCollapsedSummaryLabel(string resultSummary)
        {
            var text = string.IsNullOrEmpty(resultSummary) ? "" : resultSummary.Replace('\r', ' ').Replace('\n', ' ').Trim();

            var label = new Label(text);
            label.name = "recent-activity-summary";
            label.enableRichText = false;
            label.style.fontSize = 10;
            label.style.color = new Color(0.55f, 0.55f, 0.55f);
            label.style.marginTop = 2;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            // No manual char cap and no explicit width: the label relies on the default
            // stretch-to-parent cross-axis sizing (same as topRow) so it tracks the card's
            // actual width on every relayout, growing back on widen as well as shrinking on
            // narrow. minWidth=0 stops the NoWrap text's intrinsic width from acting as a
            // lower bound that would otherwise fight that resize in one direction.
            label.style.minWidth = 0;
            return label;
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

        private static bool TryCreateImagePreview(string imageDataUri, out Image preview)
        {
            preview = null;
            const string prefix = "data:image/png;base64,";
            if (string.IsNullOrEmpty(imageDataUri) || !imageDataUri.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            Texture2D texture = null;
            try
            {
                var bytes = Convert.FromBase64String(imageDataUri.Substring(prefix.Length));
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    return false;
                }

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
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
                return false;
            }
        }

        private void ClearRows()
        {
            _generation++;
            foreach (var row in _rows)
                row.ReleaseDetails?.Invoke();
            _rows.Clear();
            _autoExpandedRow = null;
            _scrollView?.contentContainer.Clear();
        }
    }
}
