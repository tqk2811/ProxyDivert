namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>A condition that compares a pattern against one facet of the process.</summary>
/// <remarks>
/// The facet is the derived type — file name and path, or command line, and whatever gets added
/// later. That is the combo box on the left of every row in the editor: it picks which of these a
/// row is, and the comparison list next to it follows from that choice.
/// </remarks>
public abstract class LeafCondition : ProcessCondition
{
    /// <summary>What to compare against. Empty means the condition is not filled in yet.</summary>
    public string Pattern { get; set; } = string.Empty;
}
