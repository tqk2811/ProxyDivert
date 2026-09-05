using System;
using System.IO;
using System.Text.RegularExpressions;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Core.Processes;

// Decides whether a process filter applies to a given process. Separate from the watcher so it can
// be tested without spawning anything.
//
// A condition that matches on path can only match a process whose path is readable; system
// processes and (from a 32-bit host) 64-bit processes report null there. ExeName still works for
// those, and so do the plain string comparisons, which look at the name as well as the path.
//
// Everything here answers in four states, not two — see ConditionResult. The rule of the file: a
// filter applies only when the tree comes back Match. Ignored (nothing filled in) and Unknown (the
// data could not be read) both mean "leave this process alone", which is the direction the earlier
// two-slot version took as well, and the one that cannot redirect a process nobody named.
public static class ProcessRuleMatcher
{
    // Patterns come from a text box and run against every process on the machine, every scan. An
    // expression that backtracks catastrophically would hang the watcher, so it gets a deadline
    // rather than the benefit of the doubt.
    //
    // The deadline covers the whole filter, not one pattern: a tree of twenty regexes each allowed
    // 100ms would be two seconds per process per scan.
    private const int RegexBudgetMs = 100;

    // A tree this deep is not something the editor can build — it would be a hand-edited config
    // file, and the recursion has to stop somewhere short of the stack.
    public const int MaxDepth = 16;

    /// <summary>True when the filter needs a command line before it can decide.</summary>
    public static bool NeedsCommandLine(ProcessRule rule)
        => rule is not null && rule.IsEnabled && AsksAboutCommandLine(rule.Condition, 0);

    private static bool AsksAboutCommandLine(ProcessCondition? condition, int depth)
    {
        if (condition is null || depth > MaxDepth) return false;

        switch (condition)
        {
            case CommandLineCondition leaf:
                return !string.IsNullOrWhiteSpace(leaf.Pattern);

            case ConditionGroup group:
                foreach (ProcessCondition child in group.Children)
                    if (AsksAboutCommandLine(child, depth + 1)) return true;
                return false;

            default:
                return false;
        }
    }

    public static bool IsMatch(ProcessRule rule, string processName, string? executablePath, string? commandLine = null)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        if (!rule.IsEnabled) return false;

        return Evaluate(rule.Condition, processName, executablePath, commandLine) == ConditionResult.Match;
    }

    /// <summary>
    /// The answer of one condition — or of one whole subtree — about one process. Public because
    /// the editor runs each row through it to colour that row when you try a filter against a
    /// process that is running right now.
    /// </summary>
    public static ConditionResult Evaluate(
        ProcessCondition? condition, string processName, string? executablePath, string? commandLine)
        => Evaluate(
            condition,
            new Subject(processName, executablePath, commandLine, Environment.TickCount64 + RegexBudgetMs),
            depth: 0);

    private static ConditionResult Evaluate(ProcessCondition? condition, Subject subject, int depth)
    {
        if (condition is null) return ConditionResult.Ignored;
        if (depth > MaxDepth) return ConditionResult.Unknown;

        ConditionResult result = condition switch
        {
            ConditionGroup group => EvaluateGroup(group, subject, depth),
            ProcessNameCondition name => EvaluateProcessName(name, subject),
            CommandLineCondition arguments => EvaluateCommandLine(arguments, subject),
            _ => throw new ArgumentOutOfRangeException(
                nameof(condition), condition.GetType(), "Unknown condition type"),
        };

        return condition.Negate ? Invert(result) : result;
    }

    // NOT leaves alone the two states it has no answer for. Flipping Unknown is the bug this whole
    // four-state business exists to prevent: "argument does not contain X" would then be true for
    // every process whose command line cannot be read, which is most of the system.
    private static ConditionResult Invert(ConditionResult result) => result switch
    {
        ConditionResult.Match => ConditionResult.NoMatch,
        ConditionResult.NoMatch => ConditionResult.Match,
        _ => result,
    };

    // Kleene logic, plus the fourth state for rows that are not conditions yet:
    //   All — one definite No settles it; otherwise anything unreadable makes the whole group
    //         unreadable; a group whose rows are all empty asked nothing at all.
    //   Any — one definite Yes settles it, even next to something unreadable.
    private static ConditionResult EvaluateGroup(ConditionGroup group, Subject subject, int depth)
    {
        bool sawMatch = false;
        bool sawNoMatch = false;
        bool sawUnknown = false;

        foreach (ProcessCondition child in group.Children)
        {
            switch (Evaluate(child, subject, depth + 1))
            {
                case ConditionResult.Match:
                    if (group.Operator == ConditionOperator.Any) return ConditionResult.Match;
                    sawMatch = true;
                    break;

                case ConditionResult.NoMatch:
                    if (group.Operator == ConditionOperator.All) return ConditionResult.NoMatch;
                    sawNoMatch = true;
                    break;

                case ConditionResult.Unknown:
                    sawUnknown = true;
                    break;
            }
        }

        if (sawUnknown) return ConditionResult.Unknown;

        return group.Operator == ConditionOperator.All
            ? (sawMatch ? ConditionResult.Match : ConditionResult.Ignored)
            : (sawNoMatch ? ConditionResult.NoMatch : ConditionResult.Ignored);
    }

    private static ConditionResult EvaluateProcessName(ProcessNameCondition condition, Subject subject)
    {
        if (string.IsNullOrWhiteSpace(condition.Pattern)) return ConditionResult.Ignored;

        string pattern = condition.Pattern.Trim();

        switch (condition.Matcher)
        {
            case ProcessMatcherType.ExeName:
            {
                // Compare without the extension on both sides, so "chrome" and "chrome.exe" are
                // the same condition — that difference is never what the user meant.
                string wanted = StripExe(pattern);
                if (Same(StripExe(subject.Name), wanted)) return ConditionResult.Match;
                if (subject.Path != null && Same(StripExe(Path.GetFileName(subject.Path)), wanted))
                    return ConditionResult.Match;

                // The name is always readable, so a "no" here is a real answer, not a guess.
                return ConditionResult.NoMatch;
            }

            case ProcessMatcherType.FullPath:
                return subject.Path == null
                    ? ConditionResult.Unknown
                    : Yes(Same(NormalizePath(pattern), NormalizePath(subject.Path)));

            case ProcessMatcherType.Wildcard:
                return subject.Path == null
                    ? ConditionResult.Unknown
                    : WildcardMatches(NormalizePath(pattern), NormalizePath(subject.Path), subject);

            // The plain comparisons are asked against the path and the name both, and match if
            // either does. Path only would make "contains chrome" useless for every process whose
            // path cannot be read; name only would make "starts with C:\Games" impossible.
            case ProcessMatcherType.StartsWith:
                return EitherSubject(pattern, subject, StartsWith);

            case ProcessMatcherType.EndsWith:
                return EitherSubject(pattern, subject, EndsWith);

            case ProcessMatcherType.Contains:
                return EitherSubject(pattern, subject, Contains);

            case ProcessMatcherType.Regex:
                return EitherSubject(pattern, subject, RegexMatches);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(condition), condition.Matcher, "Unknown process matcher");
        }
    }

    // A condition about the command line has said the process alone is not enough, so a command
    // line that cannot be read is not a "no" — it is "cannot tell", and the filter stays off.
    // Answering "no" stopped being safe the moment NOT existed.
    private static ConditionResult EvaluateCommandLine(CommandLineCondition condition, Subject subject)
    {
        if (string.IsNullOrWhiteSpace(condition.Pattern)) return ConditionResult.Ignored;
        if (subject.CommandLine == null) return ConditionResult.Unknown;

        string pattern = condition.Pattern.Trim();
        string text = subject.CommandLine.Trim();

        return condition.Matcher switch
        {
            ArgumentMatcherType.Contains => Contains(pattern, text, subject),
            ArgumentMatcherType.Wildcard => WildcardMatches(pattern, text, subject),
            ArgumentMatcherType.Exact => Yes(Same(pattern, text)),
            ArgumentMatcherType.StartsWith => StartsWith(pattern, text, subject),
            ArgumentMatcherType.EndsWith => EndsWith(pattern, text, subject),
            ArgumentMatcherType.Regex => RegexMatches(pattern, text, subject),
            _ => throw new ArgumentOutOfRangeException(
                nameof(condition), condition.Matcher, "Unknown argument matcher"),
        };
    }

    // The pattern is normalised the same way the path is, so a pattern pasted with forward slashes
    // still lines up; the name is compared as it stands.
    //
    // With no readable path only half the question got asked, so anything short of a match comes
    // back Unknown rather than a "no" this code cannot actually stand behind.
    private static ConditionResult EitherSubject(
        string pattern, Subject subject, Func<string, string, Subject, ConditionResult> compare)
    {
        ConditionResult byName = compare(pattern, subject.Name, subject);
        if (byName == ConditionResult.Match) return ConditionResult.Match;

        if (subject.Path == null) return ConditionResult.Unknown;

        ConditionResult byPath = compare(NormalizePath(pattern), NormalizePath(subject.Path), subject);
        if (byPath == ConditionResult.Match) return ConditionResult.Match;

        return byName == ConditionResult.Unknown || byPath == ConditionResult.Unknown
            ? ConditionResult.Unknown
            : ConditionResult.NoMatch;
    }

    private static ConditionResult Yes(bool matched)
        => matched ? ConditionResult.Match : ConditionResult.NoMatch;

    private static ConditionResult Contains(string pattern, string text, Subject subject)
        => Yes(text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

    private static ConditionResult StartsWith(string pattern, string text, Subject subject)
        => Yes(text.StartsWith(pattern, StringComparison.OrdinalIgnoreCase));

    private static ConditionResult EndsWith(string pattern, string text, Subject subject)
        => Yes(text.EndsWith(pattern, StringComparison.OrdinalIgnoreCase));

    private static bool Same(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // "*" and "?" as everyone writes them in a file dialog, turned into the regex they mean.
    private static ConditionResult WildcardMatches(string pattern, string text, Subject subject)
        => RunRegex(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            text,
            subject);

    private static ConditionResult RegexMatches(string pattern, string text, Subject subject)
        => RunRegex(pattern, text, subject);

    // Every pattern here came from a text box, so a typo must not take the watcher down — and must
    // not turn into a confident "no" either, because a NOT in front of it would then claim every
    // process on the machine. A pattern that will not compile, or one that runs past the filter
    // deadline, is Unknown: the filter simply does not apply.
    private static ConditionResult RunRegex(string pattern, string text, Subject subject)
    {
        long remainingMs = subject.DeadlineTicks - Environment.TickCount64;
        if (remainingMs <= 0) return ConditionResult.Unknown;

        try
        {
            return Yes(Regex.IsMatch(
                text,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(remainingMs)));
        }
        catch (ArgumentException)
        {
            return ConditionResult.Unknown;
        }
        catch (RegexMatchTimeoutException)
        {
            return ConditionResult.Unknown;
        }
    }

    private static string StripExe(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - 4)
            : name;

    // Forward slashes and a trailing separator are both things a user pastes by accident.
    private static string NormalizePath(string path)
        => path.Trim().Replace('/', '\\').TrimEnd('\\');

    // What every condition in one evaluation is asked about, plus the deadline they share.
    private readonly struct Subject
    {
        public Subject(string name, string? path, string? commandLine, long deadlineTicks)
        {
            Name = name ?? string.Empty;
            Path = path;
            CommandLine = commandLine;
            DeadlineTicks = deadlineTicks;
        }

        public string Name { get; }
        public string? Path { get; }
        public string? CommandLine { get; }
        public long DeadlineTicks { get; }
    }
}
