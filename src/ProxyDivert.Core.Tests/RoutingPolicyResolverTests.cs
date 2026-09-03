using System;
using System.Collections.Generic;
using System.Net;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

public class RoutingPolicyResolverTests
{
    private static readonly Guid ProxyId = Guid.NewGuid();
    private static readonly Guid PolicyId = Guid.NewGuid();
    private const uint Pid = 1234;

    private static Outbound Socks5(bool enabled = true) => new Outbound
    {
        Id = ProxyId,
        Name = "socks5",
        Kind = OutboundKind.Socks5,
        Url = "socks5://127.0.0.1:1080",
        IsEnabled = enabled,
    };

    private static Outbound HttpProxy() => new Outbound
    {
        Id = ProxyId,
        Name = "http",
        Kind = OutboundKind.HttpProxy,
        Url = "http://127.0.0.1:8080",
    };

    private static RoutingPolicy Policy(params RoutingRule[] rules)
    {
        var policy = new RoutingPolicy { Id = PolicyId, Name = "test" };
        policy.Rules.AddRange(rules);
        return policy;
    }

    private static RoutingRule Rule(HostMatcherType matcher, string pattern, Guid outboundId, int order = 0, bool isNot = false)
        => new RoutingRule
        {
            Id = Guid.NewGuid(),
            Matcher = matcher,
            Pattern = pattern,
            OutboundId = outboundId,
            Order = order,
            IsNot = isNot,
        };

    private static RoutingPolicyResolver Resolver(RoutingPolicy policy, params Outbound[] outbounds)
        => new RoutingPolicyResolver(
            new[] { policy },
            outbounds,
            new Dictionary<uint, Guid> { [Pid] = policy.Id });

    private static RouteTarget Target(string? host, string address = "93.184.216.34", int port = 443, bool isUdp = false)
        => new RouteTarget(Pid, IPAddress.Parse(address), port, host, isUdp);

    [Fact]
    public void Matching_rule_wins_over_default()
    {
        var resolver = Resolver(Policy(Rule(HostMatcherType.Wildcard, "*.google.com", ProxyId)), Socks5());

        RouteDecision decision = resolver.Resolve(Target("www.google.com"));

        Assert.Equal(OutboundKind.Socks5, decision.Outbound.Kind);
        Assert.NotNull(decision.MatchedRule);
    }

    [Fact]
    public void No_match_falls_back_to_policy_default()
    {
        var resolver = Resolver(Policy(Rule(HostMatcherType.Wildcard, "*.google.com", ProxyId)), Socks5());

        RouteDecision decision = resolver.Resolve(Target("example.com"));

        Assert.Equal(OutboundKind.Direct, decision.Outbound.Kind);
        Assert.Null(decision.MatchedRule);
    }

    [Fact]
    public void Rules_are_evaluated_in_order()
    {
        var blockFirst = Rule(HostMatcherType.DomainSuffix, "google.com", Outbound.BlockId, order: 0);
        var proxySecond = Rule(HostMatcherType.Wildcard, "*.google.com", ProxyId, order: 1);
        var resolver = Resolver(Policy(proxySecond, blockFirst), Socks5());

        RouteDecision decision = resolver.Resolve(Target("www.google.com"));

        Assert.Equal(OutboundKind.Block, decision.Outbound.Kind);
    }

    [Fact]
    public void IsNot_inverts_the_match()
    {
        var resolver = Resolver(
            Policy(Rule(HostMatcherType.DomainSuffix, "internal.local", ProxyId, isNot: true)),
            Socks5());

        Assert.Equal(OutboundKind.Socks5, resolver.Resolve(Target("example.com")).Outbound.Kind);
        Assert.Equal(OutboundKind.Direct, resolver.Resolve(Target("host.internal.local")).Outbound.Kind);
    }

    [Fact]
    public void Disabled_rule_is_skipped()
    {
        RoutingRule rule = Rule(HostMatcherType.Wildcard, "*", ProxyId);
        rule.IsEnabled = false;
        var resolver = Resolver(Policy(rule), Socks5());

        Assert.Equal(OutboundKind.Direct, resolver.Resolve(Target("example.com")).Outbound.Kind);
    }

    [Fact]
    public void Rule_pointing_at_a_disabled_outbound_is_skipped()
    {
        var resolver = Resolver(Policy(Rule(HostMatcherType.Wildcard, "*", ProxyId)), Socks5(enabled: false));

        Assert.Equal(OutboundKind.Direct, resolver.Resolve(Target("example.com")).Outbound.Kind);
    }

    [Fact]
    public void Untracked_process_gets_the_fallback_policy()
    {
        var resolver = new RoutingPolicyResolver(
            new[] { Policy(Rule(HostMatcherType.Wildcard, "*", ProxyId)) },
            new[] { Socks5() },
            new Dictionary<uint, Guid>());

        RouteDecision decision = resolver.Resolve(new RouteTarget(999, IPAddress.Loopback, 443, "example.com"));

        Assert.Equal(OutboundKind.Direct, decision.Outbound.Kind);
    }

    [Fact]
    public void Quic_is_blocked_when_the_policy_says_so()
    {
        RoutingPolicy policy = Policy();
        policy.UdpMode = UdpMode.Direct;
        policy.BlockQuic = true;
        var resolver = Resolver(policy, Socks5());

        RouteDecision decision = resolver.ResolveUdp(Target("www.google.com", port: 443, isUdp: true));

        Assert.Equal(OutboundKind.Block, decision.Outbound.Kind);
    }

    [Fact]
    public void Udp_through_an_outbound_that_cannot_carry_it_is_blocked_not_leaked()
    {
        RoutingPolicy policy = Policy(Rule(HostMatcherType.Wildcard, "*", ProxyId));
        policy.UdpMode = UdpMode.ThroughOutbound;
        policy.BlockQuic = false;
        var resolver = Resolver(policy, HttpProxy());   // HTTP proxies cannot carry UDP

        RouteDecision decision = resolver.ResolveUdp(Target("example.com", port: 12345, isUdp: true));

        Assert.Equal(OutboundKind.Block, decision.Outbound.Kind);
    }

    [Fact]
    public void Udp_through_socks5_is_allowed()
    {
        RoutingPolicy policy = Policy(Rule(HostMatcherType.Wildcard, "*", ProxyId));
        policy.UdpMode = UdpMode.ThroughOutbound;
        policy.BlockQuic = false;
        var resolver = Resolver(policy, Socks5());

        RouteDecision decision = resolver.ResolveUdp(Target("example.com", port: 12345, isUdp: true));

        Assert.Equal(OutboundKind.Socks5, decision.Outbound.Kind);
    }
}
