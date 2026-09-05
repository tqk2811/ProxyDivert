using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using TqkLibrary.WinDivert.ProcessControl.Models;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Remembers the command line of every process it has been asked about, so a rule that matches on
/// arguments is re-tested from memory instead of from WMI.
/// </summary>
/// <remarks>
/// A command line never changes after a process has started, so the only reason to read one twice
/// is having forgotten it. That mattered enough to build this: one WMI query for a single process
/// costs about 210ms on a normal machine — the same as one query for all 448 of them — so
/// re-testing 30 redirected processes after a rule edit took five to ten SECONDS, on the UI thread,
/// which is what saving a setting used to feel like.
///
/// The invariants everything here rests on:
///
///   1. A key present means "already asked about this pid" — never ask again.
///   2. A null command line under a key is an ANSWER ("asked, could not be read"), not a gap. This
///      is the one that pays: roughly half the processes on a machine are services whose command
///      line this tool may not read, and without a way to record that, every pass would ask about
///      every one of them again.
///   3. The stored process name says who the answer belongs to. Windows hands pids out again, so an
///      answer is only served when the name still matches.
///   4. Only processes believed to be alive are kept — <see cref="Forget"/> on a stop event,
///      <see cref="Retain"/> on every scan.
///   5. Every member is callable from any thread. The worst a race can cost is one duplicate query;
///      it can never produce a wrong answer, which is why <see cref="Get"/> holds no lock.
/// </remarks>
public sealed class ProcessCommandLineCache
{
    // How many unknown processes make one machine-wide query the cheaper option. Measured: a sweep
    // of 448 processes takes ~231ms, a single-process query ~212ms — so the break-even is barely
    // above two, and anything from three up is worth doing in one go.
    private const int BatchQueryThreshold = 3;

    // A ceiling only for the case where stop events go missing (a dropped WMI event, or a machine
    // that never scans again because the user left the tool running untouched). Passing it empties
    // the table rather than evicting the oldest: there is no age to sort by, the next sweep rebuilds
    // it in one query, and a rebuild is far cheaper than the bookkeeping an LRU would need.
    private const int DefaultMaxEntries = 4096;

    private readonly IProcessCommandLineReader _reader;
    private readonly ILogger _logger;
    private readonly Func<uint, bool> _runsInServiceSession;
    private readonly int _maxEntries;

    private readonly ConcurrentDictionary<uint, Entry> _entries = new ConcurrentDictionary<uint, Entry>();

    // Held only while filling gaps. Two threads can arrive at once — the UI thread through a rule
    // edit and the poll thread on its own schedule — and the lock turns a doubled sweep into one
    // sweep plus a wait that finds the work already done.
    private readonly object _sweepLock = new object();

    public ProcessCommandLineCache(
        IProcessCommandLineReader reader,
        ILogger logger,
        Func<uint, bool>? runsInServiceSession = null,
        int maxEntries = DefaultMaxEntries)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runsInServiceSession = runsInServiceSession ?? ProcessSessionLookup.RunsInServiceSession;
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
    }

    /// <summary>How many processes have an answer stored. For tests and the debug log.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// The command line of one process, read at most once. Null means either "this process has
    /// none that can be read" or "the pid is gone" — both of which leave a rule about arguments
    /// undecided rather than false, which is the safe direction (see ProcessRuleMatcher).
    /// </summary>
    /// <param name="processName">
    /// Who the answer is expected to belong to. Supplying it is what stops a recycled pid from
    /// being served the previous owner's command line.
    /// </param>
    public string? Get(uint processId, string? processName = null)
    {
        if (processId == 0) return null;

        if (_entries.TryGetValue(processId, out Entry known) && known.Belongs(processName))
            return known.CommandLine;

        // Deliberately outside _sweepLock: this is the path a process-start event takes, and how
        // fast we attach to a new process decides whether its first connection is redirected.
        string? commandLine = ReadOne(processId);
        Store(processId, new Entry(processName, commandLine));
        TrimIfTooLarge();
        return commandLine;
    }

    /// <summary>
    /// Makes sure every process in the list has an answer stored, in as few queries as possible.
    /// </summary>
    /// <remarks>
    /// This doubles as the priming step. When the user turns on their first rule about arguments,
    /// the table is empty, every process is a gap, and the gaps are filled by exactly one sweep —
    /// so no separate "have we primed yet" flag is needed, and none should be added.
    /// </remarks>
    public void EnsureLoaded(IReadOnlyList<ProcessInfo> processes)
    {
        if (processes is null) throw new ArgumentNullException(nameof(processes));

        lock (_sweepLock)
        {
            var missing = new List<ProcessInfo>();
            foreach (ProcessInfo process in processes)
            {
                if (process.Id == 0) continue;
                if (_entries.TryGetValue(process.Id, out Entry known) && known.Belongs(process.Name)) continue;
                missing.Add(process);
            }

            if (missing.Count == 0) return;

            if (missing.Count >= BatchQueryThreshold)
            {
                IReadOnlyDictionary<uint, string> sweep = _reader.ReadAll();
                foreach (ProcessInfo process in missing)
                {
                    // Absent from the sweep means unreadable, and it is recorded as such — see
                    // invariant 2. Only the processes asked about are stored, so nothing the caller
                    // does not believe to be running gets in (invariant 4).
                    sweep.TryGetValue(process.Id, out string? commandLine);
                    Store(process.Id, new Entry(process.Name, commandLine));
                }
                _logger.LogDebug("read {Count} command lines in one sweep of the machine", missing.Count);
            }
            else
            {
                foreach (ProcessInfo process in missing)
                    Store(process.Id, new Entry(process.Name, ReadOne(process.Id)));
                _logger.LogDebug("read {Count} command lines one process at a time", missing.Count);
            }

            TrimIfTooLarge();
        }
    }

    /// <summary>
    /// Drops every answer that does not belong to a process in this list — those that have exited,
    /// and those whose pid has since been handed to a different program.
    /// </summary>
    /// <remarks>
    /// Free (no query), so the caller runs it on every scan whether or not any rule currently asks
    /// about arguments. That is what keeps invariant 4 true across a spell with the feature turned
    /// off, when nothing else is touching this table.
    /// </remarks>
    public void Retain(IReadOnlyList<ProcessInfo> processes)
    {
        if (processes is null) throw new ArgumentNullException(nameof(processes));

        var alive = new Dictionary<uint, string>(processes.Count);
        foreach (ProcessInfo process in processes) alive[process.Id] = process.Name;

        foreach (KeyValuePair<uint, Entry> kv in _entries)
        {
            if (alive.TryGetValue(kv.Key, out string? name) && kv.Value.Belongs(name)) continue;
            _entries.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>The pid is gone, or has just been handed to something else.</summary>
    public void Forget(uint processId) => _entries.TryRemove(processId, out _);

    public void Clear() => _entries.Clear();

    // A service process runs under an account this tool cannot open, so the query is known to come
    // back empty: skip it and record the null it would have produced anyway.
    private string? ReadOne(uint processId)
        => _runsInServiceSession(processId) ? null : _reader.Read(processId);

    private void Store(uint processId, Entry entry) => _entries[processId] = entry;

    private void TrimIfTooLarge()
    {
        if (_entries.Count <= _maxEntries) return;
        _logger.LogDebug("the command line table passed {Max} entries and was emptied; the next scan rebuilds it", _maxEntries);
        _entries.Clear();
    }

    // One answer, and who it belongs to.
    private readonly struct Entry
    {
        public Entry(string? processName, string? commandLine)
        {
            ProcessName = processName;
            CommandLine = commandLine;
        }

        public string? ProcessName { get; }

        /// <summary>Null is an answer — "asked, could not be read" — not a missing value.</summary>
        public string? CommandLine { get; }

        // An answer stored without a name cannot be checked, so it is served as it stands; the
        // caller that stored it that way accepted the risk.
        public bool Belongs(string? processName)
            => processName is null
            || ProcessName is null
            || string.Equals(ProcessName, processName, StringComparison.OrdinalIgnoreCase);
    }
}
