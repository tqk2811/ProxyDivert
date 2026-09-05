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

    // A policy is a list of destinations and the one way out they share, so the outbound is named
    // here rather than on each rule.
    private static RoutingPolicy Policy(params RoutingRule[] rules) => PolicyTo(ProxyId, rules);

    private static RoutingPolicy PolicyTo(Guid outboundId, params RoutingRule[] rules)
    {
        var policy = new RoutingPolicy { Id = PolicyId, Name = "test", OutboundId = outboundId };
        policy.Rules.AddRange(rules);
        return policy;
    }

    private static RoutingRule Rule(HostMatcherType matcher, string pattern, int order = 0, bool isNot = false)
        => new RoutingRule
        {
            Id = Guid.NewGuid(),
            Matcher = matcher,
            Pattern = pattern,
            Order = order,
            IsNot = isNot,
        };

    private static RoutingPolicyResolver Resolver(RoutingPolicy policy, params Outbound[] outbounds)
        => new RoutingPolicyResolver(
            new[] { policy },
            outbounds,
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { policy.Id } });

    private static RouteTarget Target(string? host, string address = "93.184.216.34", int port = 443, bool isUdp = false)
        => new RouteTarget(Pid, IPAddress.Parse(address), port, host, isUdp);

    [Fact]
    public void Matching_rule_wins_over_default()
    {
        var resolver = Resolver(Policy(Rule(HostMatcherType.Wildcard, "*.google.com")), Socks5());

        RouteDecision decision = resolver.Resolve(Target("www.google.com"));

        Assert.Equal(OutboundKind.Socks5, decision.Outbound.Kind);
        Assert.NotNull(decision.MatchedRule);
    }

    // Nothing claimed the connection, so it goes out as it would have without the tool at all.
    [Fact]
    public void Nothing_matching_goes_direct()
    {
        var resolver = Resolver(Policy(Rule(HostMatcherType.Wildcard, "*.google.com")), Socks5());

        RouteDecision decision = resolver.Resolve(Target("example.com"));

        Assert.Equal(OutboundKind.Direct, decision.Outbound.Kind);
        Assert.Null(decision.MatchedRule);
    }

    // Within a policy the rules still have an order, and the first one to match is the one the
    // decision names. Both rules here match, so only the order can tell them apart.
    [Fact]
    public void Rules_are_evaluated_in_order()
    {
        var suffixFirst = Rule(HostMatcherType.DomainSuffix, "google.com", order: 0);
        var wildcardSecond = Rule(HostMatcherType.Wildcard, "*.google.com", order: 1);
        var resolver = Resolver(Policy(wildcardSecond, suffixFirst), Socks5());

        RouteDecision decision = resolver.Resolve(Target("www.google.com"));

        Assert.Equal(HostMatcherType.DomainSuffix, decision.MatchedRule!.Matcher);
    }

    [Fact]
    public void IsNot_inverts_the_match()
    {
        var resolver = Resolver(
            Policy(Rule(HostMatcherType.DomainSuffix, "internal.local", isNot: true)),
            Socks5());

        Assert.Equal(OutboundKind.Socks5, resolver.Resolve(Target("example.com")).Outbound.Kind);
        Assert.Equal(OutboundKind.Direct, resolver.Resolve(Target("host.internal.local")).Outbound.Kind);
    }

    [Fact]
    public void Disabled_rule_is_skipped()
    {
        RoutingRule rule = Rule(HostMatcherType.Wildcard, "*");
        rule.IsEnabled = false;
        var resolver = Resolver(Policy(rule), Socks5());

        Assert.Equal(OutboundKind.Direct, resolver.Resolve(Target("example.com")).Outbound.Kind);
    }

    [Fact]
    public void A_policy_whose_outbound_is_disabled_is_skipped()
    {
        var resolver = Resolver(Policy(Rule(HostMatcherType.Wildcard, "*")), Socks5(enabled: false));

        Assert.Equal(OutboundKind.Direct, resolver.Resolve(Target("example.com")).Outbound.Kind);
    }

    // A filter can name several policies, and the order it names them in is the priority: every
    // rule of the first policy is tried before any rule of the second. Not a merge by Order across
    // policies — two policies written separately have overlapping Order numbers, and merging them
    // would route by whichever number happened to be smaller.
    [Fact]
    public void Policies_are_tried_in_the_order_the_filter_lists_them()
    {
        var first = new RoutingPolicy { Id = Guid.NewGuid(), Name = "first", OutboundId = ProxyId };
        first.Rules.Add(Rule(HostMatcherType.DomainSuffix, "example.com", order: 9));

        var second = new RoutingPolicy { Id = Guid.NewGuid(), Name = "second", OutboundId = Outbound.BlockId };
        second.Rules.Add(Rule(HostMatcherType.DomainSuffix, "example.com", order: 0));

        Assert.Equal(OutboundKind.Socks5, RouteThrough(new[] { first, second }, first.Id, second.Id));

        // The same two policies the other way round give the other answer, which is the whole point.
        Assert.Equal(OutboundKind.Block, RouteThrough(new[] { first, second }, second.Id, first.Id));
    }

    // No policy in the list claims the connection, so none of their outbounds applies to it. There
    // is no "default" left to fall to: it goes Direct.
    [Fact]
    public void Nothing_matching_any_policy_goes_direct()
    {
        var first = new RoutingPolicy { Id = Guid.NewGuid(), Name = "first", OutboundId = ProxyId };
        first.Rules.Add(Rule(HostMatcherType.DomainSuffix, "corp.local"));

        var second = new RoutingPolicy { Id = Guid.NewGuid(), Name = "second", OutboundId = Outbound.BlockId };
        second.Rules.Add(Rule(HostMatcherType.DomainSuffix, "intranet"));

        Assert.Equal(OutboundKind.Direct, RouteThrough(new[] { first, second }, first.Id, second.Id));
    }

    // A policy deleted while a filter still names it leaves the rest of the list working, rather
    // than dropping the process to the untracked default — which is Direct, i.e. a leak.
    [Fact]
    public void A_policy_that_no_longer_exists_is_skipped_not_fatal()
    {
        var live = new RoutingPolicy { Id = Guid.NewGuid(), Name = "live", OutboundId = ProxyId };
        live.Rules.Add(Rule(HostMatcherType.Wildcard, "*"));

        Assert.Equal(OutboundKind.Socks5, RouteThrough(new[] { live }, Guid.NewGuid(), live.Id));
    }

    private static OutboundKind RouteThrough(RoutingPolicy[] policies, params Guid[] chosen)
    {
        var resolver = new RoutingPolicyResolver(
            policies,
            new[] { Socks5() },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = chosen });

        return resolver.Resolve(Target("www.example.com")).Outbound.Kind;
    }

    [Fact]
    public void Untracked_process_gets_the_fallback_policy()
    {
        var resolver = new RoutingPolicyResolver(
            new[] { Policy(Rule(HostMatcherType.Wildcard, "*")) },
            new[] { Socks5() },
            new Dictionary<uint, IReadOnlyList<Guid>>());

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
        RoutingPolicy policy = Policy(Rule(HostMatcherType.Wildcard, "*"));
        policy.UdpMode = UdpMode.ThroughOutbound;
        policy.BlockQuic = false;
        var resolver = Resolver(policy, HttpProxy());   // HTTP proxies cannot carry UDP

        RouteDecision decision = resolver.ResolveUdp(Target("example.com", port: 12345, isUdp: true));

        Assert.Equal(OutboundKind.Block, decision.Outbound.Kind);
    }

    [Fact]
    public void Udp_through_socks5_is_allowed()
    {
        RoutingPolicy policy = Policy(Rule(HostMatcherType.Wildcard, "*"));
        policy.UdpMode = UdpMode.ThroughOutbound;
        policy.BlockQuic = false;
        var resolver = Resolver(policy, Socks5());

        RouteDecision decision = resolver.ResolveUdp(Target("example.com", port: 12345, isUdp: true));

        Assert.Equal(OutboundKind.Socks5, decision.Outbound.Kind);
    }
}
