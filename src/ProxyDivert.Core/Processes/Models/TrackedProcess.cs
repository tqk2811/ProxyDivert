using System;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Processes.Models;

// A running process the watcher has matched to a rule, and therefore put under redirection.
public sealed class TrackedProcess
{
    public uint ProcessId { get; }
    public string Name { get; }
    public string? ExecutablePath { get; }

    // The rule that matched. Null for a child adopted through IncludeChildren — it inherits the
    // parent's policy without matching a rule of its own.
    public ProcessRule? MatchedRule { get; }

    public Guid PolicyId { get; }
    public uint ParentProcessId { get; }
    public DateTime AttachedUtc { get; } = DateTime.UtcNow;

    public TrackedProcess(uint processId, string name, string? executablePath, ProcessRule? matchedRule, Guid policyId, uint parentProcessId = 0)
    {
        ProcessId = processId;
        Name = name;
        ExecutablePath = executablePath;
        MatchedRule = matchedRule;
        PolicyId = policyId;
        ParentProcessId = parentProcessId;
    }

    public bool IsChild => MatchedRule == null;

    public override string ToString() => $"[{ProcessId}] {Name}";
}
