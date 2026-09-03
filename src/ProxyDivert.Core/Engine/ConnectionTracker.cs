using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Engine.Models;

namespace ProxyDivert.Core.Engine;

// Keeps the live connection list plus a bounded history of finished ones, and raises events a UI
// can bind to.
//
// The history cap exists because a browser opens thousands of connections in an afternoon: without
// it, a tool left running overnight would hold every one of them. Oldest entries are dropped first.
public sealed class ConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, ConnectionInfo> _active = new ConcurrentDictionary<Guid, ConnectionInfo>();
    private readonly ConcurrentQueue<ConnectionInfo> _history = new ConcurrentQueue<ConnectionInfo>();
    private readonly int _historyCapacity;

    public ConnectionTracker(int historyCapacity = 2000)
    {
        _historyCapacity = historyCapacity < 1 ? 1 : historyCapacity;
    }

    public event Action<ConnectionInfo>? Opened;

    // Raised when routing has been decided (host name and outbound are known). The UI shows the
    // row as soon as it opens and fills these in a moment later.
    public event Action<ConnectionInfo>? Updated;

    public event Action<ConnectionInfo>? Closed;

    public IReadOnlyCollection<ConnectionInfo> Active => _active.Values.ToList();

    public IReadOnlyCollection<ConnectionInfo> History => _history.ToArray();

    public int ActiveCount => _active.Count;

    public void Open(ConnectionInfo connection)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        _active[connection.Id] = connection;
        Opened?.Invoke(connection);
    }

    public void Update(ConnectionInfo connection)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        Updated?.Invoke(connection);
    }

    public void Close(ConnectionInfo connection)
    {
        if (connection is null) throw new ArgumentNullException(nameof(connection));
        _active.TryRemove(connection.Id, out _);

        _history.Enqueue(connection);
        while (_history.Count > _historyCapacity && _history.TryDequeue(out _)) { }

        Closed?.Invoke(connection);
    }

    public void ClearHistory()
    {
        while (_history.TryDequeue(out _)) { }
    }
}
