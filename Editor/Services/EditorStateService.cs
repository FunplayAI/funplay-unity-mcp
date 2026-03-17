// Copyright (C) GameBooom. Licensed under GPLv3.

using UnityEditor;

namespace GameBooom.Editor.Services
{
    internal class EditorStateService : IEditorStateService
    {
        public bool IsPlayingOrWillChangePlaymode =>
            EditorApplication.isPlayingOrWillChangePlaymode;

        public bool IsCompiling => EditorApplication.isCompiling;
    }
}
