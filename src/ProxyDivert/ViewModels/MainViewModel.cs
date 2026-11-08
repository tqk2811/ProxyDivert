using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Localization;
using ProxyDivert.Services;
using ProxyDivert.Themes;

namespace ProxyDivert.ViewModels;

// Window-level state, built with the window and disposed with it: the tab view models and what
// the window shows of the engine switch, which itself lives in EngineController.
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;

    // Polled rather than raised: edits reach the configuration from every grid and dialog, and
    // comparing it with what was applied is one cheap check instead of a hook in each of them.
    private readonly DispatcherTimer _pendingChangesTimer;

    // Positions of the two live tabs in MainWindow.xaml's TabControl; keep in step with it.
    private const int ConnectionsTabIndex = 3;
    private const int LogTabIndex = 4;

    // False until the window reports itself shown: started with --minimized it never is, and
    // nothing should poll for a window that does not exist on screen.
    private bool _isWindowVisible;

    [ObservableProperty]
    private bool _hasPendingChanges;

    public ProcessesViewModel Processes { get; }
    public OutboundsViewModel Outbounds { get; }
    public RulesViewModel Rules { get; }
    public ConnectionsViewModel Connections { get; }
    public LogViewModel Log { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>The engine switch, shared with the tray and the local API; outlives this window.</summary>
    public EngineController Engine { get; }

    public EngineState State => Engine.State;

    /// <summary>True only once redirection is actually happening.</summary>
    public bool IsRunning => Engine.IsRunning;

    /// <inheritdoc cref="EngineController.IsSwitchedOn"/>
    public bool IsSwitchedOn => Engine.IsSwitchedOn;

    /// <inheritdoc cref="EngineController.IsBusy"/>
    public bool IsBusy => Engine.IsBusy;

    /// <summary>
    /// Which tab is showing. Bound so that switching tabs can refresh what the new one displays.
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    public string? StatusMessage => Engine.StatusMessage;

    /// <summary>Whether there is anything for the notice strip to say; it is hidden otherwise.</summary>
    public bool HasNotice => !IsElevated || !string.IsNullOrWhiteSpace(StatusMessage);

    // Segoe MDL2 Assets: E713 Settings (System), E706 Brightness (Light), E708 QuietHours (Dark).
    [ObservableProperty]
    private string _themeGlyph = char.ConvertFromUtf32(0xE713);

    [ObservableProperty]
    private string? _themeTooltip;

    // WinDivert loads a kernel driver, so without elevation the engine cannot start at all. The
    // window says so up front instead of failing at the first click.
    public bool IsElevated => Engine.IsElevated;

    /// <summary>
    /// Whether the close button only closes the window (the process stays in the tray) instead of
    /// ending the process. Read straight from
    /// the configuration on every close rather than cached, so ticking the box in Settings changes
    /// what the button does at once.
    /// </summary>
    public bool MinimizeToTrayOnClose => _services.Config.MinimizeToTrayOnClose;

    private readonly CoalescedDispatcherAction _refreshApplied;

    public MainViewModel(AppServices services, EngineController engine)
    {
        _services = services;
        Engine = engine;

        Processes = new ProcessesViewModel(services);
        Outbounds = new OutboundsViewModel(services);
        Rules = new RulesViewModel(services);
        Connections = new ConnectionsViewModel(services);
        Log = new LogViewModel(services);
        Settings = new SettingsViewModel(services, engine);

        // One rebuild of the process tree per burst of events, not one per event: the engine
        // reports sixty processes one at a time, and the tree only needs to be read once after.
        // A save that merely re-routes processes attaches and detaches nothing, so it is reported
        // separately and refreshes the same way.
        // Unhooked in Dispose: the engine outlives this view model, which is built per window.
        _refreshApplied = new CoalescedDispatcherAction(() => Processes.RefreshApplied());
        services.Engine.ProcessAttached += OnProcessAttachedOrDetached;
        services.Engine.ProcessDetached += OnProcessAttachedOrDetached;
        services.Engine.ConfigurationApplied += OnConfigurationApplied;
        Engine.PropertyChanged += OnEnginePropertyChanged;
        Engine.ConfigurationReplaced += OnConfigurationReplaced;
        Settings.IsEngineRunning = Engine.IsRunning;

        // The button's tooltip is composed in code rather than written in XAML, so it is one of the
        // few things a dictionary swap does not reach on its own.
        LocalizationManager.LanguageChanged += UpdateThemeButton;
        UpdateThemeButton();

        // Background priority: the check serialises the whole configuration, and must never sit
        // ahead of input. Started by SetWindowVisible once the window is actually on screen.
        _pendingChangesTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _pendingChangesTimer.Tick += (_, _) => HasPendingChanges = _services.HasPendingChanges;
    }

    // One button for every tab: they all edit the same configuration, so a save from any of them
    // always wrote all of it anyway.
    [RelayCommand]
    private void ApplyAndSave()
    {
        _services.SaveAndApply();
        Rules.CheckPatterns();
        HasPendingChanges = false;
    }

    private void OnProcessAttachedOrDetached(TrackedProcess _) => _refreshApplied.Request();

    private void OnConfigurationApplied() => _refreshApplied.Request();

    // The local API replaced the routing lists and saved them: the grids re-read the new lists.
    private void OnConfigurationReplaced()
    {
        ReloadAll();
        Rules.CheckPatterns();
        HasPendingChanges = false;
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EngineController.State):
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsSwitchedOn));
                OnPropertyChanged(nameof(IsBusy));
                ToggleEngineCommand.NotifyCanExecuteChanged();
                if (Engine.State is EngineState.Running or EngineState.Stopped)
                {
                    // Redirection came on or went off: the tree must say which processes are
                    // redirected, a tunnel a live filter routes through may not be disconnected,
                    // and the detection settings lock while it is on.
                    Processes.RefreshApplied();
                    Outbounds.RefreshVpnCommands();
                    Settings.IsEngineRunning = Engine.IsRunning;
                }
                break;
            case nameof(EngineController.StatusMessage):
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(HasNotice));
                break;
        }
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        _services.DiscardChanges();
        ReloadAll();
        Settings.ReloadPending();
        Rules.CheckPatterns();
        HasPendingChanges = false;
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
    // opens the driver and stopping waits for it to let go, and the command stays disabled until
    // the switch has actually moved.
    [RelayCommand(CanExecute = nameof(CanToggleEngine))]
    private async Task ToggleEngine()
    {
        await Engine.ToggleAsync();

        // The switch moved itself the moment it was clicked. If Start threw, State went back to
        // Stopped and nothing would push the knob back, so say so explicitly either way.
        OnPropertyChanged(nameof(IsSwitchedOn));
    }

    private bool CanToggleEngine() => !IsBusy;

    /// <summary>
    /// Re-reads the shared lists whenever a different tab comes up.
    /// </summary>
    /// <remarks>
    /// Each tab loads the policies and outbounds once, in its constructor, and they are the same
    /// objects three tabs let the user add to and delete. Without this, a policy created on Rules
    /// is missing from the filter editor on Processes, a new outbound never reaches the "default
    /// outbound" list, and a deleted one is still offered — worse, saving a filter then falls back
    /// to the first policy in a stale list and writes an id that no longer exists. Creating a
    /// policy and assigning it to a program is the main thing this application is for, so those
    /// two lists disagreeing is not a corner case.
    ///
    /// Refreshing on the switch is the cheap version of the fix; one shared collection owned above
    /// the tabs is the right one.
    /// </remarks>
    partial void OnSelectedTabIndexChanged(int value)
    {
        ReloadAll();
        UpdateTimers();
    }

    /// <summary>
    /// Told by the window whenever it is shown, hidden to the tray, minimised or restored. While
    /// nobody can see it, every polling timer is stopped: the UI thread then has nothing queued
    /// but input, so the tray menu opens at once even with the machine busy.
    /// </summary>
    public void SetWindowVisible(bool visible)
    {
        if (_isWindowVisible == visible) return;
        _isWindowVisible = visible;
        UpdateTimers();
    }

    // The live lists poll only while their own tab is the one showing; the pending-changes check
    // runs on every tab, since the Save button it drives sits outside them.
    private void UpdateTimers()
    {
        Connections.SetActive(_isWindowVisible && SelectedTabIndex == ConnectionsTabIndex);
        Log.SetActive(_isWindowVisible && SelectedTabIndex == LogTabIndex);

        if (_isWindowVisible)
        {
            if (_pendingChangesTimer.IsEnabled) return;
            // Edits made while hidden (from the tray switch, say) show up straight away.
            HasPendingChanges = _services.HasPendingChanges;
            _pendingChangesTimer.Start();
        }
        else
        {
            _pendingChangesTimer.Stop();
        }
    }

    [RelayCommand]
    private void ReloadAll()
    {
        Processes.Reload();
        Outbounds.Reload();
        Rules.Reload();
    }

    public void Dispose()
    {
        _services.Engine.ProcessAttached -= OnProcessAttachedOrDetached;
        _services.Engine.ProcessDetached -= OnProcessAttachedOrDetached;
        _services.Engine.ConfigurationApplied -= OnConfigurationApplied;
        Engine.PropertyChanged -= OnEnginePropertyChanged;
        Engine.ConfigurationReplaced -= OnConfigurationReplaced;
        LocalizationManager.LanguageChanged -= UpdateThemeButton;
        _pendingChangesTimer.Stop();
        Connections.Dispose();
        Log.Dispose();
        Outbounds.Dispose();
        Settings.Dispose();
    }
}
