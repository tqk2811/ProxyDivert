using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.Proxy.Vpn.WireProxyCli;

namespace ProxyDivert.Core.Vpn;

// One VPN outbound, held up for as long as the engine runs.
//
// The supervision loop is the whole class: bring the tunnel up, watch it, and when it goes down
// bring it back with a growing delay. It runs on its own task from the moment the engine starts,
// which is the point — a request arriving later finds a tunnel that is already up instead of
// paying for the handshake itself.
//
// What "the tunnel" is varies (a wireproxy subprocess, an in-process VpnClient driver), so the loop
// only ever sees IKeptTunnel. That also settles who reconnects: a driver that heals itself is left
// to get on with it, and this loop steps in only once the tunnel is finished for good.
internal sealed class KeptVpnTunnel : IDisposable
{
    // 1s covers a tunnel that lost a race with something; 30s is where it settles for one that is
    // down for a real reason (no network, a dead VPN server), which is often enough to reconnect
    // promptly without turning a broken config into a spawn loop.
    private static readonly TimeSpan[] BackoffSteps =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    };

    // How often the reported state is refreshed while the tunnel is up. Only the status the user
    // reads depends on this: a tunnel mending its own link shows as reconnecting without the
    // supervisor counting it as a failure.
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(15);

    // A tunnel that stayed up this long and then dropped is a fresh incident, not a continuation
    // of a crash loop, so its retries start from the short delay again.
    private static readonly TimeSpan StableFor = TimeSpan.FromSeconds(60);

    private readonly Outbound _outbound;
    private readonly OutboundSourceFactory _factory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Task? _loop;

    public KeptVpnTunnel(Outbound outbound, string signature, OutboundSourceFactory factory, ILogger logger)
    {
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        Signature = signature ?? throw new ArgumentNullException(nameof(signature));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Status = new VpnStatus(outbound.Id, outbound.Name, VpnConnectionState.Connecting);
    }

    public Guid OutboundId => _outbound.Id;

    public string OutboundName => _outbound.Name;

    /// <summary>
    /// What the outbound looked like when this tunnel was started. The keeper compares it against
    /// the current configuration to decide whether the tunnel is still the right one.
    /// </summary>
    public string Signature { get; }

    public VpnStatus Status { get; private set; }

    /// <summary>Raised on the supervision thread. Handlers must not block it.</summary>
    public event Action<VpnStatus>? StatusChanged;

    public void Start()
    {
        // LongRunning would take a dedicated thread for something that is asleep almost always;
        // the loop is await-based, so a pool thread is only borrowed at each state change.
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            string reason;
            try
            {
                SetStatus(VpnConnectionState.Connecting, null, attempt);

                IKeptTunnel tunnel = Resolve();
                await tunnel.StartAsync(ct).ConfigureAwait(false);

                DateTime upSince = DateTime.UtcNow;
                attempt = 0;
                SetStatus(VpnConnectionState.Connected, null, 0);
                _logger.LogInformation(
                    "vpn {Outbound} is up, tunnel coming out at {Endpoint}", _outbound.Name, tunnel.Endpoint);

                reason = await WatchAsync(tunnel, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                if (DateTime.UtcNow - upSince >= StableFor) attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                reason = $"{ex.GetType().Name}: {ex.Message}";
                // A source built from a config that does not work stays broken however often it is
                // started, so it is thrown away: the retry builds a new one, and a config the user
                // has meanwhile fixed is picked up without restarting the engine.
                _factory.Invalidate(_outbound.Id);
            }

            attempt++;
            TimeSpan delay = DelayFor(attempt);
            SetStatus(VpnConnectionState.Reconnecting, reason, attempt);
            _logger.LogWarning(
                "vpn {Outbound} is down ({Reason}); reconnecting in {Delay}s, attempt {Attempt}",
                _outbound.Name, reason, (int)delay.TotalSeconds, attempt);

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        SetStatus(VpnConnectionState.Stopped, null, 0);
    }

    // The factory hands back whatever the outbound describes; anything that cannot be held open
    // means the outbound changed kind under us, which the keeper handles by dropping this tunnel.
    // wireproxy is adapted from this side because WireGuardProxySource lives in TqkLibrary.Proxy
    // and knows nothing about ProxyDivert; the wrapper is stateless, so making one here is free.
    private IKeptTunnel Resolve()
    {
        IProxySource source = _factory.GetOrCreate(_outbound);
        return source switch
        {
            IKeptTunnel kept => kept,
            WireGuardProxySource wireProxy => new WireProxyKeptTunnel(wireProxy),
            _ => throw new InvalidOperationException(
                $"Outbound '{_outbound.Name}' is no longer a VPN, so there is no tunnel to keep."),
        };
    }

    /// <summary>
    /// Blocks until the tunnel is finished for good, keeping the reported state fresh meanwhile,
    /// and says why it ended.
    /// </summary>
    private async Task<string> WatchAsync(IKeptTunnel tunnel, CancellationToken ct)
    {
        Task<string> down = tunnel.WaitUntilDownAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            Task delay = Task.Delay(StatusPollInterval, ct);
            Task first = await Task.WhenAny(down, delay).ConfigureAwait(false);

            if (ReferenceEquals(first, down)) return await down.ConfigureAwait(false);
            if (delay.IsCanceled) break;

            // Not a failure the supervisor acts on: a driver re-establishing its own link shows
            // here as reconnecting and goes back to connected by itself, retry count untouched.
            bool up = tunnel.IsRunning;
            SetStatus(
                up ? VpnConnectionState.Connected : VpnConnectionState.Reconnecting,
                up ? null : "the tunnel is re-establishing itself",
                Status.RetryCount);
        }

        return "cancelled";
    }

    private static TimeSpan DelayFor(int attempt)
    {
        int index = attempt - 1;
        if (index < 0) index = 0;
        return index < BackoffSteps.Length ? BackoffSteps[index] : BackoffSteps[BackoffSteps.Length - 1];
    }

    private void SetStatus(VpnConnectionState state, string? error, int retryCount)
    {
        var status = new VpnStatus(_outbound.Id, _outbound.Name, state, error, retryCount);
        Status = status;
        try { StatusChanged?.Invoke(status); }
        catch { /* a broken subscriber must not break the supervision loop */ }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        // Give the loop a moment to notice, so nothing logs about a tunnel after the engine has
        // said it stopped — but never block the caller on it: every await in there is cancellable,
        // and a stuck one is not worth freezing the window over.
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
