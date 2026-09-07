using System;
using System.Windows;
using System.Windows.Interop;
using ProxyDivert.Wpf.Views.Native;

namespace ProxyDivert.Wpf.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // WindowStartupLocation="CenterScreen" is not used here: it asks WPF to work the position out
    // before the window has an hwnd, and under PerMonitorV2 awareness that calculation runs
    // against the system DPI rather than the monitor's, which leaves the window parked wherever
    // Windows put it by default. By SourceInitialized the hwnd exists and its real pixel size is
    // known, so the position can simply be measured instead of predicted.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CenterOnScreen();
    }

    private void CenterOnScreen()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        if (!WindowNativeMethods.GetWindowRect(handle, out var bounds)) return;

        // The monitor the window already sits on, so moving it cannot cross a DPI boundary and
        // trigger the resize that would undo the centring. A window overlapping nothing (the
        // monitor it was last on is unplugged) falls back to the primary one.
        IntPtr monitor = WindowNativeMethods.MonitorFromWindow(
            handle, WindowNativeMethods.MONITOR_DEFAULTTOPRIMARY);

        var info = WindowNativeMethods.MONITORINFO.Create();
        if (!WindowNativeMethods.GetMonitorInfoW(monitor, ref info)) return;

        var work = info.rcWork;

        // Clamped to the top-left corner: a window taller than the work area would otherwise be
        // centred with its title bar above the screen, out of reach of the mouse.
        int left = Math.Max(work.Left, work.Left + ((work.Width - bounds.Width) / 2));
        int top = Math.Max(work.Top, work.Top + ((work.Height - bounds.Height) / 2));

        WindowNativeMethods.SetWindowPos(
            handle, IntPtr.Zero, left, top, 0, 0,
            WindowNativeMethods.SWP_NOSIZE | WindowNativeMethods.SWP_NOZORDER
                | WindowNativeMethods.SWP_NOACTIVATE);
    }

    // The system caption is gone, so its three buttons are ours to implement. They are plain
    // handlers rather than commands: none of this is state the view model has any say over.
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
