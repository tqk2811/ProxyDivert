using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace ProxyDivert.Core.Tests;

// What every test that opens a real view needs, in one place: the single Application the process is
// allowed, the STA thread a window has to be built on, and the walk down a rendered visual tree.
//
// Tests that use it belong in the "WPF" collection — there is one Application per process, and two
// of these running at once would race to be the thread that creates it.
internal static class WpfHost
{
    public static Application EnsureApplication()
    {
        if (Application.Current == null)
        {
            var application = new ProxyDivert.Wpf.App();
            application.InitializeComponent();
        }

        // Default is OnLastWindowClose, which would shut the dispatcher down the first time a test
        // closes a window and leave every view after it unloaded — and so unchecked.
        //
        // Only the thread that built the Application may touch it, and each test runs on an STA
        // thread of its own. That is harmless: a window opened from a thread that does not own the
        // Application is not in its window list, so closing it shuts nothing down either way.
        if (Application.Current.CheckAccess())
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        return Application.Current;
    }

    public static void RunOnStaThread(Action action, string what)
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

        if (failure != null) throw new Xunit.Sdk.XunitException($"{what} failed: {failure}");
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;

            foreach (T deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
