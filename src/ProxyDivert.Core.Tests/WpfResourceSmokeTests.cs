using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ProxyDivert.Wpf.Localization;
using ProxyDivert.Wpf.Themes;
using ProxyDivert.Wpf.Views;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Loads the real application resources and instantiates every view.
//
// This is the cheapest guard against the class of bug XAML does not report at build time: a
// StaticResource that does not exist, a template that fails to load, a converter missing from
// App.xaml. The app itself requires Administrator (WinDivert loads a driver), so it cannot be
// launched from a test run — this covers the part that does not need the driver.
//
// WPF requires a single-threaded apartment, and xUnit gives no STA facts, so each test body runs
// on an STA thread of its own and rethrows whatever happened there.
public class WpfResourceSmokeTests
{
    [Fact]
    public void Application_resources_and_every_view_load()
    {
        RunOnStaThread(() =>
        {
            Application application = EnsureApplication();

            // Both palettes and both languages must be swappable at runtime; loading each one
            // proves the dictionary parses and the marker key the managers look for is present.
            ThemeManager.Apply(ThemeMode.Dark);
            ThemeManager.Apply(ThemeMode.Light);
            LocalizationManager.Apply(AppLanguage.English);
            LocalizationManager.Apply(AppLanguage.Vietnamese);

            var views = new List<UserControl>
            {
                new ProcessesView(),
                new OutboundsView(),
                new RulesView(),
                new ConnectionsView(),
                new LogView(),
                new SettingsView(),
            };

            foreach (UserControl view in views)
            {
                // Applying the template is what actually evaluates the styles and their resource
                // references; merely constructing the control would not.
                view.Measure(new Size(1200, 800));
                Assert.NotNull(view.Content);
            }

            Assert.NotNull(application.Resources["Str.App.Title"]);
            Assert.NotNull(application.Resources["Brush.Window.Background"]);
            Assert.NotNull(application.Resources["ByteSize"]);
        });
    }

    [Fact]
    public void Main_window_loads()
    {
        RunOnStaThread(() =>
        {
            EnsureApplication();
            var window = new MainWindow();
            window.Measure(new Size(1200, 800));
            Assert.NotNull(window.Content);
        });
    }

    // One Application instance per process: WPF refuses a second one, and xUnit may run both
    // facts in the same process.
    private static Application EnsureApplication()
    {
        if (Application.Current != null) return Application.Current;

        var application = new ProxyDivert.Wpf.App();
        // InitializeComponent is what merges App.xaml's dictionaries; OnStartup is deliberately
        // NOT called, since that would build the engine and open a window.
        application.InitializeComponent();
        return application;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw new Xunit.Sdk.XunitException($"WPF smoke test failed: {failure}");
    }
}
