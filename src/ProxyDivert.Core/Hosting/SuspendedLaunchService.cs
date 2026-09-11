using System;
using System.Threading.Tasks;
using ProxyDivert.Core.Engine;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;

namespace ProxyDivert.Core.Hosting;

/// <summary>
/// Starts a program frozen, puts it under redirection, and only then lets it run — the one way to be
/// sure not a single connection leaves before the redirect is in place.
/// </summary>
/// <remarks>
/// Both hosts did this, each in its own way and each with the order written out by hand: the window
/// by saving a filter for the program and scanning, the command line by naming the pid. The order is
/// the whole feature, so it lives here once, and the two ways of claiming the process are the two
/// methods below.
///
/// The one thing neither used to get right is what happens when claiming it fails. The window's save
/// swallowed its own failure, so the program was resumed with the filter nowhere — the very leak this
/// exists to close. Here a failure means the program never runs: it is not resumed, and disposing a
/// process launched frozen and never resumed terminates it. The failure is handed back to the caller
/// to report.
/// </remarks>
public sealed class SuspendedLaunchService
{
    private readonly RedirectEngine _engine;
    private readonly ISuspendedProcessLauncher _launcher;

    public SuspendedLaunchService(RedirectEngine engine, ISuspendedProcessLauncher launcher)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    /// <summary>
    /// Launches <paramref name="exePath"/> frozen and resumes it once <paramref name="adopt"/> has
    /// finished bringing it under redirection. Returns its process id.
    /// </summary>
    /// <param name="adopt">
    /// Handed the frozen process's id, on the caller's thread, and must complete only once the driver
    /// knows it. Throwing — or a task that faults — ends the program instead of resuming it.
    /// </param>
    public async Task<uint> LaunchAsync(string exePath, string? args, Func<uint, Task> adopt)
    {
        if (adopt is null) throw new ArgumentNullException(nameof(adopt));

        using ISuspendedProcess process = _launcher.Launch(exePath, args);
        await adopt(process.Pid).ConfigureAwait(false);
        process.Resume();
        return process.Pid;
    }

    /// <summary>
    /// Claims the program by its id, under one policy, children included — what the command line's
    /// <c>--launch</c> does. Needs a running engine; without one the program is ended, not run bare.
    /// </summary>
    public Task<uint> LaunchUnderPolicyAsync(string exePath, string? args, Guid policyId)
        => LaunchAsync(exePath, args, pid => _engine.AttachProcessIdAsync(pid, policyId, includeChildren: true));

    /// <summary>
    /// Claims the program through whatever filters describe it — what the window's button does.
    /// </summary>
    /// <param name="filtersInForce">
    /// Puts in place whatever filter the program needs and completes once the engine has it: the
    /// program is matched against the filters only after that, then all the way into the driver.
    /// Called on the caller's thread while the program is frozen.
    /// </param>
    /// <remarks>
    /// With redirection switched off there is nothing to scan with, and the program runs as it would
    /// have without this — the filter it was given is what catches it on the next start.
    /// </remarks>
    public Task<uint> LaunchUnderFiltersAsync(string exePath, string? args, Func<Task> filtersInForce)
    {
        if (filtersInForce is null) throw new ArgumentNullException(nameof(filtersInForce));

        return LaunchAsync(exePath, args, async _ =>
        {
            await filtersInForce().ConfigureAwait(false);
            await _engine.ForceProcessScanAsync().ConfigureAwait(false);
        });
    }
}
