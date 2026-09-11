using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>
/// What one condition row looks at — the process's name and path, or its command line — and what
/// the editor needs to know about that without knowing the class behind it.
/// </summary>
/// <remarks>
/// This is the first combo box on every row of the filter editor, and it decides which comparison
/// list the second one offers. It used to be an enum, and every place that had to get from the enum
/// to a class and back — the editor row in both directions, the sentence at the top of the window —
/// did it with a switch of its own. Each of those now asks the subject.
///
/// Nothing here is written to the file: the file says the same thing by the type of the node.
/// <see cref="Name"/> is what the window's text is looked up by, so it is as fixed as a key in the
/// string tables.
/// </remarks>
public sealed class ConditionSubject
{
    public static ConditionSubject ProcessName { get; } = For(
        "ProcessName", ProcessMatcherType.ExeName, matcher => new ProcessNameCondition { Matcher = matcher });

    public static ConditionSubject CommandLine { get; } = For(
        "CommandLine", ArgumentMatcherType.Contains, matcher => new CommandLineCondition { Matcher = matcher });

    /// <summary>Every subject, in the order the editor offers them.</summary>
    /// <remarks>
    /// Each leaf type in <see cref="ProcessCondition"/>'s list of derived types appears here once,
    /// and a test holds the two lists together — a class missing from this one could be loaded
    /// from a file and never picked in the editor.
    /// </remarks>
    public static IReadOnlyList<ConditionSubject> All { get; } = new[] { ProcessName, CommandLine };

    private readonly Type _matcherType;
    private readonly Func<object, LeafCondition> _create;

    private ConditionSubject(string name, Type matcherType, object defaultMatcher, Func<object, LeafCondition> create)
    {
        Name = name;
        _matcherType = matcherType;
        DefaultMatcher = defaultMatcher;
        Matchers = Enum.GetValues(matcherType).Cast<object>().ToArray();
        _create = create;
    }

    private static ConditionSubject For<TMatcher>(
        string name, TMatcher defaultMatcher, Func<TMatcher, LeafCondition> create)
        where TMatcher : struct, Enum
        => new ConditionSubject(name, typeof(TMatcher), defaultMatcher, matcher => create((TMatcher)matcher));

    /// <summary>A name that does not change with the language. The window looks its text up by it.</summary>
    public string Name { get; }

    /// <summary>The comparisons that make sense for this subject, in the order they are offered.</summary>
    /// <remarks>
    /// Two lists rather than one because they are genuinely different: a command line is not a path,
    /// so "full path" has no meaning there, and "contains" is the sensible default rather than "is
    /// exactly".
    /// </remarks>
    public IReadOnlyList<object> Matchers { get; }

    /// <summary>What a row is set to when it is switched to this subject.</summary>
    public object DefaultMatcher { get; }

    /// <summary>Whether <paramref name="matcher"/> is one of this subject's comparisons.</summary>
    public bool Offers(object? matcher) => matcher != null && matcher.GetType() == _matcherType;

    /// <summary>A condition of this subject.</summary>
    /// <remarks>
    /// A comparison from another subject's list becomes the default instead of an exception. The
    /// editor swaps the whole comparison list when the subject changes, and a combo box whose
    /// selection is no longer in its list writes something back on the way past.
    /// </remarks>
    public LeafCondition Create(object? matcher, string? pattern, bool negate)
    {
        LeafCondition condition = _create(Offers(matcher) ? matcher! : DefaultMatcher);
        condition.Pattern = pattern ?? string.Empty;
        condition.Negate = negate;
        return condition;
    }

    public override string ToString() => Name;
}
