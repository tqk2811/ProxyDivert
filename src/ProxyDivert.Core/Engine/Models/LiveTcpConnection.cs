using System;
using System.Threading;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Engine.Models;

/// <summary>
/// A redirected TCP connection the engine is tunnelling right now, with enough of the decision
/// that routed it to ask again later whether that decision still holds.
/// </summary>
/// <remarks>
/// Owns its cancellation source and nothing else: the relay owns the connection, the tunnel owns
/// the upstream. <see cref="CloseForChangedRoute"/> is the one thing this can do to the
/// connection — cancel the forwarding and close the client side — after which the relay's own
/// cleanup runs exactly as it does when the process hangs up.
/// </remarks>
public sealed class LiveTcpConnection : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly Action _closeClient;
    private int _closedForChangedRoute;

    /// <param name="closeClient">
    /// Closes the process's side of the connection. Cancelling the token stops the forwarding
    /// loop; this makes sure of it, for a copy that is blocked in a read the token does not reach.
    /// </param>
    /// <param name="engineToken">Cancelled when the engine stops; this connection's token follows it.</param>
    public LiveTcpConnection(
        RouteTarget target, Outbound outbound, ConnectionInfo info, Action closeClient, CancellationToken engineToken)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _closeClient = closeClient ?? throw new ArgumentNullException(nameof(closeClient));

        OutboundId = outbound.Id;
        OutboundName = outbound.Name;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(engineToken);
    }

    /// <summary>What the connection was routed by: process, destination, and the name it revealed.</summary>
    public RouteTarget Target { get; }

    /// <summary>The outbound it is going through.</summary>
    public Guid OutboundId { get; }

    public string OutboundName { get; }

    /// <summary>The row the connection list shows for it.</summary>
    public ConnectionInfo Info { get; }

    /// <summary>Cancelled when the engine stops or when the route changes; the tunnel runs on it.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>True once <see cref="CloseForChangedRoute"/> has run — so the handler can tell this
    /// cancellation from the engine stopping.</summary>
    public bool ClosedForChangedRoute => Volatile.Read(ref _closedForChangedRoute) != 0;

    /// <summary>
    /// Ends the connection because the configuration no longer routes it where it is going. The
    /// application sees the connection fail and reconnects; the reconnect is routed by the new
    /// configuration. Idempotent.
    /// </summary>
    public void CloseForChangedRoute(string newOutboundName)
    {
        if (Interlocked.Exchange(ref _closedForChangedRoute, 1) != 0) return;

        Info.Error = $"closed: route changed from {OutboundName} to {newOutboundName}";
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* the handler finished in the meantime; nothing left to close */ }
        try { _closeClient(); }
        catch { /* likewise: an already closed socket is the outcome wanted */ }
    }

    public void Dispose() => _cancellation.Dispose();
}
