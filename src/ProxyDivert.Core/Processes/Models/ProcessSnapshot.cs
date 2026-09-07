using System;

namespace ProxyDivert.Core.Processes.Models;

/// <summary>
/// One process as the process table holds it: everything a routing filter can ask about, read once
/// and kept in memory for as long as the process lives.
/// </summary>
/// <remarks>
/// A record, and immutable, because the details arrive in two steps. The process id, the name and
/// the parent are known the instant a process starts; the path and the command line take a handle
/// to read and may fail. Filling those in later is a <c>with</c> expression that replaces the entry
/// atomically, so a reader either sees the old snapshot or the complete one and never a half-built
/// object — which matters, because the table is read from the packet path.
///
/// Nothing here changes for the life of a process, which is what makes the whole table worth
/// keeping: a command line read once is correct until the pid dies.
/// </remarks>
public sealed record ProcessSnapshot
{
    /// <summary>The pid Windows handed out. Unique only among LIVE processes — see <see cref="StartedUtc"/>.</summary>
    public required uint ProcessId { get; init; }

    /// <summary>
    /// Image name WITH its extension, exactly as the kernel reports it ("chrome.exe"). The whole
    /// table uses this one spelling, so a filter never has to guess which half of the tool it is
    /// being matched against.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The process that created this one. Known for EVERY process, including those already running
    /// when the tool started — which is what lets a filter's "include children" work without a
    /// separate tree poller.
    /// </summary>
    /// <remarks>
    /// Windows does not clear this when the parent exits, so a parent id may name a pid that has
    /// since been handed to something unrelated. Anything walking upwards must check
    /// <see cref="StartedUtc"/> of the parent rather than trusting the number on its own.
    /// </remarks>
    public required uint ParentProcessId { get; init; }

    /// <summary>
    /// When the process started, from the kernel's own clock. Together with the pid this is an
    /// identity: Windows reissues pids within seconds, and two processes cannot share a pid at the
    /// same instant, so (pid, StartedUtc) settles "is this still the process I was told about?"
    /// where comparing names only makes it likely.
    /// </summary>
    public required DateTime StartedUtc { get; init; }

    /// <summary>
    /// Windows session. 0 is the service session (SYSTEM, LOCAL SERVICE, NETWORK SERVICE); every
    /// interactive sign-in gets one of its own. Free here — the kernel returns it with the process
    /// list — where asking per process used to cost a handle each.
    /// </summary>
    public uint SessionId { get; init; }

    /// <summary>
    /// Full path of the executable, or null when it could not be read (a protected process, or one
    /// that exited between being listed and being opened). A filter matching on path simply does
    /// not match those.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// The command line, or null when it could not be read. Null is an ANSWER — "asked, could not
    /// be read" — not a gap: roughly half the processes on an idle machine are ones this tool may
    /// not open, and re-asking about them on every pass is the cost the table exists to avoid.
    /// </summary>
    public string? CommandLine { get; init; }

    /// <summary>True once the path and command line have been asked for, whatever the answer was.</summary>
    public bool DetailsRead { get; init; }

    /// <summary>Same process, now with whatever the detail reader managed to get.</summary>
    public ProcessSnapshot WithDetails(string? executablePath, string? commandLine)
        => this with { ExecutablePath = executablePath, CommandLine = commandLine, DetailsRead = true };

    public override string ToString() => $"[{ProcessId}] {Name}";
}
