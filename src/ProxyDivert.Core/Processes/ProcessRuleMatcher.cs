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
// and (from a 32-bit host) 64-bit processes report null there. ExeName still works for those, and
// so do the plain string comparisons, which look at the name as well as the path.
public static class ProcessRuleMatcher
{
    // A pattern typed into a text box runs against every process on the machine, every scan. An
    // expression that backtracks catastrophically would hang the watcher, so it gets a deadline
    // rather than the benefit of the doubt.
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(100);

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
        string subject = commandLine.Trim();

        return rule.ArgumentMatcher switch
        {
            ArgumentMatcherType.Contains => Contains(pattern, subject),
            ArgumentMatcherType.Wildcard => WildcardMatches(pattern, subject),
            ArgumentMatcherType.Exact => Same(pattern, subject),
            ArgumentMatcherType.StartsWith => StartsWith(pattern, subject),
            ArgumentMatcherType.EndsWith => EndsWith(pattern, subject),
            ArgumentMatcherType.Regex => RegexMatches(pattern, subject),
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
                       && Same(NormalizePath(pattern), NormalizePath(executablePath));

            case ProcessMatcherType.Wildcard:
                return executablePath != null
                       && WildcardMatches(NormalizePath(pattern), NormalizePath(executablePath));

            // The plain comparisons are asked against the path and the name both, and match if
            // either does. Path only would make "Contains chrome" useless for every process whose
            // path cannot be read; name only would make "StartsWith C:\Games" impossible.
            case ProcessMatcherType.StartsWith:
                return EitherSubject(pattern, processName, executablePath, StartsWith);

            case ProcessMatcherType.EndsWith:
                return EitherSubject(pattern, processName, executablePath, EndsWith);

            case ProcessMatcherType.Contains:
                return EitherSubject(pattern, processName, executablePath, Contains);

            case ProcessMatcherType.Regex:
                return EitherSubject(pattern, processName, executablePath, RegexMatches);

            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule.Matcher, "Unknown process matcher");
        }
    }

    // The pattern is normalised the same way the path is, so a pattern pasted with forward slashes
    // still lines up; the name is compared as it stands.
    private static bool EitherSubject(
        string pattern, string processName, string? executablePath, Func<string, string, bool> compare)
        => (executablePath != null && compare(NormalizePath(pattern), NormalizePath(executablePath)))
           || compare(pattern, processName);

    private static bool Contains(string pattern, string subject)
        => subject.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool StartsWith(string pattern, string subject)
        => subject.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);

    private static bool EndsWith(string pattern, string subject)
        => subject.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string pattern, string subject)
        => string.Equals(subject, pattern, StringComparison.OrdinalIgnoreCase);

    // "*" and "?" as everyone writes them in a file dialog, turned into the regex they mean.
    private static bool WildcardMatches(string pattern, string subject)
        => RunRegex(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            subject);

    private static bool RegexMatches(string pattern, string subject) => RunRegex(pattern, subject);

    // Every pattern here came from a text box, so a typo must not take the watcher down: one that
    // does not compile, or one that runs past its deadline, matches nothing.
    private static bool RunRegex(string pattern, string subject)
    {
        try
        {
            return Regex.IsMatch(
                subject, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
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
