// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GameBooom.Editor.MCP.Client
{
    /// <summary>
    /// Model Context Protocol host for connecting to external MCP servers.
    /// Supports stdio transport.
    /// </summary>
    internal class MCPHost : IDisposable
    {
        private Process _process;
        private StreamWriter _writer;
        private StreamReader _reader;
        private readonly string _command;
        private readonly string _arguments;
        private readonly Dictionary<string, string> _environmentVariables;
        private bool _isConnected;
        private int _requestId;
        private volatile string _stderrOutput = "";

        public bool IsConnected => _isConnected;
        public event Action<string> OnLog;

        public MCPHost(string command, string arguments = "",
            Dictionary<string, string> environmentVariables = null)
        {
            _command = command;
            _arguments = arguments;
            _environmentVariables = environmentVariables;
        }

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                var resolvedCommand = ResolveCommand(_command);
                Log($"Resolved command: '{_command}' -> '{resolvedCommand}'");

                var psi = new ProcessStartInfo
                {
                    FileName = resolvedCommand,
                    Arguments = _arguments,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Ensure PATH includes common Node.js locations
                // (macOS GUI apps have limited PATH that excludes Homebrew, nvm, etc.)
                var currentPath = psi.EnvironmentVariables["PATH"] ?? "";
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var extraPaths = new[]
                {
                    "/usr/local/bin",
                    "/opt/homebrew/bin",
                    "/opt/homebrew/sbin",
                    Path.Combine(home, ".nvm/current/bin"),
                    Path.Combine(home, ".volta/bin"),
                    Path.Combine(home, ".fnm/aliases/default/bin"),
                    "/usr/local/share/npm/bin"
                };
                foreach (var p in extraPaths)
                {
                    if (!currentPath.Contains(p))
                        currentPath = p + ":" + currentPath;
                }
                psi.EnvironmentVariables["PATH"] = currentPath;

                if (_environmentVariables != null)
                {
                    foreach (var kvp in _environmentVariables)
                        psi.EnvironmentVariables[kvp.Key] = kvp.Value;
                }

                _process = Process.Start(psi);
                if (_process == null)
                {
                    Log("Failed to start MCP server process");
                    return false;
                }

                _writer = _process.StandardInput;
                _reader = _process.StandardOutput;

                var stderrReader = _process.StandardError;
                _ = Task.Run(() => ReadStderrLoop(stderrReader));

                Log("Waiting for MCP server to initialize...");
                await Task.Delay(3000, ct);

                if (IsProcessExited())
                {
                    Log($"MCP process exited during startup. stderr: {_stderrOutput}");
                    return false;
                }

                var initRequest = BuildJsonRpc("initialize", new Dictionary<string, object>
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new Dictionary<string, object>(),
                    ["clientInfo"] = new Dictionary<string, object>
                    {
                        ["name"] = "GameBooom",
                        ["version"] = "1.0.0"
                    }
                });

                await SendRequestAsync(initRequest, ct);
                var response = await ReadResponseAsync(ct);

                if (response != null)
                {
                    _isConnected = true;
                    var notification = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";
                    await SendRequestAsync(notification, ct);
                    Log($"Connected to MCP server: {_command}");
                    return true;
                }

                Log($"No response from MCP server. Process alive: {!IsProcessExited()}. stderr: {_stderrOutput}");
            }
            catch (Exception ex)
            {
                Log($"MCP connection error: {ex.Message}");
            }

            return false;
        }

        private void ReadStderrLoop(StreamReader stderrReader)
        {
            try
            {
                var sb = new StringBuilder();
                string line;
                while ((line = stderrReader.ReadLine()) != null)
                {
                    sb.AppendLine(line);
                    _stderrOutput = sb.ToString();
                    UnityEngine.Debug.Log($"[GameBooom MCP] stderr: {line}");
                }
            }
            catch { /* Process ended or stream closed */ }
        }

        private bool IsProcessExited()
        {
            try { return _process == null || _process.HasExited; }
            catch { return true; }
        }

        public async Task<string> ListToolsAsync(CancellationToken ct = default)
        {
            if (!_isConnected) return null;
            var request = BuildJsonRpc("tools/list", new Dictionary<string, object>());
            await SendRequestAsync(request, ct);
            return await ReadResponseAsync(ct);
        }

        public async Task<string> CallToolAsync(string toolName,
            Dictionary<string, object> arguments, CancellationToken ct = default)
        {
            if (!_isConnected) return null;
            var request = BuildJsonRpc("tools/call", new Dictionary<string, object>
            {
                ["name"] = toolName,
                ["arguments"] = arguments
            });
            await SendRequestAsync(request, ct);
            return await ReadResponseAsync(ct);
        }

        private string BuildJsonRpc(string method, Dictionary<string, object> parameters)
        {
            var id = Interlocked.Increment(ref _requestId);
            var sb = new StringBuilder();
            sb.Append("{\"jsonrpc\":\"2.0\"");
            sb.Append($",\"id\":{id}");
            sb.Append($",\"method\":\"{method}\"");
            sb.Append(",\"params\":");
            sb.Append(Api.JsonParse.Serialize(parameters));
            sb.Append("}");
            return sb.ToString();
        }

        private async Task SendRequestAsync(string json, CancellationToken ct)
        {
            if (_writer == null || IsProcessExited())
                throw new InvalidOperationException("MCP process is not running");
            var header = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n";
            await _writer.WriteAsync(header);
            await _writer.WriteAsync(json);
            await _writer.FlushAsync();
        }

        private async Task<string> ReadResponseAsync(CancellationToken ct)
        {
            if (_reader == null) return null;
            try
            {
                var headerLine = await _reader.ReadLineAsync();
                if (headerLine == null) return null;

                int contentLength = 0;
                if (headerLine.StartsWith("Content-Length:"))
                    int.TryParse(headerLine.Substring("Content-Length:".Length).Trim(), out contentLength);

                await _reader.ReadLineAsync(); // empty line

                if (contentLength <= 0) return null;

                var buffer = new char[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    var count = await _reader.ReadAsync(buffer, read, contentLength - read);
                    if (count == 0) break;
                    read += count;
                }
                return new string(buffer, 0, read);
            }
            catch (Exception ex)
            {
                Log($"MCP read error: {ex.Message}");
                return null;
            }
        }

        private static string ResolveCommand(string command)
        {
            if (command.Contains("/") || command.Contains("\\"))
                return command;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var searchPaths = new[]
            {
                "/usr/local/bin", "/opt/homebrew/bin",
                Path.Combine(home, ".nvm/current/bin"),
                Path.Combine(home, ".volta/bin"),
                Path.Combine(home, ".fnm/aliases/default/bin"),
                "/usr/local/share/npm/bin",
                "/usr/bin", "/bin"
            };

            // Check nvm versioned directories
            var nvmDir = Path.Combine(home, ".nvm/versions/node");
            if (Directory.Exists(nvmDir))
            {
                try
                {
                    var dirs = Directory.GetDirectories(nvmDir);
                    Array.Sort(dirs);
                    Array.Reverse(dirs);
                    foreach (var dir in dirs)
                    {
                        var candidate = Path.Combine(dir, "bin", command);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch { }
            }

            foreach (var dir in searchPaths)
            {
                var candidate = Path.Combine(dir, command);
                if (File.Exists(candidate)) return candidate;
            }

            return command;
        }

        private void Log(string message)
        {
            UnityEngine.Debug.Log($"[GameBooom MCP] {message}");
            OnLog?.Invoke(message);
        }

        public void Dispose()
        {
            _isConnected = false;
            try
            {
                _writer?.Close();
                _reader?.Close();
                if (_process != null && !IsProcessExited())
                    _process.Kill();
                _process?.Dispose();
            }
            catch { }
        }
    }
}
