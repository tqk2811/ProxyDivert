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
using ProxyDivert.Core.Processes;

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
public sealed class AppServices : IDisposable
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
    /// Every process running on this machine, with its path, its arguments and its parent. Started
    /// when the window opens and kept current for as long as the application lives, so the engine
    /// has the answer ready the moment it is switched on rather than going and finding it then.
    /// </summary>
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

        // Before anything else asks: collecting is what makes starting the engine cheap, and the
        // first sweep is about thirty milliseconds, so it is done here rather than deferred.
        Processes = _provider.GetRequiredService<ProcessInventory>();
        Processes.EventBacklogMs = Config.ProcessEventBacklogMs;
        Processes.Start();

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

        return Enqueue(() =>
        {
            try
            {
                ConfigStore.Save(snapshot);
                if (Engine.IsRunning) Engine.ApplyConfig(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "saving and applying the configuration failed");
            }
        });
    }

    /// <summary>Starts the engine on a snapshot of the configuration, off the caller's thread.</summary>
    public Task StartEngineAsync()
    {
        AppConfig snapshot = ConfigStore.Clone(Config);
        return Enqueue(() => Engine.Start(snapshot));
    }

    public Task StopEngineAsync() => Enqueue(Engine.Stop);

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
    private Task Enqueue(Action work)
    {
        lock (_workLock)
        {
            Task next = _lastWork.ContinueWith(
                _ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _lastWork = next.ContinueWith(_ => { }, TaskScheduler.Default);
            return next;
        }
    }

    public void Dispose()
    {
        _logRollTimer.Dispose();
        // A save the user made a moment before closing the window must still reach the disk, and
        // an engine stop in progress must finish before the engine is torn down under it.
        try { WhenIdleAsync().Wait(TimeSpan.FromSeconds(10)); } catch { }
        Engine.Dispose();
        Processes.Dispose();
        _provider.Dispose();
    }
}
