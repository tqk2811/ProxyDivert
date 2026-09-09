using System;
using System.Collections.Generic;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing.Compiled;

/// <summary>One policy with its rules parsed, filtered to the enabled ones and put in Order.</summary>
/// <remarks>
/// Sorting here rather than in <c>Resolve</c> is most of the point: the resolver used to run
/// <c>Where(...).OrderBy(...)</c> over every policy of every connection, which is a sort of a list
/// that had not changed since the configuration was saved.
/// </remarks>
public sealed class CompiledPolicy
{
    public CompiledPolicy(RoutingPolicy source, IReadOnlyList<CompiledRule> rules)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public RoutingPolicy Source { get; }

    /// <summary>The enabled rules, lowest Order first. The first one that matches wins.</summary>
    public IReadOnlyList<CompiledRule> Rules { get; }

    public Guid Id => Source.Id;

    public override string ToString() => Source.Name;
}
