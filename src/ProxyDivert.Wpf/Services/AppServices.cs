using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.DependencyInjection;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Logging;

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
    /// The live configuration. View models edit this instance and call <see cref="SaveAndApply"/>
    /// when the user is done, so an edit is never half-applied to the engine.
    /// </summary>
    public AppConfig Config { get; private set; }

    public RedirectEngine Engine { get; }

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

        _provider = new ServiceCollection()
            .AddProxyDivert(EffectiveLogPath)
            .BuildServiceProvider();

        Logs = _provider.GetRequiredService<InMemoryLogStore>();
        _loggerProvider = _provider.GetRequiredService<AppLoggerProvider>();
        Engine = _provider.GetRequiredService<RedirectEngine>();

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

    // Persist and push to the running engine in one step — the two must not drift apart.
    public void SaveAndApply()
    {
        Save();
        // The log path is the one setting the engine does not own, because logging is set up before
        // the engine exists. Applying it here is what makes it take effect without a restart.
        _loggerProvider.SetFilePath(EffectiveLogPath);
        if (Engine.IsRunning) Engine.ApplyConfig(Config);
    }

    public void StartEngine() => Engine.Start(Config);

    public void StopEngine() => Engine.Stop();

    public void Dispose()
    {
        _logRollTimer.Dispose();
        Engine.Dispose();
        _provider.Dispose();
    }
}
