namespace ProxyDivert.Core.Routing.Enums;

// How the conditions inside one group are combined.
//
// A group carries exactly one of these, which is why the editor never puts an operator between two
// rows: the indentation says which group a row belongs to, and the group says how its rows join.
// Mixing "and" and "or" on one level, the way a typed expression does, is what forces a user to
// think about precedence — a group cannot express that, and that is the point.
//
// The numbers are the serialized form: append, never renumber.
public enum ConditionOperator
{
    // Every condition in the group has to match.
    All = 0,

    // At least one of them has to.
    Any = 1,
}
