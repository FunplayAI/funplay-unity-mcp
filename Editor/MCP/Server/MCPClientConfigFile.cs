// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Funplay.Editor.MCP.Server
{
    internal static class MCPClientConfigFile
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static string Read(string path)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MCPConfigJson.MaximumLength)
                throw new InvalidOperationException("Client configuration exceeds 8 MiB; file was left unchanged.");
            return Utf8.GetString(File.ReadAllBytes(path)); // Retain a UTF-8 BOM as a source character.
        }

        // Best-effort compare-and-replace, not an OS-level cross-process transaction. A detected
        // concurrent edit is always preserved. Never replace a symlink with an ordinary file.
        internal static void Write(string path, string original, string updated, Action beforeCommit = null)
        {
            path = Path.GetFullPath(path);
            CheckWritableTarget(path);
            var bytes = Utf8.GetBytes(updated);
            if (bytes.Length > MCPConfigJson.MaximumLength)
                throw new InvalidOperationException("Client configuration exceeds 8 MiB; file was left unchanged.");
            if (!string.Equals(Read(path), original, StringComparison.Ordinal)) Changed();
            if (original == updated) return;
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + ".funplay-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Configs may carry credentials. Protect the empty temporary before writing.
                    // Windows File.Replace preserves the destination ACL; Unix configs are private.
                    if (Application.platform != RuntimePlatform.WindowsEditor && chmod(temporary, 384) != 0)
                        throw new IOException("Could not protect the client configuration temporary file.");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                beforeCommit?.Invoke();
                CheckWritableTarget(path);
                if (!string.Equals(Read(path), original, StringComparison.Ordinal)) Changed();
                if (original == null) File.Move(temporary, path);
                else File.Replace(temporary, path, null);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void CheckWritableTarget(string path)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symlinked client configuration needs manual review; file was left unchanged.");
            if ((attributes & FileAttributes.ReadOnly) != 0)
                throw new InvalidOperationException("Client configuration is read-only; file was left unchanged.");
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

        private static void Changed() => throw new InvalidOperationException(
            "Client configuration changed while editing; file was left unchanged. Review it before configuring again.");
    }
}
