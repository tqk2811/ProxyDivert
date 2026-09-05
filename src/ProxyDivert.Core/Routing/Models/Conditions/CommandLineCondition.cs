using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>Tests the process's whole command line.</summary>
/// <remarks>
/// Reading another process's command line costs a WMI query, so the watcher only pays for it while
/// some enabled filter actually contains one of these with a pattern in it.
/// </remarks>
public sealed class CommandLineCondition : LeafCondition
{
    public ArgumentMatcherType Matcher { get; set; } = ArgumentMatcherType.Contains;

    public override ProcessCondition Clone()
        => new CommandLineCondition { Negate = Negate, Matcher = Matcher, Pattern = Pattern };
}
