// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Reflection;
using Funplay.Editor.Tools.Builtins;
using UnityEditor;
using UnityEngine;

namespace Funplay.Editor.Tools.Helpers
{
    [Serializable] internal sealed class PreviewViewState
    {
        public string window_id, group, size_signature;
        public int size_index, target_display;
        public bool low_resolution, zoom_supported;
        public float scale_x, scale_y, translation_x, translation_y;
    }
    // Game View has no public size/zoom API. Feature-detect, and preserve user changes instead
    // of replaying a stale global editor-window snapshot. Never resize/rearrange dock windows.
    internal static class PreviewGameView
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static object Get(object obj, string name) => obj.GetType().GetProperty(name, Flags)?.GetValue(obj) ?? obj.GetType().GetField(name, Flags)?.GetValue(obj);
        private static void Set(object obj, string name, object value)
        {
            var property = obj.GetType().GetProperty(name, Flags);
            if (property != null) property.SetValue(obj, value);
            else { var field = obj.GetType().GetField(name, Flags); if (field == null) throw new MissingMemberException(name); field.SetValue(obj, value); }
        }
        private static object Sizes()
        {
            var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameViewSizes");
            return type?.GetProperty("instance", Flags | BindingFlags.FlattenHierarchy)?.GetValue(null);
        }
        private static object Group(object sizes, string name) => sizes.GetType().GetMethod("GetGroup", Flags).Invoke(sizes,
            new[] { Enum.Parse(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameViewSizeGroupType"), name) });
        private static object Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Flags).Invoke(obj, args);
        private static string Signature(object size) => Get(size, "sizeType") + "|" + Get(size, "width") + "|" + Get(size, "height") + "|" + Get(size, "baseText");
        internal static PreviewViewState Capture()
        {
            var view = ScreenshotFunctions.GetMainPlayModeView() as EditorWindow;
            if (view == null || view.GetType().FullName != "UnityEditor.GameView") return null;
            var sizes = Sizes(); if (sizes == null) return null;
            var groupName = Get(sizes, "currentGroupType").ToString();
            var index = (int)Get(view, "selectedSizeIndex");
            var state = new PreviewViewState { window_id = ObjectIdHelper.GetSerializableId(view), group = groupName,
                size_index = index, size_signature = Signature(Invoke(Group(sizes, groupName), "GetGameViewSize", index)),
                target_display = (int)Get(view, "targetDisplay"), low_resolution = (bool)Get(view, "lowResolutionForAspectRatios") };
            var zoom = Get(view, "m_ZoomArea");
            if (zoom != null && Get(zoom, "scale") is Vector2 scale && Get(zoom, "translation") is Vector2 translation)
            {
                state.zoom_supported = true; state.scale_x = scale.x; state.scale_y = scale.y;
                state.translation_x = translation.x; state.translation_y = translation.y;
            }
            return state;
        }
        internal static void ValidateDimensions(int width, int height)
        {
            if (width == 0 && height == 0) return;
            if (width < 128 || width > 4096 || height < 128 || height > 4096) throw new ArgumentException("Preview resolution requires both width and height in 128..4096, or both zero to preserve it.");
            var view = Capture();
            if (view == null) throw new InvalidOperationException("GAME_VIEW_SETTINGS_UNAVAILABLE: open a Game tab first.");
            var group = Group(Sizes(), view.group);
            if (group.GetType().GetMethod("AddCustomSize", Flags) == null || group.GetType().GetMethod("RemoveCustomSize", Flags) == null)
                throw new InvalidOperationException("GAME_VIEW_SETTINGS_UNSUPPORTED");
        }
        internal static void Apply(PreviewViewState original, int width, int height, string label)
        {
            if (width == 0) return;
            var view = ObjectIdHelper.ToObject(original.window_id) as EditorWindow;
            if (view == null) throw new InvalidOperationException("Original Game View was closed.");
            var group = Group(Sizes(), original.group);
            var assembly = typeof(EditorWindow).Assembly;
            var size = Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"), new[] {
                Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeType"), "FixedResolution"), (object)width, height, label });
            Invoke(group, "AddCustomSize", size);
            Set(view, "selectedSizeIndex", (int)Invoke(group, "GetTotalCount") - 1);
            view.Repaint();
        }
        internal static void Restore(PreviewViewState original, PreviewViewState applied, string ownedLabel, List<string> warnings)
        {
            if (original == null) { warnings.Add("Game View settings were unavailable; no window changes were made."); return; }
            var sizes = Sizes(); var group = sizes != null ? Group(sizes, original.group) : null;
            if (group == null) { warnings.Add("Game View API unavailable; restore its settings manually."); return; }
            var view = ObjectIdHelper.ToObject(original.window_id) as EditorWindow;
            var current = view != null ? Capture() : null;
            bool sameWindow = current != null && current.window_id == original.window_id && current.group == original.group;
            int ownedIndex = -1, originalIndex = -1, count = (int)Invoke(group, "GetTotalCount");
            for (int i = 0; i < count; i++)
            {
                var size = Invoke(group, "GetGameViewSize", i);
                if ((string)Get(size, "baseText") == ownedLabel) ownedIndex = i;
                if (Signature(size) == original.size_signature) originalIndex = i;
            }
            bool sizeUnchanged = sameWindow && (current.size_signature == applied?.size_signature || current.size_signature == original.size_signature ||
                (ownedIndex >= 0 && current.size_index == ownedIndex));
            if (sizeUnchanged && originalIndex >= 0) Set(view, "selectedSizeIndex", originalIndex);
            else if (sameWindow) warnings.Add("Game View size was changed after preview setup or its original preset was removed; current user selection preserved.");
            else warnings.Add("Game View window/build-target group changed; current window settings preserved.");
            if (sameWindow && applied != null)
            {
                if (current.target_display == applied.target_display) Set(view, "targetDisplay", original.target_display);
                else warnings.Add("User-changed target display preserved.");
                if (current.low_resolution == applied.low_resolution) Set(view, "lowResolutionForAspectRatios", original.low_resolution);
                else warnings.Add("User-changed low-resolution setting preserved.");
                if (original.zoom_supported && current.zoom_supported && applied.zoom_supported &&
                    Mathf.Approximately(current.scale_x, applied.scale_x) && Mathf.Approximately(current.scale_y, applied.scale_y) &&
                    Mathf.Approximately(current.translation_x, applied.translation_x) && Mathf.Approximately(current.translation_y, applied.translation_y))
                {
                    // selectedSizeIndex alone does not refresh zoom limits. Restore against the
                    // original resolution's constraints, not the temporary preview's minimum.
                    var configure = view.GetType().GetMethod("ConfigureZoomArea", Flags);
                    if (configure == null) warnings.Add("Game View zoom constraints API unavailable; verify zoom manually.");
                    else configure.Invoke(view, null);
                    var zoom = Get(view, "m_ZoomArea");
                    Invoke(zoom, "SetTransform", new Vector2(original.translation_x, original.translation_y), new Vector2(original.scale_x, original.scale_y));
                }
                else if (original.zoom_supported) warnings.Add("Changed/unsupported Game View zoom preserved.");
            }
            // Remove only our uniquely named preset, and never one still selected in another view.
            bool usedElsewhere = Get(sizes, "currentGroupType").ToString() != original.group;
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window.GetType().FullName == "UnityEditor.GameView" && (int)Get(window, "selectedSizeIndex") == ownedIndex) usedElsewhere = true;
            if (ownedIndex >= 0 && !usedElsewhere)
            {
                // Removing a preset shifts higher indexes. Keep other windows' selected presets.
                foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                    if (window.GetType().FullName == "UnityEditor.GameView" && (int)Get(window, "selectedSizeIndex") > ownedIndex)
                        Set(window, "selectedSizeIndex", (int)Get(window, "selectedSizeIndex") - 1);
                Invoke(group, "RemoveCustomSize", ownedIndex);
            }
            else if (ownedIndex >= 0) warnings.Add("Preview size preset is still selected by a Game View; retained it.");
            view?.Repaint();
        }
        internal static bool Matches(PreviewViewState expected, PreviewViewState actual) => expected != null && actual != null &&
            expected.window_id == actual.window_id && expected.group == actual.group && expected.size_signature == actual.size_signature &&
            expected.target_display == actual.target_display && expected.low_resolution == actual.low_resolution &&
            (!expected.zoom_supported || actual.zoom_supported &&
                Mathf.Abs(expected.scale_x - actual.scale_x) < .0001f && Mathf.Abs(expected.scale_y - actual.scale_y) < .0001f &&
                Mathf.Abs(expected.translation_x - actual.translation_x) < .01f && Mathf.Abs(expected.translation_y - actual.translation_y) < .01f);
    }
}
