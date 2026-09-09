using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing.Compiled;

/// <summary>
/// Every policy of one configuration, with every rule parsed. Immutable, built once per save.
/// </summary>
/// <remarks>
/// This is the half of routing that only changes when the user changes it. Keeping it apart from
/// the half that changes constantly — which process is under redirection right now — is what lets
/// a process being attached stop meaning "rebuild the whole routing table": sixty browser tabs used
/// to mean sixty rebuilds, each one re-parsing every pattern in every policy.
///
/// It is also the only place that can say a pattern is unusable. See <see cref="Errors"/>.
/// </remarks>
public sealed class CompiledRuleSet
{
    private readonly Dictionary<Guid, CompiledPolicy> _byId;

    private CompiledRuleSet(
        Dictionary<Guid, CompiledPolicy> byId, IReadOnlyList<RulePatternError> errors)
    {
        _byId = byId;
        Errors = errors;
    }

    /// <summary>A configuration with no policies at all. Everything falls through to Direct.</summary>
    public static CompiledRuleSet Empty { get; } = Compile(Array.Empty<RoutingPolicy>());

    /// <summary>
    /// The rules whose pattern could not be parsed. They match nothing; this is how anyone finds
    /// out. Empty for a configuration that is entirely usable.
    /// </summary>
    public IReadOnlyList<RulePatternError> Errors { get; }

    public IReadOnlyCollection<CompiledPolicy> Policies => _byId.Values;

    public static CompiledRuleSet Compile(IEnumerable<RoutingPolicy> policies)
    {
        if (policies is null) throw new ArgumentNullException(nameof(policies));

        var errors = new List<RulePatternError>();
        var byId = new Dictionary<Guid, CompiledPolicy>();
        foreach (RoutingPolicy policy in policies)
        {
            // Assigned rather than added: two policies sharing an id is a corrupt configuration
            // file, and the engine refusing to start at all over it leaves the machine with no
            // redirection and no explanation.
            byId[policy.Id] = CompilePolicy(policy, errors);
        }

        return new CompiledRuleSet(byId, errors);
    }

    /// <summary>
    /// Compiles one policy on its own. Used for the resolver's fallback policy, which belongs to no
    /// configuration.
    /// </summary>
    public static CompiledPolicy CompilePolicy(RoutingPolicy policy, ICollection<RulePatternError>? errors = null)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));

        var compiled = new List<CompiledRule>();
        // Disabled rows are dropped here rather than skipped per connection, and the sort happens
        // once: this list is read end to end for every connection the policy is asked about.
        foreach (RoutingRule rule in policy.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Order))
        {
            IHostPredicate predicate = HostPredicate.Compile(rule.Matcher, rule.Pattern, out string? error);
            if (error != null)
            {
                errors?.Add(new RulePatternError(
                    policy.Id, policy.Name, rule.Id, rule.Matcher, rule.Pattern, error));
            }
            compiled.Add(new CompiledRule(rule, predicate));
        }

        return new CompiledPolicy(policy, compiled);
    }

    public bool TryGetPolicy(Guid id, out CompiledPolicy? policy) => _byId.TryGetValue(id, out policy);
}
