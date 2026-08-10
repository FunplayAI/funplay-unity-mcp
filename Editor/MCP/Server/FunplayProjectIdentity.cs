// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Funplay.Editor.MCP.Server
{
    internal static class FunplayProjectIdentity
    {
        public const string IdentityVersion = "project-path-sha256-v1";

        /// <summary>
        /// Range the per-project default port is derived into. Chosen to sit above the crowded
        /// 8000-9000 developer-tool band and below the ephemeral ranges the OS allocates from
        /// (macOS 49152-65535, Linux 32768-60999), so a derived port never collides with an
        /// outbound socket the machine assigns on its own.
        /// </summary>
        public const int DerivedPortRangeStart = 20000;

        public const int DerivedPortRangeSize = 10000;

        // Single-entry cache: the identity is asked for on every settings change and window
        // rebuild, and a project's path never changes within a session -- rehashing it each time
        // was pure waste. One immutable tuple so concurrent readers can never see a torn pair.
        private static Tuple<string, string> _cache;

        public static string FromProjectPath(string projectPath)
        {
            var normalized = NormalizeProjectPath(projectPath);
            if (string.IsNullOrEmpty(normalized))
                return string.Empty;

            var cached = _cache;
            if (cached != null && string.Equals(cached.Item1, normalized, StringComparison.Ordinal))
                return cached.Item2;

            string identity;
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                    builder.Append(b.ToString("x2"));
                identity = builder.ToString();
            }

            _cache = Tuple.Create(normalized, identity);
            return identity;
        }

        /// <summary>
        /// Derives this project's default MCP port from its identity hash, so two editors opened on
        /// different projects do not both try to bind one shared default port. Pure function of the
        /// project path: the same project always resolves to the same port without persisting
        /// anything, which is what keeps a client config entry valid across restarts.
        /// </summary>
        public static bool TryDerivePort(string identity, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(identity) || identity.Length < 8)
                return false;

            uint value;
            if (!uint.TryParse(
                    identity.Substring(0, 8),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return false;
            }

            port = DerivedPortRangeStart + (int)(value % DerivedPortRangeSize);
            return true;
        }

        public static bool TryDerivePortFromProjectPath(string projectPath, out int port)
        {
            return TryDerivePort(FromProjectPath(projectPath), out port);
        }

        private static string NormalizeProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return string.Empty;

            var fullPath = Path.GetFullPath(projectPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');

            // Windows path lookup is case-insensitive, so spelling the same project with different
            // casing must not move its derived port. Unix paths are case-sensitive by default (and
            // macOS can use a case-sensitive volume), so folding there would collapse distinct
            // projects such as /work/Game and /work/game into one identity.
            return Path.DirectorySeparatorChar == '\\'
                ? fullPath.ToLowerInvariant()
                : fullPath;
        }
    }
}
