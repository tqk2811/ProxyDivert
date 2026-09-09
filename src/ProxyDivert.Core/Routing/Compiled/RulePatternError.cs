using System;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Compiled;

/// <summary>One rule whose pattern could not be parsed, and why.</summary>
/// <remarks>
/// Before there was a compile step there was nowhere to say this: an unparseable pattern simply
/// matched nothing, on every connection, for as long as it stayed in the list. The row looked
/// enabled and the traffic it was meant to claim went somewhere else.
/// </remarks>
public sealed class RulePatternError
{
    public RulePatternError(
        Guid policyId, string policyName, Guid ruleId, HostMatcherType matcher, string? pattern, string message)
    {
        PolicyId = policyId;
        PolicyName = policyName;
        RuleId = ruleId;
        Matcher = matcher;
        Pattern = pattern;
        Message = message;
    }

    public Guid PolicyId { get; }
    public string PolicyName { get; }
    public Guid RuleId { get; }
    public HostMatcherType Matcher { get; }
    public string? Pattern { get; }
    public string Message { get; }

    // Deliberately not a sentence: the label above it says what the list is, in the user's own
    // language, and this is the row plus the parser's own words about it.
    public override string ToString()
        => $"{PolicyName}: {Matcher}:{Pattern} — {Message}";
}
