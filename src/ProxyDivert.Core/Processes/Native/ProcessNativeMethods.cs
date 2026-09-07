using System;
using System.Runtime.InteropServices;

namespace ProxyDivert.Core.Processes.Native;

/// <summary>
/// The Win32 and NT entry points the process table is built on. P/Invoke declarations have to be
/// static, so they live here in one place rather than being repeated in every reader.
/// </summary>
/// <remarks>
/// Every call here was picked for being the cheapest way to get one specific fact, because the
/// table is filled for the whole machine at startup and again for every process that starts:
///
///   * NtQuerySystemInformation(SystemProcessInformation) — one syscall for the entire process
///     list, parent process ids included. Process.GetProcesses() calls exactly this underneath and
///     then allocates an object per process; going straight to the syscall is what turns a scan
///     from hundreds of milliseconds into about two.
///   * QueryFullProcessImageNameW — the full path from a handle opened with
///     PROCESS_QUERY_LIMITED_INFORMATION, the weakest right there is. Process.MainModule needs
///     PROCESS_VM_READ and walks the module list to answer the same question.
///   * NtQueryInformationProcess(ProcessCommandLineInformation) — the command line WITHOUT reading
///     the PEB. It needs no PROCESS_VM_READ, and, unlike a PEB read, it does not care whether the
///     target is 32- or 64-bit: the kernel formats the answer. Windows 8.1 and later.
///
/// The undocumented information classes are stable — ProcessHacker and Sysinternals have shipped
/// against them for two decades — but a wrong answer must never take the tool down, so every
/// caller here treats a failure as "unknown" rather than as an error.
/// </remarks>
internal static class ProcessNativeMethods
{
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    internal const int SystemProcessInformationClass = 5;
    internal const int ProcessCommandLineInformationClass = 60;

    internal const int STATUS_SUCCESS = 0;
    internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    internal const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    internal const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);

    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint SE_PRIVILEGE_ENABLED = 0x0002;

    /// <summary>
    /// A counted string as the kernel returns it. On x64 the pointer is 8-byte aligned, so the
    /// struct is 16 bytes with four bytes of padding after MaximumLength — laid out by the
    /// runtime rather than spelled out, which is why the fields are declared in this order.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UNICODE_STRING
    {
        public ushort Length;           // in BYTES, not characters
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>
    /// One entry of the SystemProcessInformation array, truncated at SessionId — everything after
    /// it is memory and I/O counters this tool has no use for, and the entries are walked by
    /// NextEntryOffset rather than by size, so stopping early costs nothing.
    /// </summary>
    /// <remarks>
    /// The offsets that matter on x64: ImageName at 0x38, UniqueProcessId at 0x50,
    /// InheritedFromUniqueProcessId at 0x58, SessionId at 0x64. Sequential layout reproduces them
    /// exactly, including the four bytes of padding the runtime inserts after BasePriority to
    /// align the handles that follow.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, IntPtr processInformation,
        int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);
}
