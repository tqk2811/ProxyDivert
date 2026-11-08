using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using ProxyDivert.Localization;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The notification-area icon is the whole user interface when the tool starts hidden at logon, so
// the ways it can fail silently are checked here: an icon Windows will not accept, and a menu whose
// text was never translated.
//
// The icon itself is never added: Shell_NotifyIcon would put a real icon in the test machine's
// notification area.
[Collection("WPF")]
public class TrayIconTests
{
    private const string AppIcon = "pack://application:,,,/ProxyDivert;component/Assets/app.ico";

    // The tray takes the exe's own icon (ApplicationIcon, built from this file), and a broken one has
    // no symptom other than the stock icon in the notification area. Loaded the way the shell does,
    // through LoadImage into an HICON; sixteen pixels because that is the size the notification
    // area asks for.
    [Fact]
    public void The_application_icon_is_something_windows_will_show()
    {
        RunOnStaThread(() =>
        {
            EnsureApplication();

            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"proxydivert-icon-{Guid.NewGuid():N}.ico");
            try
            {
                using (System.IO.Stream stream = Application.GetResourceStream(new Uri(AppIcon))!.Stream)
                using (System.IO.FileStream file = System.IO.File.Create(path))
                {
                    stream.CopyTo(file);
                }

                IntPtr icon = LoadImage(IntPtr.Zero, path, ImageIcon, 16, 16, LoadFromFile);
                Assert.NotEqual(IntPtr.Zero, icon);
                DestroyIcon(icon);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
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

            // The menu (open, engine, exit) and the tooltip.
            Assert.True(english.Count >= 4, $"only {english.Count} tray strings found");
            Assert.Equal(english.OrderBy(key => key), vietnamese.OrderBy(key => key));
        });
    }

    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x10;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "LoadImageW")]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

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

        var application = new ProxyDivert.App();
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
