using System;
using System.Runtime.InteropServices;

namespace ProxyDivert.Wpf.Views.Native;

/// <summary>
/// The Win32 entry points used to place a window on screen. P/Invoke declarations have to be
/// static, so they live here rather than inside the code-behind that calls them.
/// </summary>
/// <remarks>
/// Placement is done in device pixels on purpose. The app declares PerMonitorV2 DPI awareness, so
/// WPF's own Left/Top are device-independent units scaled by whatever DPI the window currently
/// has — a value that is not settled yet at the moment the startup position has to be chosen, and
/// which differs per monitor on a mixed-DPI desktop. GetWindowRect and SetWindowPos speak the one
/// unit both Windows and the monitor agree on, so the arithmetic below needs no DPI at all.
/// </remarks>
internal static class WindowNativeMethods
{
    /// <summary>Fall back to the primary monitor when the window overlaps none.</summary>
    internal const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// A Win32 rectangle: right and bottom are exclusive, so the width is right - left.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    /// <summary>
    /// Monitor geometry. rcWork is the part left over once the taskbar and any other appbar has
    /// taken its share, which is what a window should be centred in — rcMonitor would push it
    /// down behind the taskbar by half the taskbar's height.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        /// <summary>GetMonitorInfo rejects the call unless cbSize is filled in first.</summary>
        public static MONITORINFO Create() => new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
