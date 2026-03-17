// Copyright (C) GameBooom. Licensed under GPLv3.

namespace GameBooom.Editor.Services
{
    internal interface IEditorStateService
    {
        bool IsPlayingOrWillChangePlaymode { get; }
        bool IsCompiling { get; }
    }
}
