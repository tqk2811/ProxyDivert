using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Engine.Interfaces;
using ProxyDivert.Core.Engine.Models;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Outbounds.Extensions;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.WinDivert.Redirect.Interfaces;
using TqkLibrary.WinDivert.Redirect.Models;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// What becomes of one redirected TCP connection between the relay accepting it and its last byte:
/// find a name for it, decide where it goes, and carry it there.
/// </summary>
/// <remarks>
/// This is the routing path, and it used to be three private methods on the engine — which is why
/// nothing tested it. It holds nothing that changes and has nothing to start or stop: it is built
/// with the run and asks <see cref="IResolverSource"/> for the routing table one connection at a
/// time, so an edit saved while a download is running reaches the next connection and not this one.
/// </remarks>
internal sealed class TcpConnectionRouter
{
    // How long a connection may stay silent before routing gives up on reading a name from it.
    // Protocols where the SERVER speaks first (SMTP, FTP, SSH) would otherwise stall here; they
    // fall back to reverse DNS or to plain IP routing.
    private static readonly TimeSpan HostPeekTimeout = TimeSpan.FromSeconds(3);

    private readonly IResolverSource _resolvers;
    private readonly IConnectionHostNameResolver _hostNames;
    private readonly ConnectionTracker _connections;
    private readonly LiveTcpConnectionRegistry _liveConnections;
    private readonly OutboundRegistry _outbounds;
    private readonly OutboundIpv6Capability _ipv6Capability;
    private readonly Func<uint, string?> _processName;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <param name="processName">
    /// What to call a pid, or null when nothing knows it. A delegate rather than the tracker
    /// itself: all this path wants is a label for the connection list, and taking the tracker would
    /// tie routing to the process machinery for the sake of one string.
    /// </param>
    public TcpConnectionRouter(
        IResolverSource resolvers,
        IConnectionHostNameResolver hostNames,
        ConnectionTracker connections,
        LiveTcpConnectionRegistry liveConnections,
        OutboundRegistry outbounds,
        OutboundIpv6Capability ipv6Capability,
        Func<uint, string?> processName,
        ILoggerFactory loggerFactory)
    {
        _resolvers = resolvers ?? throw new ArgumentNullException(nameof(resolvers));
        _hostNames = hostNames ?? throw new ArgumentNullException(nameof(hostNames));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _liveConnections = liveConnections ?? throw new ArgumentNullException(nameof(liveConnections));
        _outbounds = outbounds ?? throw new ArgumentNullException(nameof(outbounds));
        _ipv6Capability = ipv6Capability ?? throw new ArgumentNullException(nameof(ipv6Capability));
        _processName = processName ?? throw new ArgumentNullException(nameof(processName));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<TcpConnectionRouter>();
    }

    public async Task HandleAsync(RedirectedTcpConnection connection, CancellationToken ct)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));

        string processName = _processName(connection.ProcessId) ?? $"pid {connection.ProcessId}";

        var info = new ConnectionInfo(
            connection.ProcessId, processName, connection.OriginalDestination, connection.Statistics);
        _connections.Open(info);

        LiveTcpConnection? live = null;
        try
        {
            // Name first: SNI / Host header, then whatever DNS taught us about this IP.
            long nameStarted = Stopwatch.GetTimestamp();
            string? host = await _hostNames
                .TryResolveAsync(connection, HostPeekTimeout, ct).ConfigureAwait(false);
            TimeSpan nameTime = Stopwatch.GetElapsedTime(nameStarted);
            info.Host = host;

            var target = new RouteTarget(
                connection.ProcessId,
                connection.OriginalDestination.Address,
                connection.OriginalDestination.Port,
                host);

            RouteDecision decision = _resolvers.Resolver.Resolve(target);
            info.OutboundName = decision.Outbound.Name;
            info.RouteReason = decision.Reason;
            _connections.Update(info);

            _logger.LogInformation("tcp pid={Pid} {Target} -> {Decision}", connection.ProcessId, target, decision);

            if (decision.IsBlocked)
            {
                info.Error = "blocked by rule";
                return;
            }

            // From here the connection can be closed by a configuration change: the tunnel runs
            // on the registry's token, and the registry knows what would have to change.
            live = _liveConnections.Register(target, decision.Outbound, info, closeClient: connection.ClientTcp.Close, ct);
            await TunnelAsync(connection, decision.Outbound, host, info, nameTime, live.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Engine stopping, or the connection was cancelled — nothing to report, unless it was
            // the configuration that ended it: that one the user asked for and may look for.
            if (live?.ClosedForChangedRoute == true)
                _logger.LogInformation("tcp pid={Pid} -> {Destination} {Reason}", connection.ProcessId, connection.OriginalDestination, info.Error);
        }
        catch (Exception) when (live?.ClosedForChangedRoute == true)
        {
            // The socket was closed under the copy loop; whatever it threw is that closure.
            _logger.LogInformation("tcp pid={Pid} -> {Destination} {Reason}", connection.ProcessId, connection.OriginalDestination, info.Error);
        }
        catch (Exception ex)
        {
            info.Error = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogWarning(ex, "tcp pid={Pid} -> {Destination} failed", connection.ProcessId, connection.OriginalDestination);
        }
        finally
        {
            if (live != null) _liveConnections.Unregister(live);
            _connections.Close(info);
        }
    }

    private async Task TunnelAsync(
        RedirectedTcpConnection connection, Outbound outbound, string? host, ConnectionInfo info,
        TimeSpan nameTime, CancellationToken ct)
    {
        IPEndPoint destination = connection.OriginalDestination;
        bool isIpv6 = destination.Address.AddressFamily == AddressFamily.InterNetworkV6;

        // Through a proxy, hand over the HOST NAME when we have one: the proxy then resolves it on
        // its own side (remote DNS), so the destination is never leaked to the local resolver and
        // CDN answers stay correct for the proxy's location. That is also the IPv6 fallback that
        // costs nothing — a name lets an outbound without an IPv6 route pick the A record itself.
        // Going direct, use the IP the process itself chose: re-resolving could pick a different
        // server than the one the application decided on.
        bool byName = !outbound.IsDirect && !string.IsNullOrEmpty(host);

        // An IPv6 literal and no name to fall back on: there is no IPv4 address to reach this
        // destination with, so an outbound without an IPv6 route cannot serve it at all. Refusing
        // now — instead of waiting for a timeout — is what lets the application fall back to IPv4
        // on its own (Happy Eyeballs retries the A record within a couple of hundred milliseconds).
        if (isIpv6 && !byName && !_ipv6Capability.AllowsIpv6(outbound))
        {
            info.Error = "outbound has no IPv6 route and the connection carries no host name";
            _logger.LogInformation(
                "tcp pid={Pid} -> {Destination} refused: {Outbound} has no IPv6 route and the connection "
                + "carries no host name to resolve to IPv4, so the application should retry over IPv4",
                connection.ProcessId, destination, outbound.Name);
            return;
        }

        IProxySource source = (await ReadyOutboundAsync(outbound, connection, ct).ConfigureAwait(false)).Source;
        IConnectSource? tunnel = null;
        Guid tunnelId = Guid.NewGuid();
        try
        {
            tunnel = await source.GetConnectSourceAsync(tunnelId, ct).ConfigureAwait(false);

            string targetHost = byName ? host! : destination.Address.ToString();
            // UriBuilder brackets an IPv6 literal for us ("tcp://[2606:4700::1111]:443"), which is
            // what the SOCKS5/HTTP address parsers expect to see.
            var targetUri = new UriBuilder("tcp", targetHost, destination.Port).Uri;

            long connectStarted = Stopwatch.GetTimestamp();
            try
            {
                await tunnel.ConnectAsync(targetUri, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (isIpv6 && !byName && !ct.IsCancellationRequested)
            {
                // First IPv6 destination this outbound failed to reach. Nothing says the far side
                // is v4-only rather than that host being down, but assuming the cheaper of the two
                // is right: later IPv6 connections are refused immediately instead of stalling,
                // and named ones keep working because the outbound resolves them itself.
                NoteIpv6Failure(outbound, destination, ex);
                throw;
            }

            // The three legs a connection waits on, so a slow site can be blamed on the right one:
            // the handshake through the relay (a stalled pump), the wait for the client to name
            // its host (a preconnect that says nothing), or the upstream connect (the network).
            _logger.LogInformation(
                "tcp pid={Pid} -> {Target} up via {Outbound}: handshake {HandshakeMs}ms, name {NameMs}ms, connect {ConnectMs}ms",
                connection.ProcessId, targetUri.Authority, outbound.Name,
                (long)connection.CaptureToAccept.TotalMilliseconds,
                (long)nameTime.TotalMilliseconds,
                (long)Stopwatch.GetElapsedTime(connectStarted).TotalMilliseconds);

            await tunnel.ForwardAsync(
                connection.ClientStream, tunnelId, _loggerFactory,
                clientName: $"pid{connection.ProcessId}", proxyName: outbound.Name,
                cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            try { tunnel?.Dispose(); } catch { }
        }
    }

    // How often the wait below looks again. Short enough that a connection made while the tunnel
    // was still coming up starts within a blink of it being up, long enough to cost nothing.
    private static readonly TimeSpan TunnelPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The outbound's instance, once it is in a state to carry this connection.
    /// </summary>
    /// <remarks>
    /// A way out that someone else keeps up — a VPN with KeepConnected on, supervised by
    /// <c>VpnConnectionKeeper</c> — may be down for the first seconds of a run, because the tunnel
    /// is dialled only after the driver's handles are open (see the remarks on
    /// AppServices.StartEngineAsync). A connection that arrives in that window is HELD, and this is
    /// the deliberate choice among three:
    /// <list type="bullet">
    /// <item>send it out direct — never: the user asked for this traffic to be tunnelled, and the
    /// one thing worse than a slow connection is one that quietly carries the real address.</item>
    /// <item>refuse it now — the application sees the connection fail immediately, and a browser
    /// turns that into an error page a second after the switch was flipped.</item>
    /// <item>hold it until the tunnel is up (this) — which looks exactly like a destination server
    /// that is slow to answer. If the tunnel comes up first the connection proceeds and is
    /// tunnelled, which is what the user asked for; if the application gives up first, that is its
    /// call and nothing here has to guess how long is too long.</item>
    /// </list>
    /// Which timeout that is, precisely: the relay has ALREADY accepted this connection by the time
    /// the router sees it — that is how the redirect works — so the operating system's connect
    /// timeout is spent and cannot fire again. The application holds what it thinks is an
    /// established socket and is waiting for an answer, so what ends the wait on its side is its own
    /// request or read timeout, which is usually the longer of the two.
    /// The wait ends with the connection's token — the engine stopping, or a configuration change
    /// that re-routes it — so nothing is held past the run it belongs to.
    /// <para>
    /// The instance is asked for again on every pass rather than held: a tunnel that fails is
    /// thrown away by its supervisor and rebuilt, so the object this started with can be one nobody
    /// is dialling any more.
    /// </para>
    /// </remarks>
    private async Task<IOutboundInstance> ReadyOutboundAsync(
        Outbound outbound, RedirectedTcpConnection connection, CancellationToken ct)
    {
        IOutboundInstance instance = _outbounds.GetOrCreate(outbound);

        // Not supervised: nothing else is going to bring this up, so the source dials it itself on
        // the way through, exactly as it always has.
        if (!outbound.KeepConnected || instance.Tunnel is null) return instance;

        bool waited = false;
        while (instance.Tunnel is { IsRunning: false })
        {
            if (!waited)
            {
                waited = true;
                _logger.LogInformation(
                    "tcp pid={Pid} -> {Destination} is waiting for {Outbound} to come up; it will go "
                    + "through as soon as the tunnel is up, or the application will time out first",
                    connection.ProcessId, connection.OriginalDestination, outbound.Name);
            }

            await Task.Delay(TunnelPollInterval, ct).ConfigureAwait(false);
            instance = _outbounds.GetOrCreate(outbound);
        }

        return instance;
    }

    // Direct is the machine's own stack: if it had no IPv6 the process could not have opened an
    // IPv6 connection in the first place, so one unreachable destination says nothing about it.
    private void NoteIpv6Failure(Outbound outbound, IPEndPoint destination, Exception ex)
    {
        if (outbound.IsDirect) return;
        if (!_ipv6Capability.RecordIpv6Failure(outbound)) return;

        _outbounds.SetIpv6Support(outbound.Id, false);
        _logger.LogInformation(ex,
            "outbound {Outbound} marked IPv4-only after {Destination} failed. Later IPv6 destinations go "
            + "out over IPv4, by name where one is known; set Ipv6Support=Enabled to override",
            outbound.Name, destination);
    }
}
