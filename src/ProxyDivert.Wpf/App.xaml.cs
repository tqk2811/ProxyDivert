using System;
using System.Threading.Tasks;
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

        // Reads the configuration file and builds the container, and nothing else: no sweep of the
        // machine's processes, no driver, no VPN dialled. All of that belongs to redirection being
        // switched on, and is started below — in the background, and only if it is.
        _services = new AppServices();

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

        // Everything from here on is behind the window rather than in front of it. Nothing above
        // touches the registry, the driver, the network or the process table, so what the user sees
        // is the configuration they left, on screen, before any of this is asked for.
        //
        // Not awaited: starting the engine opens the driver, enumerates every process and dials the
        // tunnels the filters route through, and the window must be up and painting while that
        // happens rather than after it. Failures land on the notice strip and in the log.
        _ = _mainViewModel.RestoreEngineAsync();

        // The Run-key entry an earlier version wrote never started anything — Windows skips a Run
        // entry that needs elevation — so it is cleared rather than left listed under the machine's
        // startup apps as something that plainly does not work. A registry write nobody is waiting
        // for, so it goes off the thread that paints.
        _ = Task.Run(StartupRegistration.RemoveLegacyRunKey);
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

        // The one place left in the application that blocks on a task, and it is here on purpose.
        // Everything underneath tears down asynchronously — putting a VPN down is a conversation
        // with the far side, unloading the driver takes as long as it takes — but OnExit cannot be
        // async, and the process must not go before that work is finished. Nothing below resumes on
        // this thread (every await in the chain is ConfigureAwait(false)), so waiting here cannot
        // deadlock against the dispatcher.
        if (_services is not null) _services.DisposeAsync().AsTask().GetAwaiter().GetResult();

        base.OnExit(e);
    }
}
