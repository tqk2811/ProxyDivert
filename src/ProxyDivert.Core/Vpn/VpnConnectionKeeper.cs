using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;

namespace ProxyDivert.Core.Vpn;

/// <summary>
/// Keeps every enabled VPN outbound connected for as long as the engine runs, instead of dialling
/// one when a request happens to need it.
/// </summary>
/// <remarks>
/// A VPN outbound is a wireproxy subprocess plus a WireGuard session, and building both takes
/// seconds. Left to itself the library builds them lazily, so the cost lands on whichever request
/// is first — and again after every crash, and again after the tunnel has been idle long enough
/// for the far side to forget the session. None of that is visible as an error; it just makes one
/// page load inexplicably slow.
///
/// So the tunnels come up when the engine does and are held there: a dead subprocess is noticed
/// through its exit event rather than at the next request, and reconnected with a growing delay so
/// a genuinely broken configuration does not become a spawn loop. Idle sessions are kept alive by
/// PersistentKeepalive, which the config writer adds when the provider's file has none.
///
/// Only enabled VPN outbounds are kept, whether or not a rule currently points at one: rules are
/// edited far more often than outbounds, and a tunnel that has to warm up the moment the user
/// repoints a rule at it would defeat the purpose.
/// </remarks>
public sealed class VpnConnectionKeeper : IDisposable
{
    private readonly OutboundSourceFactory _factory;
    private readonly ILogger<VpnConnectionKeeper> _logger;
    private readonly object _lock = new object();
    private readonly Dictionary<Guid, KeptVpnTunnel> _tunnels = new Dictionary<Guid, KeptVpnTunnel>();
    private bool _disposed;

    public VpnConnectionKeeper(OutboundSourceFactory factory, ILogger<VpnConnectionKeeper> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Raised whenever a tunnel changes state, on the supervision thread rather than the caller's.
    /// A UI handler has to marshal — and must do so asynchronously, because the thread that edits
    /// the configuration is the same one that would be waiting on a synchronous marshal.
    /// </summary>
    public event Action<VpnStatus>? StatusChanged;

    public IReadOnlyList<VpnStatus> Statuses
    {
        get { lock (_lock) return _tunnels.Values.Select(t => t.Status).ToArray(); }
    }

    /// <summary>The tunnel state of one outbound, or null when it is not being kept.</summary>
    public VpnStatus? StatusOf(Guid outboundId)
    {
        lock (_lock)
            return _tunnels.TryGetValue(outboundId, out KeptVpnTunnel? tunnel) ? tunnel.Status : null;
    }

    /// <summary>
    /// Brings the set of kept tunnels in line with the configuration: starts the ones that are new
    /// or edited, stops the ones that are gone or disabled, and leaves the rest running untouched.
    /// </summary>
    public void Sync(IEnumerable<Outbound> outbounds, string? wireProxyPath)
    {
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));

        var wanted = new Dictionary<Guid, string>();
        var definitions = new Dictionary<Guid, Outbound>();
        foreach (Outbound outbound in outbounds)
        {
            if (outbound.Kind != OutboundKind.Vpn || !outbound.IsEnabled) continue;
            wanted[outbound.Id] = OutboundSignature.Of(outbound, wireProxyPath);
            definitions[outbound.Id] = outbound;
        }

        var stopping = new List<KeptVpnTunnel>();
        var starting = new List<KeptVpnTunnel>();

        lock (_lock)
        {
            if (_disposed) return;

            foreach (Guid id in _tunnels.Keys.ToArray())
            {
                KeptVpnTunnel tunnel = _tunnels[id];
                // Same settings as when it was started: leave it alone. This is the whole point of
                // comparing signatures — an unrelated edit elsewhere in the window must not drop a
                // tunnel that is up.
                if (wanted.TryGetValue(id, out string? signature)
                    && string.Equals(tunnel.Signature, signature, StringComparison.Ordinal))
                {
                    continue;
                }

                _tunnels.Remove(id);
                tunnel.StatusChanged -= OnTunnelStatusChanged;
                stopping.Add(tunnel);
            }

            foreach (var kv in wanted)
            {
                if (_tunnels.ContainsKey(kv.Key)) continue;

                var tunnel = new KeptVpnTunnel(definitions[kv.Key], kv.Value, _factory, _logger);
                tunnel.StatusChanged += OnTunnelStatusChanged;
                _tunnels[kv.Key] = tunnel;
                starting.Add(tunnel);
            }
        }

        // Outside the lock: disposing waits for a supervision loop to unwind, and starting one
        // raises a status change straight away. Neither should happen with the lock held, or a UI
        // handler reading Statuses from its own thread would be blocked behind them.
        foreach (KeptVpnTunnel tunnel in stopping)
        {
            _logger.LogInformation("vpn {Outbound} is no longer kept connected", tunnel.OutboundName);
            tunnel.Dispose();
            Raise(new VpnStatus(tunnel.OutboundId, tunnel.OutboundName, VpnConnectionState.Stopped));
        }

        foreach (KeptVpnTunnel tunnel in starting)
        {
            _logger.LogInformation("vpn {Outbound} will be kept connected", tunnel.OutboundName);
            tunnel.Start();
        }
    }

    private void OnTunnelStatusChanged(VpnStatus status) => Raise(status);

    private void Raise(VpnStatus status)
    {
        try { StatusChanged?.Invoke(status); }
        catch { /* a broken subscriber must not break supervision */ }
    }

    public void Dispose()
    {
        List<KeptVpnTunnel> tunnels;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            tunnels = _tunnels.Values.ToList();
            _tunnels.Clear();
        }

        foreach (KeptVpnTunnel tunnel in tunnels)
        {
            tunnel.StatusChanged -= OnTunnelStatusChanged;
            tunnel.Dispose();
        }
    }
}
