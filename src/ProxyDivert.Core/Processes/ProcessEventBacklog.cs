using System;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Decides, from the age of a WMI process event, whether the watcher has fallen behind far enough
/// that reading the whole machine's command lines in one query beats reading them one at a time.
/// </summary>
/// <remarks>
/// WMI delivers the events of a single watcher strictly in turn — measured: sixty handlers, not one
/// overlapping pair — so a handler that spends ~210ms on a WMI query holds up every process behind
/// it. Twenty child processes from one browser therefore attach over four seconds, and until a
/// process is attached its traffic leaves unredirected.
///
/// The signal is the event's own TIME_CREATED, which every WMI event class carries. Measured during
/// a burst, the age of the event being handled rose in exact step with the handler's own cost —
/// 5ms, 263ms, 528ms, 790ms … 15s — so it states the length of the queue in real milliseconds. That
/// is worth far more than counting reads over some window: it needs no guess about what "many" is,
/// it stays right on a machine where WMI is slower, and the first event of a burst (a few ms old)
/// never trips it.
///
/// This type only decides. Doing the catch-up is the watcher's business.
/// </remarks>
public sealed class ProcessEventBacklog
{
    /// <summary>
    /// Default age at which the watcher is considered behind. Two single reads is about what it
    /// takes to get here, so the catch-up starts early in a burst but never on an idle machine.
    /// </summary>
    public const int DefaultThresholdMs = 500;

    // A catch-up reads every process on the machine, so repeating it for each event of a long burst
    // would be its own kind of waste. One per second is enough: a process started since the last
    // sweep is read on its own, which is one query, not thirty.
    private const int MinSweepIntervalMs = 1000;

    private readonly Func<DateTime> _utcNow;
    private readonly Func<long> _monotonicMs;

    private long _lastSweepMs = long.MinValue;

    /// <param name="utcNow">For tests. Defaults to the wall clock, which is what TIME_CREATED is in.</param>
    /// <param name="monotonicMs">
    /// For tests. Defaults to <see cref="Environment.TickCount64"/> — the interval between sweeps
    /// must not be thrown off by the wall clock being adjusted.
    /// </param>
    public ProcessEventBacklog(
        int thresholdMs = DefaultThresholdMs,
        Func<DateTime>? utcNow = null,
        Func<long>? monotonicMs = null)
    {
        ThresholdMs = thresholdMs;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// How old an event may be before a catch-up is worth it. 0 or less turns catching up off.
    /// Settable while running: it comes from the configuration, which the user can edit.
    /// </summary>
    public int ThresholdMs { get; set; }

    /// <summary>
    /// True when this event is old enough to say the queue is backing up AND enough time has passed
    /// since the last catch-up. Saying true records the sweep, so a caller must actually do one.
    /// </summary>
    /// <param name="rawTimeCreated">
    /// The event's TIME_CREATED property, verbatim. Anything unreadable answers false — an unknown
    /// age is not evidence of a backlog, and the caller then behaves exactly as it did before.
    /// </param>
    public bool ShouldCatchUp(object? rawTimeCreated)
    {
        if (ThresholdMs <= 0) return false;
        if (!TryGetEventAge(rawTimeCreated, _utcNow(), out TimeSpan age)) return false;
        if (age.TotalMilliseconds < ThresholdMs) return false;

        long now = _monotonicMs();
        if (_lastSweepMs != long.MinValue && now - _lastSweepMs < MinSweepIntervalMs) return false;

        _lastSweepMs = now;
        return true;
    }

    /// <summary>
    /// Turns a WMI TIME_CREATED into how long ago the event happened. False when the value is
    /// missing or not a time this machine can make sense of.
    /// </summary>
    /// <remarks>
    /// TIME_CREATED is a FILETIME: 100-nanosecond intervals since 1601, UTC. A clock adjusted
    /// backwards can make an event look as though it happened in the future, which is reported as
    /// an age of zero rather than a negative one.
    /// </remarks>
    public static bool TryGetEventAge(object? rawTimeCreated, DateTime nowUtc, out TimeSpan age)
    {
        age = TimeSpan.Zero;
        if (rawTimeCreated is null) return false;

        try
        {
            long fileTime = Convert.ToInt64(rawTimeCreated);
            if (fileTime <= 0) return false;

            DateTime created = DateTime.FromFileTimeUtc(fileTime);
            TimeSpan elapsed = nowUtc - created;
            age = elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
            return true;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException
            or OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
