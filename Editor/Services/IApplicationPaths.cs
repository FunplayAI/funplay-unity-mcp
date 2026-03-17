// Copyright (C) GameBooom. Licensed under GPLv3.

namespace GameBooom.Editor.Services
{
    internal interface IApplicationPaths
    {
        string DataPath { get; }
        string PersistentDataPath { get; }
    }
}
