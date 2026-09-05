namespace ProxyDivert.Core.Routing.Enums;

// The answer one condition gives about one process. Four states rather than a bool, because two of
// the four are the difference between a filter that works and one that redirects the whole machine.
//
// <see cref="Unknown"/> exists for NOT. The command line of a system process cannot be read, and
// the old code called that "does not match" — correct while every condition was positive. Negate
// it and "does not match" becomes "matches", so a filter saying "argument does NOT contain X"
// would claim every process whose command line is unreadable. Unknown does not flip.
//
// <see cref="Ignored"/> is the half-typed condition: an empty value box is not a condition yet, so
// it must not drag its group down to "no" while the user is still typing, nor lift it to "yes".
public enum ConditionResult
{
    // Nothing was asked (empty value box, or a group with no usable condition in it).
    Ignored = 0,

    Match = 1,

    NoMatch = 2,

    // The data this condition asks about could not be read, or the pattern itself is broken.
    Unknown = 3,
}
