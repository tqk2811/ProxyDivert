using System;
using System.Collections.Generic;
using ProxyDivert.Core.Processes.Interfaces;
using ProxyDivert.Core.Processes.Models;
using SysProcess = System.Diagnostics.Process;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Lists processes through System.Diagnostics, as the way back when the syscall behind
/// <see cref="NativeProcessLister"/> is unavailable.
/// </summary>
/// <remarks>
/// Slower — it allocates an object per process and opens a handle to answer StartTime — and it
/// cannot report a parent at all, so a filter's "include children" only reaches processes that
/// start while the tool is running. That is a degraded table, not a broken one, which is the point:
/// a machine where the syscall fails still gets redirected traffic.
/// </remarks>
public sealed class ManagedProcessLister : IProcessLister
{
    public IReadOnlyList<ProcessSnapshot> ListAll()
    {
        var processes = new List<ProcessSnapshot>(512);
        foreach (SysProcess process in SysProcess.GetProcesses())
        {
            try
            {
                uint pid = (uint)process.Id;
                if (pid == 0) continue;

                processes.Add(new ProcessSnapshot
                {
                    ProcessId = pid,
                    // GetProcesses reports the name without its extension; the table's spelling
                    // includes it, so it is put back rather than left for filters to guess at.
                    Name = WithExeExtension(process.ProcessName),
                    ParentProcessId = 0,
                    StartedUtc = TryGetStartTime(process),
                    SessionId = (uint)process.SessionId,
                });
            }
            catch
            {
                // Exited between being listed and being read — skip it rather than lose the rest.
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }
        return processes;
    }

    private static string WithExeExtension(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";

    private static DateTime TryGetStartTime(SysProcess process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch { return DateTime.MinValue; }
    }
}
