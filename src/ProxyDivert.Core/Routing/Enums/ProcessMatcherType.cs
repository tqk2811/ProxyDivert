namespace ProxyDivert.Core.Routing.Enums;

// How a process rule recognises the processes it applies to.
public enum ProcessMatcherType
{
    // Executable file name, with or without ".exe" ("chrome", "chrome.exe").
    ExeName = 0,

    // Full path of the executable, compared case-insensitively.
    FullPath = 1,

    // Wildcard over the full path ("C:\Games\*\client.exe").
    Wildcard = 2,
}
