// Copyright (C) GameBooom. Licensed under GPLv3.
using System;

namespace GameBooom.Editor.Tools
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal class ToolParamAttribute : Attribute
    {
        public string Description { get; }
        public bool Required { get; set; } = true;
        public string DefaultValue { get; set; }

        public ToolParamAttribute(string description)
        {
            Description = description;
        }
    }
}
