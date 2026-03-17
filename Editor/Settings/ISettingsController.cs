// Copyright (C) GameBooom. Licensed under GPLv3.

using System;

namespace GameBooom.Editor.Settings
{
    internal interface ISettingsController
    {
        bool AutoApproveFunctions { get; set; }
        int AutoApproveLimit { get; set; }
        bool MCPServerEnabled { get; set; }
        int MCPServerPort { get; set; }

        event Action OnSettingsChanged;
    }
}
