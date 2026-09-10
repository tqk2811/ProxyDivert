using System;
using System.Collections.Generic;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Processes.Models;

// A running process the watcher has matched to a rule, and therefore put under redirection.
public sealed class TrackedProcess
{
    public uint ProcessId { get; }
    public string Name { get; }
    public string? ExecutablePath { get; }

    /// <summary>
    /// The command line the process was started with, or null when it could not be read. Carried
    /// along because this is the text a command-line condition is matched against: showing it is
    /// what lets the user see why a filter did or did not catch a process.
    /// </summary>
    public string? CommandLine { get; }

    // The rule that matched. Null for a child adopted through IncludeChildren, and for a process
    // the caller named directly (see IsExplicit).
    public ProcessRule? MatchedRule { get; }

    // True when the caller asked for this exact process id rather than describing it with a rule
    // (the CLI's --pid / --launch). Such a process is never dropped by a rule edit: nothing in the
    // rule list claims it in the first place.
    public bool IsExplicit { get; }

    /// <summary>
    /// The policies this process is routed by, in priority order — the filter's list, or the
    /// parent's for an adopted child. Empty only for a process the caller named directly without
    /// naming a policy.
    /// </summary>
    public IReadOnlyList<Guid> PolicyIds { get; }

    public uint ParentProcessId { get; }
    public DateTime AttachedUtc { get; } = DateTime.UtcNow;

    private readonly bool _includeChildrenOverride;

    public TrackedProcess(
        uint processId, string name, string? executablePath, ProcessRule? matchedRule,
        IReadOnlyList<Guid> policyIds,
        uint parentProcessId = 0, bool isExplicit = false, bool includeChildren = false,
        string? commandLine = null)
    {
        ProcessId = processId;
        Name = name;
        ExecutablePath = executablePath;
        CommandLine = commandLine;
        MatchedRule = matchedRule;
        PolicyIds = policyIds ?? Array.Empty<Guid>();
        ParentProcessId = parentProcessId;
        IsExplicit = isExplicit;
        _includeChildrenOverride = includeChildren;
    }

    // The copy behind WithRule/WithPolicies: same process, same moment of attachment, new routing.
    private TrackedProcess(TrackedProcess source, ProcessRule? matchedRule, IReadOnlyList<Guid> policyIds)
    {
        ProcessId = source.ProcessId;
        Name = source.Name;
        ExecutablePath = source.ExecutablePath;
        CommandLine = source.CommandLine;
        MatchedRule = matchedRule;
        PolicyIds = policyIds;
        ParentProcessId = source.ParentProcessId;
        IsExplicit = source.IsExplicit;
        AttachedUtc = source.AttachedUtc;
        _includeChildrenOverride = source._includeChildrenOverride;
    }

    /// <summary>
    /// The same process, now described by <paramref name="rule"/> and routed by its policies. This
    /// is how a rule edit reaches a process that is already redirected: the redirector only knows
    /// the pid, so nothing has to be detached for the routing to change.
    /// </summary>
    public TrackedProcess WithRule(ProcessRule rule)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));
        return new TrackedProcess(this, rule, rule.PolicyIds);
    }

    /// <summary>The same process routed by a different list — a child following its parent.</summary>
    public TrackedProcess WithPolicies(IReadOnlyList<Guid> policyIds)
        => new TrackedProcess(this, MatchedRule, policyIds ?? Array.Empty<Guid>());

    // A child adopted through IncludeChildren: it inherits a policy without claiming one itself.
    public bool IsChild => MatchedRule == null && !IsExplicit;

    // Whether processes spawned by this one should be redirected too.
    public bool IncludeChildren => MatchedRule?.IncludeChildren ?? _includeChildrenOverride;

    public override string ToString() => $"[{ProcessId}] {Name}";
}
