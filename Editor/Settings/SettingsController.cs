// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using UnityEditor;

namespace GameBooom.Editor.Settings
{
    internal class SettingsController : ISettingsController
    {
        private const string Prefix = "GameBooom_";

        public event Action OnSettingsChanged;

        public bool AutoApproveFunctions
        {
            get => EditorPrefs.GetBool(Prefix + "AutoApprove", true);
            set
            {
                EditorPrefs.SetBool(Prefix + "AutoApprove", value);
                OnSettingsChanged?.Invoke();
            }
        }

        public int AutoApproveLimit
        {
            get => EditorPrefs.GetInt(Prefix + "AutoApproveLimit", 10);
            set
            {
                EditorPrefs.SetInt(Prefix + "AutoApproveLimit", value);
                OnSettingsChanged?.Invoke();
            }
        }

        public bool MCPServerEnabled
        {
            get => EditorPrefs.GetBool(Prefix + "MCPServerEnabled", false);
            set
            {
                EditorPrefs.SetBool(Prefix + "MCPServerEnabled", value);
                OnSettingsChanged?.Invoke();
            }
        }

        public int MCPServerPort
        {
            get => EditorPrefs.GetInt(Prefix + "MCPServerPort", 8765);
            set
            {
                EditorPrefs.SetInt(Prefix + "MCPServerPort", value);
                OnSettingsChanged?.Invoke();
            }
        }
    }
}
