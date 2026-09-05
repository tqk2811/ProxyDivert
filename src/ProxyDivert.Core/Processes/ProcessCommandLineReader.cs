using System;
using System.Collections.Generic;
using System.Management;
using Microsoft.Extensions.Logging;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Reads the command line of other processes, which is the one thing
/// <see cref="TqkLibrary.WinDivert.ProcessControl.Models.ProcessInfo"/> does not carry.
/// </summary>
/// <remarks>
/// WMI rather than NtQueryInformationProcess + a PEB read: the watcher already depends on WMI for
/// its start and stop events, and the native route needs a different struct layout per bitness for
/// no gain at the rate this is called.
///
/// It is not free — a single-process query costs milliseconds — so the caller is expected to ask
/// only while a rule actually matches on arguments, and to prefer <see cref="ReadAll"/> over a
/// query per process when sweeping the whole machine.
///
/// A command line comes back null for anything this process may not open: system processes, and
/// processes of another user. A rule that asks about arguments therefore does not match those,
/// which is the safe direction — see ProcessRuleMatcher.
/// </remarks>
public sealed class ProcessCommandLineReader
{
    private readonly ILogger _logger;

    public ProcessCommandLineReader(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Command line of one process, or null when it cannot be read.</summary>
    public string? Read(uint processId)
    {
        if (processId == 0) return null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject row in results)
            {
                using (row)
                    return row["CommandLine"] as string;
            }
        }
        catch (ManagementException ex)
        {
            _logger.LogDebug(ex, "could not read the command line of pid={Pid}", processId);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "not allowed to read the command line of pid={Pid}", processId);
        }
        return null;
    }

    /// <summary>
    /// Command lines of every process this one may look at, keyed by process id. One query for the
    /// whole machine — far cheaper than <see cref="Read"/> per process during a full scan.
    /// </summary>
    public IReadOnlyDictionary<uint, string> ReadAll()
    {
        var lines = new Dictionary<uint, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject row in results)
            {
                using (row)
                {
                    if (row["CommandLine"] is not string commandLine) continue;
                    try
                    {
                        lines[Convert.ToUInt32(row["ProcessId"])] = commandLine;
                    }
                    catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
                    {
                        // One unreadable row must not cost us the rest of the sweep.
                    }
                }
            }
        }
        catch (ManagementException ex)
        {
            _logger.LogWarning(ex, "reading process command lines failed; rules that match on arguments will not match this round");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "not allowed to read process command lines; rules that match on arguments will not match this round");
        }
        return lines;
    }
}
