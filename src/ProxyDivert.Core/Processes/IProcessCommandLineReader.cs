using System.Collections.Generic;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Reads the command line of other processes — the one thing
/// <see cref="TqkLibrary.WinDivert.ProcessControl.Models.ProcessInfo"/> does not carry.
/// </summary>
/// <remarks>
/// An interface because the cost model, not the value, is what everything above cares about: a
/// query for one process costs about as much as a query for the whole machine, so the code that
/// remembers answers has to be testable by counting queries rather than by watching WMI.
/// </remarks>
public interface IProcessCommandLineReader
{
    /// <summary>Command line of one process, or null when it cannot be read.</summary>
    string? Read(uint processId);

    /// <summary>
    /// Command lines of every process this one may look at, keyed by process id. One query for the
    /// whole machine — far cheaper than <see cref="Read"/> per process during a full scan.
    /// </summary>
    /// <remarks>
    /// A process whose command line cannot be read is simply ABSENT from the result rather than
    /// present with a null. A caller that remembers answers must therefore treat "absent from a
    /// sweep" as "unreadable", not as "not asked yet" — otherwise every system process on the
    /// machine gets asked about again on every pass, which is the whole cost this exists to avoid.
    /// </remarks>
    IReadOnlyDictionary<uint, string> ReadAll();
}
