using System;
using System.IO;
using System.Text.RegularExpressions;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Processes;

// Decides whether a process rule applies to a given process. Separate from the watcher so it can
// be tested without spawning anything.
//
// A rule that matches on path can only match a process whose path is readable; system processes
// and (from a 32-bit host) 64-bit processes report null there. ExeName still works for those.
public static class ProcessRuleMatcher
{
    public static bool IsMatch(ProcessRule rule, string processName, string? executablePath)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (!rule.IsEnabled) return false;
        if (string.IsNullOrWhiteSpace(rule.Pattern)) return false;

        string pattern = rule.Pattern.Trim();

        switch (rule.Matcher)
        {
            case ProcessMatcherType.ExeName:
            {
                // Compare without the extension on both sides, so "chrome" and "chrome.exe" are
                // the same rule — that difference is never what the user meant.
                string ruleName = StripExe(pattern);
                if (string.Equals(StripExe(processName), ruleName, StringComparison.OrdinalIgnoreCase))
                    return true;
                return executablePath != null
                       && string.Equals(StripExe(Path.GetFileName(executablePath)), ruleName, StringComparison.OrdinalIgnoreCase);
            }

            case ProcessMatcherType.FullPath:
                return executablePath != null
                       && string.Equals(NormalizePath(executablePath), NormalizePath(pattern), StringComparison.OrdinalIgnoreCase);

            case ProcessMatcherType.Wildcard:
            {
                if (executablePath == null) return false;
                string regex = "^" + Regex.Escape(NormalizePath(pattern))
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                try
                {
                    return Regex.IsMatch(NormalizePath(executablePath), regex,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule.Matcher, "Unknown process matcher");
        }
    }

    private static string StripExe(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - 4)
            : name;

    // Forward slashes and a trailing separator are both things a user pastes by accident.
    private static string NormalizePath(string path)
        => path.Trim().Replace('/', '\\').TrimEnd('\\');
}
