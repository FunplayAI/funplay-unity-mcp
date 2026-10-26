// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Funplay.Editor.Tools.Helpers
{
    // No shell, installation, PATH mutation, network protocol or caller-supplied arguments.
    // Starting an installed external executable is explicitly opted into by the mutation tool.
    internal sealed class InstalledVideoDecoder : IDisposable
    {
        private Process process;
        private readonly StringBuilder diagnostics = new StringBuilder();
        private Task<string> stderr;
        internal string Version { get; private set; }
        internal string Executable { get; }
        internal string ExecutableHash { get; }
        internal InstalledVideoDecoder(string path)
        {
            Executable = Resolve(path) ?? throw new FileNotFoundException("Installed ffmpeg not found; nothing is installed automatically.");
            ExecutableHash = SerializedReferenceTransaction.HashFile(Executable);
        }
        internal static string Resolve(string explicitPath = null)
        {
            if (explicitPath != null)
            {
                if (!Path.IsPathRooted(explicitPath) || !new[] { "ffmpeg", "ffmpeg.exe" }.Contains(Path.GetFileName(explicitPath))) return null;
                return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
            }
            var dirs = new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            foreach (var dir in dirs.Where(d => !string.IsNullOrEmpty(d) && Path.IsPathRooted(d)).Distinct())
                foreach (var name in new[] { "ffmpeg", "ffmpeg.exe" }) { var file = Path.Combine(dir, name); if (File.Exists(file)) return file; }
            return null;
        }
        internal static string Quote(string arg)
        {
            if (arg == null || arg.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new ArgumentException("Invalid process argument.");
            // ProcessStartInfo's platform argv parser, not shell quoting.
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { b.Append('\\', slashes * 2 + 1).Append(c); slashes = 0; continue; }
                b.Append('\\', slashes).Append(c); slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }
        internal void StartFrame(string video, double time, string output)
        {
            if (process != null) throw new InvalidOperationException("Decoder already active.");
            if (SerializedReferenceTransaction.HashFile(Executable) != ExecutableHash) throw new IOException("Decoder executable changed.");
            diagnostics.Clear(); Directory.CreateDirectory(Path.GetDirectoryName(output));
            if (File.Exists(output)) throw new IOException("Refusing to overwrite an existing frame.");
            var args = new[] { "-nostdin", "-loglevel", "info", "-threads", "1", "-protocol_whitelist", "file,pipe", "-f", "mov", "-noautorotate", "-i", video,
                "-an", "-sn", "-dn", "-vf", "select=gte(t\\," + time.ToString("R", CultureInfo.InvariantCulture) + "),showinfo", "-frames:v", "1", "-vsync", "0", "-threads", "1", "-n", output };
            process = Process.Start(new ProcessStartInfo { FileName = Executable, Arguments = string.Join(" ", args.Select(Quote)),
                UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true });
            stderr = Task.Run(async () => {
                var buffer = new char[2048]; int count;
                while ((count = await process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    lock (diagnostics) { if (diagnostics.Length < 32768) diagnostics.Append(buffer, 0, Math.Min(count, 32768 - diagnostics.Length)); }
                lock (diagnostics) return diagnostics.ToString();
            });
        }
        internal bool TryComplete(out double timestamp)
        {
            timestamp = 0;
            if (process == null || !process.HasExited || !stderr.IsCompleted) return false;
            string log = stderr.GetAwaiter().GetResult(); int exit = process.ExitCode; process.Dispose(); process = null; stderr = null;
            Version = log.Split('\n').FirstOrDefault(line => line.StartsWith("ffmpeg version", StringComparison.Ordinal));
            var match = Regex.Match(log, @"\bn:\s*0\s+pts:.*?\bpts_time:\s*(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)");
            if (exit != 0 || !match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out timestamp))
                throw new InvalidDataException("INSTALLED_DECODER_FAILED: exit=" + exit + "; " + log.Substring(Math.Max(0, log.Length - 1500)));
            return true;
        }
        public void Dispose()
        {
            var owned = process; if (owned == null) return;
            // Keep the reference alive until stderr drainage finishes; it reads only this process.
            if (!owned.HasExited) owned.Kill();
            if (owned.WaitForExit(1000) && (stderr == null || stderr.Wait(1000))) { owned.Dispose(); process = null; }
            else throw new IOException("Decoder cleanup is still pending.");
        }
    }
}
