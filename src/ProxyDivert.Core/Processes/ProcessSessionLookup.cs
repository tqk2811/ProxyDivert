using SysProcess = System.Diagnostics.Process;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Tells a service process from one belonging to a signed-in user, by the Windows session it runs
/// in: session 0 holds SYSTEM, LOCAL SERVICE and NETWORK SERVICE, every interactive sign-in gets a
/// session of its own.
/// </summary>
/// <remarks>
/// This exists to avoid a WMI query that is known in advance to fail. A service process runs under
/// an account this tool cannot open, so asking for its command line costs a full query and comes
/// back null every time — and roughly half the processes on an idle Windows box are services.
///
/// The session id is read straight out of the process table, so it costs no more than listing
/// processes does. It is fixed for the life of a process, which is what makes an answer worth
/// remembering.
/// </remarks>
public static class ProcessSessionLookup
{
    /// <summary>
    /// True when the process belongs to a service session — or when it can no longer be found,
    /// since a process that has already exited has no command line left to read either.
    /// </summary>
    public static bool RunsInServiceSession(uint processId)
    {
        if (processId == 0) return true;
        try
        {
            using SysProcess process = SysProcess.GetProcessById((int)processId);
            return process.SessionId == 0;
        }
        catch
        {
            return true;
        }
    }
}
