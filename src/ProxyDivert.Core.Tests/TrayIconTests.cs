using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using ProxyDivert.Wpf.Localization;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The notification-area icon is the whole user interface when the tool starts hidden at logon, so
// the ways it can fail silently are checked here: an icon Windows will not accept, and a menu whose
// text was never translated.
//
// The TaskbarIcon itself is never constructed. It attaches to Application in its constructor, so it
// can only be built on the thread that owns the Application — and every test here gets an STA
// thread of its own. That constraint is the subject of the first test rather than an obstacle to it.
[Collection("WPF")]
public class TrayIconTests
{
    private const string AppIcon = "pack://application:,,,/ProxyDivert;component/Assets/app.ico";

    // TaskbarIcon subscribes to the application's events inside its constructor. Merged into
    // Application.Resources it would therefore be built by anything that merely walks the
    // resources — off the UI thread that throws, which is exactly how this was found.
    [Fact]
    public void The_tray_icon_is_not_merged_into_the_application_resources()
    {
        RunOnStaThread(() =>
        {
            Application application = EnsureApplication();

            Assert.DoesNotContain(
                application.Resources.MergedDictionaries,
                dictionary => dictionary.Source?.OriginalString.Contains(
                    "TrayIcon.xaml", StringComparison.OrdinalIgnoreCase) == true);
        });
    }

    // A wrong path here has no symptom other than an empty space in the notification area, and the
    // icon has to survive the trip through System.Drawing that the tray library makes to get an
    // HICON — which is what rejects an .ico whose small sizes were written as PNG rather than as
    // bitmaps. Sixteen pixels because that is the size the notification area asks for.
    [Fact]
    public void The_application_icon_is_something_windows_will_show()
    {
        RunOnStaThread(() =>
        {
            EnsureApplication();

            using System.IO.Stream stream = Application.GetResourceStream(new Uri(AppIcon))!.Stream;
            using var icon = new System.Drawing.Icon(stream, 16, 16);

            Assert.Equal(16, icon.Width);
            Assert.NotEqual(IntPtr.Zero, icon.Handle);
        });
    }

    // A menu entry whose key is missing from a dictionary degrades quietly to the key itself, and a
    // menu reading "Str.Tray.Exit" is the sort of thing only a user ever notices. Comparing the two
    // dictionaries rather than checking a fixed list means an entry added later is covered too.
    [Fact]
    public void The_tray_strings_are_the_same_set_in_both_languages()
    {
        RunOnStaThread(() =>
        {
            EnsureApplication();

            LocalizationManager.Apply(AppLanguage.English);
            HashSet<string> english = TrayKeys();

            LocalizationManager.Apply(AppLanguage.Vietnamese);
            HashSet<string> vietnamese = TrayKeys();

            // The menu, the tooltip and the balloon shown the first time the window disappears.
            Assert.True(english.Count >= 7, $"only {english.Count} tray strings found");
            Assert.Equal(english.OrderBy(key => key), vietnamese.OrderBy(key => key));
        });
    }

    private static HashSet<string> TrayKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (ResourceDictionary dictionary in Application.Current.Resources.MergedDictionaries)
        {
            foreach (object key in dictionary.Keys)
            {
                if (key is string name
                    && name.StartsWith("Str.Tray.", StringComparison.Ordinal)
                    && dictionary[key] is string)
                {
                    keys.Add(name);
                }
            }
        }

        return keys;
    }

    private static Application EnsureApplication()
    {
        if (Application.Current != null) return Application.Current;

        var application = new ProxyDivert.Wpf.App();
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

        if (failure != null) throw new Xunit.Sdk.XunitException($"WPF tray test failed: {failure}");
    }
}
