using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProxyDivert.Core.Processes.Interfaces;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Processes.Native;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Lists every process through NtQuerySystemInformation — one syscall for the whole machine.
/// </summary>
/// <remarks>
/// This is the same call Process.GetProcesses() makes underneath, minus the object it allocates
/// per process and the handle it opens to answer questions nobody asked. On a machine with ~450
/// processes it returns in single-digit milliseconds, which is what makes a full rescan cheap
/// enough to be the fallback when process events are unavailable.
///
/// It also returns the parent process id for EVERY process, which the managed API does not expose
/// at all. That single field is what lets a filter adopt the children of a program that was already
/// running before the tool started.
///
/// Stateless, so one instance serves the whole application; every call is a fresh snapshot.
/// </remarks>
public sealed class NativeProcessLister : IProcessLister
{
    // Enough for a busy machine on the first try, so the usual case is one syscall rather than a
    // measure-then-fetch pair. It grows from here when the kernel says it is not enough.
    private const int InitialBufferBytes = 512 * 1024;

    // The list can grow between the size being reported and the buffer being filled, so a couple of
    // rounds are expected. A number this size means something is wrong, not that the machine is busy.
    private const int MaxAttempts = 8;

    private readonly IProcessLister _fallback;

    /// <param name="fallback">
    /// Asked when the syscall cannot answer. Defaults to <see cref="ManagedProcessLister"/>, which
    /// costs more and knows no parents, but keeps the table populated on a machine where the
    /// undocumented call has been blocked.
    /// </param>
    public NativeProcessLister(IProcessLister? fallback = null)
    {
        _fallback = fallback ?? new ManagedProcessLister();
    }

    public IReadOnlyList<ProcessSnapshot> ListAll()
    {
        int size = InitialBufferBytes;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);

                int status = ProcessNativeMethods.NtQuerySystemInformation(
                    ProcessNativeMethods.SystemProcessInformationClass, buffer, size, out int needed);

                if (status == ProcessNativeMethods.STATUS_SUCCESS) return Parse(buffer);

                if (status != ProcessNativeMethods.STATUS_INFO_LENGTH_MISMATCH
                    && status != ProcessNativeMethods.STATUS_BUFFER_TOO_SMALL
                    && status != ProcessNativeMethods.STATUS_BUFFER_OVERFLOW)
                {
                    return _fallback.ListAll();
                }

                // Head-room on top of what the kernel asked for: processes keep starting while we
                // allocate, and coming back for a third round costs more than the slack does.
                size = Math.Max(needed + (needed / 8), size * 2);
            }

            return _fallback.ListAll();
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    // The entries form a singly linked list inside the buffer: each one says how far the next is,
    // and an offset of zero ends it. Walking it that way rather than by struct size is what lets
    // the declaration stop at SessionId and ignore the counters that follow.
    private static IReadOnlyList<ProcessSnapshot> Parse(IntPtr buffer)
    {
        var processes = new List<ProcessSnapshot>(512);
        IntPtr entry = buffer;

        while (true)
        {
            ProcessNativeMethods.SYSTEM_PROCESS_INFORMATION info =
                Marshal.PtrToStructure<ProcessNativeMethods.SYSTEM_PROCESS_INFORMATION>(entry);

            uint pid = (uint)info.UniqueProcessId.ToInt64();

            // Pid 0 is the idle "process", an accounting fiction rather than something that can own
            // a socket. Everything else belongs in the table, System included.
            if (pid != 0)
            {
                processes.Add(new ProcessSnapshot
                {
                    ProcessId = pid,
                    Name = ReadImageName(info.ImageName, pid),
                    ParentProcessId = (uint)info.InheritedFromUniqueProcessId.ToInt64(),
                    StartedUtc = ToDateTime(info.CreateTime),
                    SessionId = info.SessionId,
                });
            }

            if (info.NextEntryOffset == 0) break;
            entry += (int)info.NextEntryOffset;
        }

        return processes;
    }

    // Length is in bytes; the string is not null-terminated, so it is read by length rather than by
    // scanning for a terminator. The kernel leaves it empty for System, which still needs a name.
    private static string ReadImageName(ProcessNativeMethods.UNICODE_STRING name, uint pid)
    {
        if (name.Buffer == IntPtr.Zero || name.Length == 0) return pid == 4 ? "System" : $"pid {pid}";
        return Marshal.PtrToStringUni(name.Buffer, name.Length / 2) ?? $"pid {pid}";
    }

    // A FILETIME, the same shape the WMI event times come in. System reports zero, which is not a
    // time this machine can make sense of.
    private static DateTime ToDateTime(long fileTime)
    {
        if (fileTime <= 0) return DateTime.MinValue;
        try { return DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }
}
