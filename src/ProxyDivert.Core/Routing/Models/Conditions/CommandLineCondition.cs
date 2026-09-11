using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>Tests the process's whole command line.</summary>
/// <remarks>
/// The command line is read for every process whether or not any filter asks about it — the native
/// reader gets it in the same call as the path, for the whole machine in a few tens of milliseconds.
/// It used to cost a WMI query per process, which is why an earlier version only read it while
/// some filter had one of these in it.
/// </remarks>
public sealed class CommandLineCondition : LeafCondition
{
    public ArgumentMatcherType Matcher { get; set; } = ArgumentMatcherType.Contains;

    public override ProcessCondition Clone()
        => new CommandLineCondition { Negate = Negate, Matcher = Matcher, Pattern = Pattern };
}
