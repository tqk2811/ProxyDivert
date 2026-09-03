using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.WinDivert.Logging;
using TqkLibrary.WinDivert.Redirect;

namespace ProxyDivert.Core.Engine;

// Carries the target's UDP through SOCKS5 UDP ASSOCIATE tunnels and injects the replies back into
// the process.
//
// One tunnel per (outbound, process source port). A SOCKS5 reply identifies only the remote peer,
// never the local socket it belongs to, so a shared tunnel cannot tell two process sockets talking
// to the same server apart. Giving each source port its own tunnel makes the tunnel itself the
// correlation key — the reply loop knows exactly which port to inject into.
public sealed class UdpProxyForwarder : IDisposable
{
    private readonly ProcessRedirector _redirector;
    private readonly RedirectLogger _log;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<TunnelKey, PortTunnel> _tunnels = new ConcurrentDictionary<TunnelKey, PortTunnel>();
    private volatile bool _disposed;

    public UdpProxyForwarder(ProcessRedirector redirector, RedirectLogger? logger, CancellationToken cancellationToken)
    {
        _redirector = redirector ?? throw new ArgumentNullException(nameof(redirector));
        _log = logger ?? RedirectLogger.Null;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    // Queues one datagram for delivery through `source`. Returns false when the tunnel is not
    // ready yet — the datagram is dropped, which UDP callers already tolerate and which is far
    // better than falling back to a direct send that would expose the real IP.
    public bool Send(Guid outboundId, IProxySource source, ushort clientPort, IPEndPoint destination, byte[] payload)
    {
        if (_disposed) return false;

        var key = new TunnelKey(outboundId, clientPort);
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

    private void OnReply(ushort clientPort, IPEndPoint from, byte[] payload)
    {
        try
        {
            _redirector.InjectUdpReplyToProcessAsync(clientPort, payload).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Log("UDP", $"inject reply to :{clientPort} from {from} failed: {ex.GetType().Name}: {ex.Message}");
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

        public TunnelKey(Guid outboundId, ushort clientPort)
        {
            OutboundId = outboundId;
            ClientPort = clientPort;
        }

        public bool Equals(TunnelKey other) => ClientPort == other.ClientPort && OutboundId.Equals(other.OutboundId);
        public override bool Equals(object? obj) => obj is TunnelKey k && Equals(k);
        public override int GetHashCode() => OutboundId.GetHashCode() ^ ClientPort;
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
                _owner._log.Log("UDP", $"associate :{_key.ClientPort} -> relay={tunnel.RelayEndPoint}");
            }
            catch (Exception ex)
            {
                _owner._log.Log("UDP", $"associate :{_key.ClientPort} FAILED: {ex.GetType().Name}: {ex.Message}");
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
                _owner._log.Log("UDP", $"send :{_key.ClientPort} -> {destination} failed: {ex.GetType().Name}: {ex.Message}");
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
                    _owner._log.Log("UDP", $"receive :{_key.ClientPort} tunnel error: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                _owner.OnReply(_key.ClientPort, datagram.Source, datagram.Payload);
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
