using System;
using System.Threading;
using System.Threading.Tasks;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Routing;
using TqkLibrary.WinDivert.Redirect.Interfaces;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// Everything one run of the engine consists of, built together and thrown away together.
/// </summary>
/// <remarks>
/// These parts used to be five nullable fields on <see cref="RedirectEngine"/> that were assigned in
/// Start and set back to null in Stop. They switch on and off as one thing, so every reader had to
/// write <c>?.</c> for a state that cannot actually happen — and the relay threads read those fields
/// without the lock that Stop clears them under, which is a real race and not only an ugly one: a
/// handler could see the tracker of the run that is ending and the forwarder of nothing.
///
/// One reference replaces the five. A handler takes it once at the top and works with a run that is
/// whole, even if Stop is publishing null a microsecond later — a connection routed by a run that is
/// on its way out is fine, because its cancellation token is what ends it.
///
/// The one thing that changes during a run is the routing table, and it is kept behind
/// <see cref="ResolverSlot"/> for the reasons written there.
/// </remarks>
internal sealed class EngineRun : IAsyncDisposable
{
    private readonly ResolverSlot _resolvers;
    private readonly CancellationTokenSource _cts;

    public EngineRun(
        IProcessRedirector redirector,
        ProcessRuleTracker tracker,
        IConnectionHostNameResolver hostNames,
        UdpProxyForwarder udpForwarder,
        TcpConnectionRouter tcp,
        UdpFlowRouter udp,
        ResolverSlot resolvers,
        CancellationTokenSource cts)
    {
        Redirector = redirector ?? throw new ArgumentNullException(nameof(redirector));
        Tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        HostNames = hostNames ?? throw new ArgumentNullException(nameof(hostNames));
        UdpForwarder = udpForwarder ?? throw new ArgumentNullException(nameof(udpForwarder));
        Tcp = tcp ?? throw new ArgumentNullException(nameof(tcp));
        Udp = udp ?? throw new ArgumentNullException(nameof(udp));
        _resolvers = resolvers ?? throw new ArgumentNullException(nameof(resolvers));
        _cts = cts ?? throw new ArgumentNullException(nameof(cts));
    }

    /// <summary>The driver handles, the loopback relay, and the packet pipelines bending onto it.</summary>
    public IProcessRedirector Redirector { get; }

    /// <summary>Which processes are in scope, matched against this run's rules.</summary>
    public ProcessRuleTracker Tracker { get; }

    /// <summary>SNI / Host header, falling back to what DNS taught this run about an address.</summary>
    public IConnectionHostNameResolver HostNames { get; }

    /// <summary>The SOCKS5 UDP ASSOCIATE tunnels this run has open.</summary>
    public UdpProxyForwarder UdpForwarder { get; }

    /// <summary>Where a redirected TCP connection goes, and how it gets there.</summary>
    public TcpConnectionRouter Tcp { get; }

    /// <summary>Whether a UDP flow is claimed at all, and where its datagrams go.</summary>
    public UdpFlowRouter Udp { get; }

    /// <summary>The routing table as it stands right now. Read once per connection, never cached.</summary>
    public RoutingPolicyResolver Resolver => _resolvers.Resolver;

    /// <summary>Publishes a freshly built routing table. Called under the engine's lock.</summary>
    public void UseResolver(RoutingPolicyResolver resolver) => _resolvers.Use(resolver);

    /// <summary>
    /// Tells everything running on this run's token to stop. Separate from disposal because the
    /// engine cancels while it still holds its lock — that part is cheap — and then waits for the
    /// teardown outside it.
    /// </summary>
    public void Cancel()
    {
        try { _cts.Cancel(); } catch { }
    }

    /// <remarks>
    /// The order is the one Stop always used and it matters end to end: the tracker first so no
    /// further pid arrives, then the UDP tunnels (up to two seconds each), then the redirector,
    /// which unloads the driver. The token source goes last of all — the forwarder was handed a
    /// token linked to it and is only finished with it once its own teardown has been awaited.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        Tracker.Dispose();
        await UdpForwarder.DisposeAsync().ConfigureAwait(false);
        Redirector.Dispose();
        _cts.Dispose();
    }
}
