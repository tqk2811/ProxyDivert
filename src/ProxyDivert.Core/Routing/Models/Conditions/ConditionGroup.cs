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
