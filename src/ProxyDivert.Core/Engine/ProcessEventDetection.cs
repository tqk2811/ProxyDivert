using System;
using System.Collections.Generic;
using ProxyDivert.Core.Engine.Interfaces;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Redirect;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// Finds a process from the machine saying it has started: the table follows ETW or WMI, the filters
/// are read against every process it reports, and the redirector is handed the pids that match.
/// </summary>
/// <remarks>
/// The earliest a process can be caught, provided the event arrives before the process's first
/// connection does — see <see cref="ProcessDetectionMode.ProcessEvents"/>.
/// </remarks>
public sealed class ProcessEventDetection : IProcessDetectionStrategy
{
    public static ProcessEventDetection Instance { get; } = new ProcessEventDetection();

    private ProcessEventDetection()
    {
    }

    public ProcessDetectionMode Mode => ProcessDetectionMode.ProcessEvents;

    public void ConfigureInventory(ProcessInventory inventory, ProcessEventSourceKind chosenSource)
    {
        if (inventory is null) throw new ArgumentNullException(nameof(inventory));
        inventory.UseEventSource(chosenSource);
    }

    public void StartTracker(ProcessRuleTracker tracker, IReadOnlyList<ProcessRule> rules)
    {
        if (tracker is null) throw new ArgumentNullException(nameof(tracker));
        tracker.Start(rules, attachFromProcessEvents: true);
    }

    // The tracker names the pids, so the redirector has nothing to ask and stays on one handle per
    // process. Cleared rather than left alone: the options are the redirector's whole description of
    // the run, and a judge set by someone else would switch it to watching the whole machine.
    public void ConfigureRedirect(RedirectOptions options, Func<uint, bool?> judge)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        options.ShouldTrackProcess = null;
    }
}
