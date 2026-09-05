using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Core.Routing.Models;

// One named filter: "processes that look like THIS are redirected using THOSE policies."
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

    /// <summary>
    /// The policies applied to whatever this filter catches, in priority order: the rules of the
    /// first policy are tried before those of the second, and the first rule that matches decides
    /// the connection, and it leaves through that policy's outbound. A connection no policy claims
    /// goes Direct. The first policy is also the one whose UDP mode and Block QUIC apply, those
    /// being settings a connection cannot pick per rule.
    /// </summary>
    /// <remarks>
    /// A list rather than one policy because a policy is a rule set, and rule sets are worth
    /// combining: "company hosts" plus "streaming" is two lists everywhere else, not a third list
    /// that has to be kept in step with both.
    /// </remarks>
    public List<Guid> PolicyIds { get; set; } = new List<Guid>();

    /// <summary>The policy whose own settings apply, or <see cref="Guid.Empty"/> when there is none.</summary>
    [JsonIgnore]
    public Guid PrimaryPolicyId => PolicyIds.Count > 0 ? PolicyIds[0] : Guid.Empty;

    public bool IsEnabled { get; set; } = true;

    public override string ToString()
        => string.IsNullOrWhiteSpace(Name) ? $"filter {Id:D}" : Name;
}
