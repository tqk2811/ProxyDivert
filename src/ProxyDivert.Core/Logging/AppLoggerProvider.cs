using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ProxyDivert.Core.Logging;

/// <summary>
/// Where every log line in this application ends up: the in-memory store the UI's log pane binds
/// to, and — when a path is configured — a trace file.
/// </summary>
/// <remarks>
/// The WinDivert libraries log through <c>ILogger&lt;T&gt;</c> and ship no sink of their own, so
/// this class is the whole answer to "where do the lines go". Registering it once means the packet
/// trace, the proxy library's tunnel logs and this application's own lines all land in the same
/// place, in order, instead of in three parallel logging paths.
///
/// The file can be pointed somewhere else while the application runs — see
/// <see cref="SetFilePath"/> — because the path is a user setting, and a setting the user cannot
/// change without a restart is a worse setting.
///
/// Lines reach the file through a queue drained by one background thread. The callers that matter
/// are the packet pump and the socket pump: a disk write on those threads is a stall the whole
/// machine's traffic waits behind, so logging here never touches the disk on the caller's thread
/// and never blocks. A flood past <see cref="QueueCapacity"/> is dropped rather than allowed to
/// slow the packet path down or grow without bound — the in-memory store still has those lines.
/// </remarks>
public sealed class AppLoggerProvider : ILoggerProvider
{
    // Deep enough to swallow a burst from the packet path, small enough to stay bounded memory.
    private const int QueueCapacity = 16384;

    private readonly object _fileLock = new object();
    private readonly InMemoryLogStore _store;
    private readonly LogLevel _minFileLevel;

    private readonly BlockingCollection<string> _pending =
        new BlockingCollection<string>(new ConcurrentQueue<string>(), QueueCapacity);
    private readonly Thread _writerThread;
    private volatile bool _disposed;
    private long _dropped;

    private StreamWriter? _writer;
    private string? _filePath;

    /// <param name="store">Receives every line, whatever the file does.</param>
    /// <param name="filePath">Trace file, or null for no file.</param>
    /// <param name="minFileLevel">
    /// The file is the only place the packet-level trace is readable, so it defaults to taking
    /// everything; the store is capped instead.
    /// </param>
    public AppLoggerProvider(InMemoryLogStore store, string? filePath = null, LogLevel minFileLevel = LogLevel.Trace)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _minFileLevel = minFileLevel;
        _writerThread = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = "ProxyDivert log writer",
            // Below the packet pumps on purpose: writing the trace must never win CPU from the
            // thing it is tracing.
            Priority = ThreadPriority.BelowNormal,
        };
        _writerThread.Start();
        SetFilePath(filePath);
    }

    public ILogger CreateLogger(string categoryName) => new StoreLogger(this, ShortName(categoryName));

    /// <summary>
    /// Starts writing to a different file (or stops writing to one, for null). The previous file is
    /// closed. Safe to call while the application is logging.
    /// </summary>
    public void SetFilePath(string? filePath)
    {
        lock (_fileLock)
        {
            if (string.Equals(_filePath, filePath, StringComparison.OrdinalIgnoreCase) && _writer != null) return;

            CloseWriter();
            _filePath = filePath;
            if (string.IsNullOrEmpty(filePath)) return;

            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
                // Append, never truncate: the file this path names is shared by every run that
                // lands in the same hour, and a second run must add to the record rather than
                // erase what the first one wrote.
                var fs = new FileStream(filePath!, FileMode.Append, FileAccess.Write, FileShare.Read);
                // AutoFlush off — the drain loop flushes when it runs out of lines, which batches a
                // burst into one write instead of one syscall per line.
                _writer = new StreamWriter(fs) { AutoFlush = false };
                _writer.WriteLine($"=== ProxyDivert log opened {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC ===");
                _writer.Flush();
            }
            catch (Exception ex)
            {
                // A trace file we cannot open must not take the application down with it. The store
                // still has every line, so the log pane keeps working.
                _writer = null;
                _store.Add(new LogEntry(DateTime.UtcNow, nameof(AppLoggerProvider), LogLevel.Warning,
                    $"cannot write the trace file {filePath}: {ex.Message}"));
            }
        }
    }

    private void Write(LogEntry entry)
    {
        _store.Add(entry);

        if (entry.Level < _minFileLevel) return;
        if (_disposed || _writer == null) return;

        // Hand off and return. TryAdd never waits, so a caller on the packet path is not held up
        // by the disk, and a burst past the queue's capacity is dropped instead of throttling it.
        if (!_pending.TryAdd(entry.ToString()))
            Interlocked.Increment(ref _dropped);
    }

    // The only thread that touches the file. Flushes when the queue momentarily empties, which is
    // the natural batching point: under a burst it writes many lines per syscall, and when idle
    // every line is on disk within one line's time.
    private void DrainLoop()
    {
        foreach (string line in _pending.GetConsumingEnumerable())
        {
            lock (_fileLock)
            {
                try
                {
                    if (_writer == null) continue;
                    _writer.WriteLine(line);
                    if (_pending.Count == 0)
                    {
                        long dropped = Interlocked.Exchange(ref _dropped, 0);
                        if (dropped > 0)
                            _writer.WriteLine($"=== {dropped} line(s) dropped: the log queue could not keep up ===");
                        _writer.Flush();
                    }
                }
                catch { /* a full or disconnected disk must not break the packet path */ }
            }
        }
    }

    private static string ShortName(string fullName)
    {
        int idx = fullName.LastIndexOf('.');
        return idx >= 0 ? fullName.Substring(idx + 1) : fullName;
    }

    private void CloseWriter()
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Let the drain loop finish what is already queued, then close the file. A bounded wait,
        // because a log file must not be what keeps the application from exiting.
        try { _pending.CompleteAdding(); } catch { }
        try { _writerThread.Join(TimeSpan.FromSeconds(2)); } catch { }
        lock (_fileLock) CloseWriter();
        try { _pending.Dispose(); } catch { }
    }

    private sealed class StoreLogger : ILogger
    {
        private readonly AppLoggerProvider _provider;
        private readonly string _category;

        public StoreLogger(AppLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string body = formatter != null ? formatter(state, exception) : state?.ToString() ?? "";
            if (exception != null) body += $" | {exception.GetType().Name}: {exception.Message}";

            // Flatten, so one logged event is one line: the trace file stays greppable and the log
            // pane's row count matches the number of events.
            _provider.Write(new LogEntry(
                DateTime.UtcNow, _category, logLevel,
                body.Replace("\r\n", " \\n ").Replace("\n", " \\n ")));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
