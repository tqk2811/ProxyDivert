using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>A bracket: several conditions joined by one operator, optionally negated.</summary>
public sealed class ConditionGroup : ProcessCondition
{
    public ConditionOperator Operator { get; set; } = ConditionOperator.All;

    public List<ProcessCondition> Children { get; set; } = new List<ProcessCondition>();

    public override ProcessCondition Clone() => new ConditionGroup
    {
        Negate = Negate,
        Operator = Operator,
        Children = Children.Select(child => child.Clone()).ToList(),
    };

    // Kleene logic, plus the fourth state for rows that are not conditions yet:
    //   All — one definite No settles it; otherwise anything unreadable makes the whole group
    //         unreadable; a group whose rows are all empty asked nothing at all.
    //   Any — one definite Yes settles it, even next to something unreadable.
    //
    // An empty group comes back Ignored rather than the vacuous "true" of logic: a filter that
    // matches everything is how the whole machine ends up redirected.
    private protected override ConditionResult Answer(ConditionContext context)
    {
        bool sawMatch = false;
        bool sawNoMatch = false;
        bool sawUnknown = false;

        foreach (ProcessCondition? child in Children)
        {
            // A "null" written into a hand-edited file asks nothing, like an empty row.
            if (child is null) continue;

            switch (child.Evaluate(context))
            {
                case ConditionResult.Match:
                    if (Operator == ConditionOperator.Any) return ConditionResult.Match;
                    sawMatch = true;
                    break;

                case ConditionResult.NoMatch:
                    if (Operator == ConditionOperator.All) return ConditionResult.NoMatch;
                    sawNoMatch = true;
                    break;

                case ConditionResult.Unknown:
                    sawUnknown = true;
                    break;
            }
        }

        if (sawUnknown) return ConditionResult.Unknown;

        return Operator == ConditionOperator.All
            ? (sawMatch ? ConditionResult.Match : ConditionResult.Ignored)
            : (sawNoMatch ? ConditionResult.NoMatch : ConditionResult.Ignored);
    }

    /// <summary>A fresh filter: one "match all" group holding one empty process condition.</summary>
    /// <remarks>
    /// Deliberately not an empty group. A brand-new filter should look like the plain form it
    /// replaces — one row to fill in — and only grow a tree if the user asks for one.
    /// </remarks>
    public static ConditionGroup CreateDefault(string pattern = "")
        => new ConditionGroup
        {
            Operator = ConditionOperator.All,
            Children = { new ProcessNameCondition { Pattern = pattern } },
        };
}
