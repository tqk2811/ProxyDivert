using System;
using System.Security.Principal;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDivert.Localization;
using ProxyDivert.ViewModels;

namespace ProxyDivert.Services;

/// <summary>
/// The engine switch and the notice that goes with it, for the whole life of the process. Lives
/// above the window: the window is built when it is opened and thrown away when it is closed, while
/// the tray menu and the local API keep switching redirection on and off with no window at all.
/// Everything here runs on the UI thread, like the view models that read it.
/// </summary>
public sealed partial class EngineController : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsSwitchedOn))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private EngineState _state;

    /// <summary>Why redirection last failed to start (or could not be restored); null when fine.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Why the local API could not start (port already taken, say); null while it is fine or off.</summary>
    [ObservableProperty]
    private string? _localApiError;

    public EngineController(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        IsElevated = CheckElevated();
    }

    /// <summary>True only once redirection is actually happening.</summary>
    public bool IsRunning => State == EngineState.Running;

    /// <summary>
    /// Where the knob sits: on the right unless redirection is fully off. It moves to the right the
    /// moment Start is asked for and comes back only once Stop has finished, so the knob never
    /// claims the engine is gone while its driver handles are still closing.
    /// </summary>
    public bool IsSwitchedOn => State != EngineState.Stopped;

    /// <summary>True while the engine is coming up or going down; a second press is refused.</summary>
    public bool IsBusy => State is EngineState.Starting or EngineState.Stopping;

    // WinDivert loads a kernel driver, so without elevation the engine cannot start at all.
    public bool IsElevated { get; }

    /// <summary>
    /// Raised after the local API replaced the routing lists and saved them, so an open window
    /// re-reads its grids.
    /// </summary>
    public event Action? ConfigurationReplaced;

    /// <summary>
    /// One switch, so one action: what it does is decided by what the engine is doing now.
    /// Ignored while the engine is already moving.
    /// </summary>
    public async Task ToggleAsync()
    {
        if (IsBusy) return;
        if (IsRunning) await StopAsync();
        else await StartAsync();
    }

    /// <summary>
    /// Puts the switch back where the user left it at the end of the last run. Called once at
    /// startup: this restores a state rather than asking for a change, so a configuration that says
    /// "off" does nothing here.
    /// </summary>
    public async Task RestoreAsync()
    {
        if (!_services.Config.EngineEnabled) return;

        // Checked here rather than left to fail inside the driver: at logon there may be nobody
        // looking, and the notice strip is the only place the reason can go.
        if (!IsElevated)
        {
            StatusMessage = Loc.S("Str.App.RestoreNeedsAdmin");
            return;
        }

        await StartAsync();
    }

    /// <summary>The engine switch driven from the local API. Returns whether redirection is on afterwards.</summary>
    public async Task<bool> SetRunningAsync(bool running)
    {
        if (running) await StartAsync();
        else await StopAsync();
        return IsRunning;
    }

    /// <summary>What the local API does after writing the configuration: the same as Save.</summary>
    public async Task SaveFromApiAsync()
    {
        Task applied = _services.SaveAndApply();
        ConfigurationReplaced?.Invoke();
        await applied;
    }

    private async Task StartAsync()
    {
        if (State != EngineState.Stopped) return;
        // Set before the await, so the knob is already over on the right and painted "coming up"
        // while the driver opens and the tunnels dial.
        State = EngineState.Starting;
        try
        {
            await _services.StartEngineAsync();
            StatusMessage = null;
            State = EngineState.Running;
            RememberEngineState();
        }
        catch (Exception ex)
        {
            StatusMessage = $"{ex.GetType().Name}: {ex.Message}";
            // Leave nothing half-started: a failed Start must not leave WinDivert handles open.
            try { await _services.StopEngineAsync(); } catch { }
            State = EngineState.Stopped;
        }
    }

    private async Task StopAsync()
    {
        if (State != EngineState.Running) return;
        State = EngineState.Stopping;
        try
        {
            await _services.StopEngineAsync();
        }
        finally
        {
            // Whatever the driver did on the way down, nothing is redirected any more: leaving the
            // switch stuck on "going down" would mean it could never be pressed again.
            State = EngineState.Stopped;
        }

        RememberEngineState();
    }

    // Written the moment the switch moves rather than at the next Save: what is worth restoring is
    // the state the user last chose, and a reboot must not be able to forget it. This persists the
    // live configuration, edits in progress on the grids included — the appearance settings have
    // always behaved this way.
    private void RememberEngineState()
    {
        _services.Config.EngineEnabled = IsRunning;
        _services.Save();
    }

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
