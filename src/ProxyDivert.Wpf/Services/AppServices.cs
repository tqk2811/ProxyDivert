using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.DependencyInjection;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Logging;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn;

namespace ProxyDivert.Wpf.Services;

/// <summary>
/// Composition root: builds the container the window runs on and hands the view models the few
/// long-lived objects they share.
/// </summary>
/// <remarks>
/// The container exists because the libraries below ask for one — they register their services
/// through AddWinDivert*, and hand-wiring that graph would mean this class knowing about every
/// factory in them. It stays a thin facade so a view model still asks for
/// <see cref="Engine"/> rather than resolving services itself.
/// </remarks>
public sealed class AppServices : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public ConfigStore ConfigStore { get; }

    /// <summary>
    /// The configuration as the window shows it. View models edit this instance freely; nothing
    /// reaches the engine until <see cref="SaveAndApply"/>, which writes the file and hands the
    /// engine a snapshot of its own (<see cref="ConfigStore.Clone"/>). Three layers, then: this
    /// one, the engine's, and the file — an edit in progress is never half-applied, and the engine
    /// never enumerates a list the grid is adding to.
    /// </summary>
    public AppConfig Config { get; private set; }

    public RedirectEngine Engine { get; }

    /// <summary>
    /// The VPN tunnels, which are deliberately not part of an engine run: the user switches one on
    /// and it stays on across a Start and a Stop, because it is a session with their VPN provider
    /// rather than a piece of the redirection. Switching redirection ON is the one thing that
    /// touches them — see <see cref="StartEngineAsync"/>.
    /// </summary>
    public VpnConnectionKeeper Vpn { get; }

    /// <summary>
    /// What the Test button on the Outbounds tab asks. It builds a way out of its own rather than
    /// borrowing the running one, so testing a VPN cannot disturb the tunnel that is up.
    /// </summary>
    public OutboundTester OutboundTester { get; }

    /// <summary>
    /// Every process running on this machine, with its path, its arguments and its parent.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT collecting until redirection is switched on. Nothing the window shows reads
    /// this table — only the engine and the rule tracker do — so sweeping the machine, enabling
    /// SeDebugPrivilege and subscribing to process events while the user is merely editing a rule
    /// is work nobody asked for, done before the window has painted. It starts on the way into
    /// <see cref="StartEngineAsync"/> instead, where the sweep it costs is a fraction of opening
    /// the driver.
    /// </remarks>
    public ProcessInventory Processes { get; }

    /// <summary>
    /// Every log line, from the packet path up. Unlike before, this lives as long as the
    /// application rather than as long as one engine run, so the pane keeps what happened before
    /// the last Start.
    /// </summary>
    public InMemoryLogStore Logs { get; }

    private readonly AppLoggerProvider _loggerProvider;
    private readonly ILogger<AppServices> _logger;

    // Everything that takes the engine's lock — a save, a start, a stop — runs off the window's
    // thread, and one after another in the order it was asked for. Applying a configuration
    // re-reads every redirected process and every open connection; starting opens driver handles
    // and WMI; stopping waits for pumps and tunnels to unwind. None of it belongs on the thread
    // that paints the window, and none of it may overlap with the rest.
    private readonly object _workLock = new object();
    private Task _lastWork = Task.CompletedTask;

    // True while auto-save is on. The path itself is derived from the clock rather than stored,
    // because it names an HOUR: everything logged between 14:00 and 15:00 belongs in one file, no
    // matter how many times the engine is started or the settings are saved in between.
    private bool _autoSaveLog;

    // Rolls the file over when the hour turns. Nothing else notices the clock, so without this a
    // long-running session would keep writing to the hour it happened to start in.
    private readonly Timer _logRollTimer;

    // ProcessInventory.Start throws on a second call, and the engine may be started and stopped any
    // number of times in one session, so the first start is remembered here. Only ever read on the
    // work queue, which is single-threaded, so no lock is needed.
    private bool _processesStarted;

    /// <summary>
    /// Where the trace actually goes: this hour's file when auto-save is on, otherwise the path the
    /// user pinned, otherwise nowhere.
    /// </summary>
    public string? EffectiveLogPath
        => _autoSaveLog
            ? BuildAutoLogPath()
            : (string.IsNullOrWhiteSpace(Config.DiagnosticLogPath) ? null : Config.DiagnosticLogPath);

    /// <summary>
    /// This hour's <c>Logs\yyyyMMdd-HH.log</c> beside the executable. One file per hour, appended
    /// to: a run that spans 14:59 to 15:01 leaves two files, and two runs inside the same hour
    /// share one instead of the second one hiding the first.
    /// </summary>
    public static string BuildAutoLogPath()
        => Path.Combine(
            AppContext.BaseDirectory, "Logs",
            FormattableString.Invariant($"{DateTime.Now:yyyyMMdd-HH}.log"));

    public AppServices(string? configPath = null)
    {
        // The config decides where the trace file goes, so it has to be read before the container
        // that carries the logging is built.
        ConfigStore = new ConfigStore(configPath);
        Config = ConfigStore.Load();
        _autoSaveLog = Config.AutoSaveLog;

        _provider = new ServiceCollection()
            .AddProxyDivert(EffectiveLogPath)
            .BuildServiceProvider();

        Logs = _provider.GetRequiredService<InMemoryLogStore>();
        _loggerProvider = _provider.GetRequiredService<AppLoggerProvider>();
        _logger = _provider.GetRequiredService<ILoggerFactory>().CreateLogger<AppServices>();
        Engine = _provider.GetRequiredService<RedirectEngine>();
        Vpn = _provider.GetRequiredService<VpnConnectionKeeper>();
        OutboundTester = _provider.GetRequiredService<OutboundTester>();

        // Resolved but not started — see the remarks on the property. Choosing the event source is
        // free while it is stopped (it only records the choice), and doing it here means the table
        // is already configured whenever the engine does start it.
        Processes = _provider.GetRequiredService<ProcessInventory>();
        Processes.UseEventSource(
            Config.ProcessDetection == ProcessDetectionMode.ProcessEvents ? Config.ProcessEventSource : null);

        // Checked every minute rather than scheduled for the exact turn of the hour: SetFilePath
        // is a no-op when the path has not changed, so the cost of asking is nothing and there is
        // no wake-up to get wrong across sleep, a clock change or a daylight-saving jump.
        _logRollTimer = new Timer(_ => RollLogFileIfNeeded(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void RollLogFileIfNeeded()
    {
        try { _loggerProvider.SetFilePath(EffectiveLogPath); }
        catch { /* the trace file is never worth taking the application down for */ }
    }

    public void Save() => ConfigStore.Save(Config);

    /// <summary>
    /// Turns the trace file on or off, and starts writing at once rather than at the next Save —
    /// the switch is flicked precisely because the next few seconds are the interesting ones.
    /// Turning it on again inside the same hour appends to that hour's file, so two attempts at
    /// reproducing something end up in one readable sequence instead of overwriting each other.
    /// </summary>
    public void SetAutoSaveLog(bool enabled)
    {
        Config.AutoSaveLog = enabled;
        _autoSaveLog = enabled;
        Save();
        _loggerProvider.SetFilePath(EffectiveLogPath);
    }

    /// <summary>
    /// Persists the configuration and pushes it to the running engine, in one step — the two must
    /// not drift apart. The snapshot is taken here, on the caller's thread, while the grids are
    /// quiet; the file write and the engine's re-evaluation of every process and connection run on
    /// the thread pool. The returned task completes when the engine has taken the configuration;
    /// callers that only want it done may ignore it, a failure is logged either way.
    /// </summary>
    public Task SaveAndApply()
    {
        AppConfig snapshot = ConfigStore.Clone(Config);
        // The log path is the one setting the engine does not own, because logging is set up before
        // the engine exists. Applying it here is what makes it take effect without a restart.
        _loggerProvider.SetFilePath(EffectiveLogPath);

        return Enqueue(async () =>
        {
            try
            {
                ConfigStore.Save(snapshot);
                // Whether or not anything is being redirected: a VPN edited or switched off on the
                // Outbounds tab has to reach its tunnel, and the tunnels do not belong to the run.
                await Vpn.SyncAsync(snapshot.Outbounds, snapshot.WireProxyPath).ConfigureAwait(false);
                if (Engine.IsRunning) await Engine.ApplyConfigAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "saving and applying the configuration failed");
            }
        });
    }

    /// <summary>Starts the engine on a snapshot of the configuration, off the caller's thread.</summary>
    /// <remarks>
    /// This is the one door through which everything the engine needs comes up, and in order: which
    /// VPNs a filter routes through is decided and written down, the process table starts
    /// collecting, the driver opens, and only THEN are the tunnels dialled. Nothing above happens
    /// while redirection is off, which is why opening the window costs a file read and nothing else.
    /// <para>
    /// The dialling comes last, and the order is the whole of it. Opening the driver takes a
    /// machine-wide WinDivert handle, and every packet on the machine then goes through this
    /// process — including the IKE/ESP datagrams of a VPN handshake that happens to be in flight.
    /// A handshake caught by that does not fail: it stalls, silently, until the driver's 90-second
    /// dial timeout, and only the retry after it connects. Measured over 12 dials on 8 logs, every
    /// dial that began within ~1s of the driver opening stalled the full 90 seconds, and every dial
    /// that did not connected in 4–7. Dialling behind an engine that is already up is the case that
    /// has always worked — it is what pressing Connect by hand does.
    /// </para>
    /// <para>
    /// What this costs is stated where it is paid: for the first few seconds of a run, a rule
    /// pointing at a VPN has nowhere to send its traffic, and those connections are refused rather
    /// than quietly sent out direct — see TcpConnectionRouter. That is the same thing the
    /// application would see if the site itself were unreachable, and the alternative — leaking the
    /// real address for a connection the user asked to be tunnelled — is not one.
    /// </para>
    /// </remarks>
    public async Task StartEngineAsync()
    {
        // On the caller's thread, and before the snapshot: this only flicks switches, and the
        // snapshot the engine runs on has to carry them — the router reads "is this outbound kept
        // up" off the outbound it is handed. Deliberately not ConfigureAwait(false) anywhere in
        // this method: the grids belong to this thread, the same as everywhere else in this class.
        IReadOnlyCollection<Guid> switchedOn = Vpn.SwitchOnRoutedVpns(Config);
        AppConfig snapshot = ConfigStore.Clone(Config);
        await Enqueue(async () =>
        {
            if (switchedOn.Count > 0) ConfigStore.Save(snapshot);
            // Before the driver, not after: the tracker attaches the processes that are already
            // running the moment the engine starts, and it reads them from this table.
            EnsureProcessesStarted();
            await Engine.StartAsync(snapshot).ConfigureAwait(false);
            // Last, and inside the same piece of queued work, so nothing can slip between the two:
            // see the remarks above. Sync only starts the supervision loops, so this returns while
            // the tunnels are still coming up.
            await Vpn.SyncAsync(snapshot.Outbounds, snapshot.WireProxyPath).ConfigureAwait(false);
        });
    }

    // Kept running once started rather than stopped again with the engine: the sweep is the
    // expensive part and a user who switches redirection off and on again should not pay it twice.
    private void EnsureProcessesStarted()
    {
        if (_processesStarted) return;
        _processesStarted = true;
        Processes.Start();
    }

    /// <summary>
    /// Stops the engine. The VPN tunnels stay up: the user switched them on, and nothing here has
    /// been asked to end their session with the provider.
    /// </summary>
    public Task StopEngineAsync() => Enqueue(Engine.StopAsync);

    /// <summary>
    /// Switches one VPN's tunnel on or off and remembers it. The engine is not told: which
    /// tunnels are up changes nothing about how a connection is routed, and re-applying the
    /// configuration would close live connections for a setting that does not concern them.
    /// </summary>
    public Task SetVpnConnectedAsync(Outbound outbound, bool connected)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));

        outbound.KeepConnected = connected;
        AppConfig snapshot = ConfigStore.Clone(Config);
        return Enqueue(async () =>
        {
            try
            {
                ConfigStore.Save(snapshot);
                await Vpn.SyncAsync(snapshot.Outbounds, snapshot.WireProxyPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "switching the vpn {Outbound} {State} failed",
                    outbound.Name, connected ? "on" : "off");
            }
        });
    }

    /// <summary>
    /// Completes once everything queued so far — saves, starts, stops — has run. For code that
    /// must see the outcome of a save it did not await, and for shutdown.
    /// </summary>
    public Task WhenIdleAsync()
    {
        lock (_workLock) return _lastWork;
    }

    // Chains the work behind whatever is already queued. The task handed back carries the work's
    // own outcome, exception included; the chain itself never faults, so the next piece of work
    // runs whatever happened to the previous one.
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

    /// <remarks>
    /// Async all the way down: stopping the engine unloads a driver and closes tunnels, and putting
    /// a VPN down is a conversation with the far side. The application still has to wait for all of
    /// it before the process goes, but that wait belongs at the edge — see App.OnExit, which is the
    /// one place left that blocks on a task.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _logRollTimer.Dispose();
        // A save the user made a moment before closing the window must still reach the disk, and
        // an engine stop in progress must finish before the engine is torn down under it.
        try { await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        await Engine.DisposeAsync().ConfigureAwait(false);
        await Processes.DisposeAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
