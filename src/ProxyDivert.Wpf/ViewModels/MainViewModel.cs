using System;
using System.Security.Principal;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Wpf.Services;

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
    private string? _statusMessage;

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
    }

    [RelayCommand]
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

    [RelayCommand]
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
        Connections.Dispose();
        Log.Dispose();
    }
}
