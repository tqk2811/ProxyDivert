using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ProxyDivert.Core.Logging;

/// <summary>
/// Bounded FIFO of diagnostic lines for the log pane, filled by <see cref="AppLoggerProvider"/>.
/// </summary>
/// <remarks>
/// Capacity is a hard cap, not a hint: the packet path can produce thousands of lines a second
/// with tracing on, and an unbounded list would be a memory leak with a progress bar.
/// </remarks>
public sealed class InMemoryLogStore
{
    private readonly ConcurrentQueue<LogEntry> _entries = new ConcurrentQueue<LogEntry>();
    private readonly int _capacity;

    public InMemoryLogStore(int capacity = 5000)
    {
        _capacity = capacity < 1 ? 1 : capacity;
    }

    /// <summary>Raised for each line accepted, on the thread that logged it. Keep handlers short.</summary>
    public event Action<LogEntry>? EntryAdded;

    public int Count => _entries.Count;

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();

    public void Add(LogEntry entry)
    {
        if (entry is null) return;
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }

        try { EntryAdded?.Invoke(entry); }
        catch { /* a broken subscriber must not break whatever was being logged */ }
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }
}
