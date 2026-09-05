using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing;

// Turns (process, destination) into "which outbound".
//
// The resolver holds an immutable snapshot of the configuration: policies, outbounds, and the
// pid -> policy assignments the process watcher has made. Editing the config builds a NEW resolver
// rather than mutating this one, so a connection being routed right now can never observe a
// half-applied rule change.
//
// A pid with no assignment resolves to the fallback policy (normally "everything direct"), which
// matters because the relay can see a connection from a process the watcher has just dropped.
public sealed class RoutingPolicyResolver
{
    private readonly IReadOnlyDictionary<Guid, RoutingPolicy> _policies;
    private readonly IReadOnlyDictionary<Guid, Outbound> _outbounds;
    private readonly IReadOnlyDictionary<uint, IReadOnlyList<Guid>> _policiesByProcessId;
    private readonly RoutingPolicy _fallbackPolicy;

    public RoutingPolicyResolver(
        IEnumerable<RoutingPolicy> policies,
        IEnumerable<Outbound> outbounds,
        IReadOnlyDictionary<uint, IReadOnlyList<Guid>> policiesByProcessId,
        RoutingPolicy? fallbackPolicy = null)
    {
        if (policies is null) throw new ArgumentNullException(nameof(policies));
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));

        _policies = policies.ToDictionary(p => p.Id);
        _policiesByProcessId = policiesByProcessId ?? new Dictionary<uint, IReadOnlyList<Guid>>();

        var byId = outbounds.ToDictionary(o => o.Id);
        // The two built-ins always resolve, whether or not the user's list contains them.
        if (!byId.ContainsKey(Outbound.DirectId)) byId[Outbound.DirectId] = Outbound.CreateDirect();
        if (!byId.ContainsKey(Outbound.BlockId)) byId[Outbound.BlockId] = Outbound.CreateBlock();
        _outbounds = byId;

        _fallbackPolicy = fallbackPolicy ?? new RoutingPolicy
        {
            Id = Guid.Empty,
            Name = "Untracked",
            OutboundId = Outbound.DirectId,
        };
    }

    /// <summary>
    /// The policies applied to this process, in the order the filter listed them. Empty never
    /// happens: a process nothing claims gets the fallback policy.
    /// </summary>
    public IReadOnlyList<RoutingPolicy> GetPolicies(uint processId)
    {
        if (!_policiesByProcessId.TryGetValue(processId, out IReadOnlyList<Guid>? ids) || ids.Count == 0)
            return new[] { _fallbackPolicy };

        // A policy the user deleted while its filter still names it is skipped rather than faked:
        // its rules are gone, and pretending otherwise would route by a list nobody can see.
        var found = new List<RoutingPolicy>(ids.Count);
        foreach (Guid id in ids)
            if (_policies.TryGetValue(id, out RoutingPolicy? policy)) found.Add(policy);

        return found.Count > 0 ? found : new[] { _fallbackPolicy };
    }

    /// <summary>
    /// The policy whose own settings apply to this process — the first one the filter listed. The
    /// rest contribute rules only.
    /// </summary>
    public RoutingPolicy GetPolicy(uint processId) => GetPolicies(processId)[0];

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
        IReadOnlyList<RoutingPolicy> policies = GetPolicies(target.ProcessId);

        foreach (RoutingPolicy policy in policies)
        {
            foreach (RoutingRule rule in policy.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Order))
            {
                bool match = HostMatcher.IsMatch(rule.Matcher, rule.Pattern, target.Host, target.Address, target.Port);
                if (rule.IsNot) match = !match;
                if (!match) continue;

                if (TryGetUsableOutbound(policy.OutboundId, out Outbound? outbound))
                    return new RouteDecision(outbound!, policy, rule);
            }
        }

        return new RouteDecision(_outbounds[Outbound.DirectId], policies[0], null);
    }

    // UDP that is not DNS: the UdpMode decides, but an outbound that cannot carry UDP downgrades
    // ThroughOutbound to Block instead of letting the datagram out with the real source IP on it.
    //
    // Read off the first policy, like the other settings that are not rules: a filter listing three
    // policies would otherwise have three answers to "is QUIC blocked" and no way to say which.
    public RouteDecision ResolveUdp(RouteTarget target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        RoutingPolicy policy = GetPolicy(target.ProcessId);

        if (policy.BlockQuic && target.Port == 443)
            return new RouteDecision(_outbounds[Outbound.BlockId], policy, null);

        switch (policy.UdpMode)
        {
            case UdpMode.Block:
                return new RouteDecision(_outbounds[Outbound.BlockId], policy, null);

            case UdpMode.Direct:
                return new RouteDecision(_outbounds[Outbound.DirectId], policy, null);

            case UdpMode.ThroughOutbound:
            {
                RouteDecision tcpDecision = Resolve(target);
                if (tcpDecision.Outbound.SupportsUdp) return tcpDecision;
                return new RouteDecision(_outbounds[Outbound.BlockId], policy, tcpDecision.MatchedRule);
            }

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
