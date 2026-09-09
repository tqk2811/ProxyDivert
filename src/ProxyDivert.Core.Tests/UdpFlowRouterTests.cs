using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Engine.Interfaces;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Redirect.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The UDP half of the routing path, which until it was lifted off the engine could not be reached
// by a test at all: it was two private methods on a class that only exists once WinDivert has
// loaded a driver.
//
// What is being pinned here is the rule the two methods share — a datagram that cannot be carried
// is dropped, never let out — because getting it wrong does not fail, it leaks.
public class UdpFlowRouterTests
{
    private static readonly Guid ProxyId = Guid.NewGuid();
    private static readonly Guid PolicyId = Guid.NewGuid();
    private const uint Pid = 4321;

    private sealed class Slot : IResolverSource
    {
        public Slot(RoutingPolicyResolver resolver) => Resolver = resolver;
        public RoutingPolicyResolver Resolver { get; set; }
    }

    private static Outbound Socks5(Ipv6Support ipv6 = Ipv6Support.Auto) => new Outbound
    {
        Id = ProxyId,
        Name = "socks5",
        Kind = OutboundKind.Socks5,
        Url = "socks5://127.0.0.1:1080",
        Ipv6Support = ipv6,
    };

    private static RoutingPolicy Policy(Guid outboundId, UdpMode udpMode, params RoutingRule[] rules)
    {
        var policy = new RoutingPolicy
        {
            Id = PolicyId,
            Name = "test",
            OutboundId = outboundId,
            UdpMode = udpMode,
        };
        policy.Rules.AddRange(rules);
        return policy;
    }

    private static RoutingRule MatchesEverything() => new RoutingRule
    {
        Id = Guid.NewGuid(),
        Matcher = HostMatcherType.Wildcard,
        Pattern = "*",
        Order = 0,
    };

    // A rule matching on the destination address, so a test need not teach the reverse-DNS table a
    // name for the datagram to be claimed by a policy.
    private static RoutingRule MatchesAddress(string address) => new RoutingRule
    {
        Id = Guid.NewGuid(),
        Matcher = HostMatcherType.IpCidr,
        Pattern = address,
        Order = 0,
    };

    private sealed class Fixture : IDisposable
    {
        public Fixture(RoutingPolicy policy, params Outbound[] outbounds)
        {
            Redirector = new FakeProcessRedirector();
            Forwarder = new UdpProxyForwarder(
                Redirector, NullLogger<UdpProxyForwarder>.Instance, CancellationToken.None);
            Builder = new FakeOutboundSourceBuilder(OutboundKind.Socks5);
            Registry = new OutboundRegistry(new OutboundSourceFactory(new[] { Builder }));
            Slots = new Slot(new RoutingPolicyResolver(
                new[] { policy },
                outbounds,
                new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { policy.Id } }));

            Router = new UdpFlowRouter(
                Slots, Redirector.ReverseDns, Forwarder, Registry, new OutboundIpv6Capability(),
                NullLogger<UdpFlowRouter>.Instance);
        }

        public FakeProcessRedirector Redirector { get; }
        public UdpProxyForwarder Forwarder { get; }
        public FakeOutboundSourceBuilder Builder { get; }
        public OutboundRegistry Registry { get; }
        public Slot Slots { get; }
        public UdpFlowRouter Router { get; }

        public void Dispose()
        {
            Forwarder.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Redirector.Dispose();
        }
    }

    private static RedirectedUdpDatagram Datagram(string destination, int port = 443)
        => new RedirectedUdpDatagram(
            Pid,
            new IPEndPoint(IPAddress.Parse("192.168.1.5"), 51000),
            new IPEndPoint(IPAddress.Parse(destination), port),
            new byte[] { 1, 2, 3 });

    [Fact]
    public void AFlowGoingDirect_IsLeftOutOfTheRelayEntirely()
    {
        // Nothing claims it, so the policy default applies and the datagram is the process's own
        // business. Pulling it into the relay is what broke DNS: the reply comes back to a port
        // nothing can map to the process.
        using var f = new Fixture(Policy(Outbound.DirectId, UdpMode.ThroughOutbound), Socks5());

        bool redirect = f.Router.ShouldRedirect(Pid, IPAddress.Parse("8.8.8.8"), 53, isIpv6: false);

        Assert.False(redirect);
    }

    [Fact]
    public void AFlowThatIsBlocked_StillGoesThroughTheRelay()
    {
        // Block is claimed and dropped inside the relay. Passing it through would let out the very
        // datagram the rule exists to stop.
        using var f = new Fixture(
            Policy(Outbound.BlockId, UdpMode.ThroughOutbound, MatchesAddress("8.8.8.8")), Socks5());

        Assert.True(f.Router.ShouldRedirect(Pid, IPAddress.Parse("8.8.8.8"), 53, isIpv6: false));
    }

    [Fact]
    public void ABlockedDatagram_IsDroppedWithoutAskingForAWayOut()
    {
        using var f = new Fixture(
            Policy(Outbound.BlockId, UdpMode.ThroughOutbound, MatchesAddress("8.8.8.8")), Socks5());

        byte[]? forwarded = f.Router.HandleDatagram(Datagram("8.8.8.8", 53), CancellationToken.None);

        Assert.Null(forwarded);
        Assert.Empty(f.Builder.Builds);
    }

    [Fact]
    public void ADatagramThatTurnedDirectAfterItWasClaimed_IsSentOnRatherThanLost()
    {
        // The packet path said "redirect" and a DNS answer arriving a moment later gave the flow a
        // name, which the rules now route Direct. There is no reply path left, so the honest thing
        // is to let this one out and let the sender retry.
        using var f = new Fixture(Policy(Outbound.DirectId, UdpMode.ThroughOutbound), Socks5());

        RedirectedUdpDatagram datagram = Datagram("8.8.8.8", 53);
        byte[]? forwarded = f.Router.HandleDatagram(datagram, CancellationToken.None);

        Assert.Same(datagram.Payload, forwarded);
    }

    [Fact]
    public void AnIpv6DatagramForAnOutboundWithNoIpv6Route_IsDroppedAndNotLetOut()
    {
        // A datagram carries no host name, so there is no A record to fall back to the way a TCP
        // connection has. Letting it out direct would put the machine's real address on the wire.
        using var f = new Fixture(
            Policy(ProxyId, UdpMode.ThroughOutbound, MatchesAddress("2001:4860:4860::8888")),
            Socks5(Ipv6Support.Disabled));

        byte[]? forwarded = f.Router.HandleDatagram(Datagram("2001:4860:4860::8888", 53), CancellationToken.None);

        Assert.Null(forwarded);
        Assert.Empty(f.Builder.Builds);
    }

    [Fact]
    public void ADatagramRoutedThroughAnOutbound_AsksItsOwnerForTheWayOut()
    {
        // The registry is the one owner of an instance, so this is where a tunnelled datagram gets
        // its source from — not from a source the router built for itself.
        using var f = new Fixture(
            Policy(ProxyId, UdpMode.ThroughOutbound, MatchesAddress("8.8.8.8")), Socks5());

        byte[]? forwarded = f.Router.HandleDatagram(Datagram("8.8.8.8", 53), CancellationToken.None);

        // Never handed back to the relay: it is carried, or it is dropped.
        Assert.Null(forwarded);
        Assert.Equal(ProxyId, Assert.Single(f.Builder.Builds).OutboundId);
    }

    [Fact]
    public void AnEditedConfiguration_ReachesTheNextDatagramAndNotOnlyTheNextRun()
    {
        // The router reads the table per datagram. Capturing a resolver in the constructor would
        // have made this pass anyway at first and then quietly stop being true after a save.
        using var f = new Fixture(
            Policy(ProxyId, UdpMode.ThroughOutbound, MatchesAddress("8.8.8.8")), Socks5());

        Assert.True(f.Router.ShouldRedirect(Pid, IPAddress.Parse("8.8.8.8"), 53, isIpv6: false));

        RoutingPolicy direct = Policy(Outbound.DirectId, UdpMode.ThroughOutbound, MatchesEverything());
        f.Slots.Resolver = new RoutingPolicyResolver(
            new[] { direct },
            new[] { Socks5() },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { direct.Id } });

        Assert.False(f.Router.ShouldRedirect(Pid, IPAddress.Parse("8.8.8.8"), 53, isIpv6: false));
    }
}
