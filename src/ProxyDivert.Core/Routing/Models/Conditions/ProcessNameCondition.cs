using System;
using System.IO;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>Tests the process's file name and path.</summary>
/// <remarks>
/// A comparison on the path can only match a process whose path is readable; system processes and
/// (from a 32-bit host) 64-bit processes report null there. ExeName still works for those, and so
/// do the plain string comparisons, which look at the name as well as the path.
/// </remarks>
public sealed class ProcessNameCondition : LeafCondition
{
    public ProcessMatcherType Matcher { get; set; } = ProcessMatcherType.ExeName;

    public override ProcessCondition Clone()
        => new ProcessNameCondition { Negate = Negate, Matcher = Matcher, Pattern = Pattern };

    private protected override ConditionResult Compare(string pattern, ConditionContext context)
    {
        string? path = context.ExecutablePath;

        switch (Matcher)
        {
            case ProcessMatcherType.ExeName:
            {
                // Compare without the extension on both sides, so "chrome" and "chrome.exe" are
                // the same condition — that difference is never what the user meant.
                string wanted = StripExe(pattern);
                if (Same(StripExe(context.ProcessName), wanted)) return ConditionResult.Match;
                if (path != null && Same(StripExe(Path.GetFileName(path)), wanted))
                    return ConditionResult.Match;

                // The name is always readable, so a "no" here is a real answer, not a guess.
                return ConditionResult.NoMatch;
            }

            case ProcessMatcherType.FullPath:
                return path == null
                    ? ConditionResult.Unknown
                    : Yes(Same(NormalizePath(pattern), NormalizePath(path)));

            case ProcessMatcherType.Wildcard:
                return path == null
                    ? ConditionResult.Unknown
                    : WildcardMatches(NormalizePath(pattern), NormalizePath(path), context);

            // The plain comparisons are asked against the path and the name both, and match if
            // either does. Path only would make "contains chrome" useless for every process whose
            // path cannot be read; name only would make "starts with C:\Games" impossible.
            case ProcessMatcherType.StartsWith:
                return NameOrPath(pattern, context, StartsWith);

            case ProcessMatcherType.EndsWith:
                return NameOrPath(pattern, context, EndsWith);

            case ProcessMatcherType.Contains:
                return NameOrPath(pattern, context, Contains);

            case ProcessMatcherType.Regex:
                return NameOrPath(pattern, context, RegexMatches);

            default:
                throw new ArgumentOutOfRangeException(nameof(Matcher), Matcher, "Unknown process matcher");
        }
    }

    // The pattern is normalised the same way the path is, so a pattern pasted with forward slashes
    // still lines up; the name is compared as it stands.
    //
    // With no readable path only half the question got asked, so anything short of a match comes
    // back Unknown rather than a "no" this code cannot actually stand behind.
    private static ConditionResult NameOrPath(
        string pattern, ConditionContext context, Func<string, string, ConditionContext, ConditionResult> compare)
    {
        ConditionResult byName = compare(pattern, context.ProcessName, context);
        if (byName == ConditionResult.Match) return ConditionResult.Match;

        if (context.ExecutablePath == null) return ConditionResult.Unknown;

        ConditionResult byPath = compare(NormalizePath(pattern), NormalizePath(context.ExecutablePath), context);
        if (byPath == ConditionResult.Match) return ConditionResult.Match;

        return byName == ConditionResult.Unknown || byPath == ConditionResult.Unknown
            ? ConditionResult.Unknown
            : ConditionResult.NoMatch;
    }

    private static string StripExe(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - 4)
            : name;
}
