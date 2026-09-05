using System;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models;

// One line of a policy: "destinations matching Pattern belong to this policy".
// Rules are evaluated in Order; the first match wins, and where it goes is the policy's business.
public sealed class RoutingRule
{
    public required Guid Id { get; set; }

    public required HostMatcherType Matcher { get; set; }

    public required string Pattern { get; set; }

    // Inverts the match ("everything EXCEPT *.local").
    public bool IsNot { get; set; }

    // Lower runs first. Kept explicit so drag-and-drop in the UI is just a number change.
    public int Order { get; set; }

    public bool IsEnabled { get; set; } = true;

    public override string ToString()
        => $"{(IsNot ? "!" : "")}{Matcher}:{Pattern}";
}
