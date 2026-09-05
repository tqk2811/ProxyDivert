namespace ProxyDivert.Core.Routing.Enums;

// How a process rule recognises the command line of the processes it applies to.
//
// Separate from ProcessMatcherType because a command line is not a path: the useful default here is
// "mentions this somewhere", not "is exactly this". The comparisons themselves are the same set.
//
// The numbers are the serialized form: append, never renumber.
public enum ArgumentMatcherType
{
    // The command line contains the text, compared case-insensitively ("--profile-directory").
    Contains = 0,

    // Wildcard over the whole command line ("*-Dminecraft*").
    Wildcard = 1,

    // The whole command line, ignoring surrounding whitespace and case.
    Exact = 2,

    // The command line begins with the text.
    StartsWith = 3,

    // The command line ends with the text.
    EndsWith = 4,

    // .NET regular expression over the whole command line. A pattern that does not compile, or one
    // that runs too long, matches nothing.
    Regex = 5,
}
