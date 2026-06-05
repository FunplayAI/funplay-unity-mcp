// Copyright (C) Funplay. Licensed under MIT.

using System;

namespace Funplay.Editor.Settings
{
    internal interface ISettingsController
    {
        bool MCPServerEnabled { get; set; }
        int MCPServerPort { get; set; }
        string MCPToolExportProfile { get; set; }
        bool MCPCoreToolsConfigured { get; }
        string[] MCPCoreTools { get; set; }
        bool MCPFullToolsConfigured { get; }
        string[] MCPFullTools { get; set; }
        string MCPSelectedConfigTarget { get; set; }
        bool ExecuteCodeSafetyChecksEnabled { get; set; }
        bool ExecuteCodeStrictFilesystemSafetyEnabled { get; set; }
        bool ExecuteCodeProjectNamespaceInjectionEnabled { get; set; }
        bool PluginDebugLoggingEnabled { get; set; }

        // Out-of-process broker mode: survives Unity domain reloads. The broker process binds the
        // SAME port as MCPServerPort (so MCP clients need no config change), and this plugin connects
        // to it as a client. The broker runs under the Unity-bundled Mono; BrokerMonoPath optionally
        // overrides the auto-detected mono executable.
        bool BrokerModeEnabled { get; set; }
        string BrokerMonoPath { get; set; }

        event Action OnSettingsChanged;
    }
}
