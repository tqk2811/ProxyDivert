using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace ProxyDivert.Helpers;

/// <summary>
/// Gives memory back to Windows after the window is gone: a full compacting GC, then a trim of the
/// working set, so the process sitting in the tray shows only what it really touches. Private bytes
/// do not drop as far: the native WPF assemblies stay loaded once the window has been shown.
/// </summary>
public static class MemoryTrimmer
{
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        using Process process = Process.GetCurrentProcess();
        // (-1, -1) removes as many pages as possible; they fault back in when touched.
        if (!SetProcessWorkingSetSize(process.Handle, -1, -1))
            Trace.WriteLine($"SetProcessWorkingSetSize failed: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);
}
