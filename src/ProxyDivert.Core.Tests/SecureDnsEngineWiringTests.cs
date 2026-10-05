using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Compiled;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.WinDivert.SecureDns.Interfaces;
using TqkLibrary.WinDivert.SecureDns.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The engine side of secure DNS: the resolver pool, the decider handed to the redirector, the stream
// that carries DoH through an outbound, and the VPN servers kept out of the takeover. No network.
public class SecureDnsEngineWiringTests
{
    private static readonly Uri Endpoint = new Uri("https://1.1.1.1/dns-query");

    private static Outbound Socks5(Guid? id = null) => new Outbound
    {
        Id = id ?? Guid.NewGuid(),
        Name = "socks",
        Kind = OutboundKind.Socks5,
        Url = "socks5://127.0.0.1:1080",
    };

    private sealed class FakeResolver : IDnsResolver
    {
        public FakeResolver(TimeSpan timeout) => Timeout = timeout;
        public TimeSpan Timeout { get; }
        public bool IsDisposed { get; private set; }
        public Uri Endpoint => SecureDnsEngineWiringTests.Endpoint;
        public Task<byte[]?> ResolveAsync(byte[] dnsWireQuery, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public void Dispose() => IsDisposed = true;
    }

    private static (OutboundDnsResolverPool Pool, List<FakeResolver> Created) Pool(TimeSpan retireDelay)
    {
        var created = new List<FakeResolver>();
        var pool = new OutboundDnsResolverPool(
            Endpoint,
            (_, timeout) => { var r = new FakeResolver(timeout); lock (created) created.Add(r); return r; },
            retireDelay,
            NullLogger.Instance);
        return (pool, created);
    }

    // ---- pool -----------------------------------------------------------------------------------

    [Fact]
    public void Pool_returns_one_resolver_per_outbound_and_timeout_kind()
    {
        var (pool, created) = Pool(TimeSpan.Zero);
        Outbound a = Socks5(), b = Socks5();

        IDnsResolver aNormal = pool.Get(a, shortTimeout: false);
        Assert.Same(aNormal, pool.Get(a, shortTimeout: false));
        IDnsResolver aShort = pool.Get(a, shortTimeout: true);
        Assert.NotSame(aNormal, aShort);
        Assert.NotSame(aNormal, pool.Get(b, shortTimeout: false));

        Assert.Equal(3, created.Count);
        Assert.Equal(OutboundDnsResolverPool.NormalTimeout, ((FakeResolver)aNormal).Timeout);
        Assert.Equal(OutboundDnsResolverPool.ShortTimeout, ((FakeResolver)aShort).Timeout);
    }

    [Fact]
    public void Dispose_disposes_every_resolver_and_refuses_new_ones()
    {
        var (pool, created) = Pool(TimeSpan.FromHours(1));
        Outbound a = Socks5();
        pool.Get(a, false);
        pool.Get(a, true);

        pool.Dispose();

        Assert.All(created, r => Assert.True(r.IsDisposed));
        Assert.Throws<ObjectDisposedException>(() => pool.Get(a, false));
    }

    [Fact]
    public void Invalidate_disposes_only_that_outbounds_resolvers_and_the_next_query_gets_a_fresh_one()
    {
        var (pool, _) = Pool(TimeSpan.Zero);
        Outbound a = Socks5(), b = Socks5();
        var aOld = (FakeResolver)pool.Get(a, false);
        var aOldShort = (FakeResolver)pool.Get(a, true);
        var bResolver = (FakeResolver)pool.Get(b, false);

        pool.Invalidate(a.Id);

        Assert.True(aOld.IsDisposed);
        Assert.True(aOldShort.IsDisposed);
        Assert.False(bResolver.IsDisposed);
        Assert.NotSame(aOld, pool.Get(a, false));
    }

    [Fact]
    public async Task Invalidate_waits_for_the_retire_delay_before_disposing()
    {
        var (pool, _) = Pool(TimeSpan.FromMilliseconds(200));
        Outbound a = Socks5();
        var old = (FakeResolver)pool.Get(a, false);

        pool.Invalidate(a.Id);
        Assert.False(old.IsDisposed);

        for (int i = 0; i < 100 && !old.IsDisposed; i++) await Task.Delay(50);
        Assert.True(old.IsDisposed);
    }

    [Fact]
    public void Retire_refuses_new_resolvers_and_disposes_the_old_ones_after_the_delay()
    {
        var (pool, created) = Pool(TimeSpan.Zero);
        Outbound a = Socks5();
        pool.Get(a, false);

        pool.Retire();

        Assert.All(created, r => Assert.True(r.IsDisposed));
        Assert.Throws<ObjectDisposedException>(() => pool.Get(a, false));
    }

    // ---- decider --------------------------------------------------------------------------------

    private static DnsRouteDecision Decision(Outbound outbound, bool fallback)
        => new DnsRouteDecision(
            outbound,
            new RoutingPolicy { Id = Guid.NewGuid(), Name = "p", OutboundId = outbound.Id, SecureDnsFallbackToPlain = fallback },
            matchedRule: null);

    [Fact]
    public void Decider_passes_a_query_the_routing_table_leaves_alone()
    {
        var decider = new SecureDnsQueryDecider(
            (_, _, _) => null,
            (_, _) => throw new InvalidOperationException("must not be asked"),
            NullLogger.Instance);

        Assert.True(decider.Decide(new DnsQueryInfo(42, "example.com", 1, false)).IsPass);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decider_resolves_through_the_outbound_with_the_policys_fallback_flag(bool fallback)
    {
        Outbound outbound = Socks5();
        var resolver = new FakeResolver(TimeSpan.Zero);
        (Outbound, bool)? asked = null;
        (uint?, string, bool)? routed = null;
        var decider = new SecureDnsQueryDecider(
            (pid, name, v6) => { routed = (pid, name, v6); return Decision(outbound, fallback); },
            (o, shortTimeout) => { asked = (o, shortTimeout); return resolver; },
            NullLogger.Instance);

        DnsQueryDecision decision = decider.Decide(new DnsQueryInfo(7, "example.com", 28, true));

        Assert.Same(resolver, decision.Resolver);
        Assert.Equal(fallback, decision.FallbackToPlainDnsOnFailure);
        Assert.Equal((outbound, fallback), asked);
        Assert.Equal(((uint?)7, "example.com", true), routed);
    }

    [Fact]
    public void Decider_passes_when_routing_throws()
    {
        var throwingRoute = new SecureDnsQueryDecider(
            (_, _, _) => throw new InvalidOperationException(),
            (_, _) => new FakeResolver(TimeSpan.Zero),
            NullLogger.Instance);

        Assert.True(throwingRoute.Decide(new DnsQueryInfo(null, "a.com", 1, false)).IsPass);
    }

    [Fact]
    public void Decider_retries_once_when_the_pool_was_retired_between_the_read_and_the_get()
    {
        var fresh = new FakeResolver(TimeSpan.Zero);
        int calls = 0;
        var decider = new SecureDnsQueryDecider(
            (_, _, _) => Decision(Socks5(), false),
            (_, _) => ++calls == 1 ? throw new ObjectDisposedException("pool") : fresh,
            NullLogger.Instance);

        DnsQueryDecision decision = decider.Decide(new DnsQueryInfo(null, "a.com", 1, false));

        Assert.Same(fresh, decision.Resolver);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Decider_passes_when_the_engine_has_stopped()
    {
        var decider = new SecureDnsQueryDecider(
            (_, _, _) => Decision(Socks5(), false),
            (_, _) => null,
            NullLogger.Instance);

        Assert.True(decider.Decide(new DnsQueryInfo(null, "a.com", 1, false)).IsPass);
    }

    [Fact]
    public async Task Decider_answers_with_a_failure_not_a_pass_when_no_resolver_and_no_fallback()
    {
        var decider = new SecureDnsQueryDecider(
            (_, _, _) => Decision(Socks5(), fallback: false),
            (_, _) => throw new InvalidOperationException("cannot create"),
            NullLogger.Instance);

        DnsQueryDecision decision = decider.Decide(new DnsQueryInfo(null, "a.com", 1, false));

        Assert.False(decision.IsPass);
        Assert.False(decision.FallbackToPlainDnsOnFailure);
        Assert.Null(await decision.Resolver!.ResolveAsync(new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public void Decider_passes_when_no_resolver_but_the_policy_falls_back()
    {
        var decider = new SecureDnsQueryDecider(
            (_, _, _) => Decision(Socks5(), fallback: true),
            (_, _) => throw new InvalidOperationException("cannot create"),
            NullLogger.Instance);

        Assert.True(decider.Decide(new DnsQueryInfo(null, "a.com", 1, false)).IsPass);
    }

    [Fact]
    public void Dispose_also_disposes_resolvers_that_were_retired_with_a_long_delay()
    {
        var (pool, _) = Pool(TimeSpan.FromHours(1));
        Outbound a = Socks5();
        var old = (FakeResolver)pool.Get(a, false);
        pool.Invalidate(a.Id);
        Assert.False(old.IsDisposed);

        pool.Dispose();

        Assert.True(old.IsDisposed);
    }

    // ---- stream through an outbound -------------------------------------------------------------

    private sealed class FakeConnectSource : IConnectSource
    {
        public bool FailConnect { get; init; }
        public Uri? ConnectedTo { get; private set; }
        public bool IsDisposed { get; private set; }
        public MemoryStream Stream { get; } = new MemoryStream();

        public Task ConnectAsync(Uri address, CancellationToken cancellationToken = default)
        {
            ConnectedTo = address;
            return FailConnect ? Task.FromException(new IOException("refused")) : Task.CompletedTask;
        }

        public Task<Stream> GetStreamAsync(CancellationToken cancellationToken = default) => Task.FromResult<Stream>(Stream);
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeProxySource : IProxySource
    {
        private readonly FakeConnectSource _connect;
        public FakeProxySource(FakeConnectSource connect) => _connect = connect;
        public Task<IConnectSource> GetConnectSourceAsync(Guid tunnelId, CancellationToken cancellationToken = default)
            => Task.FromResult<IConnectSource>(_connect);
        public ValueTask DisposeAsync() => default;
    }

    [Fact]
    public async Task Disposing_the_stream_closes_the_tunnel_behind_it()
    {
        var connect = new FakeConnectSource();

        Stream stream = await ConnectSourceStream.OpenAsync(new FakeProxySource(connect), "1.1.1.1", 443, CancellationToken.None);
        Assert.Equal(new Uri("tcp://1.1.1.1:443"), connect.ConnectedTo);
        Assert.False(connect.IsDisposed);

        await stream.DisposeAsync();

        Assert.True(connect.IsDisposed);
        Assert.False(connect.Stream.CanRead);
    }

    [Fact]
    public async Task A_failed_connect_closes_the_half_opened_tunnel()
    {
        var connect = new FakeConnectSource { FailConnect = true };

        await Assert.ThrowsAsync<IOException>(
            () => ConnectSourceStream.OpenAsync(new FakeProxySource(connect), "dns.example", 443, CancellationToken.None));

        Assert.True(connect.IsDisposed);
    }

    // ---- VPN servers named in profile files -----------------------------------------------------

    [Fact]
    public void Config_hosts_are_read_from_openvpn_remote_and_wireguard_endpoint_lines()
    {
        string[] lines =
        {
            "client",
            "# remote commented.example 1194",
            "remote vpn.example.com 1194 udp",
            "[Peer]",
            "Endpoint = wg.example.net:51820",
            "Endpoint = [2001:db8::1]:51820",
        };

        Assert.Equal(
            new[] { "vpn.example.com", "wg.example.net", "2001:db8::1" },
            VpnServerHostReader.ParseConfigHosts(lines).ToArray());
    }

    [Fact]
    public void Vpn_server_named_in_a_profile_file_is_never_taken_over()
    {
        string path = Path.Combine(Path.GetTempPath(), "pd-test-" + Guid.NewGuid().ToString("N") + ".ovpn");
        File.WriteAllLines(path, new[] { "client", "remote vpn.example.com 1194" });
        try
        {
            var vpn = new Outbound { Id = Guid.NewGuid(), Name = "vpn", Kind = OutboundKind.Vpn, Url = path };
            Assert.Contains("vpn.example.com", VpnServerHostReader.Read(new[] { vpn }, NullLogger.Instance));

            Outbound proxy = Socks5();
            RoutingPolicy policy = new RoutingPolicy
            {
                Id = Guid.NewGuid(), Name = "all", OutboundId = proxy.Id, SecureDnsProcess = true,
            };
            policy.Rules.Add(new RoutingRule
            {
                Id = Guid.NewGuid(), Matcher = HostMatcherType.DomainSuffix, Pattern = "example.com", IsEnabled = true,
            });
            var map = new Dictionary<uint, IReadOnlyList<Guid>> { [5] = new[] { policy.Id } };

            var without = new RoutingPolicyResolver(new[] { policy }, new[] { proxy, vpn }, map);
            var with = new RoutingPolicyResolver(
                CompiledRuleSet.Compile(new[] { policy }), new[] { proxy, vpn }, ProcessPolicyMap.From(map),
                extraDnsPassThroughHosts: VpnServerHostReader.Read(new[] { vpn }, NullLogger.Instance));

            Assert.NotNull(without.ResolveDns(5, "vpn.example.com"));
            Assert.Null(with.ResolveDns(5, "vpn.example.com"));
            Assert.NotNull(with.ResolveDns(5, "www.example.com"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
