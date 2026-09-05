using System;
using System.IO;
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

    // The file THIS run auto-logs to, or null when auto-save is off. Computed once and kept,
    // because the name carries a timestamp: recomputing it on every Save would scatter a run's
    // trace across a new file per keystroke.
    private string? _autoLogPath;

    /// <summary>
    /// Where the trace actually goes: this run's timestamped file when auto-save is on, otherwise
    /// the path the user pinned, otherwise nowhere.
    /// </summary>
    public string? EffectiveLogPath
        => _autoLogPath ?? (string.IsNullOrWhiteSpace(Config.DiagnosticLogPath) ? null : Config.DiagnosticLogPath);

    /// <summary>A fresh <c>Logs\yyyyMMdd-HHmmss.log</c> beside the executable.</summary>
    public static string BuildAutoLogPath()
        => Path.Combine(
            AppContext.BaseDirectory, "Logs",
            FormattableString.Invariant($"{DateTime.Now:yyyyMMdd-HHmmss}.log"));

    public AppServices(string? configPath = null)
    {
        // The config decides where the trace file goes, so it has to be read before the container
        // that carries the logging is built.
        ConfigStore = new ConfigStore(configPath);
        Config = ConfigStore.Load();
        if (Config.AutoSaveLog) _autoLogPath = BuildAutoLogPath();

        _provider = new ServiceCollection()
            .AddProxyDivert(EffectiveLogPath)
            .BuildServiceProvider();

        Logs = _provider.GetRequiredService<InMemoryLogStore>();
        _loggerProvider = _provider.GetRequiredService<AppLoggerProvider>();
        Engine = _provider.GetRequiredService<RedirectEngine>();
    }

    public void Save() => ConfigStore.Save(Config);

    /// <summary>
    /// Turns the per-run trace file on or off, and starts writing at once rather than at the next
    /// Save — the switch is flicked precisely because the next few seconds are the interesting
    /// ones. Turning it on again later opens a NEW file, so two attempts at reproducing something
    /// do not overwrite each other.
    /// </summary>
    public void SetAutoSaveLog(bool enabled)
    {
        Config.AutoSaveLog = enabled;
        _autoLogPath = enabled ? BuildAutoLogPath() : null;
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
        Engine.Dispose();
        _provider.Dispose();
    }
}
