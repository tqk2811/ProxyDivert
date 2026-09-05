using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Models;
using Microsoft.Extensions.Logging;
using TqkLibrary.WinDivert.ProcessControl;
using TqkLibrary.WinDivert.ProcessControl.Interfaces;
using TqkLibrary.WinDivert.ProcessControl.Models;

namespace ProxyDivert.Core.Processes;

// Watches the machine for processes that match the user's process rules and reports them, so the
// engine can put them under redirection and take them out again when they exit.
//
// Two sources of truth, because neither alone is enough, and in this order:
//   * WMI start/stop events (Win32_ProcessStartTrace), hooked the moment the watcher starts and
//     fast enough to catch a process before it opens its first socket in the common case;
//   * a sweep of what was already running, which follows on a background thread. Hooking first is
//     what stops a process started during the sweep from falling between the two.
// WMI needs administrator rights and can fail on a broken WMI repository, so a polling fallback
// takes over automatically — slower to attach, but never silently blind.
//
// Children: when a matched process spawns another, the child inherits the parent's policy. That
// is what makes launchers and multi-process browsers work.
public sealed class ProcessWatcher : IDisposable
{
    // How often the fallback poll re-scans when WMI is unavailable. Fast enough that a browser
    // started by hand is caught before the page loads; slow enough not to burn a core.
    private const int PollIntervalMs = 750;

    // How many new processes may be waiting to have their command line read in the background. Past
    // this the oldest are simply dropped: the next scan fills the gaps in one query anyway, and a
    // queue that grows without limit during a burst would outlive the processes it describes.
    private const int PrefetchQueueLimit = 256;

    private readonly ILogger<ProcessWatcher> _logger;
    private readonly IProcessFinder _processFinder;
    private readonly ProcessCommandLineCache _commandLines;
    private readonly ConcurrentDictionary<uint, TrackedProcess> _tracked = new ConcurrentDictionary<uint, TrackedProcess>();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly object _rulesLock = new object();

    // New processes whose command line nothing is waiting for. Read on a thread of their own so the
    // table stays complete without the process-start handler paying for it.
    private readonly BlockingCollection<(uint Pid, string Name)> _prefetch
        = new BlockingCollection<(uint Pid, string Name)>(PrefetchQueueLimit);

    private readonly ProcessEventBacklog _backlog = new ProcessEventBacklog();

    private IReadOnlyList<ProcessRule> _rules = Array.Empty<ProcessRule>();

    // Recomputed with the rule set. It no longer decides whether command lines are read at all —
    // they are, in the background, so the table is ready before the first rule asks — only whether
    // reading one is on the path of a decision and therefore worth waiting for.
    private volatile bool _anyRuleNeedsCommandLine;

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private Task? _pollTask;
    private Task? _initialScanTask;
    private Task? _prefetchTask;
    private bool _started;

    /// <summary>A matched (or inherited) process appeared and should be redirected.</summary>
    public event Action<TrackedProcess>? ProcessAttached;

    /// <summary>A tracked process exited, or stopped matching after a rule change.</summary>
    public event Action<TrackedProcess>? ProcessDetached;

    /// <summary>True while process discovery runs on WMI events; false while it is polling.</summary>
    public bool IsUsingWmi { get; private set; }

    /// <summary>
    /// How far behind the process-start events may fall, in milliseconds, before their command
    /// lines are read for the whole machine at once instead of one process at a time. 0 or less
    /// turns that off. Comes from the configuration and can be changed while running.
    /// </summary>
    public int EventBacklogMs
    {
        get => _backlog.ThresholdMs;
        set => _backlog.ThresholdMs = value;
    }

    public ProcessWatcher(
        ILogger<ProcessWatcher> logger,
        IProcessFinder? processFinder = null,
        IProcessCommandLineReader? commandLineReader = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processFinder = processFinder ?? new ProcessFinder();
        _commandLines = new ProcessCommandLineCache(
            commandLineReader ?? new ProcessCommandLineReader(_logger), _logger);
    }

    public IReadOnlyCollection<TrackedProcess> Tracked => _tracked.Values.ToList();

    public bool IsTracked(uint processId) => _tracked.ContainsKey(processId);

    public bool TryGetTracked(uint processId, out TrackedProcess? process)
        => _tracked.TryGetValue(processId, out process);

    // The pid -> policies map the routing resolver needs. The list is in priority order.
    public IReadOnlyDictionary<uint, IReadOnlyList<Guid>> BuildPolicyMap()
        => _tracked.ToDictionary(kv => kv.Key, kv => kv.Value.PolicyIds);

    /// <summary>
    /// Starts watching. The hooks for new and exiting processes go up straight away; the sweep of
    /// what is already running happens in the background.
    /// </summary>
    /// <remarks>
    /// That order is deliberate. The first sweep costs a process enumeration plus a WMI query —
    /// a few hundred milliseconds — and Start is called from the UI thread, so doing it first would
    /// freeze the window; worse, a process started during it would fall in the gap between the sweep
    /// and the hooks and never be seen. Hooking first closes that gap: an event that arrives while
    /// the sweep is still running is harmless, because attaching twice does nothing the second time.
    /// </remarks>
    public void Start(IReadOnlyList<ProcessRule> rules)
    {
        if (_started) throw new InvalidOperationException("Already started");
        _started = true;
        SetRules(rules);

        _prefetchTask = Task.Run(() => PrefetchLoop(_cts.Token));

        if (TryStartWmi())
        {
            _initialScanTask = Task.Run(InitialScan);
        }
        else
        {
            IsUsingWmi = false;
            // The poll loop's first pass is the initial scan, so there is nothing extra to start.
            _pollTask = Task.Run(() => PollLoop(_cts.Token));
            _logger.LogWarning("WMI is unavailable, polling every {IntervalMs}ms instead — attaching to a new process will be slower", PollIntervalMs);
        }
    }

    // Applies a new rule set: newly matching processes are attached, processes that no longer match
    // are detached. Safe to call while running — this is what the UI does after a rule edit.
    public void ApplyRules(IReadOnlyList<ProcessRule> rules)
    {
        SetRules(rules);
        ScanOnce();
        DropProcessesThatNoLongerMatch();
    }

    private void SetRules(IReadOnlyList<ProcessRule> rules)
    {
        lock (_rulesLock)
        {
            _rules = rules ?? Array.Empty<ProcessRule>();
            _anyRuleNeedsCommandLine = _rules.Any(ProcessRuleMatcher.NeedsCommandLine);
        }
    }

    // One full pass over the running process list. Also used as the poll body.
    public void ScanOnce() => Scan(readCommandLines: _anyRuleNeedsCommandLine);

    // The first pass after Start, on a background thread.
    private void InitialScan()
    {
        if (_cts.IsCancellationRequested) return;
        try
        {
            // Command lines are read here even when no rule asks about arguments yet. It is one
            // query for the whole machine, off the UI thread, once — against one query per
            // redirected process, on the UI thread, the moment the user writes their first rule
            // about arguments. Paying it now is what makes Save fast later.
            Scan(readCommandLines: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the initial process scan failed");
        }
    }

    private void Scan(bool readCommandLines)
    {
        IReadOnlyList<ProcessInfo> processes;
        try
        {
            processes = _processFinder.ListAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "process enumeration failed");
            return;
        }

        // Costs nothing and is the safety net for a stop event that never arrived: a pid Windows has
        // handed to a different program must not be answered for out of the table.
        _commandLines.Retain(processes);
        // Only the processes with no answer yet cost anything here, and if there are more than a
        // couple they are read in a single machine-wide query.
        if (readCommandLines) _commandLines.EnsureLoaded(processes);

        foreach (ProcessInfo process in processes)
        {
            string? commandLine = _anyRuleNeedsCommandLine
                ? _commandLines.Get(process.Id, process.Name)
                : null;
            TryAttach(process.Id, process.Name, process.ExecutablePath, parentPid: 0, commandLine);
        }

        ReapExitedProcesses(processes);
    }

    // Puts one specific process under redirection without a rule describing it. This is what the
    // CLI's --pid and --launch use, and it is the only safe way to redirect "this Chrome" rather
    // than every chrome.exe on the machine — including the user's own browser.
    public TrackedProcess? AttachProcessId(uint processId, Guid policyId, bool includeChildren = true)
    {
        if (processId == 0) return null;
        if (_tracked.TryGetValue(processId, out TrackedProcess? existing)) return existing;

        ProcessInfo? info = _processFinder.FindById(processId);
        // Even asked for by hand: redirecting this process or the VPN helper loops the traffic
        // back into the relay and nothing works again until the tool is killed.
        if (IsSelfOrHelper(processId, info?.Name ?? string.Empty))
        {
            _logger.LogWarning("refusing to attach pid={Pid} ({Name}) — it carries the redirected traffic itself, so redirecting it would loop", processId, info?.Name);
            return null;
        }
        var tracked = new TrackedProcess(
            processId,
            info?.Name ?? $"pid {processId}",
            info?.ExecutablePath,
            matchedRule: null,
            policyIds: new[] { policyId },
            parentProcessId: 0,
            isExplicit: true,
            includeChildren: includeChildren);

        if (!_tracked.TryAdd(processId, tracked)) return _tracked[processId];
        _logger.LogInformation("attached pid={Pid} name={Name} explicitly, policy={Policy}", processId, tracked.Name, policyId);
        ProcessAttached?.Invoke(tracked);
        return tracked;
    }

    // Called by the engine when the redirector's tree monitor reports a child of a tracked process.
    public void AttachChild(uint childPid, uint parentPid)
    {
        if (_tracked.ContainsKey(childPid)) return;
        if (!_tracked.TryGetValue(parentPid, out TrackedProcess? parent)) return;
        if (!parent.IncludeChildren) return;

        ProcessInfo? info = _processFinder.FindById(childPid);
        var child = new TrackedProcess(
            childPid,
            info?.Name ?? $"pid {childPid}",
            info?.ExecutablePath,
            matchedRule: null,               // inherited, not matched
            policyIds: parent.PolicyIds,
            parentProcessId: parentPid);

        if (!_tracked.TryAdd(childPid, child)) return;
        _logger.LogInformation("attached child pid={Pid} of parent={ParentPid}, policies={Policies}", childPid, parentPid, string.Join(", ", parent.PolicyIds));
        ProcessAttached?.Invoke(child);
    }

    private bool TryAttach(uint pid, string name, string? path, uint parentPid, string? commandLine)
    {
        if (pid == 0 || _tracked.ContainsKey(pid)) return false;
        if (IsSelfOrHelper(pid, name)) return false;

        ProcessRule? rule = FindMatchingRule(name, path, commandLine);
        if (rule == null) return false;

        var tracked = new TrackedProcess(pid, name, path, rule, rule.PolicyIds, parentPid);
        if (!_tracked.TryAdd(pid, tracked)) return false;

        _logger.LogInformation("attached pid={Pid} name={Name} by rule {Rule}, policies={Policies}", pid, name, rule, string.Join(", ", rule.PolicyIds));
        ProcessAttached?.Invoke(tracked);
        return true;
    }

    // Two processes must never be redirected, however broad a rule is ("*.exe", Contains "e"):
    //
    //   * this one — its traffic IS the redirected traffic once it leaves the proxy, so capturing
    //     it again would loop every connection back into the relay forever;
    //   * wireproxy — it carries the VPN outbound. Redirecting the tunnel through the tunnel is the
    //     same loop, one process further out. A wireproxy the user started for something else is
    //     excluded too; that is the safe way round, and a rule can never be written that needs it.
    private static bool IsSelfOrHelper(uint pid, string name)
    {
        if (pid == CurrentProcessId) return true;
        return name.Equals("wireproxy.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wireproxy", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    private ProcessRule? FindMatchingRule(string name, string? path, string? commandLine)
    {
        IReadOnlyList<ProcessRule> rules;
        lock (_rulesLock) rules = _rules;

        foreach (ProcessRule rule in rules)
        {
            if (ProcessRuleMatcher.IsMatch(rule, name, path, commandLine)) return rule;
        }
        return null;
    }

    private void Detach(uint pid, string reason)
    {
        if (!_tracked.TryRemove(pid, out TrackedProcess? tracked)) return;
        _logger.LogInformation("detached pid={Pid} name={Name} ({Reason})", pid, tracked.Name, reason);
        ProcessDetached?.Invoke(tracked);
    }

    private void ReapExitedProcesses(IReadOnlyList<ProcessInfo> alive)
    {
        var alivePids = new HashSet<uint>(alive.Select(p => p.Id));
        foreach (uint pid in _tracked.Keys)
        {
            if (!alivePids.Contains(pid)) Detach(pid, "exited");
        }
    }

    // After a rule change, a process attached by the old rules may no longer match. Children keep
    // following their parent: they were never matched by a rule in the first place.
    private void DropProcessesThatNoLongerMatch()
    {
        foreach (var kv in _tracked)
        {
            TrackedProcess tracked = kv.Value;

            // A process the caller named directly is not described by any rule, so a rule edit has
            // nothing to say about it.
            if (tracked.IsExplicit) continue;

            if (tracked.IsChild)
            {
                if (!_tracked.ContainsKey(tracked.ParentProcessId)) Detach(kv.Key, "parent no longer tracked");
                continue;
            }

            // Answered from the table, which the scan above has just brought up to date. A query
            // each is what this used to do, and at ~210ms apiece it is what made saving a rule
            // freeze the window for the better part of ten seconds.
            string? commandLine = _anyRuleNeedsCommandLine
                ? _commandLines.Get(tracked.ProcessId, tracked.Name)
                : null;

            ProcessRule? rule = FindMatchingRule(tracked.Name, tracked.ExecutablePath, commandLine);
            if (rule == null) Detach(kv.Key, "no longer matches any rule");
            else if (!rule.PolicyIds.SequenceEqual(tracked.PolicyIds))
            {
                // The policy changed: re-attach so the engine reads the new assignment.
                Detach(kv.Key, "policies changed");
                TryAttach(tracked.ProcessId, tracked.Name, tracked.ExecutablePath, tracked.ParentProcessId, commandLine);
            }
        }
    }

    private bool TryStartWmi()
    {
        try
        {
            // WITHIN 1 = deliver events in up to 1-second batches. Lower values raise CPU cost on
            // a busy machine without meaningfully improving the attach race.
            _startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _startWatcher.EventArrived += OnProcessStarted;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
            _stopWatcher.EventArrived += OnProcessStopped;
            _stopWatcher.Start();

            IsUsingWmi = true;
            _logger.LogDebug("watching processes through WMI process traces");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the WMI watcher failed to start");
            DisposeWmi();
            return false;
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            uint pid = Convert.ToUInt32(e.NewEvent.Properties["ProcessID"].Value);
            uint parentPid = Convert.ToUInt32(e.NewEvent.Properties["ParentProcessID"].Value);
            string name = e.NewEvent.Properties["ProcessName"].Value?.ToString() ?? string.Empty;

            // Windows hands pids out again, so whatever the table remembers about this one belongs
            // to its previous owner.
            _commandLines.Forget(pid);

            // The trace gives no path; look it up, tolerating a process that has already exited.
            string? path = _processFinder.FindById(pid)?.ExecutablePath;

            string? commandLine = null;
            if (_anyRuleNeedsCommandLine)
            {
                // Events are delivered one at a time, so an event that reaches us already stale
                // means more are queued behind it — each of which would otherwise cost a query of
                // its own. One query for the machine answers for all of them at once.
                if (_backlog.ShouldCatchUp(TryReadProperty(e.NewEvent, "TIME_CREATED")))
                    CatchUpCommandLines();

                // A rule needs it to decide, so it is read here and the attach waits for it.
                commandLine = _commandLines.Get(pid, name);
            }
            else
            {
                // Nothing is waiting on it right now, but reading it in the background keeps the
                // table complete for the moment the user does write a rule about arguments.
                QueuePrefetch(pid, name);
            }

            if (!TryAttach(pid, name, path, parentPid, commandLine))
            {
                // Not a match itself — but it may be the child of something already tracked.
                AttachChild(pid, parentPid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "handling a WMI process-start event failed");
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            uint pid = Convert.ToUInt32(e.NewEvent.Properties["ProcessID"].Value);
            // Before the detach, and unconditionally: the pid may be reissued within milliseconds,
            // and the next owner must not inherit this one's command line.
            _commandLines.Forget(pid);
            Detach(pid, "exited");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "handling a WMI process-stop event failed");
        }
    }

    // Reads the command lines of every running process in one query, so the events still queued
    // behind this one are answered from memory instead of a query each.
    private void CatchUpCommandLines()
    {
        try
        {
            IReadOnlyList<ProcessInfo> processes = _processFinder.ListAll();
            _commandLines.EnsureLoaded(processes);
            _logger.LogDebug(
                "process events were running behind, so the command lines of {Count} processes were read in one go",
                processes.Count);
        }
        catch (Exception ex)
        {
            // Falling back to a query per process is slow, not wrong.
            _logger.LogDebug(ex, "catching up on command lines failed");
        }
    }

    // A property WMI may or may not have put on the event. Nothing here is worth an exception.
    private static object? TryReadProperty(ManagementBaseObject wmiEvent, string name)
    {
        try { return wmiEvent[name]; }
        catch (ManagementException) { return null; }
    }

    // Queued rather than read here: process-start events are delivered one at a time — measured,
    // not assumed: sixty handlers in a burst, not one overlapping pair — so a read on that thread
    // delays every process behind it. A full queue drops the request; the next scan fills that gap
    // in one query.
    private void QueuePrefetch(uint pid, string name)
    {
        try { _prefetch.TryAdd((pid, name)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The watcher is shutting down; there is nothing left to keep warm.
        }
    }

    private void PrefetchLoop(CancellationToken ct)
    {
        try
        {
            foreach ((uint pid, string name) in _prefetch.GetConsumingEnumerable(ct))
            {
                try { _commandLines.Get(pid, name); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "reading the command line of pid={Pid} ahead of time failed", pid);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        // The first pass here IS the initial scan, so it primes the command line table the same way
        // InitialScan does; the passes after it only fill in what has appeared since.
        bool isFirstPass = true;

        while (!ct.IsCancellationRequested)
        {
            try { Scan(readCommandLines: isFirstPass || _anyRuleNeedsCommandLine); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "a polling scan failed");
            }
            isFirstPass = false;

            try { await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void DisposeWmi()
    {
        try { _startWatcher?.Stop(); } catch { }
        try { _startWatcher?.Dispose(); } catch { }
        try { _stopWatcher?.Stop(); } catch { }
        try { _stopWatcher?.Dispose(); } catch { }
        _startWatcher = null;
        _stopWatcher = null;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        DisposeWmi();
        try { _prefetch.CompleteAdding(); } catch { }
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _initialScanTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _prefetchTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _prefetch.Dispose(); } catch { }
        _cts.Dispose();
        _tracked.Clear();
        _commandLines.Clear();
    }
}
