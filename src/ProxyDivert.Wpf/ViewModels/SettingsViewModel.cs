using System;
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
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ProxyDivert";

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
        _eventSource = services.Config.ProcessEventSource;
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
        _services.Processes.UseEventSource(kind);

        OnPropertyChanged(nameof(UsesEtw));
        OnPropertyChanged(nameof(UsesWmi));
    }

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

    partial void OnStartWithWindowsChanged(bool value)
    {
        _services.Config.StartWithWindows = value;
        ApplyStartWithWindows(value);
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

    // Registry Run key rather than a scheduled task: the tool needs elevation anyway, and a Run
    // entry is something the user can find and remove without this program's help.
    private static void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled) key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // A locked-down registry is the user's environment, not an error worth a dialog.
        }
    }
}
