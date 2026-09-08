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
public sealed class UdpProxyForwarder : IAsyncDisposable
{
    // A tunnel unused for this long is finished with. Browsers open a fresh source port for every
    // QUIC connection and every DNS query, so without an upper bound on their lifetime the table
    // grows for as long as the engine runs — thousands of entries in a few hours, each holding a
    // TCP control connection to the proxy, a UDP socket and two tasks.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(15);

    // And a ceiling for the case the sweep cannot keep up with: a burst faster than the idle
    // window. Evicting the least recently used one costs that flow its tunnel — the datagram is
    // dropped, which UDP callers tolerate — where running out of sockets costs the whole engine.
    private const int MaxTunnelsPerOutbound = 512;

    private readonly IProcessRedirector _redirector;
    private readonly ILogger<UdpProxyForwarder> _logger;
    private readonly CancellationTokenSource _cts;
    private readonly Timer _reaper;
    // Lazy, because GetOrAdd may run its factory on several threads and keep only one result. A
    // PortTunnel opens a SOCKS5 control connection and a UDP socket as it is constructed, so the
    // copies the dictionary discards would go on running with nobody holding them.
    private readonly ConcurrentDictionary<TunnelKey, Lazy<PortTunnel>> _tunnels = new ConcurrentDictionary<TunnelKey, Lazy<PortTunnel>>();
    private volatile bool _disposed;

    public UdpProxyForwarder(IProcessRedirector redirector, ILogger<UdpProxyForwarder> logger, CancellationToken cancellationToken)
    {
        _redirector = redirector ?? throw new ArgumentNullException(nameof(redirector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Re-armed at the end of each pass rather than periodic: disposing a tunnel waits on its
        // tasks, and overlapping passes would pile callbacks onto the pool.
        _reaper = new Timer(_ => Reap(), null, ReapInterval, Timeout.InfiniteTimeSpan);
    }

    // Queues one datagram for delivery through `source`. Returns false when the tunnel is not
    // ready yet — the datagram is dropped, which UDP callers already tolerate and which is far
    // better than falling back to a direct send that would expose the real IP.
    public bool Send(Guid outboundId, IProxySource source, ushort clientPort, IPEndPoint destination, byte[] payload, bool isIpv6)
    {
        if (_disposed) return false;

        var key = new TunnelKey(outboundId, clientPort, isIpv6);
        if (!_tunnels.TryGetValue(key, out Lazy<PortTunnel>? entry))
        {
            // Only when a port is new, so the cost is per flow rather than per datagram.
            EnforceCap(outboundId);
            entry = _tunnels.GetOrAdd(key, k => new Lazy<PortTunnel>(
                () => new PortTunnel(this, source, k), LazyThreadSafetyMode.ExecutionAndPublication));
        }

        PortTunnel tunnel = entry.Value;
        tunnel.Touch();
        return tunnel.Send(destination, payload);
    }

    // Closes the tunnels nothing has used lately.
    private void Reap()
    {
        try
        {
            if (_disposed) return;
            long cutoff = Environment.TickCount64 - (long)IdleTimeout.TotalMilliseconds;

            foreach (var kv in _tunnels)
            {
                // A tunnel still being built has been used by definition — the caller that built it
                // is about to send through it.
                if (!kv.Value.IsValueCreated) continue;
                if (kv.Value.Value.LastUsedTicks > cutoff) continue;
                if (!_tunnels.TryRemove(kv.Key, out Lazy<PortTunnel>? idle)) continue;

                _logger.LogTrace("closing the idle UDP tunnel for :{ClientPort}", kv.Key.ClientPort);
                DisposeInBackground(idle);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the idle-tunnel sweep failed");
        }
        finally
        {
            try { if (!_disposed) _reaper.Change(ReapInterval, Timeout.InfiniteTimeSpan); } catch { }
        }
    }

    // Makes room for one more tunnel on this outbound by dropping the one used longest ago.
    private void EnforceCap(Guid outboundId)
    {
        int count = 0;
        long oldestTicks = 0;
        TunnelKey oldestKey = default;
        bool haveOldest = false;

        foreach (var kv in _tunnels)
        {
            if (!kv.Key.OutboundId.Equals(outboundId)) continue;
            count++;
            if (!kv.Value.IsValueCreated) continue;

            long used = kv.Value.Value.LastUsedTicks;
            if (haveOldest && used >= oldestTicks) continue;
            oldestTicks = used;
            oldestKey = kv.Key;
            haveOldest = true;
        }

        if (count < MaxTunnelsPerOutbound || !haveOldest) return;
        if (!_tunnels.TryRemove(oldestKey, out Lazy<PortTunnel>? evicted)) return;

        _logger.LogDebug(
            "outbound {Outbound} is at its {Cap}-tunnel ceiling; dropping the one for :{ClientPort}",
            outboundId, MaxTunnelsPerOutbound, oldestKey.ClientPort);
        DisposeInBackground(evicted);
    }

    // Disposing waits on the tunnel's tasks, which is not something to do on the timer thread or
    // on the relay's receive path.
    private static void DisposeInBackground(Lazy<PortTunnel> tunnel)
        => _ = Task.Run(async () => await DisposeAsync(tunnel).ConfigureAwait(false));

    /// <summary>Drops the tunnels of an outbound the user has just edited or removed.</summary>
    /// <remarks>
    /// Removing them from the table is what stops traffic using them, and that happens here. The
    /// closing itself goes to the thread pool, because this is called from the engine while it
    /// holds the lock that Start, Stop and the next Save all queue behind — and each tunnel waits
    /// up to two seconds to close. Editing one outbound with a handful of tunnels open would hold
    /// that lock for long enough to be felt as the window not responding.
    /// </remarks>
    public void InvalidateOutbound(Guid outboundId)
    {
        foreach (var kv in _tunnels)
        {
            if (kv.Key.OutboundId != outboundId) continue;
            if (_tunnels.TryRemove(kv.Key, out Lazy<PortTunnel>? tunnel)) DisposeInBackground(tunnel);
        }
    }

    // Waits for a tunnel still being constructed rather than skipping it: a half-built one would
    // finish and hold its socket open with nobody left to close it.
    private static async ValueTask DisposeAsync(Lazy<PortTunnel> tunnel)
    {
        try { await tunnel.Value.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    private async Task OnReplyAsync(ushort clientPort, IPEndPoint from, byte[] payload, bool isIpv6)
    {
        try
        {
            await _redirector.InjectUdpReplyToProcessAsync(clientPort, payload, isIpv6).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "injecting a reply to :{ClientPort} from {From} failed", clientPort, from);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try { _reaper.Dispose(); } catch { }
        try { _cts.Cancel(); } catch { }
        foreach (var kv in _tunnels) await DisposeAsync(kv.Value).ConfigureAwait(false);
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
    private sealed class PortTunnel : IAsyncDisposable
    {
        private readonly UdpProxyForwarder _owner;
        private readonly IProxySource _source;
        private readonly TunnelKey _key;
        private readonly CancellationTokenSource _cts;
        private readonly Task _ready;
        private IUdpAssociateSource? _tunnel;
        private Task? _receiveLoop;
        private long _lastUsedTicks;

        public PortTunnel(UdpProxyForwarder owner, IProxySource source, TunnelKey key)
        {
            _owner = owner;
            _source = source;
            _key = key;
            _lastUsedTicks = Environment.TickCount64;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(owner._cts.Token);
            _ready = Task.Run(() => AssociateAsync(_cts.Token));
        }

        /// <summary>When a datagram last went either way through this tunnel.</summary>
        public long LastUsedTicks => Interlocked.Read(ref _lastUsedTicks);

        /// <summary>
        /// Marks the tunnel as in use. Called for both directions: a long download is silent on the
        /// way out, and reaping it because nothing was sent would cut it off mid-transfer.
        /// </summary>
        public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, Environment.TickCount64);

        private async Task AssociateAsync(CancellationToken ct)
        {
            try
            {
                // The routing decision already said this outbound carries UDP, so reaching here with
                // one that does not is a disagreement between the two — worth a message that names
                // it rather than a NotSupportedException from inside the source.
                if (_source is not IUdpCapable udp || !udp.IsSupportUdp)
                {
                    throw new NotSupportedException(
                        "This outbound was routed UDP but cannot carry it. A datagram routed through "
                        + "an outbound without UDP should have been downgraded to Block.");
                }

                IUdpAssociateSource tunnel = await udp.GetUdpAssociateSourceAsync(Guid.NewGuid(), ct).ConfigureAwait(false);
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
                // Not awaited: awaiting here would stall the relay's receive loop. But a send that
                // fails does so asynchronously almost every time — a closed socket, a proxy that
                // dropped the association — and the catch below sees only the failures that happen
                // before the first await. Those went nowhere at all: the fault was never observed,
                // and this still reported the datagram as sent.
                Task send = tunnel.SendAsync(destination, payload, 0, payload.Length, _cts.Token);
                if (!send.IsCompletedSuccessfully) WatchForFailure(send, destination);
                return true;
            }
            catch (Exception ex)
            {
                _owner._logger.LogWarning(ex, "sending :{ClientPort} -> {Destination} failed", _key.ClientPort, destination);
                return false;
            }
        }

        private void WatchForFailure(Task send, IPEndPoint destination)
            => _ = send.ContinueWith(
                (t, state) =>
                {
                    var self = (PortTunnel)state!;
                    self._owner._logger.LogWarning(
                        t.Exception, "sending :{ClientPort} -> {Destination} failed",
                        self._key.ClientPort, destination);
                },
                this, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

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

                Touch();
                await _owner.OnReplyAsync(_key.ClientPort, datagram.Source, datagram.Payload, _key.IsIpv6).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            try { await _ready.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            try { _tunnel?.Dispose(); } catch { }
            if (_receiveLoop is not null)
            {
                try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            }
            _cts.Dispose();
        }
    }
}
