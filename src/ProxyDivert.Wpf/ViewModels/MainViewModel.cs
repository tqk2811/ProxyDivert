using System;
using System.Security.Principal;
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

        services.Engine.ProcessAttached += _ => Application.Current?.Dispatcher.BeginInvoke(
            () => Processes.RefreshProcesses());
        services.Engine.ProcessDetached += _ => Application.Current?.Dispatcher.BeginInvoke(
            () => Processes.RefreshProcesses());

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
    // the engine is doing now, not by which control was pressed.
    [RelayCommand]
    private void ToggleEngine()
    {
        if (IsRunning) Stop();
        else Start();

        // The switch moved itself the moment it was clicked. If Start threw, IsRunning never
        // changed and nothing would push the knob back — so say so explicitly either way.
        OnPropertyChanged(nameof(IsRunning));
    }

    private void Start()
    {
        if (IsRunning) return;
        try
        {
            _services.StartEngine();
            IsRunning = true;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"{ex.GetType().Name}: {ex.Message}";
            // Leave nothing half-started: a failed Start must not leave WinDivert handles open.
            try { _services.StopEngine(); } catch { }
            IsRunning = false;
        }
    }

    private void Stop()
    {
        if (!IsRunning) return;
        _services.StopEngine();
        IsRunning = false;
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
