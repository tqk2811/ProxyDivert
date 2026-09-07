using System;
using System.Runtime.InteropServices;
using ProxyDivert.Core.Processes.Interfaces;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Processes.Native;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Reads a process's path and command line straight from the kernel, in about the time a WMI query
/// takes to be parsed.
/// </summary>
/// <remarks>
/// The command line comes from NtQueryInformationProcess(ProcessCommandLineInformation), which is
/// the whole reason this class exists. The two alternatives both cost far more:
///
///   * WMI (Win32_Process.CommandLine) answers in roughly 210 MILLISECONDS for a single process —
///     the cost that used to make saving a filter freeze the window;
///   * reading the PEB by hand needs PROCESS_VM_READ, three or four ReadProcessMemory round trips,
///     and a second struct layout for 32-bit processes, because a 64-bit reader sees a different
///     PEB shape in a WOW64 target.
///
/// This information class needs neither: PROCESS_QUERY_LIMITED_INFORMATION is enough, the kernel
/// formats the answer, and a 32-bit target is no different from a 64-bit one. It arrived in
/// Windows 8.1; on anything older every read simply comes back empty and the WMI reader behind it
/// answers instead.
///
/// One handle serves both facts, because both fail for the same reasons and opening twice would
/// double the only expensive part.
/// </remarks>
public sealed class NativeProcessDetailsReader : IProcessDetailsReader
{
    // Long paths are a thing, but a path this long in a process listing means something is wrong
    // rather than deeply nested. Two rounds are allowed before giving up.
    private const int InitialPathChars = 1024;
    private const int MaxPathChars = 32768;
    private const int ErrorInsufficientBuffer = 122;

    public ProcessDetails Read(uint processId)
    {
        // Pid 4 (System) and its kernel threads cannot be opened at all, whatever the caller holds.
        if (processId <= 4) return ProcessDetails.None;

        IntPtr handle = ProcessNativeMethods.OpenProcess(
            ProcessNativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return ProcessDetails.None;

        try
        {
            return new ProcessDetails(ReadPath(handle), ReadCommandLine(handle));
        }
        finally
        {
            ProcessNativeMethods.CloseHandle(handle);
        }
    }

    private static string? ReadPath(IntPtr handle)
    {
        for (int chars = InitialPathChars; chars <= MaxPathChars; chars *= 4)
        {
            var buffer = new char[chars];
            uint size = (uint)chars;

            // Flags 0 asks for the Win32 path ("C:\...") rather than the native one ("\Device\...").
            if (ProcessNativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                return new string(buffer, 0, (int)size);

            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;
        }
        return null;
    }

    private static string? ReadCommandLine(IntPtr handle)
    {
        // Asked with no buffer first: the kernel reports the exact size, which is a UNICODE_STRING
        // header plus the characters themselves, and it is never the same twice for two processes.
        int status = ProcessNativeMethods.NtQueryInformationProcess(
            handle, ProcessNativeMethods.ProcessCommandLineInformationClass, IntPtr.Zero, 0, out int needed);

        if (status != ProcessNativeMethods.STATUS_INFO_LENGTH_MISMATCH
            && status != ProcessNativeMethods.STATUS_BUFFER_TOO_SMALL
            && status != ProcessNativeMethods.STATUS_BUFFER_OVERFLOW)
        {
            // Also the path taken on Windows 8 and older, where the information class does not
            // exist: STATUS_INVALID_INFO_CLASS, and the caller falls back to WMI.
            return null;
        }
        if (needed <= 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal(needed);
        try
        {
            status = ProcessNativeMethods.NtQueryInformationProcess(
                handle, ProcessNativeMethods.ProcessCommandLineInformationClass, buffer, needed, out _);
            if (status != ProcessNativeMethods.STATUS_SUCCESS) return null;

            var text = Marshal.PtrToStructure<ProcessNativeMethods.UNICODE_STRING>(buffer);
            if (text.Buffer == IntPtr.Zero || text.Length == 0) return null;

            // Length is in bytes and the string is not null-terminated, so it is read by length.
            return Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
