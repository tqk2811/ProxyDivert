namespace ProxyDivert.Core.Routing.Enums;

// How a process rule recognises the command line of the processes it applies to.
//
// Separate from ProcessMatcherType because a command line is not a path: the useful default there
// is "mentions this somewhere", not "is exactly this".
public enum ArgumentMatcherType
{
    // The command line contains the text, compared case-insensitively ("--profile-directory").
    Contains = 0,

    // Wildcard over the whole command line ("*-Dminecraft*").
    Wildcard = 1,

    // The whole command line, ignoring surrounding whitespace and case.
    Exact = 2,
}
