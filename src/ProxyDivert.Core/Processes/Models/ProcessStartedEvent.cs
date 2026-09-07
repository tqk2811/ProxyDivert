namespace ProxyDivert.Core.Processes.Models;

/// <summary>
/// What an event source knows about a process the moment it was created — everything the event
/// itself carries, and nothing more.
/// </summary>
/// <param name="ProcessId">The new process.</param>
/// <param name="ParentProcessId">Who created it, which is what makes "and its children" work.</param>
/// <param name="ImageName">
/// The file name of the image ("chrome.exe"), never a full path: ETW reports an NT device path and
/// WMI reports a bare name, so the file name is the one shape both can agree on. The real path is
/// read from the process's own handle straight afterwards.
/// </param>
/// <param name="SessionId">The Windows session, for telling a service apart from the user's copy.</param>
public readonly record struct ProcessStartedEvent(
    uint ProcessId, uint ParentProcessId, string ImageName, uint SessionId);
