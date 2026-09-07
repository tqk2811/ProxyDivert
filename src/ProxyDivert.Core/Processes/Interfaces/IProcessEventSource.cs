using System;
using ProxyDivert.Core.Processes.Models;

namespace ProxyDivert.Core.Processes.Interfaces;

/// <summary>
/// Tells the process table when a process appears or ends, so it does not have to keep asking the
/// operating system.
/// </summary>
/// <remarks>
/// Two implementations answer this, and the difference between them is only how the news travels:
/// <see cref="EtwProcessEventSource"/> reads the kernel's ETW provider directly, and
/// <see cref="WmiProcessEventSource"/> reads the same facts after WMI has repackaged them. The
/// table takes whichever starts, so a machine where one is unavailable still follows events instead
/// of falling back to polling.
///
/// Threading: both events are raised on the source's own pump thread, one at a time. A handler that
/// blocks holds up the next event, so handlers are expected to be short.
/// </remarks>
public interface IProcessEventSource : IDisposable
{
    /// <summary>What to call this in a log line — "ETW", "WMI".</summary>
    string Name { get; }

    /// <summary>A process was created.</summary>
    event Action<ProcessStartedEvent>? Started;

    /// <summary>A process ended. Carries the pid only; the table already knows the rest.</summary>
    event Action<uint>? Stopped;

    /// <summary>
    /// Subscribes to the operating system and starts delivering. False when this source is not
    /// available on this machine — no elevation, a service that is off, a session that cannot be
    /// created — which is not an error: the caller tries the next source, then polling.
    /// </summary>
    bool TryStart();
}
