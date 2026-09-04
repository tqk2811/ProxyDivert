using System;
using System.IO;
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
/// </remarks>
public sealed class AppLoggerProvider : ILoggerProvider
{
    private readonly object _fileLock = new object();
    private readonly InMemoryLogStore _store;
    private readonly LogLevel _minFileLevel;

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
                // Delete first: an editor holding a read handle would otherwise let stale bytes
                // survive past the truncation FileMode.Create is supposed to perform.
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                var fs = new FileStream(filePath!, FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(fs) { AutoFlush = true };
                _writer.WriteLine($"=== ProxyDivert log opened {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC ===");
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
        lock (_fileLock)
        {
            try { _writer?.WriteLine(entry.ToString()); }
            catch { /* a full or disconnected disk must not break the packet path */ }
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
        lock (_fileLock) CloseWriter();
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
