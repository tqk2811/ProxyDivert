using System;
using System.Collections.Generic;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Redirect;

namespace ProxyDivert.Core.Engine.Interfaces;

/// <summary>
/// One way of finding out which processes' traffic belongs to us — everything a detection mode
/// means, in one place.
/// </summary>
/// <remarks>
/// A mode is not one switch but three, turned in three different objects at three different
/// moments: what the process table listens to, whether the filters are read against a process the
/// moment the table reports it, and whether the redirector watches the whole machine and asks about
/// every pid it meets. They used to be three comparisons against <see cref="ProcessDetectionMode"/>
/// — one in the engine's start, one in the options it builds, and one copied into every host — so a
/// third mode meant finding all of them, and missing one would have produced a mode that was half
/// of each with nothing failing to say so. Now a mode is one class, and
/// <see cref="Extensions.ProcessDetectionModeExtensions.Strategy"/> is the only place the saved
/// setting is turned into one.
///
/// Implementations hold no state: the same instance serves every run and every host.
/// </remarks>
public interface IProcessDetectionStrategy
{
    /// <summary>The saved setting this strategy is.</summary>
    ProcessDetectionMode Mode { get; }

    /// <summary>
    /// Tells the process table which events to follow. <paramref name="chosenSource"/> is the user's
    /// pick between ETW and WMI; a mode that needs no process events at all leaves the table to keep
    /// itself current by sweeping.
    /// </summary>
    /// <remarks>
    /// Free while the table is stopped — it only records the choice — and a live swap once it runs.
    /// </remarks>
    void ConfigureInventory(ProcessInventory inventory, ProcessEventSourceKind chosenSource);

    /// <summary>Starts a run's filters, listening to whichever of the table's events this mode relies on.</summary>
    void StartTracker(ProcessRuleTracker tracker, IReadOnlyList<ProcessRule> rules);

    /// <summary>
    /// The redirector's half. <paramref name="judge"/> is the engine's own answer to "is this pid
    /// ours": a mode that finds processes from their sockets hands it to the redirector, a mode that
    /// names the pids itself does not.
    /// </summary>
    void ConfigureRedirect(RedirectOptions options, Func<uint, bool?> judge);
}
