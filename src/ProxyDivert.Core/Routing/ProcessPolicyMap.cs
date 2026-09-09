using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ProxyDivert.Core.Routing;

/// <summary>
/// A standalone pid -> policies table.
/// </summary>
/// <remarks>
/// The running engine does NOT use this one: <see cref="Processes.ProcessRuleTracker"/> answers
/// from the table of tracked processes it already keeps, and a second dictionary beside it would be
/// one more thing to keep in step at six mutation sites — which is exactly how a process could be
/// re-described (a filter edit moving it to another policy) and go on being routed by the old one.
///
/// This exists for callers that have a map and no tracker: tests, and asking what a configuration
/// would do with a hypothetical process.
/// </remarks>
public sealed class ProcessPolicyMap : IProcessPolicySource
{
    private readonly ConcurrentDictionary<uint, IReadOnlyList<Guid>> _byProcessId
        = new ConcurrentDictionary<uint, IReadOnlyList<Guid>>();

    /// <summary>
    /// A map that claims nothing. Every process falls through to the fallback policy.
    /// </summary>
    /// <remarks>
    /// A fresh one each time rather than a shared singleton: this class is mutable, and a shared
    /// "empty" map is one Set call away from being neither empty nor shared by accident.
    /// </remarks>
    public static ProcessPolicyMap Empty => new ProcessPolicyMap();

    public static ProcessPolicyMap From(IReadOnlyDictionary<uint, IReadOnlyList<Guid>>? entries)
    {
        var map = new ProcessPolicyMap();
        if (entries is null) return map;

        foreach (KeyValuePair<uint, IReadOnlyList<Guid>> entry in entries)
            map.Set(entry.Key, entry.Value);
        return map;
    }

    public bool TryGetPolicyIds(uint processId, out IReadOnlyList<Guid> policyIds)
    {
        if (_byProcessId.TryGetValue(processId, out IReadOnlyList<Guid>? found))
        {
            policyIds = found;
            return true;
        }

        policyIds = Array.Empty<Guid>();
        return false;
    }

    /// <summary>Replaces this process's list. Never edits the one a connection may be reading.</summary>
    public void Set(uint processId, IReadOnlyList<Guid> policyIds)
        => _byProcessId[processId] = policyIds ?? Array.Empty<Guid>();

    public void Remove(uint processId) => _byProcessId.TryRemove(processId, out _);

    public void Clear() => _byProcessId.Clear();
}
