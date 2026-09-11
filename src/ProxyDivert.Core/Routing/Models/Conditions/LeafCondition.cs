using System;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>A condition that compares a pattern against one facet of the process.</summary>
/// <remarks>
/// The facet is the derived type — file name and path, or command line, and whatever gets added
/// later. That is the combo box on the left of every row in the editor: it picks which of these a
/// row is, and the comparison list next to it follows from that choice.
/// </remarks>
public abstract class LeafCondition : ProcessCondition
{
    /// <summary>What to compare against. Empty means the condition is not filled in yet.</summary>
    public string Pattern { get; set; } = string.Empty;

    // An empty value box is not a condition yet, whatever kind of row it sits in. It must not
    // drag its group down to "no" while the user is still typing, nor lift it to "yes".
    private protected sealed override ConditionResult Answer(ConditionContext context)
        => string.IsNullOrWhiteSpace(Pattern)
            ? ConditionResult.Ignored
            : Compare(Pattern.Trim(), context);

    /// <summary>The comparison itself, for a pattern that has something in it.</summary>
    /// <param name="pattern">The pattern, already trimmed.</param>
    private protected abstract ConditionResult Compare(string pattern, ConditionContext context);

    // ==== the comparisons every kind of row is built from ====
    //
    // All case-insensitive: nothing a user types about a Windows process means something different
    // in another case. The context parameter the plain ones ignore is there so all of them fit the
    // same slot as the two that spend from the regex budget.

    private protected static ConditionResult Yes(bool matched)
        => matched ? ConditionResult.Match : ConditionResult.NoMatch;

    private protected static bool Same(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private protected static ConditionResult Contains(string pattern, string text, ConditionContext context)
        => Yes(text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

    private protected static ConditionResult StartsWith(string pattern, string text, ConditionContext context)
        => Yes(text.StartsWith(pattern, StringComparison.OrdinalIgnoreCase));

    private protected static ConditionResult EndsWith(string pattern, string text, ConditionContext context)
        => Yes(text.EndsWith(pattern, StringComparison.OrdinalIgnoreCase));

    private protected static ConditionResult WildcardMatches(string pattern, string text, ConditionContext context)
        => context.MatchWildcard(pattern, text);

    private protected static ConditionResult RegexMatches(string pattern, string text, ConditionContext context)
        => context.MatchRegex(pattern, text);

    // Forward slashes and a trailing separator are both things a user pastes by accident.
    private protected static string NormalizePath(string path)
        => path.Trim().Replace('/', '\\').TrimEnd('\\');
}
