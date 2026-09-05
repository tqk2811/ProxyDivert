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
    /// <summary>True when the rule needs a command line before it can decide.</summary>
    public static bool NeedsCommandLine(ProcessRule rule)
        => rule is not null && rule.IsEnabled && !string.IsNullOrWhiteSpace(rule.ArgumentPattern);

    public static bool IsMatch(ProcessRule rule, string processName, string? executablePath, string? commandLine = null)
        => MatchesProcess(rule, processName, executablePath) && MatchesArguments(rule, commandLine);

    // The second condition, ANDed with the first. A rule that fills it in is asking about the
    // command line specifically, so a process whose command line cannot be read does not match:
    // guessing "probably yes" would redirect processes the rule was written to exclude.
    private static bool MatchesArguments(ProcessRule rule, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(rule.ArgumentPattern)) return true;
        if (commandLine == null) return false;

        string pattern = rule.ArgumentPattern.Trim();

        return rule.ArgumentMatcher switch
        {
            ArgumentMatcherType.Contains =>
                commandLine.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0,
            ArgumentMatcherType.Wildcard => WildcardMatches(pattern, commandLine.Trim()),
            ArgumentMatcherType.Exact =>
                string.Equals(commandLine.Trim(), pattern, StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(
                nameof(rule), rule.ArgumentMatcher, "Unknown argument matcher"),
        };
    }

    private static bool MatchesProcess(ProcessRule rule, string processName, string? executablePath)
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
                return executablePath != null
                       && WildcardMatches(NormalizePath(pattern), NormalizePath(executablePath));

            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule.Matcher, "Unknown process matcher");
        }
    }

    // "*" and "?" as everyone writes them in a file dialog, turned into the regex they mean. A
    // pattern that cannot compile matches nothing rather than throwing at the caller: it came from
    // a text box, and a typo there must not take the watcher down.
    private static bool WildcardMatches(string pattern, string subject)
    {
        string regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        try
        {
            return Regex.IsMatch(subject, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return false;
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
