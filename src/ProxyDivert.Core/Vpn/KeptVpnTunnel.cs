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
// paying for the subprocess launch and the WireGuard handshake itself.
internal sealed class KeptVpnTunnel : IDisposable
{
    // 1s covers a wireproxy that lost a race with something; 30s is where it settles for a tunnel
    // that is down for a real reason (no network, a dead VPN server), which is often enough to
    // reconnect promptly without turning a broken config into a spawn loop.
    private static readonly TimeSpan[] BackoffSteps =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    };

    // The exit event is the fast path; this is the backstop for a process that stops being ours
    // without the event arriving.
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(15);

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

                WireGuardProxySource source = Resolve();
                await source.StartAsync(ct).ConfigureAwait(false);

                DateTime upSince = DateTime.UtcNow;
                attempt = 0;
                SetStatus(VpnConnectionState.Connected, null, 0);
                _logger.LogInformation(
                    "vpn {Outbound} is up, tunnel listening on {Endpoint}", _outbound.Name, source.Socks5Endpoint);

                reason = await WaitUntilDownAsync(source, ct).ConfigureAwait(false);
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
                // started, so it is thrown away: the retry builds a new one, and a .conf the user
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

    // The factory hands back whatever the outbound describes; anything but a WireGuard tunnel here
    // means the outbound changed kind under us, which the keeper handles by dropping this tunnel.
    private WireGuardProxySource Resolve()
    {
        IProxySource source = _factory.GetOrCreate(_outbound);
        return source as WireGuardProxySource
            ?? throw new InvalidOperationException(
                $"Outbound '{_outbound.Name}' is no longer a VPN, so there is no tunnel to keep.");
    }

    /// <summary>
    /// Blocks until the tunnel stops being usable, and says why.
    /// </summary>
    private static async Task<string> WaitUntilDownAsync(WireGuardProxySource source, CancellationToken ct)
    {
        var down = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? sender, WireProxyExitedEventArgs e) => down.TrySetResult(Describe(e));

        source.Exited += OnExited;
        try
        {
            // Subscribing cannot catch an exit that already happened, so check once before waiting.
            if (!source.IsRunning) return "the wireproxy process is gone";

            while (!ct.IsCancellationRequested)
            {
                Task delay = Task.Delay(HealthPollInterval, ct);
                Task first = await Task.WhenAny(down.Task, delay).ConfigureAwait(false);

                if (ReferenceEquals(first, down.Task)) return await down.Task.ConfigureAwait(false);
                if (delay.IsCanceled) break;
                if (!source.IsRunning) return "the wireproxy process is gone";
            }
            return "cancelled";
        }
        finally
        {
            source.Exited -= OnExited;
        }
    }

    private static string Describe(WireProxyExitedEventArgs e)
    {
        string message = $"wireproxy exited with code {e.ExitCode}";
        // stderr can be a whole startup transcript; the last line is the one that says what broke.
        string[] lines = e.StandardError.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? message : $"{message}: {lines[lines.Length - 1].Trim()}";
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
