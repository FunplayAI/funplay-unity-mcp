// Copyright (C) GameBooom. Licensed under GPLv3.

using UnityEngine;

namespace GameBooom.Editor.Services
{
    internal class ApplicationPaths : IApplicationPaths
    {
        public string DataPath => Application.dataPath;
        public string PersistentDataPath => Application.persistentDataPath;
    }
}
