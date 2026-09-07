using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ProxyDivert.Core.Engine.Models;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// The TCP connections the engine is tunnelling at this moment, kept so a configuration change can
/// be applied to them and not only to the connections that come after it.
/// </summary>
/// <remarks>
/// A connection is registered once it has been routed and unregistered when its handler ends,
/// whatever the reason. Everything in between is what <see cref="CloseWhereRouteChanged"/> looks
/// at: each connection is resolved again against the new configuration, and the ones it would now
/// send somewhere else are closed. The ones it would route the same way are left alone — a download
/// in progress does not restart because an unrelated setting was saved. The registry owns the
/// entries; whoever registers must unregister.
/// </remarks>
public sealed class LiveTcpConnectionRegistry
{
    private readonly ConcurrentDictionary<LiveTcpConnection, byte> _connections = new ConcurrentDictionary<LiveTcpConnection, byte>();

    public int Count => _connections.Count;

    public IReadOnlyCollection<LiveTcpConnection> Snapshot() => _connections.Keys.ToList();

    public LiveTcpConnection Register(
        RouteTarget target, Outbound outbound, ConnectionInfo info, Action closeClient, CancellationToken engineToken)
    {
        var connection = new LiveTcpConnection(target, outbound, info, closeClient, engineToken);
        _connections[connection] = 0;
        return connection;
    }

    /// <summary>Forgets the connection and releases what it holds. Safe to call twice.</summary>
    public void Unregister(LiveTcpConnection connection)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        if (_connections.TryRemove(connection, out _)) connection.Dispose();
    }

    /// <summary>
    /// Asks <paramref name="resolver"/> about every live connection and closes those it now routes
    /// through a different outbound — Block and "no policy claims it any more" included. Returns
    /// how many were closed. <paramref name="onClosed"/> sees each one with its new decision.
    /// </summary>
    public int CloseWhereRouteChanged(RoutingPolicyResolver resolver, Action<LiveTcpConnection, RouteDecision>? onClosed = null)
    {
        if (resolver is null) throw new ArgumentNullException(nameof(resolver));

        int closed = 0;
        foreach (LiveTcpConnection connection in _connections.Keys)
        {
            RouteDecision now = resolver.Resolve(connection.Target);
            if (now.Outbound.Id == connection.OutboundId) continue;

            connection.CloseForChangedRoute(now.Outbound.Name);
            onClosed?.Invoke(connection, now);
            closed++;
        }
        return closed;
    }
}
