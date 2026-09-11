using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Engine.Extensions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn;

namespace ProxyDivert.Core.Hosting;

/// <summary>
/// Redirection as an application runs it: the engine, the process table it reads and the VPN tunnels
/// it routes through, brought up, reconfigured and taken down in the one order that works — whichever
/// host is asking.
/// </summary>
/// <remarks>
/// This used to live in the window's composition root, and the command line kept a copy of the parts
/// it remembered: the order a start goes in, the process table that must be collecting before the
/// engine reads it, the tunnels that are switched on before the snapshot and dialled after the
/// driver. A host now hands this a configuration and says start, apply or stop; what those mean is
/// decided here, once.
///
/// What stays with a host is what differs between hosts: which configuration is being edited, where
/// the trace goes, and whether there is a file at all.
///
/// Ownership: the engine, the table and the keeper belong to the container that built them. This
/// starts and stops them; it never disposes them.
///
/// Threading: every method may be called from any thread. What must see the caller's configuration
/// as it is right now — switching tunnels on, taking the snapshot — happens before the method
/// returns, on the caller's thread; everything else runs on the thread pool, one piece of work after
/// another, in the order it was asked for. Applying a configuration re-reads every redirected process
/// and every open connection; starting opens driver handles; stopping waits for pumps and tunnels to
/// unwind. None of it belongs on a thread that paints a window, and none of it may overlap with the
/// rest.
/// </remarks>
public sealed class ProxyDivertSession : IDisposable, IAsyncDisposable
{
    private readonly ILogger<ProxyDivertSession> _logger;
    private readonly ConfigStore? _store;

    private readonly object _workLock = new object();
    private Task _lastWork = Task.CompletedTask;

    // ProcessInventory.Start throws on a second call, and the engine may be started and stopped any
    // number of times in one session, so the first start is remembered here. Only ever read on the
    // work queue, which runs one thing at a time, so no lock is needed.
    private bool _processesStarted;

    private volatile bool _disposed;

    /// <param name="store">
    /// Where a configuration is written when it changes on the way through — a save, or a start that
    /// switched a tunnel on. Null for a host with no file, which is the command line: its
    /// configuration comes from its arguments and ends with it.
    /// </param>
    public ProxyDivertSession(
        RedirectEngine engine,
        ProcessInventory processes,
        VpnConnectionKeeper vpn,
        ILogger<ProxyDivertSession> logger,
        ConfigStore? store = null)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Processes = processes ?? throw new ArgumentNullException(nameof(processes));
        Vpn = vpn ?? throw new ArgumentNullException(nameof(vpn));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store;
    }

    public RedirectEngine Engine { get; }

    /// <summary>
    /// Every process running on this machine, with its path, its arguments and its parent.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT collecting until redirection is first switched on. Only the engine and the
    /// rule tracker read this table, so sweeping the machine, enabling SeDebugPrivilege and
    /// subscribing to process events while the user is merely editing a rule is work nobody asked
    /// for. It starts on the way into <see cref="StartAsync"/>, where the sweep it costs is a fraction
    /// of opening the driver — and it is kept running after a stop, because a user who switches
    /// redirection off and on again should not pay for the sweep twice.
    /// </remarks>
    public ProcessInventory Processes { get; }

    /// <summary>
    /// The VPN tunnels, which are deliberately not part of an engine run: the user switches one on and
    /// it stays on across a start and a stop, because it is a session with their VPN provider rather
    /// than a piece of the redirection. Switching redirection ON is the one thing here that touches
    /// them unasked — see <see cref="StartAsync"/>.
    /// </summary>
    public VpnConnectionKeeper Vpn { get; }

    public bool IsRunning => Engine.IsRunning;

    /// <summary>Starts redirecting, on a snapshot of <paramref name="config"/>.</summary>
    /// <remarks>
    /// The one door through which everything the engine needs comes up, and in order: which VPNs a
    /// filter routes through is decided and written down, the process table starts collecting, the
    /// driver opens, and only THEN are the tunnels dialled.
    /// <para>
    /// The switches are flicked on <paramref name="config"/> itself, before the snapshot, because the
    /// snapshot the engine runs on has to carry them — the router reads "is this outbound kept up" off
    /// the outbound it is handed — and because the caller should see them flicked: a window shows the
    /// tunnel as switched on.
    /// </para>
    /// <para>
    /// The dialling comes last, and the order is the whole of it. Opening the driver takes a
    /// machine-wide WinDivert handle, and every packet on the machine then goes through this process —
    /// including the IKE/ESP datagrams of a VPN handshake that happens to be in flight. A handshake
    /// caught by that does not fail: it stalls, silently, until the driver's 90-second dial timeout,
    /// and only the retry after it connects. Measured over 12 dials on 8 logs, every dial that began
    /// within ~1s of the driver opening stalled the full 90 seconds, and every dial that did not
    /// connected in 4–7. Dialling behind an engine that is already up is the case that has always
    /// worked — it is what pressing Connect by hand does.
    /// </para>
    /// <para>
    /// What this costs is stated where it is paid: for the first few seconds of a run, a rule pointing
    /// at a VPN has nowhere to send its traffic yet, and those connections are held until the tunnel
    /// is up rather than quietly sent out direct — see TcpConnectionRouter. To the application that
    /// looks like a server slow to answer, and the alternative — leaking the real address for a
    /// connection the user asked to be tunnelled — is not one.
    /// </para>
    /// </remarks>
    public Task StartAsync(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        // A start queued behind the disposal would open driver handles nobody is left to close.
        ObjectDisposedException.ThrowIf(_disposed, this);

        IReadOnlyCollection<Guid> switchedOn = Vpn.SwitchOnRoutedVpns(config);
        AppConfig snapshot = ConfigStore.Clone(config);

        return Enqueue(async () =>
        {
            if (switchedOn.Count > 0) _store?.Save(snapshot);

            // Before the driver, not after: the tracker attaches the processes that are already
            // running the moment the engine starts, and it reads them from this table — which has to
            // be following whatever this run's detection mode listens to.
            ConfigureDetection(snapshot);
            EnsureProcessesStarted();

            await Engine.StartAsync(snapshot).ConfigureAwait(false);

            // Last, and inside the same piece of queued work, so nothing can slip between the two:
            // see the remarks above. Sync only starts the supervision loops, so this returns while
            // the tunnels are still coming up.
            await Vpn.SyncAsync(snapshot.Outbounds, snapshot.WireProxyPath).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Stops redirecting. The VPN tunnels stay up: the user switched them on, and nothing here has
    /// been asked to end their session with the provider.
    /// </summary>
    public Task StopAsync() => Enqueue(Engine.StopAsync);

    /// <summary>
    /// Writes <paramref name="config"/> to the file and hands it to everything that runs on it — the
    /// tunnels always, the engine when it is running — in one step, so the three never drift apart.
    /// </summary>
    /// <remarks>
    /// The tunnels whether or not anything is being redirected: a VPN edited or switched off has to
    /// reach its tunnel, and the tunnels do not belong to a run.
    /// <para>
    /// A failure is logged and then handed back through the task rather than swallowed. A caller that
    /// only wants the work done may ignore the task; one whose next step depends on the configuration
    /// being in force — resuming a process that was launched frozen so that it could be caught — has
    /// to be able to tell that it is not.
    /// </para>
    /// </remarks>
    public Task ApplyAsync(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        AppConfig snapshot = ConfigStore.Clone(config);
        return Enqueue(async () =>
        {
            try
            {
                _store?.Save(snapshot);
                await Vpn.SyncAsync(snapshot.Outbounds, snapshot.WireProxyPath).ConfigureAwait(false);
                if (Engine.IsRunning) await Engine.ApplyConfigAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "saving and applying the configuration failed");
                throw;
            }
        });
    }

    /// <summary>
    /// Switches one VPN's tunnel on or off, writes that down, and brings the tunnels in line.
    /// <paramref name="outbound"/> is one of <paramref name="config"/>'s own outbounds.
    /// </summary>
    /// <remarks>
    /// The engine is not told: which tunnels are up changes nothing about how a connection is
    /// routed, and re-applying the configuration would close live connections for a setting that
    /// does not concern them.
    /// </remarks>
    public Task SetVpnConnectedAsync(AppConfig config, Outbound outbound, bool connected)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));

        outbound.KeepConnected = connected;
        AppConfig snapshot = ConfigStore.Clone(config);
        return Enqueue(async () =>
        {
            try
            {
                _store?.Save(snapshot);
                await Vpn.SyncAsync(snapshot.Outbounds, snapshot.WireProxyPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "switching the vpn {Outbound} {State} failed",
                    outbound.Name, connected ? "on" : "off");
                throw;
            }
        });
    }

    /// <summary>
    /// Points the process table at whatever <paramref name="config"/>'s detection mode listens to, at
    /// once rather than at the next start.
    /// </summary>
    /// <remarks>
    /// Queued like everything else, because the same swap happens on the way into every start: a
    /// setting changed while a start is still running would otherwise have two threads tearing down
    /// and subscribing event sources on one table.
    /// </remarks>
    public Task UseDetectionAsync(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        // Read now, on the caller's thread: the two settings as they are when the user picked them,
        // not as they are whenever the queue gets round to it.
        ProcessDetectionMode mode = config.ProcessDetection;
        ProcessEventSourceKind source = config.ProcessEventSource;
        return Enqueue(() =>
        {
            mode.Strategy().ConfigureInventory(Processes, source);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Completes once everything queued so far — saves, starts, stops — has run. For code that must
    /// see the outcome of work it did not await, and for shutdown.
    /// </summary>
    public Task WhenIdleAsync()
    {
        lock (_workLock) return _lastWork;
    }

    private void ConfigureDetection(AppConfig config)
        => config.ProcessDetection.Strategy().ConfigureInventory(Processes, config.ProcessEventSource);

    private void EnsureProcessesStarted()
    {
        if (_processesStarted) return;
        _processesStarted = true;
        Processes.Start();
    }

    // Chains the work behind whatever is already queued. The task handed back carries the work's own
    // outcome, exception included; the chain itself never faults, so the next piece of work runs
    // whatever happened to the previous one.
    private Task Enqueue(Func<Task> work)
    {
        lock (_workLock)
        {
            // Unwrapped, or the queue would move on the moment the work STARTED and a save could
            // overtake the engine still applying the one before it.
            Task next = _lastWork
                .ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
            _lastWork = next.ContinueWith(_ => { }, TaskScheduler.Default);
            return next;
        }
    }

    /// <summary>Takes redirection down with the session that brought it up.</summary>
    /// <remarks>
    /// Stopped, not disposed: the engine and the table belong to the container, which disposes them
    /// after this. Safe to call twice — the host does, and so does the container.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        lock (_workLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // A save made a moment before the host closed must still reach the disk, and a start in
        // progress must finish before the engine is stopped under it. Bounded, because a host that
        // is going away cannot wait on a tunnel that will not answer.
        try { await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { _logger.LogWarning("queued work did not finish within 10s of shutdown"); }

        await Engine.StopAsync().ConfigureAwait(false);
    }

    // What the container calls: ServiceProvider.Dispose refuses a singleton that offers DisposeAsync
    // alone. The hosts themselves go through DisposeAsync.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
