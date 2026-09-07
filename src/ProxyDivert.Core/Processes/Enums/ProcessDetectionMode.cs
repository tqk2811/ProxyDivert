namespace ProxyDivert.Core.Processes.Enums;

/// <summary>How the engine finds out that a process's traffic belongs to it.</summary>
public enum ProcessDetectionMode
{
    /// <summary>
    /// From a process event: the machine says a process has started, the filters are read against
    /// it, and a match is redirected from that moment on. Earliest possible, but it depends on the
    /// event arriving before the process's first connection does.
    /// </summary>
    ProcessEvents = 0,

    /// <summary>
    /// From the connection itself: one sniffing WinDivert SOCKET handle watches the whole machine,
    /// and every socket event already carries the pid that opened it. A process is judged the first
    /// time it connects rather than when it started, so nothing depends on a separate event stream
    /// keeping up. The process table is still read — that is where the path, the command line and
    /// the parent come from — but nothing waits on it announcing anything.
    /// </summary>
    NetworkSniff = 1,
}
