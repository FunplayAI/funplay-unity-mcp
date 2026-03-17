// Copyright (C) GameBooom. Licensed under GPLv3.

using System;

namespace GameBooom.Editor.DI
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal sealed class InjectAttribute : Attribute
    {
    }
}
