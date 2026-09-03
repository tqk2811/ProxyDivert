using System;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models;

// "Processes that look like this are redirected, using that policy."
public sealed class ProcessRule
{
    public required Guid Id { get; set; }

    public required ProcessMatcherType Matcher { get; set; }

    public required string Pattern { get; set; }

    // Follow processes the matched process spawns. Needed for anything with a launcher or a
    // multi-process browser.
    public bool IncludeChildren { get; set; } = true;

    public required Guid PolicyId { get; set; }

    public bool IsEnabled { get; set; } = true;

    public override string ToString() => $"{Matcher}:{Pattern}";
}
