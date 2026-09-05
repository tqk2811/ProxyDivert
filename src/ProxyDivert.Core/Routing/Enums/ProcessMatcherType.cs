namespace ProxyDivert.Core.Routing.Enums;

// How a process rule recognises the processes it applies to.
//
// The first three name what is compared as well as how; the rest are the plain string comparisons,
// and those look at the full path and the executable name both, matching if either does. That is
// what makes "Contains chrome" work for a system process whose path cannot be read, where a rule
// written against the path alone would quietly never fire.
//
// The numbers are the serialized form, so existing configurations keep their meaning: append, never
// renumber.
public enum ProcessMatcherType
{
    // Executable file name, with or without ".exe" ("chrome", "chrome.exe").
    ExeName = 0,

    // Full path of the executable, compared case-insensitively.
    FullPath = 1,

    // Wildcard over the full path ("C:\Games\*\client.exe").
    Wildcard = 2,

    // Path or name begins with the text ("C:\Games\").
    StartsWith = 3,

    // Path or name ends with the text ("\client.exe").
    EndsWith = 4,

    // Path or name mentions the text anywhere ("chrome").
    Contains = 5,

    // .NET regular expression over the path or the name. A pattern that does not compile, or one
    // that runs too long, matches nothing rather than taking the watcher down with it.
    Regex = 6,
}
