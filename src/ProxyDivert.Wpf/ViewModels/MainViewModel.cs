using System;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Wpf.Localization;
using ProxyDivert.Wpf.Services;
using ProxyDivert.Wpf.Themes;

namespace ProxyDivert.Wpf.ViewModels;

// Window-level state: the engine switch, the admin warning, and the tab view models.
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;

    public ProcessesViewModel Processes { get; }
    public OutboundsViewModel Outbounds { get; }
    public RulesViewModel Rules { get; }
    public ConnectionsViewModel Connections { get; }
    public LogViewModel Log { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _statusMessage;

    /// <summary>Whether there is anything for the notice strip to say; it is hidden otherwise.</summary>
    public bool HasNotice => !IsElevated || !string.IsNullOrWhiteSpace(StatusMessage);

    // Segoe MDL2 Assets: E713 Settings (System), E706 Brightness (Light), E708 QuietHours (Dark).
    [ObservableProperty]
    private string _themeGlyph = char.ConvertFromUtf32(0xE713);

    [ObservableProperty]
    private string? _themeTooltip;

    // WinDivert loads a kernel driver, so without elevation the engine cannot start at all. The
    // window says so up front instead of failing at the first click.
    public bool IsElevated { get; }

    /// <summary>
    /// Whether the close button hides the window instead of ending the process. Read straight from
    /// the configuration on every close rather than cached, so ticking the box in Settings changes
    /// what the button does at once.
    /// </summary>
    public bool MinimizeToTrayOnClose => _services.Config.MinimizeToTrayOnClose;

    public MainViewModel(AppServices services)
    {
        _services = services;
        IsElevated = CheckElevated();

        Processes = new ProcessesViewModel(services);
        Outbounds = new OutboundsViewModel(services);
        Rules = new RulesViewModel(services);
        Connections = new ConnectionsViewModel(services);
        Log = new LogViewModel(services);
        Settings = new SettingsViewModel(services);

        // One rebuild of the process tree per burst of events, not one per event: the engine
        // reports sixty processes one at a time, and the tree only needs to be read once after.
        // A save that merely re-routes processes attaches and detaches nothing, so it is reported
        // separately and refreshes the same way.
        var refreshApplied = new CoalescedDispatcherAction(() => Processes.RefreshApplied());
        services.Engine.ProcessAttached += _ => refreshApplied.Request();
        services.Engine.ProcessDetached += _ => refreshApplied.Request();
        services.Engine.ConfigurationApplied += () => refreshApplied.Request();

        // The button's tooltip is composed in code rather than written in XAML, so it is one of the
        // few things a dictionary swap does not reach on its own.
        LocalizationManager.LanguageChanged += UpdateThemeButton;
        UpdateThemeButton();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        // Assigning through the Settings tab rather than calling ThemeManager directly: that setter
        // is the one place that applies the mode and writes it to the configuration, and going
        // around it would leave the two views of the same setting disagreeing.
        Settings.Theme = ThemeManager.Next(ThemeManager.CurrentMode);
        UpdateThemeButton();
    }

    private void UpdateThemeButton()
    {
        int glyph = ThemeManager.CurrentMode switch
        {
            ThemeMode.Light => 0xE706,
            ThemeMode.Dark => 0xE708,
            _ => 0xE713,
        };
        ThemeGlyph = char.ConvertFromUtf32(glyph);
        ThemeTooltip = LocalizationManager.Format(
            "Str.Theme.Tooltip", LocalizationManager.EnumText(ThemeManager.CurrentMode));
    }

    // One switch rather than two buttons, so there is one command: what it does is decided by what
    // the engine is doing now, not by which control was pressed. Asynchronous because starting
    // opens the driver and stopping waits for it to let go — neither belongs on the thread that
    // paints the window — and the command stays disabled until the switch has actually moved.
    [RelayCommand]
    private async Task ToggleEngine()
    {
        if (IsRunning) await StopAsync();
        else await StartAsync();

        // The switch moved itself the moment it was clicked. If Start threw, IsRunning never
        // changed and nothing would push the knob back — so say so explicitly either way.
        OnPropertyChanged(nameof(IsRunning));
    }

    /// <summary>
    /// Puts the switch back where the user left it at the end of the last run. Called once at
    /// startup, and deliberately not through <see cref="ToggleEngineCommand"/>: this restores a
    /// state rather than asking for a change, so a configuration that says "off" does nothing here.
    /// </summary>
    public async Task RestoreEngineAsync()
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
        OnPropertyChanged(nameof(IsRunning));
    }

    private async Task StartAsync()
    {
        if (IsRunning) return;
        try
        {
            await _services.StartEngineAsync();
            IsRunning = true;
            StatusMessage = null;
            RememberEngineState();
            Processes.RefreshApplied();
            // A tunnel a filter routes through may not be disconnected while that filter is live,
            // so the Outbounds tab has to be told the moment redirection comes on.
            Outbounds.RefreshVpnCommands();
            // ...and the detection settings lock while it is on.
            Settings.IsEngineRunning = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"{ex.GetType().Name}: {ex.Message}";
            // Leave nothing half-started: a failed Start must not leave WinDivert handles open.
            try { await _services.StopEngineAsync(); } catch { }
            IsRunning = false;
        }
    }

    private async Task StopAsync()
    {
        if (!IsRunning) return;
        await _services.StopEngineAsync();
        IsRunning = false;
        RememberEngineState();
        // Nothing is being redirected any more, so the tree must not keep claiming otherwise.
        Processes.RefreshApplied();
        // The tunnels stay up — but nothing is routing through them now, so they may be
        // disconnected by hand again.
        Outbounds.RefreshVpnCommands();
        Settings.IsEngineRunning = false;
    }

    // Written the moment the switch moves rather than at the next Save, for the same reason the
    // theme is: what is worth restoring is the state the user last chose, and a reboot must not be
    // able to forget it. This persists the live configuration, edits in progress on the grids
    // included — the appearance settings have always behaved this way, and two different meanings
    // of "write the file" would be worse than the one shared meaning.
    private void RememberEngineState()
    {
        _services.Config.EngineEnabled = IsRunning;
        _services.Save();
    }

    [RelayCommand]
    private void ReloadAll()
    {
        Processes.Reload();
        Outbounds.Reload();
        Rules.Reload();
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

    public void Dispose()
    {
        LocalizationManager.LanguageChanged -= UpdateThemeButton;
        Connections.Dispose();
        Log.Dispose();
    }
}
