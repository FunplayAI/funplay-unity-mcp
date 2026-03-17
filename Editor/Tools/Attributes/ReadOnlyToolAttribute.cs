// Copyright (C) GameBooom. Licensed under GPLv3.
using System;

namespace GameBooom.Editor.Tools
{
    /// <summary>
    /// Marks a tool function as read-only (safe to auto-approve).
    /// Functions with this attribute do not modify the scene or project.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal class ReadOnlyToolAttribute : Attribute { }
}
