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
    private readonly IReadOnlyDictionary<uint, Guid> _policyByProcessId;
    private readonly RoutingPolicy _fallbackPolicy;

    public RoutingPolicyResolver(
        IEnumerable<RoutingPolicy> policies,
        IEnumerable<Outbound> outbounds,
        IReadOnlyDictionary<uint, Guid> policyByProcessId,
        RoutingPolicy? fallbackPolicy = null)
    {
        if (policies is null) throw new ArgumentNullException(nameof(policies));
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));

        _policies = policies.ToDictionary(p => p.Id);
        _policyByProcessId = policyByProcessId ?? new Dictionary<uint, Guid>();

        var byId = outbounds.ToDictionary(o => o.Id);
        // The two built-ins always resolve, whether or not the user's list contains them.
        if (!byId.ContainsKey(Outbound.DirectId)) byId[Outbound.DirectId] = Outbound.CreateDirect();
        if (!byId.ContainsKey(Outbound.BlockId)) byId[Outbound.BlockId] = Outbound.CreateBlock();
        _outbounds = byId;

        _fallbackPolicy = fallbackPolicy ?? new RoutingPolicy
        {
            Id = Guid.Empty,
            Name = "Untracked",
            DefaultOutboundId = Outbound.DirectId,
        };
    }

    public RoutingPolicy GetPolicy(uint processId)
        => _policyByProcessId.TryGetValue(processId, out Guid policyId)
           && _policies.TryGetValue(policyId, out RoutingPolicy? policy)
            ? policy
            : _fallbackPolicy;

    // First matching enabled rule wins; otherwise the policy default. A rule pointing at an
    // outbound that no longer exists, or at a disabled one, is skipped rather than silently
    // sending the connection direct.
    public RouteDecision Resolve(RouteTarget target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        RoutingPolicy policy = GetPolicy(target.ProcessId);

        foreach (RoutingRule rule in policy.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Order))
        {
            bool match = HostMatcher.IsMatch(rule.Matcher, rule.Pattern, target.Host, target.Address, target.Port);
            if (rule.IsNot) match = !match;
            if (!match) continue;

            if (TryGetUsableOutbound(rule.OutboundId, out Outbound? outbound))
                return new RouteDecision(outbound!, policy, rule);
        }

        Outbound fallback = TryGetUsableOutbound(policy.DefaultOutboundId, out Outbound? def)
            ? def!
            : _outbounds[Outbound.DirectId];
        return new RouteDecision(fallback, policy, null);
    }

    // UDP that is not DNS: the policy's UdpMode decides, but an outbound that cannot carry UDP
    // downgrades ThroughOutbound to Block instead of letting the datagram out with the real
    // source IP on it.
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
