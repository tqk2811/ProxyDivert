using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.DependencyInjection;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Hosting;
using ProxyDivert.Core.Logging;
using ProxyDivert.Core.Outbounds;
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
///
/// What redirection MEANS — the order a start goes in, what a save reaches, the queue all of it runs
/// on — is the <see cref="Session"/>'s, shared with the command line. What is left here is what only
/// a window has: the configuration being edited, the file it came from, and where the trace goes.
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

    /// <summary>The engine, the process table and the tunnels, and the one queue they are driven through.</summary>
    public ProxyDivertSession Session { get; }

    public RedirectEngine Engine => Session.Engine;

    /// <inheritdoc cref="ProxyDivertSession.Vpn"/>
    public VpnConnectionKeeper Vpn => Session.Vpn;

    /// <inheritdoc cref="SuspendedLaunchService"/>
    public SuspendedLaunchService Launcher { get; }

    /// <summary>
    /// What the Test button on the Outbounds tab asks. It builds a way out of its own rather than
    /// borrowing the running one, so testing a VPN cannot disturb the tunnel that is up.
    /// </summary>
    public OutboundTester OutboundTester { get; }

    /// <summary>
    /// Where the SoftEther watermark blob is on this machine, and the download that fetches it.
    /// </summary>
    /// <remarks>
    /// The blob is GPL data the application cannot ship, so a SoftEther outbound is unusable until
    /// someone goes and gets it. That is a button rather than something done on the way to dialling:
    /// a network tool should not reach out to the internet on its own.
    /// </remarks>
    public SoftEtherWatermarkStore Watermarks { get; }

    /// <summary>
    /// Every log line, from the packet path up. Unlike before, this lives as long as the
    /// application rather than as long as one engine run, so the pane keeps what happened before
    /// the last Start.
    /// </summary>
    public InMemoryLogStore Logs { get; }

    private readonly AppLoggerProvider _loggerProvider;

    // True while auto-save is on. The path itself is derived from the clock rather than stored,
    // because it names an HOUR: everything logged between 14:00 and 15:00 belongs in one file, no
    // matter how many times the engine is started or the settings are saved in between.
    private bool _autoSaveLog;

    // Rolls the file over when the hour turns. Nothing else notices the clock, so without this a
    // long-running session would keep writing to the hour it happened to start in.
    private readonly Timer _logRollTimer;

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

        // The store is handed to the container rather than built by it — it already exists, it was
        // needed to read the file — and registering it is what gives the session a file to write.
        _provider = new ServiceCollection()
            .AddSingleton(ConfigStore)
            .AddProxyDivert(EffectiveLogPath)
            .BuildServiceProvider();

        Logs = _provider.GetRequiredService<InMemoryLogStore>();
        _loggerProvider = _provider.GetRequiredService<AppLoggerProvider>();
        // Resolved, nothing started: the session brings the process table, the driver and the
        // tunnels up only when redirection is switched on, so opening the window costs a file read.
        Session = _provider.GetRequiredService<ProxyDivertSession>();
        Launcher = _provider.GetRequiredService<SuspendedLaunchService>();
        OutboundTester = _provider.GetRequiredService<OutboundTester>();
        Watermarks = _provider.GetRequiredService<SoftEtherWatermarkStore>();

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
    /// Persists the configuration and pushes it to the tunnels and the running engine, in one step.
    /// The snapshot is taken here, on the caller's thread, while the grids are quiet; the rest runs
    /// on the session's queue. The returned task completes when the engine has taken the
    /// configuration, and faults when it could not — callers that only want it done may ignore it,
    /// the failure is logged either way.
    /// </summary>
    public Task SaveAndApply()
    {
        // The log path is the one setting the engine does not own, because logging is set up before
        // the engine exists. Applying it here is what makes it take effect without a restart.
        _loggerProvider.SetFilePath(EffectiveLogPath);
        return Session.ApplyAsync(Config);
    }

    /// <inheritdoc cref="ProxyDivertSession.StartAsync"/>
    /// <remarks>
    /// Handed the configuration being edited, not a copy: the tunnels a filter routes through are
    /// switched on in it, so the Outbounds tab shows them as on. See the session for the rest.
    /// </remarks>
    public Task StartEngineAsync() => Session.StartAsync(Config);

    /// <inheritdoc cref="ProxyDivertSession.StopAsync"/>
    public Task StopEngineAsync() => Session.StopAsync();

    /// <inheritdoc cref="ProxyDivertSession.SetVpnConnectedAsync"/>
    public Task SetVpnConnectedAsync(Outbound outbound, bool connected)
        => Session.SetVpnConnectedAsync(Config, outbound, connected);

    /// <inheritdoc cref="ProxyDivertSession.WhenIdleAsync"/>
    public Task WhenIdleAsync() => Session.WhenIdleAsync();

    /// <remarks>
    /// Async all the way down: stopping the engine unloads a driver and closes tunnels, and putting
    /// a VPN down is a conversation with the far side. The application still has to wait for all of
    /// it before the process goes, but that wait belongs at the edge — see App.OnExit, which is the
    /// one place left that blocks on a task.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _logRollTimer.Dispose();
        // First the session, which lets queued work finish and switches redirection off; then the
        // container, which disposes the engine, the table and the tunnels in reverse order of making.
        await Session.DisposeAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
