using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Engine.Models;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Redirect.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// What a save does to the connections already running: the ones the new configuration would send
// elsewhere are closed, the rest are left exactly as they are.
public class LiveTcpConnectionRegistryTests
{
    private const uint Pid = 4242;
    private static readonly Guid PolicyId = Guid.NewGuid();
    private static readonly IPAddress Address = IPAddress.Parse("93.184.216.34");

    private static readonly Outbound Socks5 = new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "socks5",
        Kind = OutboundKind.Socks5,
        Url = "socks5://127.0.0.1:1080",
    };

    // One policy, one catch-all rule, routed to whichever outbound the test names.
    private static RoutingPolicyResolver ResolverRoutingTo(Guid outboundId, bool processStillTracked = true)
    {
        var policy = new RoutingPolicy { Id = PolicyId, Name = "policy", OutboundId = outboundId };
        policy.Rules.Add(new RoutingRule { Id = Guid.NewGuid(), Matcher = HostMatcherType.Wildcard, Pattern = "*" });

        var map = new Dictionary<uint, IReadOnlyList<Guid>>();
        if (processStillTracked) map[Pid] = new[] { PolicyId };

        return new RoutingPolicyResolver(new[] { policy }, new[] { Socks5 }, map);
    }

    private static RouteTarget Target() => new RouteTarget(Pid, Address, 443, "example.com");

    private static ConnectionInfo Info()
        => new ConnectionInfo(Pid, "chrome", new IPEndPoint(Address, 443), new ConnectionStatistics());

    [Fact]
    public void A_connection_still_routed_the_same_way_is_left_alone()
    {
        var registry = new LiveTcpConnectionRegistry();
        bool clientClosed = false;
        LiveTcpConnection live = registry.Register(Target(), Socks5, Info(), () => clientClosed = true, CancellationToken.None);

        int closed = registry.CloseWhereRouteChanged(ResolverRoutingTo(Socks5.Id));

        Assert.Equal(0, closed);
        Assert.False(live.Token.IsCancellationRequested);
        Assert.False(clientClosed);
        Assert.False(live.ClosedForChangedRoute);
        Assert.Null(live.Info.Error);
    }

    [Fact]
    public void A_connection_the_new_configuration_routes_elsewhere_is_closed()
    {
        var registry = new LiveTcpConnectionRegistry();
        bool clientClosed = false;
        LiveTcpConnection live = registry.Register(Target(), Socks5, Info(), () => clientClosed = true, CancellationToken.None);
        RouteDecision? reported = null;

        int closed = registry.CloseWhereRouteChanged(ResolverRoutingTo(Outbound.DirectId), (_, now) => reported = now);

        Assert.Equal(1, closed);
        Assert.True(live.Token.IsCancellationRequested);
        Assert.True(clientClosed);
        Assert.True(live.ClosedForChangedRoute);
        Assert.Equal(OutboundKind.Direct, reported!.Outbound.Kind);
        Assert.Contains("socks5", live.Info.Error);
        Assert.Contains("Direct", live.Info.Error);
    }

    [Fact]
    public void Block_is_elsewhere_too()
    {
        var registry = new LiveTcpConnectionRegistry();
        LiveTcpConnection live = registry.Register(Target(), Socks5, Info(), () => { }, CancellationToken.None);

        Assert.Equal(1, registry.CloseWhereRouteChanged(ResolverRoutingTo(Outbound.BlockId)));
        Assert.True(live.ClosedForChangedRoute);
    }

    // A process no filter describes any more falls back to Direct. Its tunnelled connections are
    // therefore closed; a connection that was going direct through the relay already is not.
    [Fact]
    public void A_process_no_policy_claims_any_more_falls_back_to_direct()
    {
        var registry = new LiveTcpConnectionRegistry();
        LiveTcpConnection tunnelled = registry.Register(Target(), Socks5, Info(), () => { }, CancellationToken.None);
        LiveTcpConnection direct = registry.Register(Target(), Outbound.CreateDirect(), Info(), () => { }, CancellationToken.None);

        int closed = registry.CloseWhereRouteChanged(ResolverRoutingTo(Socks5.Id, processStillTracked: false));

        Assert.Equal(1, closed);
        Assert.True(tunnelled.ClosedForChangedRoute);
        Assert.False(direct.ClosedForChangedRoute);
    }

    [Fact]
    public void The_engine_token_reaches_the_connection()
    {
        using var engine = new CancellationTokenSource();
        var registry = new LiveTcpConnectionRegistry();
        LiveTcpConnection live = registry.Register(Target(), Socks5, Info(), () => { }, engine.Token);

        engine.Cancel();

        Assert.True(live.Token.IsCancellationRequested);
        Assert.False(live.ClosedForChangedRoute);
    }

    [Fact]
    public void Unregister_forgets_the_connection_and_a_late_close_is_harmless()
    {
        var registry = new LiveTcpConnectionRegistry();
        LiveTcpConnection live = registry.Register(Target(), Socks5, Info(), () => { }, CancellationToken.None);
        Assert.Equal(1, registry.Count);

        registry.Unregister(live);
        registry.Unregister(live);

        Assert.Equal(0, registry.Count);
        live.CloseForChangedRoute("Direct");   // the handler finished first; must not throw
        Assert.Equal(0, registry.CloseWhereRouteChanged(ResolverRoutingTo(Outbound.DirectId)));
    }
}
