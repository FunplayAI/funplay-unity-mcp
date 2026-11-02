// Copyright (C) Funplay. Licensed under MIT.

using System.Text.RegularExpressions;

namespace Funplay.Editor.Tools.Builtins
{
    internal static class ExecuteCodeSafetyPolicy
    {
        private static readonly SafetyRule[] BaseRules =
        {
            new SafetyRule(@"\bFile\.Delete\b", "File.Delete blocked by safety_checks"),
            new SafetyRule(@"\bDirectory\.Delete\b", "Directory.Delete blocked by safety_checks"),
            new SafetyRule(@"\bSystem\.IO\.File\.Delete\b", "System.IO.File.Delete blocked by safety_checks"),
            new SafetyRule(@"\bProcess\.Start\b", "Process.Start blocked by safety_checks"),
            new SafetyRule(@"\bSystem\.Diagnostics\.Process\b", "System.Diagnostics.Process blocked by safety_checks"),
            new SafetyRule(@"\bEnvironment\.Exit\b", "Environment.Exit blocked by safety_checks"),
            new SafetyRule(@"\bApplication\.Quit\b", "Application.Quit blocked by safety_checks"),
            new SafetyRule(@"\bAssetDatabase\.DeleteAsset\b", "AssetDatabase.DeleteAsset blocked by safety_checks"),
            new SafetyRule(@"\bwhile\s*\(\s*true\s*\)", "Infinite while(true) loop blocked by safety_checks"),
            new SafetyRule(@"\bfor\s*\(\s*;\s*;\s*\)", "Infinite for(;;) loop blocked by safety_checks"),
        };

        private static readonly SafetyRule[] StrictRules =
        {
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?File\.(?:WriteAllText|WriteAllBytes|WriteAllLines|AppendAllText|AppendAllLines|Copy|Create|CreateText|OpenWrite|Move|Replace|SetAttributes|SetCreationTime|SetLastAccessTime|SetLastWriteTime)\b", "File write/move operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?Directory\.(?:CreateDirectory|Delete|Move)\b", "Directory write/destructive operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?FileInfo\.(?:CopyTo|Create|CreateText|Delete|MoveTo|Replace)\b", "FileInfo write/destructive operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?DirectoryInfo\.(?:Create|CreateSubdirectory|Delete|MoveTo)\b", "DirectoryInfo write/destructive operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?FileStream\s*\(", "Raw FileStream construction blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?StreamWriter\s*\(", "Raw StreamWriter construction blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?StreamReader\s*\(", "Raw StreamReader construction blocked by strict filesystem safety"),
            new SafetyRule("\"(?:~|%USERPROFILE%|%APPDATA%|%LOCALAPPDATA%|\\$HOME)(?:/|\\\\|\\\\\\\\|\"|$)", "User home/config path blocked by strict filesystem safety"),
            // A quoted run of backslashes can be a separator literal, e.g. Replace("\\", "/").
            // Require path content after this prefix; keep rooted folders/UNC servers blocked.
            new SafetyRule(@"""(?:[A-Za-z]:\\|\\\\(?!\\*"")|/Users/|/home/|/root/|/System/|/Library/|/Applications/|/bin/|/sbin/|/usr/|/etc/|/var/|/private/|/tmp/)", "Absolute or system path blocked by strict filesystem safety"),
            new SafetyRule("\"[^\"]*(?:\\.\\./|\\.\\.\\\\)[^\"]*\"", "Path traversal blocked by strict filesystem safety"),
        };

        public static bool TryFindViolation(string code, bool strictFilesystemChecks, out string pattern, out string reason)
        {
            code = code ?? string.Empty;

            if (TryFindViolation(code, BaseRules, out pattern, out reason))
                return true;

            if (strictFilesystemChecks &&
                TryFindViolation(code, StrictRules, out pattern, out reason))
            {
                return true;
            }

            pattern = null;
            reason = null;
            return false;
        }

        private static bool TryFindViolation(string code, SafetyRule[] rules, out string pattern, out string reason)
        {
            foreach (var rule in rules)
            {
                if (!Regex.IsMatch(code, rule.Pattern))
                    continue;

                pattern = rule.Pattern;
                reason = rule.Reason;
                return true;
            }

            pattern = null;
            reason = null;
            return false;
        }

        private sealed class SafetyRule
        {
            public SafetyRule(string pattern, string reason)
            {
                Pattern = pattern;
                Reason = reason;
            }

            public string Pattern { get; }
            public string Reason { get; }
        }
    }
}
