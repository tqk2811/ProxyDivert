using System;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>Tests the process's whole command line.</summary>
/// <remarks>
/// The command line is read for every process whether or not any filter asks about it — the native
/// reader gets it in the same call as the path, for the whole machine in a few tens of milliseconds.
/// It used to cost a WMI query per process, which is why an earlier version only read it while
/// some filter had one of these in it.
/// </remarks>
public sealed class CommandLineCondition : LeafCondition
{
    public ArgumentMatcherType Matcher { get; set; } = ArgumentMatcherType.Contains;

    public override ProcessCondition Clone()
        => new CommandLineCondition { Negate = Negate, Matcher = Matcher, Pattern = Pattern };

    // A condition about the command line has said the process alone is not enough, so a command
    // line that cannot be read is not a "no" — it is "cannot tell", and the filter stays off.
    // Answering "no" stopped being safe the moment NOT existed.
    private protected override ConditionResult Compare(string pattern, ConditionContext context)
    {
        if (context.CommandLine == null) return ConditionResult.Unknown;

        string text = context.CommandLine.Trim();

        return Matcher switch
        {
            ArgumentMatcherType.Contains => Contains(pattern, text, context),
            ArgumentMatcherType.Wildcard => WildcardMatches(pattern, text, context),
            ArgumentMatcherType.Exact => Yes(Same(pattern, text)),
            ArgumentMatcherType.StartsWith => StartsWith(pattern, text, context),
            ArgumentMatcherType.EndsWith => EndsWith(pattern, text, context),
            ArgumentMatcherType.Regex => RegexMatches(pattern, text, context),
            _ => throw new ArgumentOutOfRangeException(nameof(Matcher), Matcher, "Unknown argument matcher"),
        };
    }
}
