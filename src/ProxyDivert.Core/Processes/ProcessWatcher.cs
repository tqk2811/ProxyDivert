using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.WinDivert.Logging;
using TqkLibrary.WinDivert.ProcessControl;
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

    private readonly RedirectLogger _log;
    private readonly ConcurrentDictionary<uint, TrackedProcess> _tracked = new ConcurrentDictionary<uint, TrackedProcess>();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly object _rulesLock = new object();

    private IReadOnlyList<ProcessRule> _rules = Array.Empty<ProcessRule>();
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

    public ProcessWatcher(RedirectLogger? logger = null)
    {
        _log = logger ?? RedirectLogger.Null;
    }

    public IReadOnlyCollection<TrackedProcess> Tracked => _tracked.Values.ToList();

    public bool IsTracked(uint processId) => _tracked.ContainsKey(processId);

    public bool TryGetTracked(uint processId, out TrackedProcess? process)
        => _tracked.TryGetValue(processId, out process);

    // The pid -> policy map the routing resolver needs.
    public IReadOnlyDictionary<uint, Guid> BuildPolicyMap()
        => _tracked.ToDictionary(kv => kv.Key, kv => kv.Value.PolicyId);

    public void Start(IReadOnlyList<ProcessRule> rules)
    {
        if (_started) throw new InvalidOperationException("Already started");
        _started = true;
        ApplyRules(rules);

        if (!TryStartWmi())
        {
            IsUsingWmi = false;
            _pollTask = Task.Run(() => PollLoop(_cts.Token));
            _log.Log("PRC", $"WMI unavailable — polling every {PollIntervalMs}ms instead");
        }
    }

    // Applies a new rule set: newly matching processes are attached, processes that no longer match
    // are detached. Safe to call while running — this is what the UI does after a rule edit.
    public void ApplyRules(IReadOnlyList<ProcessRule> rules)
    {
        lock (_rulesLock)
        {
            _rules = rules ?? Array.Empty<ProcessRule>();
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
            processes = ProcessFinder.ListAll();
        }
        catch (Exception ex)
        {
            _log.Log("PRC", $"Process enumeration failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (ProcessInfo process in processes)
            TryAttach(process.Id, process.Name, process.ExecutablePath, parentPid: 0);

        ReapExitedProcesses(processes);
    }

    // Puts one specific process under redirection without a rule describing it. This is what the
    // CLI's --pid and --launch use, and it is the only safe way to redirect "this Chrome" rather
    // than every chrome.exe on the machine — including the user's own browser.
    public TrackedProcess? AttachProcessId(uint processId, Guid policyId, bool includeChildren = true)
    {
        if (processId == 0) return null;
        if (_tracked.TryGetValue(processId, out TrackedProcess? existing)) return existing;

        ProcessInfo? info = ProcessFinder.FindById(processId);
        var tracked = new TrackedProcess(
            processId,
            info?.Name ?? $"pid {processId}",
            info?.ExecutablePath,
            matchedRule: null,
            policyId: policyId,
            parentProcessId: 0,
            isExplicit: true,
            includeChildren: includeChildren);

        if (!_tracked.TryAdd(processId, tracked)) return _tracked[processId];
        _log.Log("PRC", $"attach pid={processId} name={tracked.Name} (explicit) policy={policyId}");
        ProcessAttached?.Invoke(tracked);
        return tracked;
    }

    // Called by the engine when the redirector's tree monitor reports a child of a tracked process.
    public void AttachChild(uint childPid, uint parentPid)
    {
        if (_tracked.ContainsKey(childPid)) return;
        if (!_tracked.TryGetValue(parentPid, out TrackedProcess? parent)) return;
        if (!parent.IncludeChildren) return;

        ProcessInfo? info = ProcessFinder.FindById(childPid);
        var child = new TrackedProcess(
            childPid,
            info?.Name ?? $"pid {childPid}",
            info?.ExecutablePath,
            matchedRule: null,               // inherited, not matched
            policyId: parent.PolicyId,
            parentProcessId: parentPid);

        if (!_tracked.TryAdd(childPid, child)) return;
        _log.Log("PRC", $"attach child pid={childPid} parent={parentPid} policy={parent.PolicyId}");
        ProcessAttached?.Invoke(child);
    }

    private bool TryAttach(uint pid, string name, string? path, uint parentPid)
    {
        if (pid == 0 || _tracked.ContainsKey(pid)) return false;

        ProcessRule? rule = FindMatchingRule(name, path);
        if (rule == null) return false;

        var tracked = new TrackedProcess(pid, name, path, rule, rule.PolicyId, parentPid);
        if (!_tracked.TryAdd(pid, tracked)) return false;

        _log.Log("PRC", $"attach pid={pid} name={name} rule={rule} policy={rule.PolicyId}");
        ProcessAttached?.Invoke(tracked);
        return true;
    }

    private ProcessRule? FindMatchingRule(string name, string? path)
    {
        IReadOnlyList<ProcessRule> rules;
        lock (_rulesLock) rules = _rules;

        foreach (ProcessRule rule in rules)
        {
            if (ProcessRuleMatcher.IsMatch(rule, name, path)) return rule;
        }
        return null;
    }

    private void Detach(uint pid, string reason)
    {
        if (!_tracked.TryRemove(pid, out TrackedProcess? tracked)) return;
        _log.Log("PRC", $"detach pid={pid} name={tracked.Name} ({reason})");
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

            ProcessRule? rule = FindMatchingRule(tracked.Name, tracked.ExecutablePath);
            if (rule == null) Detach(kv.Key, "no longer matches any rule");
            else if (rule.PolicyId != tracked.PolicyId)
            {
                // The policy changed: re-attach so the engine reads the new assignment.
                Detach(kv.Key, "policy changed");
                TryAttach(tracked.ProcessId, tracked.Name, tracked.ExecutablePath, tracked.ParentProcessId);
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
            _log.Log("PRC", "Process watching via WMI process traces");
            return true;
        }
        catch (Exception ex)
        {
            _log.Log("PRC", $"WMI watcher failed: {ex.GetType().Name}: {ex.Message}");
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
            string? path = ProcessFinder.FindById(pid)?.ExecutablePath;

            if (!TryAttach(pid, name, path, parentPid))
            {
                // Not a match itself — but it may be the child of something already tracked.
                AttachChild(pid, parentPid);
            }
        }
        catch (Exception ex)
        {
            _log.Log("PRC", $"WMI start event failed: {ex.GetType().Name}: {ex.Message}");
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
            _log.Log("PRC", $"WMI stop event failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { ScanOnce(); }
            catch (Exception ex)
            {
                _log.Log("PRC", $"Poll scan failed: {ex.GetType().Name}: {ex.Message}");
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
