// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Funplay.Editor.MCP.Server
{
    [InitializeOnLoad]
    internal static class MCPBrokerProcessManager
    {
        private const int StartProbeAttempts = 40;
        private const int StartProbeDelayMs = 125;
        private static readonly object Gate = new object();
        private static readonly MCPBrokerRuntimePaths DefaultPaths;

        public static string LastError { get; private set; }

        static MCPBrokerProcessManager()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            var runtimeDir = Path.Combine(projectRoot, "Library", "FunplayMcp", "Broker");
            DefaultPaths = new MCPBrokerRuntimePaths(
                projectRoot,
                Path.Combine(runtimeDir, "broker.pid"),
                runtimeDir,
                ResolveBrokerSourcePath(projectRoot));

            EditorApplication.quitting += Stop;
        }

        public static bool EnsureRunning(int port, string monoPathOverride)
        {
            return EnsureRunning(port, monoPathOverride, DefaultPaths);
        }

        internal static bool EnsureRunning(int port, string monoPathOverride, MCPBrokerRuntimePaths paths)
        {
            lock (Gate)
            {
                LastError = null;

                if (TryReadState(paths.PidFilePath, out var existing))
                {
                    if (existing.Port == port &&
                        TryProbeBroker(existing.Port, existing.Token, out var health) &&
                        health.Pid == existing.Pid)
                    {
                        return true;
                    }

                    // The pid file points at a broker we started, but it no longer passes
                    // the probe (typically a protocol-version mismatch after a package
                    // upgrade). Shut it down with its recorded token so the port frees up,
                    // instead of leaving the stale process squatting on the port forever.
                    if (existing.Port == port && IsTcpPortOpen(port))
                    {
                        SendShutdown(existing.Port, existing.Token);
                        WaitForExit(existing.Pid, 2500);
                        KillVerifiedProcess(existing.Pid);
                    }

                    DeletePidFile(paths.PidFilePath);
                    if (existing.Port == port && IsTcpPortOpen(port))
                    {
                        LastError = "Port is already in use, but it is not a verified Funplay broker.";
                        return false;
                    }
                }

                if (IsTcpPortOpen(port))
                {
                    LastError = "Port is already in use by another process.";
                    return false;
                }

                var mono = ResolveMono(monoPathOverride);
                if (string.IsNullOrEmpty(mono))
                {
                    LastError = "Unity-bundled Mono runtime was not found.";
                    Debug.LogWarning("[Funplay MCP Server] " + LastError);
                    return false;
                }

                var brokerExe = EnsureBrokerExe(paths, mono);
                if (string.IsNullOrEmpty(brokerExe))
                {
                    LastError = LastError ?? "Broker executable could not be prepared.";
                    Debug.LogWarning("[Funplay MCP Server] " + LastError);
                    return false;
                }

                var token = Guid.NewGuid().ToString("N");
                Process process;
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = mono,
                        Arguments = Quote(brokerExe) + " --port " + port + " --token " + token,
                        WorkingDirectory = Path.GetDirectoryName(brokerExe),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    process = Process.Start(startInfo);
                    if (process == null)
                    {
                        LastError = "Failed to start broker process.";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    LastError = "Failed to start broker process: " + ex.Message;
                    Debug.LogError("[Funplay MCP Server] " + LastError);
                    return false;
                }

                var state = new BrokerProcessState
                {
                    Pid = process.Id,
                    Port = port,
                    Token = token,
                    Protocol = MCPBrokerProtocol.Version
                };
                WriteState(paths.PidFilePath, state);

                for (var attempt = 0; attempt < StartProbeAttempts; attempt++)
                {
                    if (TryProbeBroker(port, token, out var health) && health.Pid == process.Id)
                    {
                        Debug.Log("[Funplay MCP Server] Broker started (pid=" + process.Id + ", port=" + port + ").");
                        return true;
                    }

                    Thread.Sleep(StartProbeDelayMs);
                }

                LastError = "Broker process started but did not pass health checks.";
                Debug.LogWarning("[Funplay MCP Server] " + LastError);
                KillVerifiedProcess(process.Id);
                DeletePidFile(paths.PidFilePath);
                return false;
            }
        }

        public static bool IsRunning(out int pid, out int port)
        {
            return IsRunning(DefaultPaths, out pid, out port);
        }

        public static bool TryGetConnectionInfo(int expectedPort, out BrokerConnectionInfo connection)
        {
            return TryGetConnectionInfo(DefaultPaths, expectedPort, out connection);
        }

        internal static bool TryGetConnectionInfo(MCPBrokerRuntimePaths paths, int expectedPort, out BrokerConnectionInfo connection)
        {
            connection = null;
            if (!TryReadState(paths.PidFilePath, out var state) || state.Port != expectedPort)
                return false;

            if (!TryProbeBroker(state.Port, state.Token, out var health) || health.Pid != state.Pid)
                return false;

            connection = new BrokerConnectionInfo
            {
                Pid = state.Pid,
                Port = state.Port,
                Token = state.Token
            };
            return true;
        }

        internal static bool IsRunning(MCPBrokerRuntimePaths paths, out int pid, out int port)
        {
            pid = 0;
            port = 0;

            if (!TryReadState(paths.PidFilePath, out var state))
                return false;

            if (!TryProbeBroker(state.Port, state.Token, out var health) || health.Pid != state.Pid)
                return false;

            pid = state.Pid;
            port = state.Port;
            return true;
        }

        public static void Stop()
        {
            Stop(DefaultPaths);
        }

        internal static void Stop(MCPBrokerRuntimePaths paths)
        {
            lock (Gate)
            {
                if (!TryReadState(paths.PidFilePath, out var state))
                    return;

                var verified = TryProbeBroker(state.Port, state.Token, out var health) && health.Pid == state.Pid;
                if (verified)
                {
                    SendShutdown(state.Port, state.Token);
                    WaitForExit(state.Pid, 2500);
                    KillVerifiedProcess(state.Pid);
                }

                DeletePidFile(paths.PidFilePath);
            }
        }

        internal static bool TryProbeBroker(int port, string token, out BrokerHealth health)
        {
            health = null;
            if (port <= 0 || string.IsNullOrEmpty(token))
                return false;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + MCPBrokerProtocol.HealthPath);
                request.Method = "GET";
                request.Timeout = 500;
                request.ReadWriteTimeout = 500;
                request.KeepAlive = false;
                request.Headers[MCPBrokerProtocol.TokenHeader] = token;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        return false;

                    if (!string.Equals(response.Headers[MCPBrokerProtocol.BrokerHeader],
                            MCPBrokerProtocol.Version.ToString(), StringComparison.Ordinal))
                        return false;

                    var json = reader.ReadToEnd();
                    var dict = SimpleJsonHelper.Deserialize(json) as Dictionary<string, object>;
                    if (dict == null)
                        return false;

                    health = new BrokerHealth
                    {
                        Name = GetString(dict, "name"),
                        Pid = GetInt(dict, "pid"),
                        Protocol = GetInt(dict, "protocol"),
                        Pending = GetInt(dict, "pending")
                    };

                    return string.Equals(health.Name, MCPBrokerProtocol.Name, StringComparison.Ordinal) &&
                           health.Protocol == MCPBrokerProtocol.Version &&
                           health.Pid > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static string ResolveMono(string overridePath)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;

            var exe = Application.platform == RuntimePlatform.WindowsEditor ? "mono.exe" : "mono";
            var contents = EditorApplication.applicationContentsPath ?? string.Empty;
            var candidates = new[]
            {
                Path.Combine(contents, "MonoBleedingEdge", "bin", exe),
                Path.Combine(contents, "Resources", "Scripting", "MonoBleedingEdge", "bin", exe),
                Path.Combine(contents, "Data", "MonoBleedingEdge", "bin", exe)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            try
            {
                if (Directory.Exists(contents))
                {
                    foreach (var dir in Directory.GetDirectories(contents, "MonoBleedingEdge", SearchOption.AllDirectories))
                    {
                        var candidate = Path.Combine(dir, "bin", exe);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string EnsureBrokerExe(MCPBrokerRuntimePaths paths, string mono)
        {
            try
            {
                if (string.IsNullOrEmpty(paths.SourcePath) || !File.Exists(paths.SourcePath))
                {
                    LastError = "Broker source file is missing from the package.";
                    return null;
                }

                Directory.CreateDirectory(paths.CacheDirectory);
                var cacheSource = Path.Combine(paths.CacheDirectory, "keepalive-broker.cs");
                var cacheExe = Path.Combine(paths.CacheDirectory, "keepalive-broker.exe");

                if (File.Exists(cacheExe) &&
                    File.GetLastWriteTimeUtc(cacheExe) >= File.GetLastWriteTimeUtc(paths.SourcePath))
                {
                    return cacheExe;
                }

                File.Copy(paths.SourcePath, cacheSource, true);

                var compiler = ResolveCompiler(mono);
                if (string.IsNullOrEmpty(compiler))
                {
                    LastError = "Unity-bundled C# compiler was not found.";
                    return null;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = mono,
                    Arguments = Quote(compiler) + " -nologo -target:exe -out:" + Quote(cacheExe) + " " + Quote(cacheSource),
                    WorkingDirectory = paths.CacheDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        LastError = "Failed to start Unity-bundled C# compiler.";
                        return null;
                    }

                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(20000))
                    {
                        try { process.Kill(); } catch { }
                        LastError = "Broker compile timed out.";
                        return null;
                    }

                    if (process.ExitCode != 0 || !File.Exists(cacheExe))
                    {
                        LastError = "Broker compile failed: " + (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
                        return null;
                    }
                }

                return cacheExe;
            }
            catch (Exception ex)
            {
                LastError = "Broker compile failed: " + ex.Message;
                return null;
            }
        }

        private static string ResolveCompiler(string mono)
        {
            var monoBin = Path.GetDirectoryName(mono);
            if (string.IsNullOrEmpty(monoBin))
                return null;

            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(monoBin, "..", "lib", "mono", "4.5", "mcs.exe")),
                Path.GetFullPath(Path.Combine(monoBin, "..", "lib", "mono", "4.5", "csc.exe")),
                Path.GetFullPath(Path.Combine(monoBin, "..", "lib", "mono", "msbuild", "Current", "bin", "Roslyn", "csc.exe"))
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static string ResolveBrokerSourcePath(string projectRoot)
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPBrokerProcessManager).Assembly);
                if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
                {
                    var path = Path.Combine(package.resolvedPath, "Editor", "MCP", "Server", "Broker", "keepalive-broker.cs.txt");
                    if (File.Exists(path))
                        return path;
                }
            }
            catch
            {
            }

            var candidates = new[]
            {
                Path.Combine(Application.dataPath, "unity-mcp", "Editor", "MCP", "Server", "Broker", "keepalive-broker.cs.txt"),
                Path.Combine(projectRoot, "Packages", "com.gamebooom.unity.mcp", "Editor", "MCP", "Server", "Broker", "keepalive-broker.cs.txt")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static bool SendShutdown(int port, string token)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + MCPBrokerProtocol.ShutdownPath);
                request.Method = "POST";
                request.Timeout = 1000;
                request.ReadWriteTimeout = 1000;
                request.ContentLength = 0;
                request.KeepAlive = false;
                request.Headers[MCPBrokerProtocol.TokenHeader] = token;
                using (var response = (HttpWebResponse)request.GetResponse())
                    return response.StatusCode == HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteState(string pidFilePath, BrokerProcessState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pidFilePath));
            File.WriteAllText(pidFilePath,
                state.Pid + "\n" +
                state.Port + "\n" +
                state.Token + "\n" +
                state.Protocol + "\n");
        }

        private static bool TryReadState(string pidFilePath, out BrokerProcessState state)
        {
            state = null;
            try
            {
                if (!File.Exists(pidFilePath))
                    return false;

                var lines = File.ReadAllLines(pidFilePath);
                if (lines.Length < 4)
                    return false;

                if (!int.TryParse(lines[0], out var pid) ||
                    !int.TryParse(lines[1], out var port) ||
                    !int.TryParse(lines[3], out var protocol))
                {
                    return false;
                }

                state = new BrokerProcessState
                {
                    Pid = pid,
                    Port = port,
                    Token = lines[2],
                    Protocol = protocol
                };

                return state.Protocol == MCPBrokerProtocol.Version &&
                       state.Pid > 0 &&
                       state.Port > 0 &&
                       !string.IsNullOrEmpty(state.Token);
            }
            catch
            {
                return false;
            }
        }

        private static void DeletePidFile(string pidFilePath)
        {
            try
            {
                if (File.Exists(pidFilePath))
                    File.Delete(pidFilePath);
            }
            catch
            {
            }
        }

        private static bool IsTcpPortOpen(int port)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var result = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(250))
                        return false;

                    client.EndConnect(result);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void WaitForExit(int pid, int timeoutMs)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    process.WaitForExit(timeoutMs);
            }
            catch
            {
            }
        }

        private static void KillVerifiedProcess(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            return dict.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
        }

        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var value) || value == null)
                return 0;

            if (value is int intValue)
                return intValue;

            if (value is long longValue)
                return (int)longValue;

            if (value is double doubleValue)
                return (int)doubleValue;

            if (int.TryParse(value.ToString(), out var parsed))
                return parsed;

            return 0;
        }

        internal sealed class BrokerHealth
        {
            public string Name;
            public int Pid;
            public int Protocol;
            public int Pending;
        }

        internal sealed class BrokerConnectionInfo
        {
            public int Pid;
            public int Port;
            public string Token;
        }

        internal sealed class MCPBrokerRuntimePaths
        {
            public MCPBrokerRuntimePaths(string projectRoot, string pidFilePath, string cacheDirectory, string sourcePath)
            {
                ProjectRoot = projectRoot;
                PidFilePath = pidFilePath;
                CacheDirectory = cacheDirectory;
                SourcePath = sourcePath;
            }

            public string ProjectRoot { get; }
            public string PidFilePath { get; }
            public string CacheDirectory { get; }
            public string SourcePath { get; }
        }

        private sealed class BrokerProcessState
        {
            public int Pid;
            public int Port;
            public string Token;
            public int Protocol;
        }
    }
}
