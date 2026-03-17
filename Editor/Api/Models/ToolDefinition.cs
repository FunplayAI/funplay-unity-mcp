// Copyright (C) GameBooom. Licensed under GPLv3.
using System.Collections.Generic;

namespace GameBooom.Editor.Api.Models
{
    internal class ToolDefinition
    {
        public string type = "function";
        public ToolFunctionDef function;
    }

    internal class ToolFunctionDef
    {
        public string name;
        public string description;
        public ToolParametersDef parameters;
    }

    internal class ToolParametersDef
    {
        public string type = "object";
        public Dictionary<string, ToolPropertyDef> properties = new Dictionary<string, ToolPropertyDef>();
        public List<string> required = new List<string>();
    }

    internal class ToolPropertyDef
    {
        public string type;
        public string description;
        public string @default;
        public List<string> @enum;
    }
}
