// Copyright (C) GameBooom. Licensed under GPLv3.
using System;
using System.Collections.Generic;

namespace GameBooom.Editor.Tools
{
    internal class FunctionCall
    {
        public string Id { get; set; }
        public string ToolCallId { get; set; }
        public string FunctionName { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
        public string RawArguments { get; set; }
        public FunctionState State { get; set; } = FunctionState.Pending;
        public string Result { get; set; }
        public string Error { get; set; }
        public bool IsReadOnly { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public event Action<FunctionCall> OnStateChanged;

        public void SetState(FunctionState state)
        {
            State = state;
            OnStateChanged?.Invoke(this);
        }
    }
}
