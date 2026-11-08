using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
#if LOCAL_API
using ProxyDivert.Api;
#endif
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Helpers;
using ProxyDivert.Localization;
using ProxyDivert.Services;
using ProxyDivert.Themes;
using ProxyDivert.ViewModels;
using ProxyDivert.Views;

namespace ProxyDivert;

public partial class App : Application
{
    private AppServices? _services;
    private EngineController? _engine;
    private TrayIconController? _tray;

    // Both exist only while the window is open: built on each opening, disposed on each close, so
    // the process sitting in the tray holds none of the visual tree, the grids' rows or the styles.
    private MainWindow? _window;
    private MainViewModel? _mainViewModel;
#if LOCAL_API
    private LocalApiHost? _api;
#endif

    /// <summary>
    /// True once the user has actually asked to quit. The window checks it before deciding whether
    /// its close button ends the process, and the close handler skips the cleanup meant for a
    /// process that goes on running in the tray.
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
        HorizontalWheelScroll.Register();

        // Reads the configuration file and builds the container, and nothing else: no sweep of the
        // machine's processes, no driver, no VPN dialled. All of that belongs to redirection being
        // switched on, and is started below — in the background, and only if it is.
        _services = new AppServices();

        // Appearance comes from the same config file as everything else, so the window opens the
        // way the user left it rather than flashing the default palette first. Only the mode is
        // remembered here: the palette itself is loaded with the window (UiResources.Load).
        ThemeManager.Apply(ThemeManager.Parse(_services.Config.Theme));
        LocalizationManager.Apply(LocalizationManager.Parse(_services.Config.Language));

        _engine = new EngineController(_services);
        _tray = new TrayIconController(_engine, ShowWindow, BeginExit);

        // Started at logon with --minimized: no window, so none of WPF's visual side is built
        // until the user opens it from the tray.
        AppArguments arguments = AppArguments.Parse(e.Args);
        if (arguments.Minimized) ScheduleTrim();
        else ShowWindow();

        // Everything from here on is behind the window rather than in front of it. Nothing above
        // touches the registry, the driver, the network or the process table, so what the user sees
        // is the configuration they left, on screen, before any of this is asked for.
        //
        // Not awaited: starting the engine opens the driver, enumerates every process and dials the
        // tunnels the filters route through, and the window must be up and painting while that
        // happens rather than after it. Failures land on the notice strip and in the log.
        _ = _engine.RestoreAsync();

#if LOCAL_API
        // Built always, listening only when Settings says so; a change there restarts it.
        _api = new LocalApiHost(
            new WindowApiBackend(_services, _engine, Dispatcher),
            () => _services.Config.LocalApi,
            _services.LoggerProvider);
        _services.LocalApiChanged += settings => _ = ApplyLocalApiAsync(settings);
        if (_services.Config.LocalApi.Enabled) _ = ApplyLocalApiAsync(_services.Config.LocalApi);
#endif

        // The Run-key entry an earlier version wrote never started anything — Windows skips a Run
        // entry that needs elevation — so it is cleared rather than left listed under the machine's
        // startup apps as something that plainly does not work. A registry write nobody is waiting
        // for, so it goes off the thread that paints.
        _ = Task.Run(StartupRegistration.RemoveLegacyRunKey);
    }

    /// <summary>
    /// Brings the window up, building it (and its resources and view model) if it is closed, or
    /// bringing it back from minimised or behind something else if it is open.
    /// </summary>
    private void ShowWindow()
    {
        if (IsExiting || _services is null || _engine is null) return;

        if (_window is null)
        {
            UiResources.Load(this);
            _mainViewModel = new MainViewModel(_services, _engine);
            _window = new MainWindow { DataContext = _mainViewModel };
            _window.Closed += OnWindowClosed;
            // Owner for the dialogs the view models open.
            MainWindow = _window;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _window!.Closed -= OnWindowClosed;
        _window.DataContext = null;
        _window = null;
        MainWindow = null;
        _mainViewModel?.Dispose();
        _mainViewModel = null;

        if (IsExiting) return;
        UiResources.Unload(this);
        ScheduleTrim();
    }

    /// <summary>Trims once the dispatcher is idle, after the closed window's last messages are processed.</summary>
    private void ScheduleTrim()
        => Dispatcher.BeginInvoke(() =>
        {
            if (_window is null && !IsExiting) MemoryTrimmer.Trim();
        }, DispatcherPriority.ApplicationIdle);

#if LOCAL_API
    // Fire-and-forget from the settings event, so nothing may escape: a port already taken is
    // reported on the Settings tab, anything else in the log.
    private async Task ApplyLocalApiAsync(LocalApiSettings settings)
    {
        try
        {
            string? error = await _api!.ApplyAsync(settings);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_engine is not null) _engine.LocalApiError = error;
            });
        }
        catch (Exception ex)
        {
            try
            {
                _services?.LoggerProvider.CreateLogger(nameof(LocalApiHost))
                    .LogError(ex, "local API could not be applied");
            }
            catch
            {
                // Logging is never worth taking the application down for.
            }
        }
    }
#endif

    protected override void OnExit(ExitEventArgs e)
    {
        // First, so no request is still reaching into the window while it goes away.
#if LOCAL_API
        _api?.DisposeAsync().AsTask().GetAwaiter().GetResult();
#endif

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
