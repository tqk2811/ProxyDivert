using System;

namespace ProxyDivert.Core.Processes.Models;

/// <summary>
/// What a detail reader managed to get about one process, through one handle. Any part may be
/// missing: a protected process, or one that exited between being listed and being opened.
/// </summary>
/// <param name="ExecutablePath">Full Win32 path of the image, or null.</param>
/// <param name="CommandLine">The command line as the process was started with, or null.</param>
/// <param name="StartedUtc">
/// When the kernel created the process, or <see cref="DateTime.MinValue"/>. It comes from the same
/// handle as the rest so that a process discovered through an event and the same process seen in a
/// full listing carry the SAME instant — the pair (pid, StartedUtc) is what identifies a process
/// across a pid being reissued, and two clocks would break that comparison.
/// </param>
public readonly record struct ProcessDetails(
    string? ExecutablePath, string? CommandLine, DateTime StartedUtc)
{
    /// <summary>Nothing could be read — a protected process, or one that has already exited.</summary>
    public static ProcessDetails None => new ProcessDetails(null, null, DateTime.MinValue);

    /// <summary>True when the handle opened at all, whatever it then answered.</summary>
    public bool AnythingRead => ExecutablePath != null || CommandLine != null || StartedUtc != DateTime.MinValue;
}
