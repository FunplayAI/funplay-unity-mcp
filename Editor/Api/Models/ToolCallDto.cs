// Copyright (C) GameBooom. Licensed under GPLv3.
using System.Collections.Generic;

namespace GameBooom.Editor.Api.Models
{
    internal class ToolCallDto
    {
        public string id;
        public string type = "function";
        public ToolCallFunctionDto function;
    }

    internal class ToolCallFunctionDto
    {
        public string name;
        public string arguments; // JSON string
    }
}
