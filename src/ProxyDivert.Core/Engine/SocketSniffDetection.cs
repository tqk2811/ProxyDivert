using System;
using System.Collections.Generic;
using ProxyDivert.Core.Engine.Interfaces;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Redirect;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// Finds a process from its own connection: one sniffing handle watches every socket on the
/// machine, and the redirector asks about each pid the first time one of its sockets shows up.
/// </summary>
/// <remarks>
/// Nothing here waits on a process event, so the table follows none — it keeps itself current by
/// sweeping, which is all it is needed for: the sweep retires a process that has exited, and the
/// path and parent of a new one are read on demand when its first connection asks about it. See
/// <see cref="ProcessDetectionMode.NetworkSniff"/>.
/// </remarks>
public sealed class SocketSniffDetection : IProcessDetectionStrategy
{
    public static SocketSniffDetection Instance { get; } = new SocketSniffDetection();

    private SocketSniffDetection()
    {
    }

    public ProcessDetectionMode Mode => ProcessDetectionMode.NetworkSniff;

    public void ConfigureInventory(ProcessInventory inventory, ProcessEventSourceKind chosenSource)
    {
        if (inventory is null) throw new ArgumentNullException(nameof(inventory));
        inventory.UseEventSource(null);
    }

    // Processes that start are not attached from the event. The ones already running still are —
    // the tracker matches the whole table when it starts, whichever mode — and the ones that exit
    // are still detached: that half of the table's events is listened to either way.
    public void StartTracker(ProcessRuleTracker tracker, IReadOnlyList<ProcessRule> rules)
    {
        if (tracker is null) throw new ArgumentNullException(nameof(tracker));
        tracker.Start(rules, attachFromProcessEvents: false);
    }

    public void ConfigureRedirect(RedirectOptions options, Func<uint, bool?> judge)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        options.ShouldTrackProcess = judge ?? throw new ArgumentNullException(nameof(judge));
    }
}
