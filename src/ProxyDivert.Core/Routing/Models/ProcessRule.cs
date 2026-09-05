using System;
using System.Text.Json.Serialization;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Core.Routing.Models;

// One named filter: "processes that look like THIS are redirected using THAT policy."
//
// Three parts, in the order the editor shows them: a name, a tree of conditions, and what to do
// with whatever matches. The name exists because a condition tree is no longer something you can
// read at a glance in a grid row — "Minecraft" is, and the tree is one click away.
public sealed class ProcessRule
{
    public required Guid Id { get; set; }

    /// <summary>What the user calls this filter. Free text, not an identifier — duplicates are fine.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The condition tree. Null, or a tree with nothing filled in, matches no process at all —
    /// never every process, which is the direction that would redirect the whole machine.
    /// </summary>
    public ProcessCondition? Condition { get; set; }

    // Follow processes the matched process spawns. Needed for anything with a launcher or a
    // multi-process browser.
    public bool IncludeChildren { get; set; } = true;

    public required Guid PolicyId { get; set; }

    public bool IsEnabled { get; set; } = true;

    // ==== config v2 and older ====
    //
    // Back then a filter was exactly two conditions ANDed together, each a fixed slot on the rule
    // itself. ConfigStore turns them into a tree on load and then clears them, so they are never
    // written back (null properties are omitted).
    //
    // They keep their old JSON names so old files still bind, but not their old C# names: anything
    // still reaching for rule.Matcher is code that would silently stop working, and it should fail
    // to build instead.

    [JsonPropertyName("Matcher")]
    public ProcessMatcherType? LegacyMatcher { get; set; }

    [JsonPropertyName("Pattern")]
    public string? LegacyPattern { get; set; }

    [JsonPropertyName("ArgumentMatcher")]
    public ArgumentMatcherType? LegacyArgumentMatcher { get; set; }

    [JsonPropertyName("ArgumentPattern")]
    public string? LegacyArgumentPattern { get; set; }

    public override string ToString()
        => string.IsNullOrWhiteSpace(Name) ? $"filter {Id:D}" : Name;
}
