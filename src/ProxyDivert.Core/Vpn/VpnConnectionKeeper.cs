using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;

namespace ProxyDivert.Core.Vpn;

/// <summary>
/// Keeps the VPN outbounds that are switched on connected, instead of dialling one when a request
/// happens to need it.
/// </summary>
/// <remarks>
/// A VPN outbound is a wireproxy subprocess plus a WireGuard session, and building both takes
/// seconds. Left to itself the library builds them lazily, so the cost lands on whichever request
/// is first — and again after every crash, and again after the tunnel has been idle long enough
/// for the far side to forget the session. None of that is visible as an error; it just makes one
/// page load inexplicably slow.
///
/// So a tunnel that is switched on is held there: a dead subprocess is noticed through its exit
/// event rather than at the next request, and reconnected with a growing delay so a genuinely
/// broken configuration does not become a spawn loop. Idle sessions are kept alive by
/// PersistentKeepalive, which is supplied when the provider's file has none — by the config writer
/// for a tunnel on wireproxy, and by VpnTunnelOptions for one running in this process.
///
/// Which tunnels those are is <see cref="Outbound.KeepConnected"/>, and it is the user's switch —
/// this lives for as long as the application rather than for as long as an engine run. A VPN is a
/// connection to a provider, not part of a redirection: switching WinDivert off leaves it up, so
/// the browser that was already using it does not fall off the tunnel mid-download. Switching
/// WinDivert on goes the other way and turns on whatever the rules route through a VPN — see
/// <see cref="ConnectRoutedVpns"/> — because a rule pointing at a tunnel that is down would send
/// its traffic into a connection error.
/// </remarks>
public sealed class VpnConnectionKeeper : IDisposable, IAsyncDisposable
{
    private readonly OutboundRegistry _registry;
    private readonly ILogger<VpnConnectionKeeper> _logger;
    private readonly object _lock = new object();
    private readonly Dictionary<Guid, KeptVpnTunnel> _tunnels = new Dictionary<Guid, KeptVpnTunnel>();
    private bool _disposed;

    public VpnConnectionKeeper(OutboundRegistry registry, ILogger<VpnConnectionKeeper> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
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
    /// Brings the set of kept tunnels in line with the configuration: starts the ones that are
    /// newly switched on or edited, stops the ones that are gone, disabled or switched off, and
    /// leaves the rest running untouched.
    /// </summary>
    public async Task SyncAsync(IEnumerable<Outbound> outbounds, string? wireProxyPath)
    {
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));

        // Before anything is compared or built. This runs at startup, before the engine has ever
        // applied a configuration, and the registry is where the path a VPN is built from lives: a
        // tunnel dialled first and told about the setting afterwards was built as if the box were
        // empty, and only came right once the user pressed Start.
        _registry.UseWireProxyPath(wireProxyPath);

        var wanted = new Dictionary<Guid, string>();
        var definitions = new Dictionary<Guid, Outbound>();
        foreach (Outbound outbound in outbounds)
        {
            // Whether a way out is something that can be held open is the builder's answer, asked
            // through the registry. It used to be Kind == Vpn, decided here — a rule living nowhere
            // near the builders, which would leave the next kind that keeps a session open
            // unsupervised with nothing failing to say so.
            if (!_registry.CanBeKeptConnected(outbound) || !outbound.IsEnabled || !outbound.KeepConnected)
                continue;
            // Asked of the registry rather than worked out here: what makes an outbound different
            // is one rule, and a supervisor comparing by a rule of its own is how a tunnel comes to
            // be dropped for an edit that did not touch it.
            wanted[outbound.Id] = _registry.SignatureOf(outbound);
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

                var tunnel = new KeptVpnTunnel(definitions[kv.Key], kv.Value, _registry, _logger);
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
            await tunnel.DisposeAsync().ConfigureAwait(false);
            Raise(new VpnStatus(tunnel.OutboundId, tunnel.OutboundName, VpnConnectionState.Stopped));
        }

        foreach (KeptVpnTunnel tunnel in starting)
        {
            _logger.LogInformation("vpn {Outbound} will be kept connected", tunnel.OutboundName);
            tunnel.Start();
        }
    }
    /// <summary>
    /// Switches on every VPN the configuration actually routes through — the filters' policies'
    /// outbounds — and brings the tunnels up. Returns the ones that were off until now, so the
    /// caller can write the change to the file it came from.
    /// </summary>
    /// <remarks>
    /// Called when the engine starts. Nothing is switched off here, and stopping the engine calls
    /// nothing at all: a tunnel is only ever put down by the user.
    /// </remarks>
    public async Task<IReadOnlyCollection<Guid>> ConnectRoutedVpnsAsync(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        HashSet<Guid> routed = OutboundUsage.RoutedOutboundIds(config);
        var switchedOn = new List<Guid>();
        foreach (Outbound outbound in config.Outbounds)
        {
            if (!_registry.CanBeKeptConnected(outbound) || !outbound.IsEnabled) continue;
            if (outbound.KeepConnected || !routed.Contains(outbound.Id)) continue;

            outbound.KeepConnected = true;
            switchedOn.Add(outbound.Id);
            _logger.LogInformation(
                "vpn {Outbound} is switched on: a filter routes through it", outbound.Name);
        }

        await SyncAsync(config.Outbounds, config.WireProxyPath).ConfigureAwait(false);
        return switchedOn;
    }
    private void OnTunnelStatusChanged(VpnStatus status) => Raise(status);

    private void Raise(VpnStatus status)
    {
        try { StatusChanged?.Invoke(status); }
        catch { /* a broken subscriber must not break supervision */ }
    }

    public async ValueTask DisposeAsync()
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
            await tunnel.DisposeAsync().ConfigureAwait(false);
        }
    }

    // What the container calls: ServiceProvider.Dispose refuses a singleton that only
    // offers DisposeAsync. The application itself goes through DisposeAsync.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
