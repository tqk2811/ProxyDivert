using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ProxyDivert.Core.Configuration.Enums;
using ProxyDivert.Core.Processes.Enums;
using TqkLibrary.WinDivert.Redirect.Enums;
using ProxyDivert.Wpf.Localization;
using ProxyDivert.Wpf.Services;
using ProxyDivert.Wpf.Themes;

namespace ProxyDivert.Wpf.ViewModels;

// The Settings tab. Everything here writes straight into AppConfig and saves; the options that
// live inside the WinDivert handles (DNS mode, the IPv6 mode, log file) only take effect on the
// next engine start, which the view says out loud rather than pretending otherwise.
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public Array DnsModes { get; } = Enum.GetValues(typeof(DnsMode));

    public Array Ipv6Modes { get; } = Enum.GetValues(typeof(Ipv6Mode));

    public Array Themes { get; } = Enum.GetValues(typeof(ThemeMode));

    public Array Languages { get; } = Enum.GetValues(typeof(AppLanguage));

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _dnsMode = services.Config.Dns.Mode;
        _dohEndpoint = services.Config.Dns.DohEndpoint;
        _ipv6 = services.Config.Ipv6;
        _wireProxyPath = services.Config.WireProxyPath ?? string.Empty;
        _diagnosticLogPath = services.Config.DiagnosticLogPath ?? string.Empty;
        _autoSaveLog = services.Config.AutoSaveLog;
        _theme = ThemeManager.Parse(services.Config.Theme);
        _language = LocalizationManager.Parse(services.Config.Language);
        _startWithWindows = services.Config.StartWithWindows;
        _minimizeToTrayOnClose = services.Config.MinimizeToTrayOnClose;
        _eventSource = services.Config.ProcessEventSource;
        _detection = services.Config.ProcessDetection;

        RefreshWatermarkStatus();
        VerifyStartWithWindows();
    }

    // The logon task can be gone without this tool knowing — removed by hand, or left behind on a
    // machine that was rebuilt — so the file is only the opening guess and the scheduler has the
    // final say. Asked on a background thread because schtasks is a process launch and this runs
    // while the window is still being built; the answer almost always agrees with the file, so
    // nothing visibly moves.
    private async void VerifyStartWithWindows()
    {
        try
        {
            bool actual = await Task.Run(StartupRegistration.IsEnabled);
            if (actual == StartWithWindows) return;

            _applyingStartWithWindows = true;
            try { StartWithWindows = actual; }
            finally { _applyingStartWithWindows = false; }

            _services.Config.StartWithWindows = actual;
            _services.Save();
        }
        catch
        {
            // Nothing here is worth interrupting the user for: the box keeps what the file said.
        }
    }

    // How a process is found at all. The event source below only means anything under the first of
    // these; the second hears the machine's sockets instead and needs no process events, which is
    // why picking it stops them.
    private ProcessDetectionMode _detection;

    public bool UsesProcessEvents
    {
        get => _detection == ProcessDetectionMode.ProcessEvents;
        set { if (value) SetDetection(ProcessDetectionMode.ProcessEvents); }
    }

    public bool UsesNetworkSniff
    {
        get => _detection == ProcessDetectionMode.NetworkSniff;
        set { if (value) SetDetection(ProcessDetectionMode.NetworkSniff); }
    }

    private void SetDetection(ProcessDetectionMode mode)
    {
        if (_detection == mode) return;

        _detection = mode;
        _services.Config.ProcessDetection = mode;
        _services.Save();
        ApplyEventSource();

        OnPropertyChanged(nameof(UsesProcessEvents));
        OnPropertyChanged(nameof(UsesNetworkSniff));
    }

    // Which source the process table listens to. Two booleans rather than the enum itself because
    // a RadioButton binds to IsChecked; setting one to false is the other one being picked, and is
    // ignored here so the group cannot end up with nothing selected.
    private ProcessEventSourceKind _eventSource;

    public bool UsesEtw
    {
        get => _eventSource == ProcessEventSourceKind.Etw;
        set { if (value) SetEventSource(ProcessEventSourceKind.Etw); }
    }

    public bool UsesWmi
    {
        get => _eventSource == ProcessEventSourceKind.Wmi;
        set { if (value) SetEventSource(ProcessEventSourceKind.Wmi); }
    }

    /// <summary>
    /// True while redirection is on, which is when the detection settings are locked. Swapping the
    /// source under a running engine would mean a window where neither the old nor the new one is
    /// delivering, and a process that starts in it is redirected late or not at all.
    /// </summary>
    [ObservableProperty]
    private bool _isEngineRunning;

    // Applied at once rather than at the next Save: the table is running now, and a user who picks
    // a source expects the next process to arrive through it.
    private void SetEventSource(ProcessEventSourceKind kind)
    {
        if (_eventSource == kind) return;

        _eventSource = kind;
        _services.Config.ProcessEventSource = kind;
        _services.Save();
        ApplyEventSource();

        OnPropertyChanged(nameof(UsesEtw));
        OnPropertyChanged(nameof(UsesWmi));
    }

    // Null while sniffing: the table then keeps itself current by sweeping, which is all it is
    // needed for there — the sweep is what retires a process that has exited, and the path and
    // parent of a new one are read on demand when its first connection asks about it.
    private void ApplyEventSource()
        => _services.Processes.UseEventSource(
            _detection == ProcessDetectionMode.ProcessEvents ? _eventSource : null);

    [ObservableProperty]
    private DnsMode _dnsMode;

    [ObservableProperty]
    private string _dohEndpoint;

    [ObservableProperty]
    private Ipv6Mode _ipv6;

    [ObservableProperty]
    private string _wireProxyPath;

    [ObservableProperty]
    private string _diagnosticLogPath;

    [ObservableProperty]
    private bool _autoSaveLog;

    /// <summary>Where this run's trace is being written, for the view to show under the switch.</summary>
    public string CurrentLogPath => _services.EffectiveLogPath ?? string.Empty;

    [ObservableProperty]
    private ThemeMode _theme;

    [ObservableProperty]
    private AppLanguage _language;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        _services.Config.MinimizeToTrayOnClose = value;
        _services.Save();
    }

    partial void OnDnsModeChanged(DnsMode value) => _services.Config.Dns.Mode = value;

    partial void OnDohEndpointChanged(string value) => _services.Config.Dns.DohEndpoint = value;

    partial void OnIpv6Changed(Ipv6Mode value) => _services.Config.Ipv6 = value;

    partial void OnWireProxyPathChanged(string value)
        => _services.Config.WireProxyPath = string.IsNullOrWhiteSpace(value) ? null : value;

    partial void OnDiagnosticLogPathChanged(string value)
    {
        _services.Config.DiagnosticLogPath = string.IsNullOrWhiteSpace(value) ? null : value;
        OnPropertyChanged(nameof(CurrentLogPath));
    }

    // Applied the moment it is ticked, like the appearance settings and for the same reason: a
    // switch whose whole purpose is to capture what happens next is useless if it waits for Save.
    partial void OnAutoSaveLogChanged(bool value)
    {
        _services.SetAutoSaveLog(value);
        OnPropertyChanged(nameof(CurrentLogPath));
    }

    // Appearance is the one pair of settings that takes effect the moment it is picked, so it is
    // also written out at once: a theme that reverts on the next launch because Save was never
    // pressed would look like the tool forgot rather than like a pending edit.
    partial void OnThemeChanged(ThemeMode value)
    {
        _services.Config.Theme = value.ToString();
        ThemeManager.Apply(value);
        _services.Save();
    }

    partial void OnLanguageChanged(AppLanguage value)
    {
        _services.Config.Language = value.ToString();
        LocalizationManager.Apply(value);
        _services.Save();
    }

    // Applied at once like the appearance settings, and then read back: registering the logon task
    // can fail on a machine whose policy forbids it, and a checkbox that stays ticked after a
    // failure would be the tool lying about what it arranged. The guard is for that correction —
    // putting the box back would otherwise re-enter here and ask the scheduler all over again.
    private bool _applyingStartWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_applyingStartWithWindows) return;
        _ = ApplyStartWithWindowsAsync(value);
    }

    // Off the window's thread: registering a task means launching schtasks and waiting for the
    // scheduler service to answer, which is not something a tick box should freeze the tab for.
    private async Task ApplyStartWithWindowsAsync(bool enabled)
    {
        try
        {
            bool actual = await Task.Run(() =>
            {
                if (enabled) StartupRegistration.Enable();
                else StartupRegistration.Disable();
                return StartupRegistration.IsEnabled();
            });

            _services.Config.StartWithWindows = actual;
            _services.Save();

            if (actual == enabled) return;

            _applyingStartWithWindows = true;
            try { StartWithWindows = actual; }
            finally { _applyingStartWithWindows = false; }
        }
        catch
        {
        }
    }

    [RelayCommand]
    private void Save() => _services.SaveAndApply();

    [RelayCommand]
    private void BrowseLogPath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Log files (*.log)|*.log|All files (*.*)|*.*",
            FileName = "proxydivert.log",
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog() == true) DiagnosticLogPath = dialog.FileName;
    }

    /// <summary>
    /// What the SoftEther watermark line says: where the blob is, or that it is not here yet.
    /// </summary>
    /// <remarks>
    /// Read off the disk each time rather than remembered, because the file can also arrive from
    /// the script that ships beside the exe — and a line claiming "not fetched" next to a file that
    /// is plainly there is worse than no line at all.
    /// </remarks>
    [ObservableProperty]
    private string _watermarkStatus = string.Empty;

    [ObservableProperty]
    private bool _isFetchingWatermark;

    public void RefreshWatermarkStatus()
    {
        string? path = _services.Watermarks.Find();
        WatermarkStatus = path is null
            ? Loc.S("Str.Settings.WatermarkMissing")
            : LocalizationManager.Format("Str.Settings.WatermarkFound", path);
    }

    // Never on the way to dialling a tunnel, only here: a tool whose whole job is to control what
    // leaves this machine should not make a connection of its own that nobody asked for.
    [RelayCommand(CanExecute = nameof(CanFetchWatermark))]
    private async Task FetchWatermarkAsync()
    {
        IsFetchingWatermark = true;
        FetchWatermarkCommand.NotifyCanExecuteChanged();
        WatermarkStatus = Loc.S("Str.Settings.WatermarkFetching");
        try
        {
            string path = await _services.Watermarks.DownloadAsync().ConfigureAwait(true);
            WatermarkStatus = LocalizationManager.Format("Str.Settings.WatermarkFound", path);
        }
        catch (Exception ex)
        {
            WatermarkStatus = LocalizationManager.Format("Str.Settings.WatermarkFailed", ex.Message);
        }
        finally
        {
            IsFetchingWatermark = false;
            FetchWatermarkCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanFetchWatermark() => !IsFetchingWatermark;

    [RelayCommand]
    private void BrowseWireProxy()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "wireproxy (wireproxy.exe)|wireproxy.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) WireProxyPath = dialog.FileName;
    }
}
