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
// Two sources of truth, because neither alone is enough:
//   * an initial scan, for processes that were already running when the tool started;
//   * WMI start/stop events (Win32_ProcessStartTrace), which fire fast enough to catch a process
//     before it opens its first socket in the common case.
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

    private readonly ILogger<ProcessWatcher> _logger;
    private readonly IProcessFinder _processFinder;
    private readonly ProcessCommandLineReader _commandLines;
    private readonly ConcurrentDictionary<uint, TrackedProcess> _tracked = new ConcurrentDictionary<uint, TrackedProcess>();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly object _rulesLock = new object();

    private IReadOnlyList<ProcessRule> _rules = Array.Empty<ProcessRule>();

    // Recomputed with the rule set: nothing here reads a command line while no rule asks about
    // arguments, so the WMI cost only exists for the people who use the feature.
    private volatile bool _anyRuleNeedsCommandLine;

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private Task? _pollTask;
    private bool _started;

    /// <summary>A matched (or inherited) process appeared and should be redirected.</summary>
    public event Action<TrackedProcess>? ProcessAttached;

    /// <summary>A tracked process exited, or stopped matching after a rule change.</summary>
    public event Action<TrackedProcess>? ProcessDetached;

    /// <summary>True while process discovery runs on WMI events; false while it is polling.</summary>
    public bool IsUsingWmi { get; private set; }

    public ProcessWatcher(ILogger<ProcessWatcher> logger, IProcessFinder? processFinder = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processFinder = processFinder ?? new ProcessFinder();
        _commandLines = new ProcessCommandLineReader(_logger);
    }

    public IReadOnlyCollection<TrackedProcess> Tracked => _tracked.Values.ToList();

    public bool IsTracked(uint processId) => _tracked.ContainsKey(processId);

    public bool TryGetTracked(uint processId, out TrackedProcess? process)
        => _tracked.TryGetValue(processId, out process);

    // The pid -> policies map the routing resolver needs. The list is in priority order.
    public IReadOnlyDictionary<uint, IReadOnlyList<Guid>> BuildPolicyMap()
        => _tracked.ToDictionary(kv => kv.Key, kv => kv.Value.PolicyIds);

    public void Start(IReadOnlyList<ProcessRule> rules)
    {
        if (_started) throw new InvalidOperationException("Already started");
        _started = true;
        ApplyRules(rules);

        if (!TryStartWmi())
        {
            IsUsingWmi = false;
            _pollTask = Task.Run(() => PollLoop(_cts.Token));
            _logger.LogWarning("WMI is unavailable, polling every {IntervalMs}ms instead — attaching to a new process will be slower", PollIntervalMs);
        }
    }

    // Applies a new rule set: newly matching processes are attached, processes that no longer match
    // are detached. Safe to call while running — this is what the UI does after a rule edit.
    public void ApplyRules(IReadOnlyList<ProcessRule> rules)
    {
        lock (_rulesLock)
        {
            _rules = rules ?? Array.Empty<ProcessRule>();
            _anyRuleNeedsCommandLine = _rules.Any(ProcessRuleMatcher.NeedsCommandLine);
        }
        ScanOnce();
        DropProcessesThatNoLongerMatch();
    }

    // One full pass over the running process list. Also used as the poll body.
    public void ScanOnce()
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

        // One query for the whole machine rather than one per process — and none at all unless a
        // rule asks about arguments.
        IReadOnlyDictionary<uint, string> commandLines = _anyRuleNeedsCommandLine
            ? _commandLines.ReadAll()
            : EmptyCommandLines;

        foreach (ProcessInfo process in processes)
        {
            commandLines.TryGetValue(process.Id, out string? commandLine);
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

    private static readonly IReadOnlyDictionary<uint, string> EmptyCommandLines
        = new Dictionary<uint, string>();

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

            // Only the processes already being redirected are re-tested here, so a query each is
            // affordable where a sweep of the whole machine would not be.
            string? commandLine = _anyRuleNeedsCommandLine ? _commandLines.Read(tracked.ProcessId) : null;

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

            // The trace gives no path; look it up, tolerating a process that has already exited.
            string? path = _processFinder.FindById(pid)?.ExecutablePath;
            // Nor a command line — and this one costs a WMI query, so only when a rule wants it.
            string? commandLine = _anyRuleNeedsCommandLine ? _commandLines.Read(pid) : null;

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
            Detach(pid, "exited");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "handling a WMI process-stop event failed");
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { ScanOnce(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "a polling scan failed");
            }

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
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _tracked.Clear();
    }
}
