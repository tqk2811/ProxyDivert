using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = new AppServices();

        // Appearance comes from the same config file as everything else, so the window opens the
        // way the user left it rather than flashing the default palette first.
        ThemeManager.Apply(ThemeManager.Parse(_services.Config.Theme));
        LocalizationManager.Apply(LocalizationManager.Parse(_services.Config.Language));

        _mainViewModel = new MainViewModel(_services);
        var window = new MainWindow { DataContext = _mainViewModel };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop the redirect before the process goes away: leaving WinDivert handles open would
        // keep the target's traffic pointed at a relay that no longer exists.
        _mainViewModel?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
