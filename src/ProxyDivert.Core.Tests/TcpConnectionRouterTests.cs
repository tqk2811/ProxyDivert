using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Engine.Interfaces;
using ProxyDivert.Core.Engine.Models;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Redirect.Interfaces;
using TqkLibrary.WinDivert.Redirect.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The TCP routing path, tested for the first time. It used to be three private methods on an engine
// that needs a loaded driver to exist at all, so what a connection is told and what it costs could
// only be checked by running the application.
//
// Nothing here opens a real upstream: the way out is a fake that refuses, which is exactly the case
// worth pinning — what the row says when a connection cannot be made, and how much was done before
// finding out.
public class TcpConnectionRouterTests
{
    private static readonly Guid ProxyId = Guid.NewGuid();
    private static readonly Guid PolicyId = Guid.NewGuid();
    private const uint Pid = 777;

    private sealed class Slot : IResolverSource
    {
        public Slot(RoutingPolicyResolver resolver) => Resolver = resolver;
        public RoutingPolicyResolver Resolver { get; set; }
    }

    // Says whatever the test says a connection revealed, without reading a byte off it.
    private sealed class FixedHostNames : IConnectionHostNameResolver
    {
        private readonly string? _host;
        public FixedHostNames(string? host) => _host = host;

        public Task<string?> TryResolveAsync(
            RedirectedTcpConnection connection, TimeSpan? peekTimeout = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_host);
    }

    private static Outbound Socks5(Ipv6Support ipv6 = Ipv6Support.Auto) => new Outbound
    {
        Id = ProxyId,
        Name = "socks5",
        Kind = OutboundKind.Socks5,
        Url = "socks5://127.0.0.1:1080",
        Ipv6Support = ipv6,
    };

    private static RoutingPolicy PolicyTo(Guid outboundId, string pattern)
    {
        var policy = new RoutingPolicy { Id = PolicyId, Name = "test", OutboundId = outboundId };
        policy.Rules.Add(new RoutingRule
        {
            Id = Guid.NewGuid(),
            Matcher = HostMatcherType.IpCidr,
            Pattern = pattern,
            Order = 0,
        });
        return policy;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            RoutingPolicy policy, Outbound outbound, string? host = null,
            FakeOutboundSourceBuilder? builder = null)
        {
            Builder = builder ?? new FakeOutboundSourceBuilder(OutboundKind.Socks5);
            Registry = new OutboundRegistry(new OutboundSourceFactory(new[] { Builder }));
            Connections = new ConnectionTracker();
            Live = new LiveTcpConnectionRegistry();
            Slots = new Slot(new RoutingPolicyResolver(
                new[] { policy },
                new[] { outbound },
                new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { policy.Id } }));

            Router = new TcpConnectionRouter(
                Slots, new FixedHostNames(host), Connections, Live, Registry,
                new OutboundIpv6Capability(),
                processName: _ => "browser.exe",
                NullLoggerFactory.Instance);
        }

        public FakeOutboundSourceBuilder Builder { get; }
        public OutboundRegistry Registry { get; }
        public ConnectionTracker Connections { get; }
        public LiveTcpConnectionRegistry Live { get; }
        public Slot Slots { get; }
        public TcpConnectionRouter Router { get; }

        public void Dispose() => Registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // The relay hands the router a connection it has already accepted, so a test needs a real pair
    // of sockets — but only a pair: nothing is read from them and nothing is written.
    private sealed class AcceptedConnection : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _far;

        public AcceptedConnection(IPEndPoint originalDestination)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var near = new TcpClient();
            near.Connect((IPEndPoint)_listener.LocalEndpoint);
            _far = _listener.AcceptTcpClient();

            Connection = new RedirectedTcpConnection(
                Pid, new IPEndPoint(IPAddress.Parse("192.168.1.5"), 50000), originalDestination, near);
        }

        public RedirectedTcpConnection Connection { get; }

        public void Dispose()
        {
            Connection.Dispose();
            _far.Close();
            _listener.Stop();
        }
    }

    private static IPEndPoint Destination(string address, int port = 443)
        => new IPEndPoint(IPAddress.Parse(address), port);

    [Fact]
    public async Task ABlockedConnection_SaysSoAndNeverAsksForAWayOut()
    {
        using var f = new Fixture(PolicyTo(Outbound.BlockId, "93.184.216.34"), Socks5());
        using var accepted = new AcceptedConnection(Destination("93.184.216.34"));

        await f.Router.HandleAsync(accepted.Connection, CancellationToken.None);

        ConnectionInfo row = Assert.Single(f.Connections.History);
        Assert.Equal("blocked by rule", row.Error);
        // Nothing was built and nothing was registered as live: a blocked connection is decided and
        // finished in one step.
        Assert.Empty(f.Builder.Builds);
        Assert.Equal(0, f.Live.Count);
    }

    [Fact]
    public async Task AnIpv6DestinationWithNoName_IsRefusedRatherThanLeftToTimeOut()
    {
        // No name means no A record to fall back on, so an outbound without an IPv6 route cannot
        // serve this destination at all. Saying so at once is what lets the application retry over
        // IPv4 within a couple of hundred milliseconds instead of waiting out a connect timeout.
        using var f = new Fixture(
            PolicyTo(ProxyId, "2606:4700:4700::1111"), Socks5(Ipv6Support.Disabled), host: null);
        using var accepted = new AcceptedConnection(Destination("2606:4700:4700::1111"));

        await f.Router.HandleAsync(accepted.Connection, CancellationToken.None);

        ConnectionInfo row = Assert.Single(f.Connections.History);
        Assert.Equal("socks5", row.OutboundName);
        Assert.Contains("no IPv6 route", row.Error);
        Assert.Empty(f.Builder.Builds);
    }

    [Fact]
    public async Task AnIpv6DestinationThatCameWithAName_IsHandedToTheOutboundAnyway()
    {
        // The name is the fallback that costs nothing: the outbound resolves it on its own side and
        // is free to pick the A record. Refusing here would break a site that has both.
        using var f = new Fixture(
            PolicyTo(ProxyId, "2606:4700:4700::1111"), Socks5(Ipv6Support.Disabled), host: "example.com");
        using var accepted = new AcceptedConnection(Destination("2606:4700:4700::1111"));

        await f.Router.HandleAsync(accepted.Connection, CancellationToken.None);

        ConnectionInfo row = Assert.Single(f.Connections.History);
        Assert.Equal("example.com", row.Host);
        Assert.Equal(ProxyId, Assert.Single(f.Builder.Builds).OutboundId);
    }

    [Fact]
    public async Task AWayOutThatRefusesToOpen_LeavesTheReasonOnTheRow()
    {
        using var f = new Fixture(PolicyTo(ProxyId, "93.184.216.34"), Socks5(), host: "example.com");
        using var accepted = new AcceptedConnection(Destination("93.184.216.34"));

        await f.Router.HandleAsync(accepted.Connection, CancellationToken.None);

        ConnectionInfo row = Assert.Single(f.Connections.History);
        Assert.Contains("NotSupportedException", row.Error);
        // And it let go of everything it took: a failed connection that stayed registered would be
        // closed again by the next configuration change, on a socket that is long gone.
        Assert.Equal(0, f.Live.Count);
        Assert.Empty(f.Connections.Active);
    }

    [Fact]
    public async Task EveryConnection_IsPutOnTheListBeforeItIsRouted()
    {
        // The row appears the moment the relay accepts, and the outbound is filled in a moment
        // later — that is why the tracker has both Open and Update.
        using var f = new Fixture(PolicyTo(ProxyId, "93.184.216.34"), Socks5(), host: "example.com");
        using var accepted = new AcceptedConnection(Destination("93.184.216.34"));

        var seen = new List<string>();
        f.Connections.Opened += info => seen.Add($"open:{info.OutboundName}");
        f.Connections.Updated += info => seen.Add($"update:{info.OutboundName}");
        f.Connections.Closed += info => seen.Add($"close:{info.OutboundName}");

        await f.Router.HandleAsync(accepted.Connection, CancellationToken.None);

        Assert.Equal(new[] { "open:", "update:socks5", "close:socks5" }, seen);
        ConnectionInfo row = Assert.Single(f.Connections.History);
        Assert.Equal("browser.exe", row.ProcessName);
    }

    // The first seconds of a run: the driver is open, the tunnel a rule routes through is still
    // dialling. The connection waits there rather than being sent out direct (which would carry the
    // real address) or refused (which would be an error page a second after the switch was flipped).
    [Fact]
    public async Task AConnectionRoutedThroughATunnelThatIsStillDialling_IsHeldRatherThanSentDirect()
    {
        var tunnel = new FakeManagedProxySource();
        using var f = KeptVpnFixture(tunnel);
        using var accepted = new AcceptedConnection(Destination("93.184.216.34"));

        using var cts = new CancellationTokenSource();
        Task routing = f.Router.HandleAsync(accepted.Connection, cts.Token);

        // Long enough for several passes of the wait. Nothing has been asked of the way out: the
        // fake throws on GetConnectSourceAsync, so a connection that went through would have
        // finished with an error by now.
        await Task.Delay(600);
        Assert.False(routing.IsCompleted);
        Assert.Equal(0, tunnel.Starts);
        Assert.Empty(f.Connections.History);

        // The engine stopping (or a re-route) ends the wait, and nothing is left registered.
        cts.Cancel();
        await routing;
        Assert.Equal(0, f.Live.Count);
    }

    // ...and the moment the tunnel is up, the connection it was waiting for goes through it. The
    // fake refuses to open a tunnel, so reaching that refusal IS the proof it stopped waiting.
    [Fact]
    public async Task AHeldConnection_GoesThroughAsSoonAsTheTunnelIsUp()
    {
        var tunnel = new FakeManagedProxySource();
        using var f = KeptVpnFixture(tunnel);
        using var accepted = new AcceptedConnection(Destination("93.184.216.34"));

        Task routing = f.Router.HandleAsync(accepted.Connection, CancellationToken.None);
        await Task.Delay(300);
        Assert.False(routing.IsCompleted);

        await tunnel.StartAsync();
        await routing;

        ConnectionInfo row = Assert.Single(f.Connections.History);
        Assert.Equal("vpn", row.OutboundName);
        Assert.Contains("NotSupportedException", row.Error);
    }

    // A way out nobody keeps up is a different case: there is no supervisor to wait for, so it is
    // handed over at once and dials itself on the way through, exactly as it always has.
    [Fact]
    public async Task AnOutboundNothingKeepsUp_IsHandedOverWithoutWaiting()
    {
        var tunnel = new FakeManagedProxySource();
        using var f = KeptVpnFixture(tunnel, keepConnected: false);
        using var accepted = new AcceptedConnection(Destination("93.184.216.34"));

        await f.Router.HandleAsync(accepted.Connection, CancellationToken.None);

        ConnectionInfo row = Assert.Single(f.Connections.History);
        Assert.Contains("NotSupportedException", row.Error);
    }

    // One VPN outbound, kept up by a supervisor that is not part of this test, with a filter routing
    // at it.
    private static Fixture KeptVpnFixture(FakeManagedProxySource tunnel, bool keepConnected = true)
    {
        var outbound = new Outbound
        {
            Id = ProxyId,
            Name = "vpn",
            Kind = OutboundKind.Vpn,
            Url = "wg://tunnel.conf",
            KeepConnected = keepConnected,
        };
        return new Fixture(
            PolicyTo(ProxyId, "93.184.216.34"), outbound, host: null,
            builder: new FakeOutboundSourceBuilder(OutboundKind.Vpn, _ => tunnel));
    }
}
