// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Dialog")]
    internal static class ModalDialogFunctions
    {
        [Description("List open EditorWindows that look like a modal/utility popup (floating, i.e. docked==false), " +
                     "reporting title, type name, focus and docked state. " +
                     "IMPORTANT LIMITATION: Unity's blocking EditorUtility.DisplayDialog / DisplayDialogComplex are " +
                     "OS-native dialogs and do NOT appear as EditorWindows -- they will NOT show up here. " +
                     "When the returned list is empty (or the dialog you expect is missing), the blocking dialog is " +
                     "almost certainly a native OS dialog that can only be dismissed with simulate_key_press(\"return\") " +
                     "(accept) or simulate_key_press(\"escape\") (cancel).")]
        [ReadOnlyTool]
        public static object ListOpenDialogs()
        {
            var focused = EditorWindow.focusedWindow;
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();

            var candidates = windows
                .Where(w => w != null && !w.docked)
                .Select(w => new
                {
                    instanceId = ObjectIdHelper.GetSerializableId(w),
                    title = w.titleContent != null ? w.titleContent.text : w.GetType().Name,
                    type = w.GetType().FullName,
                    hasFocus = w == focused,
                    docked = w.docked
                })
                .ToList();

            if (candidates.Count == 0)
            {
                return Response.Success(
                    "No floating/utility EditorWindows are open.",
                    new
                    {
                        dialogs = candidates,
                        count = 0,
                        scannedWindows = windows.Count(w => w != null),
                        hint = "If a blocking dialog is stuck, it is a native OS dialog (EditorUtility.DisplayDialog) " +
                               "that is invisible to Resources.FindObjectsOfTypeAll<EditorWindow>(). " +
                               "Dismiss it with simulate_key_press(\"return\") to accept or simulate_key_press(\"escape\") to cancel."
                    });
            }

            return Response.Success(
                $"{candidates.Count} floating/utility window(s) open (candidate modal dialogs).",
                new
                {
                    dialogs = candidates,
                    count = candidates.Count,
                    scannedWindows = windows.Count(w => w != null),
                    hint = "Native OS dialogs (EditorUtility.DisplayDialog) are NOT listed here and can only be " +
                           "dismissed via simulate_key_press(\"return\")."
                });
        }

        [Description("Best-effort dismiss of an open EditorWindow dialog by calling Close() on it. " +
                     "With a title, closes the first open window whose title contains it (case-insensitive), " +
                     "preferring a floating/utility match; without a title, closes the topmost floating/utility window " +
                     "(the focused floating window if any, else the first non-docked one). " +
                     "LIMITATION: 'accept' cannot be reliably honored -- there is no public API to invoke a dialog's " +
                     "OK/default button, so the window is simply Close()d regardless of accept. " +
                     "This ONLY works on dialogs implemented as EditorWindows. Native OS blocking dialogs " +
                     "(EditorUtility.DisplayDialog) are invisible here and MUST be dismissed with " +
                     "simulate_key_press(\"return\") to accept or simulate_key_press(\"escape\") to cancel.")]
        [SceneEditingTool]
        public static object DismissDialog(
            [ToolParam("Substring of the window title to match (case-insensitive). If omitted, the topmost floating/utility window is closed.", Required = false)] string title = null,
            [ToolParam("Intent to accept the dialog. NOTE: cannot be reliably honored -- the window is Close()d either way; report only.", Required = false)] bool accept = true)
        {
            var focused = EditorWindow.focusedWindow;
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(w => w != null)
                .ToList();

            EditorWindow target = null;

            if (!string.IsNullOrEmpty(title))
            {
                var trimmed = title.Trim();
                var matches = windows.Where(w =>
                        w.titleContent != null &&
                        w.titleContent.text != null &&
                        w.titleContent.text.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                if (matches.Count == 0)
                {
                    return Response.Error("DIALOG_NOT_FOUND", new
                    {
                        title,
                        closed = false,
                        openWindows = windows.Select(w => new
                        {
                            title = w.titleContent != null ? w.titleContent.text : w.GetType().Name,
                            docked = w.docked
                        }).ToList(),
                        hint = "No open EditorWindow title contained that text. If this is a native OS dialog, " +
                               "dismiss it with simulate_key_press(\"return\") / simulate_key_press(\"escape\") instead."
                    });
                }

                // Only ever target a NON-DOCKED (floating/utility) window. If the title matched
                // only docked windows (Inspector/Console/etc.), refuse -- closing a docked core
                // panel would wreck the user's layout and is never a "dialog dismissal".
                target = matches.FirstOrDefault(w => !w.docked && w == focused)
                         ?? matches.FirstOrDefault(w => !w.docked);
                if (target == null)
                {
                    return Response.Error("ONLY_DOCKED_MATCH", new
                    {
                        title,
                        closed = false,
                        matched = matches.Select(w => w.titleContent != null ? w.titleContent.text : w.GetType().Name).ToList(),
                        hint = "The title matched only docked windows (e.g. Inspector/Console). dismiss_dialog only closes floating/utility windows to avoid disturbing the editor layout."
                    });
                }
            }
            else
            {
                // No title: topmost floating/utility window -- focused floating first, else first non-docked.
                var floating = windows.Where(w => !w.docked).ToList();
                target = floating.FirstOrDefault(w => w == focused) ?? floating.FirstOrDefault();

                if (target == null)
                {
                    return Response.Error("NO_DIALOG_OPEN", new
                    {
                        closed = false,
                        hint = "No floating/utility EditorWindow is open. If a blocking dialog is stuck, it is a native " +
                               "OS dialog -- dismiss it with simulate_key_press(\"return\") to accept or " +
                               "simulate_key_press(\"escape\") to cancel."
                    });
                }
            }

            var windowInfo = new
            {
                instanceId = ObjectIdHelper.GetSerializableId(target),
                title = target.titleContent != null ? target.titleContent.text : target.GetType().Name,
                type = target.GetType().FullName,
                docked = target.docked
            };

            try
            {
                target.Close();
            }
            catch (Exception ex)
            {
                return Response.Error("CLOSE_FAILED", new
                {
                    closed = false,
                    window = windowInfo,
                    message = ex.Message,
                    hint = "Close() threw. If this is a native OS dialog, use simulate_key_press(\"return\") instead."
                });
            }

            return Response.Success(
                $"Closed window '{windowInfo.title}'.",
                new
                {
                    closed = true,
                    window = windowInfo,
                    accept,
                    note = "Window was Close()d. 'accept' could not be reliably honored (no public API to press a " +
                           "dialog's OK/default button); for native OS dialogs use simulate_key_press(\"return\")."
                });
        }
    }
}
