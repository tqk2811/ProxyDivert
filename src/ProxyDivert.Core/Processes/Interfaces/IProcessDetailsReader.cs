using ProxyDivert.Core.Processes.Models;

namespace ProxyDivert.Core.Processes.Interfaces;

/// <summary>
/// Reads the two facts a process listing cannot carry: the full path of the executable and the
/// command line it was started with.
/// </summary>
/// <remarks>
/// Both need a handle on the process, so both can fail for the same reasons — a protected process,
/// or one that exited a moment ago — and both are therefore asked for together rather than one at
/// a time.
/// </remarks>
public interface IProcessDetailsReader
{
    /// <summary>
    /// The path and command line of one process. Either may come back null, which is an answer
    /// ("asked, could not be read") and not a reason to ask again.
    /// </summary>
    ProcessDetails Read(uint processId);
}
