// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Text;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Builds the name this editor is written under in a client's MCP config. Every project used to
    /// write the same <see cref="LegacyKey"/>, so configuring a second project silently replaced the
    /// first project's entry. Naming the entry after the project keeps the entries side by side, and
    /// in clients that namespace tools by server (Claude Code exposes <c>mcp__&lt;key&gt;__&lt;tool&gt;</c>)
    /// it also makes it structurally impossible to send a call to the wrong editor.
    /// </summary>
    internal static class FunplayMCPServerKey
    {
        public const string LegacyKey = "funplay";
        public const string Prefix = "funplay-";

        /// <summary>
        /// Key length cap. Clients namespace tools as <c>mcp__&lt;key&gt;__&lt;tool&gt;</c> and cap tool
        /// names at 64 characters; with the longest tool name here at 32 characters that leaves
        /// 64 - "mcp__" - "__" - 32 = 25 for the key, so a long name is truncated rather than pushing
        /// a client's tool names past the limit.
        /// </summary>
        public const int MaxKeyLength = 25;

        public const int ProjectHashLength = 6;

        /// <summary>
        /// Builds the entry name from <paramref name="projectFolderName"/> -- the project directory
        /// name, not <c>Application.productName</c>. The product name is frequently left at Unity's
        /// default or set to non-ASCII text, and tool names are restricted to
        /// <c>[a-zA-Z0-9_-]</c>: a CJK product name produced an entry name whose tools a client
        /// rejects outright. A directory name always exists and is what developers call the project.
        /// </summary>
        public static string Build(string projectFolderName, string projectIdentity, bool includeProjectHash)
        {
            var hash = includeProjectHash ? ProjectHash(projectIdentity) : string.Empty;
            var reserved = Prefix.Length + (hash.Length > 0 ? hash.Length + 1 : 0);
            var slug = Slug(projectFolderName, MaxKeyLength - reserved);

            if (slug.Length == 0)
            {
                // Nothing survived ASCII filtering (a fully non-ASCII directory name). The identity
                // hash is a deterministic, always-valid stand-in; when it is unavailable too, the
                // colliding names are separated by the automatic project-hash suffix instead.
                slug = ProjectHash(projectIdentity);
                if (slug.Length == 0)
                    slug = FallbackSlug;
            }

            var key = Prefix + slug;
            return hash.Length > 0 ? key + "-" + hash : key;
        }

        private const string FallbackSlug = "unity";

        /// <summary>
        /// True when <paramref name="key"/> is one this plugin would have written -- the legacy shared
        /// key or any project-scoped one. Used to clean up entries this plugin owns without touching a
        /// hand-written entry that merely mentions Funplay.
        /// </summary>
        public static bool IsFunplayKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;

            return string.Equals(key, LegacyKey, StringComparison.Ordinal) ||
                   key.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Lowercases, keeps only ASCII letters and digits, replaces every run of anything else with a
        /// single '-', and clamps to <paramref name="maxLength"/>. Filtering is deliberately ASCII-only
        /// rather than <c>char.IsLetterOrDigit</c>, which accepts CJK and accented letters: those pass
        /// the C# check but not the <c>[a-zA-Z0-9_-]</c> charset a client requires of a tool name, so
        /// they used to produce entry names whose tools were rejected. Returns an empty string when
        /// nothing survives, leaving the fallback choice to <see cref="Build"/>. There is deliberately
        /// no single-argument overload: the cap depends on whether a project hash is appended, and a
        /// caller reaching for a "default" cap would silently produce over-budget keys.
        /// </summary>
        public static string Slug(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
                return string.Empty;

            var builder = new StringBuilder(value.Length);
            var pendingSeparator = false;
            foreach (var character in value)
            {
                if (IsAsciiLetterOrDigit(character))
                {
                    // A separator plus the letter can be two characters, so check the budget before
                    // appending rather than after -- appending first overshot maxLength by one.
                    var needed = pendingSeparator && builder.Length > 0 ? 2 : 1;
                    if (builder.Length + needed > maxLength)
                        break;

                    if (needed == 2)
                        builder.Append('-');

                    pendingSeparator = false;
                    builder.Append(char.ToLowerInvariant(character));
                }
                else
                {
                    // Collapse any run of separators into a single '-', and never lead with one.
                    pendingSeparator = true;
                }
            }

            return builder.ToString().Trim('-');
        }

        private static bool IsAsciiLetterOrDigit(char character)
        {
            return (character >= 'a' && character <= 'z') ||
                   (character >= 'A' && character <= 'Z') ||
                   (character >= '0' && character <= '9');
        }

        private static string ProjectHash(string projectIdentity)
        {
            if (string.IsNullOrEmpty(projectIdentity))
                return string.Empty;

            return projectIdentity.Length <= ProjectHashLength
                ? projectIdentity
                : projectIdentity.Substring(0, ProjectHashLength);
        }
    }
}
