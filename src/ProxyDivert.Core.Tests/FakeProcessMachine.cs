using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Processes.Interfaces;
using ProxyDivert.Core.Processes.Models;

namespace ProxyDivert.Core.Tests;

/// <summary>
/// A machine whose process list the test decides, standing in for the two kernel calls the process
/// table is built on.
/// </summary>
/// <remarks>
/// It counts how often the details of a process are read, because that count is the property the
/// whole design turns on: a command line never changes, so reading one twice means the table forgot
/// it — which is exactly what used to make saving a filter freeze the window.
/// </remarks>
public sealed class FakeProcessMachine : IProcessLister, IProcessDetailsReader
{
    private readonly Dictionary<uint, Entry> _running = new Dictionary<uint, Entry>();

    /// <summary>How many times a detail read was asked for, per pid.</summary>
    public Dictionary<uint, int> DetailReads { get; } = new Dictionary<uint, int>();

    public int TotalDetailReads => DetailReads.Values.Sum();

    /// <summary>Pids whose details cannot be read at all — a protected process.</summary>
    public HashSet<uint> Unreadable { get; } = new HashSet<uint>();

    public FakeProcessMachine Start(
        uint pid, string name, string? path = null, string? commandLine = null,
        uint parentPid = 0, DateTime? startedUtc = null)
    {
        _running[pid] = new Entry(
            name, path ?? $@"C:\Programs\{name}", commandLine ?? $@"""C:\Programs\{name}""",
            parentPid, startedUtc ?? new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc).AddSeconds(pid));
        return this;
    }

    public FakeProcessMachine Exit(uint pid)
    {
        _running.Remove(pid);
        return this;
    }

    public IReadOnlyList<ProcessSnapshot> ListAll()
        => _running.Select(kv => new ProcessSnapshot
        {
            ProcessId = kv.Key,
            Name = kv.Value.Name,
            ParentProcessId = kv.Value.ParentPid,
            StartedUtc = kv.Value.StartedUtc,
        }).ToList();

    public ProcessDetails Read(uint processId)
    {
        DetailReads[processId] = DetailReads.TryGetValue(processId, out int seen) ? seen + 1 : 1;

        if (Unreadable.Contains(processId) || !_running.TryGetValue(processId, out Entry? entry))
            return ProcessDetails.None;

        return new ProcessDetails(entry.Path, entry.CommandLine, entry.StartedUtc);
    }

    private sealed record Entry(
        string Name, string? Path, string? CommandLine, uint ParentPid, DateTime StartedUtc);
}
