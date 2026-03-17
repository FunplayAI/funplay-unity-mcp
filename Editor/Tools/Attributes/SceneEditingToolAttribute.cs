// Copyright (C) GameBooom. Licensed under GPLv3.
using System;

namespace GameBooom.Editor.Tools
{
    /// <summary>
    /// Marks a tool function as modifying the scene.
    /// These functions should use Undo-safe operations.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal class SceneEditingToolAttribute : Attribute { }
}
