using System;
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

    public AppServices(string? configPath = null)
    {
        // The config decides where the trace file goes, so it has to be read before the container
        // that carries the logging is built.
        ConfigStore = new ConfigStore(configPath);
        Config = ConfigStore.Load();

        _provider = new ServiceCollection()
            .AddProxyDivert(Config.DiagnosticLogPath)
            .BuildServiceProvider();

        Logs = _provider.GetRequiredService<InMemoryLogStore>();
        _loggerProvider = _provider.GetRequiredService<AppLoggerProvider>();
        Engine = _provider.GetRequiredService<RedirectEngine>();
    }

    public void Save() => ConfigStore.Save(Config);

    // Persist and push to the running engine in one step — the two must not drift apart.
    public void SaveAndApply()
    {
        Save();
        // The log path is the one setting the engine does not own, because logging is set up before
        // the engine exists. Applying it here is what makes it take effect without a restart.
        _loggerProvider.SetFilePath(Config.DiagnosticLogPath);
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
