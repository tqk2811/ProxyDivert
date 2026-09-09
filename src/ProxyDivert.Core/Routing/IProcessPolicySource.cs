using System;
using System.Collections.Generic;

namespace ProxyDivert.Core.Routing;

/// <summary>
/// Which policies apply to a process, asked once per connection.
/// </summary>
/// <remarks>
/// Read live rather than copied into the resolver, because this is the half of routing that keeps
/// moving: a browser start attaches sixty processes in a few seconds, and each of those used to
/// mean copying the whole table and rebuilding the routing snapshot on top of it.
///
/// The contract that makes reading it live safe is that a pid's list is REPLACED, never edited: a
/// connection takes the list it finds and works with that one to the end, so it can be routed by
/// the assignment from a moment ago but never by half of two.
/// </remarks>
public interface IProcessPolicySource
{
    /// <summary>
    /// The policies that apply to this process, in priority order. False when nothing claims it,
    /// which is the answer for every process that is not under redirection.
    /// </summary>
    bool TryGetPolicyIds(uint processId, out IReadOnlyList<Guid> policyIds);
}
