using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Anti-DPI set on a policy: what its rules match goes out with the policy's switches and chunk
// size, which win over the outbound's; everything else keeps the outbound's.
public class AntiDpiPolicyTests
{
    private const uint Pid = 77;

    private static Outbound Make(OutboundKind kind, string? url, bool tls = false, bool connect = false, int chunk = 2)
        => new Outbound
        {
            Id = Guid.NewGuid(), Name = kind.ToString(), Kind = kind, Url = url,
            AntiDpiTls = tls, AntiDpiConnect = connect, AntiDpiChunkSize = chunk,
        };

    private static RoutingPolicy PolicyFor(Outbound outbound, string domain, bool? tls, bool? connect = null, int? chunk = null)
    {
        var policy = new RoutingPolicy
        {
            Id = Guid.NewGuid(), Name = "p", OutboundId = outbound.Id,
            AntiDpiTls = tls, AntiDpiConnect = connect, AntiDpiChunkSize = chunk,
        };
        policy.Rules.Add(new RoutingRule { Id = Guid.NewGuid(), Matcher = HostMatcherType.DomainSuffix, Pattern = domain });
        return policy;
    }

    private static RoutingPolicyResolver Resolver(RoutingPolicy policy, Outbound outbound)
        => new RoutingPolicyResolver(
            new[] { policy },
            new[] { outbound },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { policy.Id } });

    private static RouteTarget Target(string host, bool udp = false)
        => new RouteTarget(Pid, IPAddress.Parse("1.2.3.4"), 443, host, udp);

    [Fact]
    public void Policy_turns_TLS_on_for_the_sites_it_matches()
    {
        Outbound proxy = Make(OutboundKind.Socks5, "socks5://127.0.0.1:1080");
        RouteDecision decision = Resolver(PolicyFor(proxy, "blocked.example", tls: true, chunk: 5), proxy)
            .Resolve(Target("www.blocked.example"));

        Assert.Equal(proxy.Id, decision.Outbound.Id);
        Assert.Equal(5, decision.Outbound.EffectiveTlsChunkSize);
        Assert.Equal(0, decision.Outbound.EffectiveConnectChunkSize);
        // The configured outbound itself is left alone.
        Assert.False(proxy.AntiDpiTls);
    }

    [Fact]
    public void Policy_turns_TLS_off_over_an_outbound_that_has_it_on()
    {
        Outbound direct = Make(OutboundKind.Direct, null, tls: true);
        RouteDecision decision = Resolver(PolicyFor(direct, "fragile.example", tls: false), direct)
            .Resolve(Target("fragile.example"));

        Assert.Equal(0, decision.Outbound.EffectiveTlsChunkSize);
    }

    [Fact]
    public void Null_follows_the_outbound_and_returns_the_same_instance()
    {
        Outbound proxy = Make(OutboundKind.HttpProxy, "http://127.0.0.1:8080", tls: true, connect: true, chunk: 3);
        RouteDecision decision = Resolver(PolicyFor(proxy, "a.example", tls: null), proxy)
            .Resolve(Target("a.example"));

        Assert.Same(proxy, decision.Outbound);
    }

    [Fact]
    public void An_outbound_that_cannot_split_ignores_the_policy()
    {
        Outbound ssh = Make(OutboundKind.Ssh, "ssh://user@127.0.0.1:22");
        Assert.Same(ssh, ssh.WithAntiDpi(true, true, 4));

        // CONNECT on Direct means nothing: Direct sends no CONNECT.
        Outbound direct = Make(OutboundKind.Direct, null);
        Assert.Same(direct, direct.WithAntiDpi(null, true));
    }

    [Fact]
    public void Udp_gets_the_outbound_as_configured()
    {
        Outbound proxy = Make(OutboundKind.Socks5, "socks5://127.0.0.1:1080");
        RoutingPolicy policy = PolicyFor(proxy, "a.example", tls: true);
        policy.UdpMode = UdpMode.ThroughOutbound;

        RouteDecision decision = Resolver(policy, proxy).ResolveUdp(Target("a.example", udp: true));

        Assert.Same(proxy, decision.Outbound);
    }

    [Fact]
    public async Task Registry_builds_the_policy_variant_a_source_of_its_own_and_drops_both_together()
    {
        Outbound proxy = Make(OutboundKind.Socks5, "socks5://127.0.0.1:1080");
        Outbound variant = proxy.WithAntiDpi(true, null, 4);
        await using var registry = new OutboundRegistry(OutboundSourceFactory.CreateDefault());

        IProxySource plain = registry.GetOrCreate(proxy).Source;
        IProxySource split = registry.GetOrCreate(variant).Source;
        Assert.NotSame(plain, split);
        Assert.Same(split, registry.GetOrCreate(proxy.WithAntiDpi(true, null, 4)).Source);

        // Untouched outbound: both stay.
        Assert.Empty(await registry.ReconcileAsync(new[] { proxy }, null));
        Assert.Same(split, registry.GetOrCreate(variant).Source);

        // Editing the outbound rebuilds the variant too.
        proxy.Url = "socks5://127.0.0.1:9999";
        Assert.Equal(new[] { proxy.Id }, await registry.ReconcileAsync(new[] { proxy }, null));
        Assert.NotSame(split, registry.GetOrCreate(proxy.WithAntiDpi(true, null, 4)).Source);
    }

    [Fact]
    public async Task Registry_shares_one_source_between_settings_that_build_the_same_thing()
    {
        // CONNECT ticked on Direct changes nothing Direct sends, so it must not cost a second source.
        Outbound direct = Make(OutboundKind.Direct, null);
        Outbound sameThing = Make(OutboundKind.Direct, null, connect: true, chunk: 9);
        sameThing.Id = direct.Id;
        await using var registry = new OutboundRegistry(OutboundSourceFactory.CreateDefault());

        Assert.Same(registry.GetOrCreate(direct).Source, registry.GetOrCreate(sameThing).Source);
    }

    [Fact]
    public void Config_clears_a_policy_chunk_size_below_one()
    {
        AppConfig config = AppConfig.CreateDefault();
        config.Policies[0].AntiDpiChunkSize = 0;

        config.Normalize();

        Assert.Null(config.Policies[0].AntiDpiChunkSize);
    }
}
