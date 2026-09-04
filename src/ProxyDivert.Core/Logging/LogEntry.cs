using System;
using Microsoft.Extensions.Logging;

namespace ProxyDivert.Core.Logging;

/// <summary>
/// One diagnostic line, as the UI wants it: already formatted, already timestamped, and carrying
/// the category and level so a log pane can filter and colour without re-parsing text.
/// </summary>
public sealed class LogEntry
{
    public DateTime TimestampUtc { get; }

    /// <summary>Short form of the logger category — the type name, without its namespace.</summary>
    public string Category { get; }

    public LogLevel Level { get; }

    public string Message { get; }

    public LogEntry(DateTime timestampUtc, string category, LogLevel level, string message)
    {
        TimestampUtc = timestampUtc;
        Category = category;
        Level = level;
        Message = message;
    }

    public override string ToString()
        => $"{TimestampUtc.ToLocalTime():HH:mm:ss.fff} [{LevelChar}] {Category}: {Message}";

    /// <summary>Single letter for the level, for a compact log pane column.</summary>
    public char LevelChar => Level switch
    {
        LogLevel.Trace => 'T',
        LogLevel.Debug => 'D',
        LogLevel.Information => 'I',
        LogLevel.Warning => 'W',
        LogLevel.Error => 'E',
        LogLevel.Critical => 'C',
        _ => '?',
    };
}
