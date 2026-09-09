using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Routing.Compiled;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing;

// Turns (process, destination) into "which outbound".
//
// The resolver holds an immutable snapshot of the configuration: the compiled policies and the
// outbounds. Editing the config builds a NEW resolver rather than mutating this one, so a
// connection being routed right now can never observe a half-applied rule change.
//
// The pid -> policy assignments are NOT part of that snapshot: they are read live from whoever
// tracks processes. Which process is under redirection changes several times a minute on a machine
// running a browser, and rebuilding the whole table for each one meant re-parsing every pattern of
// every policy sixty times over a browser start. Each pid's list of policy ids is itself replaced
// whole, never edited in place, so a connection still resolves against one coherent list.
//
// A pid with no assignment resolves to the fallback policy (normally "everything direct"), which
// matters because the relay can see a connection from a process the watcher has just dropped.
public sealed class RoutingPolicyResolver
{
    private readonly CompiledRuleSet _ruleSet;
    private readonly IReadOnlyDictionary<Guid, Outbound> _outbounds;
    private readonly IProcessPolicySource _policiesByProcessId;
    private readonly CompiledPolicy _fallbackPolicy;

    public RoutingPolicyResolver(
        CompiledRuleSet ruleSet,
        IEnumerable<Outbound> outbounds,
        IProcessPolicySource policiesByProcessId,
        RoutingPolicy? fallbackPolicy = null)
    {
        _ruleSet = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));
        _policiesByProcessId = policiesByProcessId ?? ProcessPolicyMap.Empty;

        var byId = outbounds.ToDictionary(o => o.Id);
        // The two built-ins always resolve, whether or not the user's list contains them.
        if (!byId.ContainsKey(Outbound.DirectId)) byId[Outbound.DirectId] = Outbound.CreateDirect();
        if (!byId.ContainsKey(Outbound.BlockId)) byId[Outbound.BlockId] = Outbound.CreateBlock();
        _outbounds = byId;

        _fallbackPolicy = CompiledRuleSet.CompilePolicy(fallbackPolicy ?? new RoutingPolicy
        {
            Id = Guid.Empty,
            Name = "Untracked",
            OutboundId = Outbound.DirectId,
        });
    }

    /// <summary>
    /// Compiles the policies and resolves against a fixed pid map. For callers that have a
    /// configuration and nothing tracking processes — tests, and one-off questions about what a
    /// configuration would do.
    /// </summary>
    public RoutingPolicyResolver(
        IEnumerable<RoutingPolicy> policies,
        IEnumerable<Outbound> outbounds,
        IReadOnlyDictionary<uint, IReadOnlyList<Guid>>? policiesByProcessId,
        RoutingPolicy? fallbackPolicy = null)
        : this(
            CompiledRuleSet.Compile(policies ?? throw new ArgumentNullException(nameof(policies))),
            outbounds,
            ProcessPolicyMap.From(policiesByProcessId),
            fallbackPolicy)
    {
    }

    /// <summary>The rules of this configuration that could not be parsed. Empty when all of them can.</summary>
    public IReadOnlyList<RulePatternError> RuleErrors => _ruleSet.Errors;

    /// <summary>
    /// The policies applied to this process, in the order the filter listed them. Empty never
    /// happens: a process nothing claims gets the fallback policy.
    /// </summary>
    public IReadOnlyList<RoutingPolicy> GetPolicies(uint processId)
        => GetCompiledPolicies(processId).Select(p => p.Source).ToList();

    /// <summary>
    /// The policy whose own settings apply to this process — the first one the filter listed. The
    /// rest contribute rules only.
    /// </summary>
    public RoutingPolicy GetPolicy(uint processId) => GetCompiledPolicies(processId)[0].Source;

    private IReadOnlyList<CompiledPolicy> GetCompiledPolicies(uint processId)
    {
        if (!_policiesByProcessId.TryGetPolicyIds(processId, out IReadOnlyList<Guid> ids) || ids.Count == 0)
            return new[] { _fallbackPolicy };

        // A policy the user deleted while its filter still names it is skipped rather than faked:
        // its rules are gone, and pretending otherwise would route by a list nobody can see.
        var found = new List<CompiledPolicy>(ids.Count);
        foreach (Guid id in ids)
            if (_ruleSet.TryGetPolicy(id, out CompiledPolicy? policy)) found.Add(policy!);

        return found.Count > 0 ? found : new[] { _fallbackPolicy };
    }

    // First matching enabled rule wins, across every policy in turn: all of the first policy's
    // rules in their own Order, then the second policy's, and so on. That is what the order in the
    // filter means — one list read end to end, not a merge.
    //
    // Where it goes is the policy's outbound, not the rule's: a rule says which destinations belong
    // to this policy, and everything that belongs to it leaves the same way.
    //
    // Nothing matching anywhere means no policy claimed the connection, and it goes Direct. A
    // policy whose outbound no longer exists, or is disabled, is skipped rather than silently
    // sending the connection direct under that policy's name.
    public RouteDecision Resolve(RouteTarget target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        return Resolve(target, GetCompiledPolicies(target.ProcessId));
    }

    private RouteDecision Resolve(RouteTarget target, IReadOnlyList<CompiledPolicy> policies)
    {
        foreach (CompiledPolicy policy in policies)
        {
            foreach (CompiledRule rule in policy.Rules)
            {
                if (!rule.IsMatch(target)) continue;

                if (TryGetUsableOutbound(policy.Source.OutboundId, out Outbound? outbound))
                    return new RouteDecision(outbound!, policy.Source, rule.Source);
            }
        }

        return new RouteDecision(_outbounds[Outbound.DirectId], policies[0].Source, null);
    }

    // UDP that is not DNS. The datagram follows the SAME decision its TCP twin would get, and only
    // when that decision is a proxy or a VPN do the UDP settings come into it:
    //
    //   * TCP would go Direct — nothing claimed the destination, or the policy's outbound is Direct
    //     — then the datagram goes direct too, QUIC included. BlockQuic exists to stop a browser
    //     from slipping past the proxy over UDP/443 while its TCP is tunnelled; with no proxy in the
    //     picture there is nothing to slip past, and blocking it only makes the browser spend
    //     seconds retrying QUIC before it falls back to the TCP that was going to work all along.
    //   * TCP would be tunnelled: UdpMode decides. ThroughOutbound rides the tunnel when the
    //     outbound can carry UDP; otherwise the QUIC block applies (a datagram that reaches the
    //     destination direct carries the real source address), then Direct or Block as configured.
    //
    // The UDP settings are read off the first policy, like every setting that is not a rule: a
    // filter listing three policies would otherwise have three answers to "is QUIC blocked".
    public RouteDecision ResolveUdp(RouteTarget target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));

        // Read once, and both the settings and the rules come from the same list: asking twice
        // could see the process re-described in between and answer with one policy's BlockQuic
        // against another policy's rules.
        IReadOnlyList<CompiledPolicy> policies = GetCompiledPolicies(target.ProcessId);
        RoutingPolicy policy = policies[0].Source;

        if (policy.UdpMode == UdpMode.Block)
            return new RouteDecision(_outbounds[Outbound.BlockId], policy, null);

        RouteDecision tcpDecision = Resolve(target, policies);
        // Neither of these has an outbound to ride, so UdpMode has nothing left to decide.
        if (!tcpDecision.UsesTunnel) return tcpDecision;

        switch (policy.UdpMode)
        {
            case UdpMode.ThroughOutbound when tcpDecision.Outbound.SupportsUdp:
                return tcpDecision;

            case UdpMode.ThroughOutbound:
            case UdpMode.Direct when policy.BlockQuic && target.Port == 443:
                return new RouteDecision(_outbounds[Outbound.BlockId], policy, tcpDecision.MatchedRule);

            case UdpMode.Direct:
                return new RouteDecision(_outbounds[Outbound.DirectId], policy, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(target), policy.UdpMode, "Unknown UdpMode");
        }
    }

    private bool TryGetUsableOutbound(Guid id, out Outbound? outbound)
    {
        outbound = null;
        if (!_outbounds.TryGetValue(id, out Outbound? found)) return false;
        if (!found.IsEnabled) return false;
        outbound = found;
        return true;
    }
}
