using System.Collections.Generic;
using ProxyDivert.Core.Processes.Models;

namespace ProxyDivert.Core.Processes.Interfaces;

/// <summary>
/// Lists every process on the machine, with its parent, in one call.
/// </summary>
/// <remarks>
/// An interface because the process table has to be testable without spawning anything, and
/// because the cost of a listing is what the table is built around: one call for the whole machine,
/// never one call per process.
/// </remarks>
public interface IProcessLister
{
    /// <summary>
    /// Every process, without their paths or command lines — those need a handle each and are the
    /// detail reader's job. Processes that exit mid-enumeration are skipped rather than throwing.
    /// </summary>
    IReadOnlyList<ProcessSnapshot> ListAll();
}
