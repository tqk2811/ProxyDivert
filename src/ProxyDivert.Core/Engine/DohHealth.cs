using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using TqkLibrary.WinDivert.SecureDns.Helpers;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// Whether the DNS over HTTPS lookups going out through one outbound are working, logged by state
/// change: one Warning once enough lookups in a row failed, one Information when they recover (only
/// if the Warning was logged), and a rate-limited reminder while they keep failing.
/// </summary>
/// <remarks>Shared by every resolver of the outbound (short and normal timeout); lock-free.</remarks>
internal sealed class DohHealth
{
    /// <summary>Failures in a row before the first Warning: a lone timeout is not an outage.</summary>
    public const int DefaultFailureThreshold = 3;

    /// <summary>Least time between two "still failing" reminders.</summary>
    public static readonly TimeSpan DefaultReminderInterval = TimeSpan.FromSeconds(30);

    private readonly string _outboundName;
    private readonly ILogger _logger;
    private readonly int _failureThreshold;
    private readonly LogThrottle _reminder;
    private int _failuresInARow;
    private int _reported;

    /// <param name="failureThreshold">Consecutive failures that make the outage worth a Warning.</param>
    /// <param name="reminderInterval">Least time between "still failing" lines while reported.</param>
    /// <param name="timestamp">Clock in <see cref="System.Diagnostics.Stopwatch.Frequency"/> ticks; null uses the real one.</param>
    public DohHealth(
        string outboundName, ILogger logger,
        int failureThreshold = DefaultFailureThreshold,
        TimeSpan? reminderInterval = null,
        Func<long>? timestamp = null)
    {
        _outboundName = outboundName ?? throw new ArgumentNullException(nameof(outboundName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (failureThreshold < 1) throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        _failureThreshold = failureThreshold;
        TimeSpan interval = reminderInterval ?? DefaultReminderInterval;
        _reminder = timestamp is null ? new LogThrottle(interval) : new LogThrottle(interval, timestamp);
    }

    public void OnSuccess()
    {
        int failures = Interlocked.Exchange(ref _failuresInARow, 0);
        if (Interlocked.Exchange(ref _reported, 0) == 1)
            _logger.LogInformation("DoH via {Outbound} recovered after {Failures} failures", _outboundName, failures);
    }

    public void OnFailure(string reason)
    {
        int failures = Interlocked.Increment(ref _failuresInARow);
        if (failures < _failureThreshold) return;

        if (Interlocked.CompareExchange(ref _reported, 1, 0) == 0)
        {
            _logger.LogWarning("DoH via {Outbound} failing: {Failures} failures in a row, last: {Reason}",
                _outboundName, failures, reason);
            // Starts the reminder interval: the first reminder comes one interval after this line.
            _reminder.TryEnter(out _);
        }
        else if (_reminder.TryEnter(out _))
        {
            _logger.LogWarning("DoH via {Outbound} still failing: {Failures} failures in a row, last: {Reason}",
                _outboundName, failures, reason);
        }
    }
}
