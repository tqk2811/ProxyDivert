using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TqkLibrary.WinDivert.Logging;
using TqkLibrary.WinDivert.Logging.Models;

namespace ProxyDivert.Core.Logging;

// Bounded FIFO of diagnostic lines for the UI's log pane. Subscribes to a RedirectLogger, so the
// same stream that goes to the trace file also feeds the window without a second logging path.
//
// Capacity is a hard cap: the packet path can produce thousands of lines a second with tracing on,
// and an unbounded list would be a memory leak with a progress bar.
public sealed class InMemoryLogStore : IDisposable
{
    private readonly ConcurrentQueue<RedirectLogEntry> _entries = new ConcurrentQueue<RedirectLogEntry>();
    private readonly RedirectLogger? _source;
    private readonly int _capacity;

    public InMemoryLogStore(RedirectLogger? source = null, int capacity = 5000)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _source = source;
        if (_source != null) _source.EntryWritten += OnEntryWritten;
    }

    /// <summary>Raised for each line accepted, on the thread that logged it.</summary>
    public event Action<RedirectLogEntry>? EntryAdded;

    public int Count => _entries.Count;

    public IReadOnlyList<RedirectLogEntry> Snapshot() => _entries.ToArray();

    public void Add(RedirectLogEntry entry)
    {
        if (entry is null) return;
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }
        EntryAdded?.Invoke(entry);
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    private void OnEntryWritten(RedirectLogEntry entry) => Add(entry);

    public void Dispose()
    {
        if (_source != null) _source.EntryWritten -= OnEntryWritten;
        Clear();
    }
}
