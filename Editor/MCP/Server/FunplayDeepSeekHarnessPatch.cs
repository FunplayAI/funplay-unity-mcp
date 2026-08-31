// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Funplay.Editor.MCP.Server
{
    /// <summary>
    /// Writes this project's MCP entry into DeepSeek Harness (DSH) profile patch files
    /// (<c>~/.dsh/profiles/&lt;profile&gt;/cordis.patch.yml</c>). DSH composes every session from
    /// these loader-patch layers, and an MCP connection is just one more plugin entry:
    /// <c>@deepseek-ai/dsh-mcp-client</c> with a <c>streamable-http</c> transport pointed at the
    /// editor's URL. Tool names surface to the model as <c>mcp__&lt;serverName&gt;__&lt;tool&gt;</c>,
    /// the same server-qualified shape the other clients here use.
    ///
    /// A patch file is a top-level YAML array that may already carry unrelated entries (other
    /// plugins' inserts, <c>!!js</c> expressions). Rather than parse and re-serialize someone's hand
    /// config, the entry lives inside a clearly-delimited managed block that is matched by marker
    /// comments and replaced wholesale on reconfigure -- everything outside the block is preserved
    /// byte-for-byte, and deleting the block by hand is always a clean uninstall.
    /// </summary>
    internal static class FunplayDeepSeekHarnessPatch
    {
        /// <summary>The plugin DSH loads to bridge one external MCP server.</summary>
        public const string ClientPluginName = "'@deepseek-ai/dsh-mcp-client'";

        /// <summary>
        /// Every DSH profile keeps its composition patches here. Profiles are selected at launch
        /// (<c>--profile</c>), so there is no single active one to target from outside: the entry is
        /// written into each profile that exists, which keeps a developer switching launch modes
        /// (web backend, desktop app) covered by one Configure click.
        /// </summary>
        public const string ProfilesRootFolder = "profiles";
        public const string DshHomeFolder = ".dsh";
        public const string PatchFileName = "cordis.patch.yml";

        /// <summary>
        /// Fallback when ~/.dsh exists but no profile has a patch file yet. "web" is what a plain
        /// <c>dsh web</c> boot uses, so it is the safest thing to seed.
        /// </summary>
        public const string DefaultProfileName = "web";

        public static string GetProfilesRoot(string homePath)
        {
            return Path.Combine(homePath, DshHomeFolder, ProfilesRootFolder);
        }

        /// <summary>
        /// The patch file of every existing profile, sorted for stable display. Only directories that
        /// look like real profiles are considered -- they carry their own <c>package.json</c>, which
        /// cleanly excludes stray non-profile folders that can sit beside them (a shared
        /// <c>node_modules</c> is the one that actually occurs).
        ///
        /// The path is reported whether or not the file exists yet: a profile whose patch file was
        /// never created is still a profile <c>--profile</c> can select, and skipping it would leave
        /// that launch mode without the MCP entry while Configure reported success. Callers read
        /// missing files as empty content and create them on write.
        /// </summary>
        public static List<string> GetProfilePatchPaths(string homePath)
        {
            var result = new List<string>();
            var root = GetProfilesRoot(homePath);
            if (!Directory.Exists(root))
                return result;

            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (!File.Exists(Path.Combine(dir, "package.json")))
                        continue;

                    result.Add(Path.Combine(dir, PatchFileName));
                }
            }
            catch (IOException)
            {
                // An unreadable ~/.dsh is reported as "no profiles" rather than thrown: this runs on
                // the panel's status path, which must never break the window (the write path reports
                // its own failures).
                return result;
            }
            catch (UnauthorizedAccessException)
            {
                return result;
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        public static string GetDefaultPatchPath(string homePath)
        {
            return Path.Combine(GetProfilesRoot(homePath), DefaultProfileName, PatchFileName);
        }

        /// <summary>What the panel's path label shows for this target.</summary>
        public static string GetDisplayPath(string homePath)
        {
            var paths = GetProfilePatchPaths(homePath);
            if (paths.Count > 0)
                return string.Join(" | ", paths.ToArray());

            if (Directory.Exists(GetProfilesRoot(homePath)))
                return GetDefaultPatchPath(homePath);

            return "~/.dsh not found (start DeepSeek Harness once, then Configure)";
        }

        public static string BeginMarker(string serverKey)
        {
            return "# >>> funplay-mcp:" + serverKey +
                   " begin (managed by Funplay MCP -- reconfigure from Unity > Funplay > MCP Server)";
        }

        public static string EndMarker(string serverKey)
        {
            return "# <<< funplay-mcp:" + serverKey + " end";
        }

        /// <summary>
        /// The YAML array item DSH loads. Field names/values follow the plugin's documented config
        /// shape exactly: <c>serverName</c> becomes the model-facing tool namespace, and the loopback
        /// URL is the editor's stable-port endpoint (same one every other client gets).
        ///
        /// Lines are terminated with "\n" rather than <c>Environment.NewLine</c>: the block is spliced
        /// into a file whose other lines the user (and DSH itself) wrote with LF, and every offset
        /// this class computes -- the single trailing newline consumed after the end marker above all
        /// -- is written against that one terminator.
        /// </summary>
        public static string BuildManagedBlock(string serverKey, string url)
        {
            var builder = new System.Text.StringBuilder();
            AppendLine(builder, BeginMarker(serverKey));
            AppendLine(builder, "# Funplay Unity MCP endpoint served by this editor; tools appear as mcp__"
                                + serverKey + "__<tool>.");
            AppendLine(builder, "- insert:");
            AppendLine(builder, "    - id: mcp-" + serverKey);
            AppendLine(builder, "      name: " + ClientPluginName);
            AppendLine(builder, "      config:");
            AppendLine(builder, "        serverName: " + serverKey);
            AppendLine(builder, "        transport: streamable-http");
            AppendLine(builder, "        url: " + url);
            AppendLine(builder, EndMarker(serverKey));
            return builder.ToString();
        }

        private static void AppendLine(System.Text.StringBuilder builder, string line)
        {
            builder.Append(line).Append('\n');
        }

        /// <summary>
        /// Inserts this project's block, or replaces the existing one in place. Content outside the
        /// managed span is never touched, so unrelated plugin entries survive every reconfigure.
        /// Applying it twice yields the same text (idempotent).
        /// </summary>
        public static string UpsertManagedBlock(string content, string serverKey, string url)
        {
            var block = BuildManagedBlock(serverKey, url);

            int startIdx;
            int endIdx;
            if (TryFindBlockSpan(content, serverKey, out startIdx, out endIdx))
            {
                // Consume exactly one trailing newline after the end marker so repeated upserts
                // never accumulate blank lines between the block and what follows.
                var afterEnd = endIdx;
                if (afterEnd < content.Length && content[afterEnd] == '\r')
                    afterEnd++;
                if (afterEnd < content.Length && content[afterEnd] == '\n')
                    afterEnd++;

                return content.Substring(0, startIdx) + block + content.Substring(afterEnd);
            }

            content = RemoveEmptySequencePlaceholder(content);

            if (content.Length > 0 && !content.EndsWith("\n"))
                content += "\n";

            return content + block;
        }

        /// <summary>
        /// Drops the "[]" a freshly created profile's patch file ships as its placeholder body.
        /// That is a complete flow sequence serving as the document root, so appending a block
        /// sequence after it is not valid YAML -- DSH's parser stops at the first "-" and fails the
        /// whole patch layer, taking any other entry in the file with it, while the Unity side
        /// reports a successful write. Only an otherwise-empty document is touched: the placeholder
        /// has to be the single piece of content that is not a comment or blank line, so a file the
        /// user has actually filled in is never rewritten. Comments (including the template's own
        /// explanation) are kept exactly where they are.
        /// </summary>
        private static string RemoveEmptySequencePlaceholder(string content)
        {
            if (string.IsNullOrEmpty(content))
                return content;

            var lines = content.Split('\n');
            var placeholderIdx = -1;

            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                    continue;

                // A second piece of real content means the file is in use; leave it alone.
                if (placeholderIdx >= 0)
                    return content;

                if (trimmed != "[]")
                    return content;

                placeholderIdx = i;
            }

            if (placeholderIdx < 0)
                return content;

            var kept = new List<string>(lines.Length - 1);
            for (var i = 0; i < lines.Length; i++)
            {
                if (i != placeholderIdx)
                    kept.Add(lines[i]);
            }

            return string.Join("\n", kept.ToArray());
        }

        /// <summary>
        /// Removes only this project's own managed span (used when a rename retires the previously
        /// recorded key). Anything outside it -- including other projects' blocks and hand-written
        /// entries -- is left alone. Returns the content unchanged when no block is present.
        /// </summary>
        public static string RemoveManagedBlock(string content, string serverKey)
        {
            int startIdx;
            int endIdx;
            if (!TryFindBlockSpan(content, serverKey, out startIdx, out endIdx))
                return content;

            var afterEnd = endIdx;
            if (afterEnd < content.Length && content[afterEnd] == '\r')
                afterEnd++;
            if (afterEnd < content.Length && content[afterEnd] == '\n')
                afterEnd++;

            return content.Substring(0, startIdx) + content.Substring(afterEnd);
        }

        /// <summary>
        /// Reads back the <c>url</c> inside this project's managed block, or false when the block is
        /// absent or carries no readable url. Powers the panel's configured/stale status.
        /// </summary>
        public static bool TryGetManagedBlockUrl(string content, string serverKey, out string url)
        {
            url = null;
            int startIdx;
            int endIdx;
            if (!TryFindBlockSpan(content, serverKey, out startIdx, out endIdx))
                return false;

            var match = Regex.Match(
                content.Substring(startIdx, endIdx - startIdx),
                @"^\s*url:\s*['""]?([^'""\s]+)['""]?\s*$",
                RegexOptions.Multiline);
            if (!match.Success)
                return false;

            url = match.Groups[1].Value;
            return true;
        }

        private static bool TryFindBlockSpan(string content, string serverKey, out int startIdx, out int endIdx)
        {
            var begin = BeginMarker(serverKey);
            var end = EndMarker(serverKey);

            startIdx = content.IndexOf(begin, StringComparison.Ordinal);
            if (startIdx < 0)
            {
                endIdx = -1;
                return false;
            }

            var endMarkerIdx = content.IndexOf(end, startIdx + begin.Length, StringComparison.Ordinal);
            if (endMarkerIdx < 0)
            {
                throw new InvalidOperationException(
                    $"'{BeginMarker(serverKey)}' has no matching end marker; fix or delete the block by hand in the patch file, then Configure again.");
            }

            endIdx = endMarkerIdx + end.Length;
            return true;
        }

        // The trailing [ \t\r]* is what makes this work on a CRLF-terminated file: Multiline "$"
        // matches before the "\n" without consuming the "\r", so a line ending the pattern at
        // [ \t]* would fail every CRLF line that has no comment -- i.e. every ordinary one. A file
        // this plugin wrote uses LF, but the user (and their editor on Windows) may well have
        // rewritten it, and missing a serverName here means missing a real collision.
        private static readonly Regex ServerNameLineRegex =
            new Regex(@"^[ \t]*serverName:[ \t]*['""]?([A-Za-z0-9_-]+)['""]?[ \t]*(?:#[^\r\n]*)?[ \t\r]*$",
                RegexOptions.Multiline);

        /// <summary>
        /// Every <c>serverName</c> in the file that follows this plugin's project-scoped naming
        /// convention -- our own managed block's name and any hand-written or other-project entry,
        /// since DSH rejects duplicate <c>serverName</c> values across live instances and two
        /// projects resolving to one name would otherwise collide silently.
        /// </summary>
        public static HashSet<string> ReadFunplayServerNames(string content)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            // Cheap gate before any parsing, matched to IsFunplayKey's widest accepted form: the
            // legacy bare key. Gating on the project-scoped prefix instead would skip a file whose
            // only funplay entry is the legacy one -- the same gate the JSON/TOML path uses.
            if (string.IsNullOrEmpty(content) ||
                content.IndexOf(FunplayMCPServerKey.LegacyKey, StringComparison.Ordinal) < 0)
            {
                return names;
            }

            foreach (Match match in ServerNameLineRegex.Matches(content))
            {
                var name = match.Groups[1].Value;
                if (FunplayMCPServerKey.IsFunplayKey(name))
                    names.Add(name);
            }

            return names;
        }

        /// <summary>
        /// True when some entry outside our managed block already uses <paramref name="serverKey"/>.
        /// That combination makes DSH fail the later plugin instance at load ("A duplicate
        /// serverName across live instances fails the later plugin instance"), so it is surfaced
        /// loudly instead of being cleaned up automatically -- a hand-written entry is the user's,
        /// and this plugin never deletes what it did not write.
        /// </summary>
        public static bool HasServerNameOutsideManagedBlock(string content, string serverKey)
        {
            if (string.IsNullOrEmpty(content))
                return false;

            int blockStart;
            int blockEnd;
            var hasBlock = TryFindBlockSpan(content, serverKey, out blockStart, out blockEnd);

            // Same CRLF-tolerant line ending as ServerNameLineRegex above.
            var pattern = @"^[ \t]*serverName:[ \t]*['""]?" + Regex.Escape(serverKey) +
                          @"['""]?[ \t]*(?:#[^\r\n]*)?[ \t\r]*$";
            foreach (Match match in Regex.Matches(content, pattern, RegexOptions.Multiline))
            {
                if (!hasBlock || match.Index < blockStart || match.Index >= blockEnd)
                    return true;
            }

            return false;
        }
    }
}
