using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.Proxy.Interfaces;
using Microsoft.Extensions.Logging;
using TqkLibrary.WinDivert.Redirect;
using TqkLibrary.WinDivert.Redirect.Interfaces;

namespace ProxyDivert.Core.Engine;

// Carries the target's UDP through SOCKS5 UDP ASSOCIATE tunnels and injects the replies back into
// the process.
//
// One tunnel per (outbound, process source port, address family). A SOCKS5 reply identifies only
// the remote peer, never the local socket it belongs to, so a shared tunnel cannot tell two process
// sockets talking to the same server apart. Giving each source port its own tunnel makes the tunnel
// itself the correlation key — the reply loop knows exactly which port to inject into, and on which
// of the relay's two loopback listeners.
public sealed class UdpProxyForwarder : IDisposable
{
    private readonly IProcessRedirector _redirector;
    private readonly ILogger<UdpProxyForwarder> _logger;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<TunnelKey, PortTunnel> _tunnels = new ConcurrentDictionary<TunnelKey, PortTunnel>();
    private volatile bool _disposed;

    public UdpProxyForwarder(IProcessRedirector redirector, ILogger<UdpProxyForwarder> logger, CancellationToken cancellationToken)
    {
        _redirector = redirector ?? throw new ArgumentNullException(nameof(redirector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    // Queues one datagram for delivery through `source`. Returns false when the tunnel is not
    // ready yet — the datagram is dropped, which UDP callers already tolerate and which is far
    // better than falling back to a direct send that would expose the real IP.
    public bool Send(Guid outboundId, IProxySource source, ushort clientPort, IPEndPoint destination, byte[] payload, bool isIpv6)
    {
        if (_disposed) return false;

        var key = new TunnelKey(outboundId, clientPort, isIpv6);
        PortTunnel tunnel = _tunnels.GetOrAdd(key, k => new PortTunnel(this, source, k));
        return tunnel.Send(destination, payload);
    }

    // Drops the tunnels of an outbound the user has just edited or removed.
    public void InvalidateOutbound(Guid outboundId)
    {
        foreach (var kv in _tunnels)
        {
            if (kv.Key.OutboundId != outboundId) continue;
            if (_tunnels.TryRemove(kv.Key, out PortTunnel? tunnel)) tunnel.Dispose();
        }
    }

    private void OnReply(ushort clientPort, IPEndPoint from, byte[] payload, bool isIpv6)
    {
        try
        {
            _redirector.InjectUdpReplyToProcessAsync(clientPort, payload, isIpv6).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "injecting a reply to :{ClientPort} from {From} failed", clientPort, from);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        foreach (var kv in _tunnels) kv.Value.Dispose();
        _tunnels.Clear();
        _cts.Dispose();
    }

    private readonly struct TunnelKey : IEquatable<TunnelKey>
    {
        public Guid OutboundId { get; }
        public ushort ClientPort { get; }
        // IPv4 and IPv6 have separate port spaces, so the same port number can belong to two
        // different sockets at once. Without this the two would share a tunnel and the replies of
        // one would be injected into the other.
        public bool IsIpv6 { get; }

        public TunnelKey(Guid outboundId, ushort clientPort, bool isIpv6)
        {
            OutboundId = outboundId;
            ClientPort = clientPort;
            IsIpv6 = isIpv6;
        }

        public bool Equals(TunnelKey other)
            => ClientPort == other.ClientPort && IsIpv6 == other.IsIpv6 && OutboundId.Equals(other.OutboundId);
        public override bool Equals(object? obj) => obj is TunnelKey k && Equals(k);
        public override int GetHashCode() => OutboundId.GetHashCode() ^ ClientPort ^ (IsIpv6 ? 1 << 17 : 0);
    }

    // One UDP ASSOCIATE dedicated to a single process source port on a single outbound.
    // The association is negotiated lazily on the first datagram; datagrams that arrive while the
    // handshake is still running are dropped.
    private sealed class PortTunnel : IDisposable
    {
        private readonly UdpProxyForwarder _owner;
        private readonly IProxySource _source;
        private readonly TunnelKey _key;
        private readonly CancellationTokenSource _cts;
        private readonly Task _ready;
        private IUdpAssociateSource? _tunnel;
        private Task? _receiveLoop;

        public PortTunnel(UdpProxyForwarder owner, IProxySource source, TunnelKey key)
        {
            _owner = owner;
            _source = source;
            _key = key;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(owner._cts.Token);
            _ready = Task.Run(() => AssociateAsync(_cts.Token));
        }

        private async Task AssociateAsync(CancellationToken ct)
        {
            try
            {
                IUdpAssociateSource tunnel = await _source.GetUdpAssociateSourceAsync(Guid.NewGuid(), ct).ConfigureAwait(false);
                await tunnel.AssociateAsync(ct).ConfigureAwait(false);
                _tunnel = tunnel;
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(ct));
                _owner._logger.LogDebug("UDP associate for :{ClientPort} is up, relay={Relay}", _key.ClientPort, tunnel.RelayEndPoint);
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(ex, "UDP associate for :{ClientPort} failed", _key.ClientPort);
            }
        }

        public bool Send(IPEndPoint destination, byte[] payload)
        {
            IUdpAssociateSource? tunnel = _tunnel;
            if (tunnel is null) return false;

            try
            {
                // Fire-and-forget: awaiting here would stall the relay's receive loop.
                _ = tunnel.SendAsync(destination, payload, 0, payload.Length, _cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(ex, "sending :{ClientPort} -> {Destination} failed", _key.ClientPort, destination);
                return false;
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            IUdpAssociateSource? tunnel = _tunnel;
            while (!ct.IsCancellationRequested && tunnel != null)
            {
                UdpAssociateDatagram datagram;
                try
                {
                    datagram = await tunnel.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex)
                {
                    _owner._logger.LogWarning(ex, "the receive side of the tunnel for :{ClientPort} failed", _key.ClientPort);
                    return;
                }

                _owner.OnReply(_key.ClientPort, datagram.Source, datagram.Payload, _key.IsIpv6);
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _ready.Wait(TimeSpan.FromSeconds(1)); } catch { }
            try { _tunnel?.Dispose(); } catch { }
            try { _receiveLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _cts.Dispose();
        }
    }
}
