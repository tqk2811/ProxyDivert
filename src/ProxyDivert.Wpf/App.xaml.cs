using System;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using ProxyDivert.Wpf.Localization;
using ProxyDivert.Wpf.Services;
using ProxyDivert.Wpf.Themes;
using ProxyDivert.Wpf.ViewModels;
using ProxyDivert.Wpf.Views;

namespace ProxyDivert.Wpf;

public partial class App : Application
{
    private AppServices? _services;
    private MainViewModel? _mainViewModel;
    private TrayIconController? _tray;

    /// <summary>
    /// True once the user has actually asked to quit. The window checks it before deciding whether
    /// closing means hiding: without it, Exit on the tray menu would be swallowed by the very rule
    /// that keeps the tool running when the window is closed.
    /// </summary>
    internal static bool IsExiting { get; private set; }

    /// <summary>Ends the process for real. The only way out, now that closing the window may not be.</summary>
    internal static void BeginExit()
    {
        IsExiting = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Nothing closes this application by accident any more: the window may be hidden to the
        // tray, and starting with --minimized means it is never shown at all, so the default rule
        // of "quit when the last window closes" would end a redirect that is still wanted.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _services = new AppServices();

        // The Run-key entry an earlier version wrote never started anything — Windows skips a Run
        // entry that needs elevation — so it is cleared here rather than left listed under the
        // machine's startup apps as something that plainly does not work.
        StartupRegistration.RemoveLegacyRunKey();

        // Appearance comes from the same config file as everything else, so the window opens the
        // way the user left it rather than flashing the default palette first.
        ThemeManager.Apply(ThemeManager.Parse(_services.Config.Theme));
        LocalizationManager.Apply(LocalizationManager.Parse(_services.Config.Language));

        _mainViewModel = new MainViewModel(_services);
        var window = new MainWindow { DataContext = _mainViewModel };
        // Assigned even when it is never shown: the tray needs something to bring back, and the
        // window has to exist for the engine to have somewhere to report to.
        MainWindow = window;

        _tray = new TrayIconController(LoadTrayIcon(), window, _mainViewModel);

        AppArguments arguments = AppArguments.Parse(e.Args);
        if (!arguments.Minimized) window.Show();

        // Not awaited: starting the engine opens the driver and enumerates every process, and the
        // window must be up and painting while that happens rather than after it.
        _ = _mainViewModel.RestoreEngineAsync();
    }

    // Loaded here rather than merged into Application.Resources: TaskbarIcon subscribes to the
    // application's events in its constructor, so merging it would mean anything that walks the
    // application's resources builds a live tray icon as a side effect — off the UI thread, that
    // throws. Its context menu still finds the shared styles and strings, because a menu with no
    // parent falls back to the application's resources anyway.
    private static TaskbarIcon LoadTrayIcon()
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ProxyDivert;component/Views/TrayIcon.xaml"),
        };
        return (TaskbarIcon)dictionary["Tray"];
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop the redirect before the process goes away: leaving WinDivert handles open would
        // keep the target's traffic pointed at a relay that no longer exists.
        _tray?.Dispose();
        _mainViewModel?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
