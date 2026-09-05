using System;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models;

// "Processes that look like this are redirected, using that policy."
public sealed class ProcessRule
{
    public required Guid Id { get; set; }

    public required ProcessMatcherType Matcher { get; set; }

    public required string Pattern { get; set; }

    // A second condition on the same rule, ANDed with the one above: "java.exe, but only the one
    // running Minecraft". Left empty it is not consulted at all, which is what every rule written
    // before this existed means — so an old configuration keeps behaving exactly as it did.
    //
    // Reading another process's command line costs a WMI query, so the engine only pays for it
    // while at least one enabled rule fills this in.
    public ArgumentMatcherType ArgumentMatcher { get; set; } = ArgumentMatcherType.Contains;

    public string? ArgumentPattern { get; set; }

    // Follow processes the matched process spawns. Needed for anything with a launcher or a
    // multi-process browser.
    public bool IncludeChildren { get; set; } = true;

    public required Guid PolicyId { get; set; }

    public bool IsEnabled { get; set; } = true;

    public override string ToString()
        => string.IsNullOrWhiteSpace(ArgumentPattern)
            ? $"{Matcher}:{Pattern}"
            : $"{Matcher}:{Pattern} + {ArgumentMatcher}:{ArgumentPattern}";
}
