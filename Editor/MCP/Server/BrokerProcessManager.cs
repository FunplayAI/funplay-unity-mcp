// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Manages the lifecycle of the out-of-process keepalive broker (Broker~/keepalive-broker.exe),
    /// run under the Unity-bundled Mono runtime — so it needs no Node.js or any external install.
    ///
    /// The broker is a separate OS process, so it survives Unity domain reloads (that is the whole
    /// point). This manager launches it when broker mode is enabled and kills it when switching back
    /// to direct mode or when the editor quits. The managed Process handle is lost across a domain
    /// reload, so the running broker is tracked via a PID file under Library/ and reacquired by PID.
    ///
    /// Mono location is resolved dynamically because its path under the editor install differs by
    /// Unity version (e.g. Unity ≤2022: Contents/MonoBleedingEdge; Unity 6: Contents/Resources/
    /// Scripting/MonoBleedingEdge). If the prebuilt .exe is missing it is compiled from the shipped
    /// .cs with the bundled compiler (cached under Library/).
    /// </summary>
    [InitializeOnLoad]
    internal static class BrokerProcessManager
    {
        private const string ReqIdMarker = "keepalive-broker";

        private static readonly string ProjectRoot;
        private static readonly string EditorContentsPath;
        private static readonly string PidFilePath;
        private static readonly string PackageBrokerExe;   // shipped prebuilt exe (Broker~/)
        private static readonly string PackageBrokerSrc;   // shipped source   (Broker~/)
        private static readonly object Gate = new object();

        public static string LastError { get; private set; }

        static BrokerProcessManager()
        {
            ProjectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            EditorContentsPath = EditorApplication.applicationContentsPath;
            PidFilePath = Path.Combine(ProjectRoot, "Library", "funplay-broker.pid");

            var brokerDir = ResolveBrokerDir();
            PackageBrokerExe = brokerDir != null ? Path.Combine(brokerDir, "keepalive-broker.exe") : null;
            PackageBrokerSrc = brokerDir != null ? Path.Combine(brokerDir, "keepalive-broker.cs") : null;

            EditorApplication.quitting += Stop;
        }

        public static bool IsRunning(out int pid, out int port)
        {
            return TryReadPidFile(out pid, out port) && IsBrokerProcessAlive(pid);
        }

        /// <summary>
        /// Ensure the broker is running on <paramref name="port"/>. Returns false (with
        /// <see cref="LastError"/> set) if Mono / the broker assembly could not be found or launched,
        /// so the caller can fall back to the in-process transport.
        /// </summary>
        public static bool EnsureRunning(int port, string monoPathOverride)
        {
            lock (Gate)
            {
                LastError = null;

                if (TryReadPidFile(out var existingPid, out var existingPort) && IsBrokerProcessAlive(existingPid))
                {
                    if (existingPort == port) return true;
                    KillPid(existingPid);
                    DeletePidFile();
                }

                // Race guard: a broker may already be listening on the port even when the pid file
                // is missing/stale — e.g. a sibling start during cold-boot that bound the port before
                // its pid file landed. Don't launch a duplicate; adopt the existing listener.
                if (PortIsOpen(port))
                    return true;

                var mono = ResolveMono(monoPathOverride);
                if (string.IsNullOrEmpty(mono))
                {
                    LastError = "Bundled Mono runtime not found under the editor install.";
                    Debug.LogWarning($"[Funplay MCP Server] {LastError}");
                    return false;
                }

                var exe = EnsureBrokerExe(mono);
                if (string.IsNullOrEmpty(exe))
                {
                    LastError = LastError ?? "Broker assembly not found and could not be compiled.";
                    Debug.LogWarning($"[Funplay MCP Server] {LastError}");
                    return false;
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = mono,
                        Arguments = $"\"{exe}\" {port}",
                        WorkingDirectory = Path.GetDirectoryName(exe),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        LastError = "Failed to start broker process.";
                        return false;
                    }
                    WritePidFile(proc.Id, port);
                    Debug.Log($"[Funplay MCP Server] Broker started (pid={proc.Id}, port={port}) via {mono}.");
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = $"Failed to launch broker: {ex.Message}";
                    Debug.LogError($"[Funplay MCP Server] {LastError}");
                    return false;
                }
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                if (TryReadPidFile(out var pid, out _))
                {
                    KillPid(pid);
                    DeletePidFile();
                }
            }
        }

        // ---- mono / exe resolution ----

        private static bool IsWindows => Application.platform == RuntimePlatform.WindowsEditor;

        private static string ResolveMono(string overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                return overridePath;

            var exe = IsWindows ? "mono.exe" : "mono";
            var contents = EditorContentsPath ?? string.Empty;

            // Known relative layouts (Unity version dependent).
            var candidates = new[]
            {
                Path.Combine(contents, "MonoBleedingEdge", "bin", exe),
                Path.Combine(contents, "Resources", "Scripting", "MonoBleedingEdge", "bin", exe),
                Path.Combine(contents, "Data", "MonoBleedingEdge", "bin", exe), // Windows applicationContentsPath safety
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            // Fallback: find any MonoBleedingEdge under the editor install and use its bin/mono.
            try
            {
                if (Directory.Exists(contents))
                {
                    foreach (var dir in Directory.GetDirectories(contents, "MonoBleedingEdge", SearchOption.AllDirectories))
                    {
                        var p = Path.Combine(dir, "bin", exe);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        /// <summary>Return the prebuilt broker exe if shipped, otherwise compile it from the shipped
        /// source with the bundled C# compiler (cached under Library/). Returns null on failure.</summary>
        private static string EnsureBrokerExe(string mono)
        {
            if (!string.IsNullOrEmpty(PackageBrokerExe) && File.Exists(PackageBrokerExe))
                return PackageBrokerExe;

            if (string.IsNullOrEmpty(PackageBrokerSrc) || !File.Exists(PackageBrokerSrc))
            {
                LastError = "Broker assembly and source both missing.";
                return null;
            }

            // Compile <src> -> Library/funplay-broker/keepalive-broker.exe using the bundled compiler.
            try
            {
                var cacheDir = Path.Combine(ProjectRoot, "Library", "funplay-broker");
                Directory.CreateDirectory(cacheDir);
                var cacheExe = Path.Combine(cacheDir, "keepalive-broker.exe");
                if (File.Exists(cacheExe) && File.GetLastWriteTimeUtc(cacheExe) >= File.GetLastWriteTimeUtc(PackageBrokerSrc))
                    return cacheExe;

                var monoBin = Path.GetDirectoryName(mono);
                var mcsExe = Path.GetFullPath(Path.Combine(monoBin, "..", "lib", "mono", "4.5", "mcs.exe"));
                if (!File.Exists(mcsExe))
                {
                    LastError = "Broker exe missing and bundled C# compiler (mcs.exe) not found to build it.";
                    return null;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = mono,
                    Arguments = $"\"{mcsExe}\" -out:\"{cacheExe}\" \"{PackageBrokerSrc}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    if (p.ExitCode != 0 || !File.Exists(cacheExe))
                    {
                        LastError = "Broker compile failed: " + err;
                        return null;
                    }
                }
                Debug.Log($"[Funplay MCP Server] Compiled broker assembly to {cacheExe}.");
                return cacheExe;
            }
            catch (Exception ex)
            {
                LastError = $"Broker compile error: {ex.Message}";
                return null;
            }
        }

        private static string ResolveBrokerDir()
        {
            try
            {
                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BrokerProcessManager).Assembly);
                if (pkg != null && !string.IsNullOrEmpty(pkg.resolvedPath))
                {
                    var dir = Path.Combine(pkg.resolvedPath, "Broker~");
                    if (Directory.Exists(dir)) return dir;
                }
            }
            catch { /* fall through */ }

            var embedded = Path.GetFullPath(Path.Combine(
                ProjectRoot ?? Directory.GetCurrentDirectory(),
                "Packages", "com.gamebooom.unity.mcp", "Broker~"));
            return Directory.Exists(embedded) ? embedded : null;
        }

        // ---- pid file ----

        private static void WritePidFile(int pid, int port)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath));
                File.WriteAllText(PidFilePath, $"{pid}:{port}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Funplay MCP Server] Could not write broker pid file: {ex.Message}");
            }
        }

        private static bool TryReadPidFile(out int pid, out int port)
        {
            pid = 0;
            port = 0;
            try
            {
                if (!File.Exists(PidFilePath)) return false;
                var parts = File.ReadAllText(PidFilePath).Trim().Split(':');
                return parts.Length == 2 && int.TryParse(parts[0], out pid) && int.TryParse(parts[1], out port);
            }
            catch
            {
                return false;
            }
        }

        private static void DeletePidFile()
        {
            try { if (File.Exists(PidFilePath)) File.Delete(PidFilePath); }
            catch { /* ignore */ }
        }

        private static bool PortIsOpen(int port)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var ar = client.BeginConnect(System.Net.IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(300))
                        return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBrokerProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited) return false;
                // Broker runs under mono; guard against PID reuse by an unrelated process.
                var name = (p.ProcessName ?? string.Empty).ToLowerInvariant();
                return name.IndexOf("mono", StringComparison.Ordinal) >= 0
                    || name.IndexOf(ReqIdMarker, StringComparison.Ordinal) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static void KillPid(int pid)
        {
            if (pid <= 0) return;
            try
            {
                var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(2000);
                    Debug.Log($"[Funplay MCP Server] Broker process stopped (pid={pid}).");
                }
            }
            catch { /* already gone */ }
        }
    }
}
