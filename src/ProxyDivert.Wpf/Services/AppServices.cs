using System;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Logging;

namespace ProxyDivert.Wpf.Services;

// Composition root: the few long-lived objects the whole window shares, created once and disposed
// on shutdown. Deliberately not a DI container — the graph is four objects deep and a container
// would hide more than it saves.
public sealed class AppServices : IDisposable
{
    public ConfigStore ConfigStore { get; }

    // The live configuration. ViewModels edit this instance and call SaveAndApply when the user is
    // done, so an edit is never half-applied to the engine.
    public AppConfig Config { get; private set; }

    public RedirectEngine Engine { get; }

    // Log pane storage. Re-bound to the engine's logger every time the engine starts, because a
    // fresh RedirectLogger is created per run.
    public InMemoryLogStore Logs { get; private set; }

    private InMemoryLogStore? _boundStore;

    public AppServices(string? configPath = null)
    {
        ConfigStore = new ConfigStore(configPath);
        Config = ConfigStore.Load();
        Engine = new RedirectEngine();
        Logs = new InMemoryLogStore();
    }

    public void Save() => ConfigStore.Save(Config);

    // Persist and push to the running engine in one step — the two must not drift apart.
    public void SaveAndApply()
    {
        Save();
        if (Engine.IsRunning) Engine.ApplyConfig(Config);
    }

    public void StartEngine()
    {
        Engine.Start(Config);
        // The engine builds its logger in Start(), so the pane can only attach afterwards.
        AttachLogStore();
    }

    public void StopEngine()
    {
        Engine.Stop();
        DetachLogStore();
    }

    private void AttachLogStore()
    {
        DetachLogStore();
        _boundStore = new InMemoryLogStore(Engine.Logger);
        Logs = _boundStore;
        LogStoreChanged?.Invoke(Logs);
    }

    private void DetachLogStore()
    {
        _boundStore?.Dispose();
        _boundStore = null;
    }

    /// <summary>Raised when a new engine run replaces the log store the UI is bound to.</summary>
    public event Action<InMemoryLogStore>? LogStoreChanged;

    public void Dispose()
    {
        Engine.Dispose();
        DetachLogStore();
        Logs.Dispose();
    }
}
