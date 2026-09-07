using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Processes.Models;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// Decides which of the running processes the user's filters describe, and reports them so the
/// engine can put them under redirection and take them out again when they exit.
/// </summary>
/// <remarks>
/// This is one half of what used to be a single watcher; <see cref="ProcessInventory"/> is the
/// other. The split follows their lifetimes: the table of running processes is worth keeping from
/// the moment the window opens, while matching them against filters only means anything while the
/// engine runs — and starting the engine should not mean rediscovering the machine.
///
/// It also means every question this class asks is answered from memory. A rule edit used to cost
/// one WMI query per redirected process (~210ms each, on the UI thread); it now costs a dictionary
/// lookup each, because the process table already holds the command line.
///
/// Children: when a tracked process has spawned another, the child inherits its policies. That is
/// what makes launchers and multi-process browsers work, and — because the table knows the parent
/// of EVERY process, not just of those that started while the tool was watching — it now works for
/// a browser that was already open before the engine was started.
/// </remarks>
public sealed class ProcessRuleTracker : IDisposable
{
    // A child's parent may itself be a child, so following the chain takes more than one pass. The
    // depth is bounded by the process tree, not by this number; it only stops a cycle (which the
    // kernel cannot produce, but a pid handed out again can fake) from spinning forever.
    private const int MaxTreePasses = 16;

    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    private readonly ILogger<ProcessRuleTracker> _logger;
    private readonly ProcessInventory _inventory;
    private readonly ConcurrentDictionary<uint, TrackedProcess> _tracked
        = new ConcurrentDictionary<uint, TrackedProcess>();
    private readonly object _rulesLock = new object();

    private IReadOnlyList<ProcessRule> _rules = Array.Empty<ProcessRule>();
    private bool _started;

    /// <summary>A matched (or inherited) process appeared and should be redirected.</summary>
    public event Action<TrackedProcess>? ProcessAttached;

    /// <summary>A tracked process exited, or stopped matching after a rule change.</summary>
    public event Action<TrackedProcess>? ProcessDetached;

    /// <param name="inventory">
    /// The process table to read. NOT owned: it outlives every engine run, so it is never disposed
    /// here.
    /// </param>
    public ProcessRuleTracker(ILogger<ProcessRuleTracker> logger, ProcessInventory inventory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
    }

    public IReadOnlyCollection<TrackedProcess> Tracked => _tracked.Values.ToList();

    public bool IsTracked(uint processId) => _tracked.ContainsKey(processId);

    public bool TryGetTracked(uint processId, out TrackedProcess? process)
        => _tracked.TryGetValue(processId, out process);

    /// <summary>The pid to policies map the routing resolver needs. Each list is in priority order.</summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<Guid>> BuildPolicyMap()
        => _tracked.ToDictionary(kv => kv.Key, kv => kv.Value.PolicyIds);

    /// <summary>
    /// Starts matching. Every process already in the table is considered first, and only then are
    /// the table's events subscribed to.
    /// </summary>
    /// <remarks>
    /// That order is the safe one: a process that starts in between raises an event which finds it
    /// already attached, and attaching twice does nothing. The other order would let a process slip
    /// through the gap.
    /// </remarks>
    public void Start(IReadOnlyList<ProcessRule> rules)
    {
        if (_started) throw new InvalidOperationException("Already started");
        _started = true;

        SetRules(rules);
        MatchEverything();

        _inventory.ProcessStarted += OnProcessStarted;
        _inventory.ProcessStopped += OnProcessStopped;
    }

    /// <summary>
    /// Applies a new set of filters: newly matching processes are attached, processes no rule
    /// describes any more are detached, and processes that still match are re-described in place.
    /// Safe to call while running — this is what the window does after a filter is edited.
    /// </summary>
    public void ApplyRules(IReadOnlyList<ProcessRule> rules)
    {
        SetRules(rules);
        MatchEverything();
        ReconcileTrackedWithRules();
    }

    private void SetRules(IReadOnlyList<ProcessRule> rules)
    {
        lock (_rulesLock) _rules = rules ?? Array.Empty<ProcessRule>();
    }

    /// <summary>
    /// Runs every process in the table past the filters. Nothing is read from the operating system:
    /// the table already holds the name, the path and the command line.
    /// </summary>
    public void MatchEverything()
    {
        foreach (ProcessSnapshot process in _inventory.All) TryAttach(process);
        AdoptChildren();
    }

    // Puts one specific process under redirection without a filter describing it. This is what the
    // command line's --pid and --launch use, and it is the only safe way to redirect "this Chrome"
    // rather than every chrome.exe on the machine, the user's own browser included.
    public TrackedProcess? AttachProcessId(uint processId, Guid policyId, bool includeChildren = true)
    {
        if (processId == 0) return null;
        if (_tracked.TryGetValue(processId, out TrackedProcess? existing)) return existing;

        ProcessSnapshot? info = _inventory.Get(processId);

        // Even when asked for by hand: redirecting this process or the VPN helper loops the traffic
        // back into the relay, and nothing works again until the tool is killed.
        if (IsSelfOrHelper(processId, info?.Name ?? string.Empty))
        {
            _logger.LogWarning(
                "refusing to attach pid={Pid} ({Name}) — it carries the redirected traffic itself, so redirecting it would loop",
                processId, info?.Name);
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

        // Something it started before it was named may already be running.
        AdoptChildren();
        return tracked;
    }

    /// <summary>Adopts a child of a tracked process, giving it the parent's policies.</summary>
    public void AttachChild(uint childPid, uint parentPid)
    {
        if (_tracked.ContainsKey(childPid)) return;
        if (!_tracked.TryGetValue(parentPid, out TrackedProcess? parent)) return;
        if (!parent.IncludeChildren) return;
        if (IsSelfOrHelper(childPid, _inventory.Get(childPid)?.Name ?? string.Empty)) return;

        ProcessSnapshot? info = _inventory.Get(childPid);
        var child = new TrackedProcess(
            childPid,
            info?.Name ?? $"pid {childPid}",
            info?.ExecutablePath,
            matchedRule: null,               // inherited, not matched
            policyIds: parent.PolicyIds,
            parentProcessId: parentPid);

        if (!_tracked.TryAdd(childPid, child)) return;
        _logger.LogInformation(
            "attached child pid={Pid} of parent={ParentPid}, policies={Policies}",
            childPid, parentPid, string.Join(", ", parent.PolicyIds));
        ProcessAttached?.Invoke(child);
    }

    // Walks the table looking for descendants of anything already tracked, and keeps walking until
    // a pass changes nothing: a grandchild can only be recognised once its parent has been.
    //
    // This is what the separate tree poller used to do, one background thread per redirected root,
    // and it did it only for processes that started while the tool watched. The table knows the
    // parent of every process, so a browser that was already open when the engine started now has
    // its tabs adopted too.
    private void AdoptChildren()
    {
        for (int pass = 0; pass < MaxTreePasses; pass++)
        {
            bool adopted = false;

            foreach (ProcessSnapshot process in _inventory.All)
            {
                if (process.ParentProcessId == 0) continue;
                if (_tracked.ContainsKey(process.ProcessId)) continue;
                if (!_tracked.TryGetValue(process.ParentProcessId, out TrackedProcess? parent)) continue;
                if (!parent.IncludeChildren) continue;
                if (!IsPlausibleParent(process)) continue;

                AttachChild(process.ProcessId, process.ParentProcessId);
                adopted |= _tracked.ContainsKey(process.ProcessId);
            }

            if (!adopted) return;
        }
    }

    // Windows never clears a parent id, so a long-lived process can name a pid that has since been
    // handed to something unrelated — and that something is often younger than the "child". A
    // parent that started AFTER its supposed child is not a parent at all.
    private bool IsPlausibleParent(ProcessSnapshot child)
    {
        ProcessSnapshot? parent = _inventory.Get(child.ParentProcessId);
        if (parent is null) return false;
        if (parent.StartedUtc == DateTime.MinValue || child.StartedUtc == DateTime.MinValue) return true;
        return parent.StartedUtc <= child.StartedUtc;
    }

    private bool TryAttach(ProcessSnapshot process)
    {
        if (process.ProcessId == 0 || _tracked.ContainsKey(process.ProcessId)) return false;
        if (IsSelfOrHelper(process.ProcessId, process.Name)) return false;

        ProcessRule? rule = FindMatchingRule(process);
        if (rule == null) return false;

        var tracked = new TrackedProcess(
            process.ProcessId, process.Name, process.ExecutablePath,
            rule, rule.PolicyIds, process.ParentProcessId);

        if (!_tracked.TryAdd(process.ProcessId, tracked)) return false;

        _logger.LogInformation(
            "attached pid={Pid} name={Name} by filter {Rule}, policies={Policies}",
            process.ProcessId, process.Name, rule, string.Join(", ", rule.PolicyIds));
        ProcessAttached?.Invoke(tracked);
        return true;
    }

    // Two processes must never be redirected, however broad a filter is ("*.exe", Contains "e"):
    //
    //   * this one — its traffic IS the redirected traffic once it leaves the proxy, so capturing
    //     it again would loop every connection back into the relay forever;
    //   * wireproxy — it carries the VPN outbound. Redirecting the tunnel through the tunnel is the
    //     same loop, one process further out. A wireproxy the user started for something else is
    //     excluded too; that is the safe way round, and no filter can ever need it.
    private static bool IsSelfOrHelper(uint pid, string name)
    {
        if (pid == CurrentProcessId) return true;
        return name.Equals("wireproxy.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wireproxy", StringComparison.OrdinalIgnoreCase);
    }

    private ProcessRule? FindMatchingRule(ProcessSnapshot process)
    {
        IReadOnlyList<ProcessRule> rules;
        lock (_rulesLock) rules = _rules;

        foreach (ProcessRule rule in rules)
        {
            if (ProcessRuleMatcher.IsMatch(rule, process.Name, process.ExecutablePath, process.CommandLine))
                return rule;
        }
        return null;
    }

    private void Detach(uint pid, string reason)
    {
        if (!_tracked.TryRemove(pid, out TrackedProcess? tracked)) return;
        _logger.LogInformation("detached pid={Pid} name={Name} ({Reason})", pid, tracked.Name, reason);
        ProcessDetached?.Invoke(tracked);
    }

    private void OnProcessStarted(ProcessSnapshot process)
    {
        if (TryAttach(process)) return;

        // Not a match itself, but it may be the child of something already tracked.
        AttachChild(process.ProcessId, process.ParentProcessId);
    }

    private void OnProcessStopped(ProcessSnapshot process)
    {
        Detach(process.ProcessId, "exited");

        // Its children were tracked only because of it.
        foreach (var kv in _tracked)
        {
            if (kv.Value.IsChild && kv.Value.ParentProcessId == process.ProcessId)
                Detach(kv.Key, "parent no longer tracked");
        }
    }

    // After a filter change, every tracked process is read against the new filters. One no filter
    // describes any more is detached. One a filter still describes is re-described IN PLACE — the
    // entry under its pid swapped for one carrying the new filter and policies — rather than
    // detached and attached again: the redirector only ever cared about the pid, so there is nothing
    // at that level to redo. Redoing it anyway is what used to freeze the window on Save (sixty
    // browser processes, each closing and reopening a driver handle and reading the kernel tables)
    // and, worse, it forgot every flow those processes had for an instant, so each running
    // connection slipped out of capture and went direct.
    //
    // Children keep following their parent: no filter ever matched them, so they take whatever the
    // parent now has, and they go when it goes.
    private void ReconcileTrackedWithRules()
    {
        foreach (var kv in _tracked)
        {
            TrackedProcess tracked = kv.Value;

            // A process the caller named directly is not described by any filter, so a filter edit
            // has nothing to say about it. Children are dealt with after their parents, below.
            if (tracked.IsExplicit || tracked.IsChild) continue;

            ProcessSnapshot? process = _inventory.Get(tracked.ProcessId);
            if (process is null)
            {
                Detach(kv.Key, "exited");
                continue;
            }

            ProcessRule? rule = FindMatchingRule(process);
            if (rule == null)
            {
                Detach(kv.Key, "no longer matches any filter");
                continue;
            }
            if (ReferenceEquals(rule, tracked.MatchedRule)) continue;

            // The rule object is new after every save (the engine runs on a snapshot), so this swap
            // happens for every process on every save. It is an allocation, nothing more.
            if (_tracked.TryUpdate(kv.Key, tracked.WithRule(rule), tracked)
                && !rule.PolicyIds.SequenceEqual(tracked.PolicyIds))
            {
                _logger.LogInformation(
                    "pid={Pid} name={Name} now routed by policies={Policies}",
                    tracked.ProcessId, tracked.Name, string.Join(", ", rule.PolicyIds));
            }
        }

        FollowParents();
    }

    // Children after parents, and repeated until nothing moves: a grandchild read before its parent
    // was brought up to date would otherwise keep the old list for one more save.
    private void FollowParents()
    {
        for (int pass = 0; pass < MaxTreePasses; pass++)
        {
            bool moved = false;
            foreach (var kv in _tracked)
            {
                TrackedProcess child = kv.Value;
                if (!child.IsChild) continue;

                if (!_tracked.TryGetValue(child.ParentProcessId, out TrackedProcess? parent))
                {
                    Detach(kv.Key, "parent no longer tracked");
                    continue;
                }
                if (parent.PolicyIds.SequenceEqual(child.PolicyIds)) continue;

                moved |= _tracked.TryUpdate(kv.Key, child.WithPolicies(parent.PolicyIds), child);
            }
            if (!moved) return;
        }
    }

    public void Dispose()
    {
        _inventory.ProcessStarted -= OnProcessStarted;
        _inventory.ProcessStopped -= OnProcessStopped;
        _tracked.Clear();
    }
}
