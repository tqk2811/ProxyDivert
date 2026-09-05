using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>Tests the process's file name and path.</summary>
public sealed class ProcessNameCondition : LeafCondition
{
    public ProcessMatcherType Matcher { get; set; } = ProcessMatcherType.ExeName;

    public override ProcessCondition Clone()
        => new ProcessNameCondition { Negate = Negate, Matcher = Matcher, Pattern = Pattern };
}
