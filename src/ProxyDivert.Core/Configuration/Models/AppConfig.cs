using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Processes.Enums;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Core.Vpn.Enums;
using TqkLibrary.WinDivert.Redirect.Enums;

namespace ProxyDivert.Core.Configuration.Models;

// Everything the tool remembers between runs, and the rules that keep it consistent.
//
// Serialised to JSON next to the executable, passwords included and in the clear — the file is
// meant to be readable and editable by hand (see ConfigStore), which is exactly why the integrity
// rules at the bottom of this file exist rather than being assumed.
public sealed class AppConfig
{
    public List<Outbound> Outbounds { get; set; } = new List<Outbound>();

    public List<RoutingPolicy> Policies { get; set; } = new List<RoutingPolicy>();

    public List<ProcessRule> ProcessRules { get; set; } = new List<ProcessRule>();

    public DnsSettings Dns { get; set; } = new DnsSettings();

    // Written next to the executable when set. Null = no packet-level trace file (the in-memory
    // log pane still works).
    public string? DiagnosticLogPath { get; set; }

    // Keep a trace file without having to name one: each run writes to its own timestamped file
    // under Logs\ next to the executable. A fresh file per run is the point — a bug reported an
    // hour ago is still on disk instead of having been truncated by the next start — and it takes
    // precedence over DiagnosticLogPath, which stays for pinning the trace to a fixed place.
    public bool AutoSaveLog { get; set; }

    // What happens to the target's IPv6 traffic: Redirect (default) sends it through the relay and
    // the routing rules exactly like IPv4; Block drops it so the application falls back to IPv4;
    // Ignore lets it leave untouched (it then bypasses the proxy — diagnostics only).
    public Ipv6Mode Ipv6 { get; set; } = Ipv6Mode.Redirect;

    // Path to wireproxy.exe, which runs the WireGuard tunnel of a VPN outbound in user space.
    // Null = look next to this executable and then on PATH. One setting for the whole machine:
    // it is the same binary whichever tunnel it runs.
    public string? WireProxyPath { get; set; }

    // How a process is found in the first place: from a process event, or from the socket it
    // opens. See ProcessDetectionMode — the second one judges a pid at the moment it connects,
    // which is later than a process event but tells us about a process that connects immediately.
    public ProcessDetectionMode ProcessDetection { get; set; } = ProcessDetectionMode.ProcessEvents;

    // Where the process table hears about processes starting and stopping. ETW is the kernel's own
    // provider and the shortest path; WMI is the same events after a service has repackaged them,
    // kept for a machine where a trace session cannot be created. A source that will not start
    // falls back to the other one on its own, so this is a preference rather than a requirement.
    public ProcessEventSourceKind ProcessEventSource { get; set; } = ProcessEventSourceKind.Etw;

    // UI preferences kept with the rest so one file is the whole state.
    public string? Language { get; set; }
    public string? Theme { get; set; }
    public bool StartWithWindows { get; set; }

    // Whether the switch was on when the application last closed. Restored on the next launch, so
    // a machine that reboots overnight comes back redirecting rather than quietly not. Written by
    // the switch itself, not by Save, so it records what the engine was doing rather than what the
    // configuration tab happened to be showing.
    public bool EngineEnabled { get; set; }

    // Whether closing the window hides it to the tray instead of ending the process. On by default:
    // the engine is meant to keep running, and the tray icon is there to say it still is.
    public bool MinimizeToTrayOnClose { get; set; } = true;

    // A fresh install still needs something that works: the Direct outbound, and one policy that
    // has no rules yet — so nothing is claimed and nothing is redirected until the user says so.
    public static AppConfig CreateDefault()
    {
        var policy = new RoutingPolicy
        {
            Id = Guid.NewGuid(),
            Name = "Default",
            OutboundId = Outbound.DirectId,
        };
        return new AppConfig
        {
            Outbounds = { Outbound.CreateDirect(), Outbound.CreateBlock() },
            Policies = { policy },
        };
    }

    // ==== integrity ====
    //
    // The three lists reference each other by id: a policy names an outbound, a filter names
    // policies. Nothing in the type system keeps those in step, and for a while four separate
    // places patched them up after the fact — one view model on deleting a policy, another on
    // deleting an outbound, the config store on load, and the resolver quietly skipping what it
    // could not find. They did not agree with each other, and the one caller that patched nothing
    // at all (the command line, which builds a configuration from scratch) could hand the engine
    // something none of those repairs had ever seen.
    //
    // So the rules live here instead, on the object that owns both sides of every reference.

    /// <summary>
    /// Removes a policy and every reference to it. False when there is no such policy, or when it
    /// is the last one left.
    /// </summary>
    /// <remarks>
    /// The last policy stays because a filter must always have somewhere to point. A filter that
    /// catches processes and then has no rules at all does not stop redirecting them — it sends
    /// them out direct, under the user's own address, which is the one outcome that must never
    /// happen by accident.
    /// </remarks>
    public bool RemovePolicy(Guid id)
    {
        RoutingPolicy? policy = Policies.FirstOrDefault(p => p.Id == id);
        if (policy is null || Policies.Count <= 1) return false;

        Policies.Remove(policy);
        DropMissingPolicyReferences();
        return true;
    }

    /// <summary>
    /// Removes an outbound and repoints at Block every policy that used it. False for the two
    /// built-ins, which are not the user's to delete, and for an id that is not in the list.
    /// </summary>
    /// <remarks>
    /// Block rather than Direct on purpose: a policy whose way out has gone is a mistake, and a
    /// mistake that shows up as a failed connection is one the user can find. Falling back to
    /// Direct would put their own address on the wire and say nothing about it.
    /// </remarks>
    public bool RemoveOutbound(Guid id)
    {
        Outbound? outbound = Outbounds.FirstOrDefault(o => o.Id == id);
        if (outbound is null || outbound.IsBuiltIn) return false;

        Outbounds.Remove(outbound);
        foreach (RoutingPolicy policy in Policies)
            if (policy.OutboundId == id) policy.OutboundId = Outbound.BlockId;

        return true;
    }

    /// <summary>
    /// Puts every reference in the configuration back into a state that resolves, and returns this
    /// same instance so it can be written as one expression.
    /// </summary>
    /// <remarks>
    /// Called on the way in from the file and on the way out to the engine, because those are the
    /// two doors a configuration that was never edited through this object comes in by: the file is
    /// plain JSON anyone may open in an editor, and the command line assembles its own. Edits made
    /// through <see cref="RemovePolicy"/> and <see cref="RemoveOutbound"/> never need it.
    ///
    /// Idempotent, and deliberately quiet — it repairs, it does not report. What it will not do is
    /// guess intent: an outbound whose URL is nonsense is left exactly as typed, to fail where the
    /// user can see it happen.
    /// </remarks>
    public AppConfig Normalize()
    {
        // An id that appears twice is not something routing can answer questions about: asked for
        // that policy it would have to pick one. Every table built from these lists is filled by
        // assignment, so the last one written is the one that survives — the same one here.
        KeepOneOfEachId(Outbounds, o => o.Id);
        KeepOneOfEachId(Policies, p => p.Id);
        KeepOneOfEachId(ProcessRules, r => r.Id);

        RestoreBuiltInOutbounds();

        // Nothing to point at is worse than pointing somewhere dull: the Rules tab with no policy
        // has no row to add a rule to, and every filter in the file is already dangling.
        if (Policies.Count == 0)
        {
            Policies.Add(new RoutingPolicy
            {
                Id = Guid.NewGuid(),
                Name = "Default",
                OutboundId = Outbound.DirectId,
            });
        }

        var outboundIds = new HashSet<Guid>(Outbounds.Select(o => o.Id));
        foreach (RoutingPolicy policy in Policies)
            if (!outboundIds.Contains(policy.OutboundId)) policy.OutboundId = Outbound.BlockId;

        DropMissingPolicyReferences();

        foreach (ProcessRule rule in ProcessRules)
            DropHolesInConditions(rule.Condition);

        return this;
    }

    /// <summary>
    /// Takes the nulls out of a condition tree, and gives a group with no list an empty one.
    /// </summary>
    /// <remarks>
    /// Both are valid JSON that the editor never writes. The engine reads a null as a row with
    /// nothing in it, and always has; but the filter window copies the tree before showing it, and
    /// the copy walked straight into the hole — so the one window that could have put the file
    /// right was the one that would not open.
    ///
    /// Not stopped at <see cref="ProcessCondition.MaxDepth"/> the way evaluation is: a hole below
    /// that line would still stop the copy. The recursion is bounded anyway, by the serializer's own
    /// limit on how deeply a file may nest.
    /// </remarks>
    private static void DropHolesInConditions(ProcessCondition? condition)
    {
        if (condition is not ConditionGroup group) return;

        group.Children ??= new List<ProcessCondition>();
        group.Children.RemoveAll(child => child is null);

        foreach (ProcessCondition child in group.Children)
            DropHolesInConditions(child);
    }

    /// <summary>
    /// Takes out of every filter the policies that are no longer there, and gives a filter left
    /// with none the first policy in the list.
    /// </summary>
    /// <remarks>
    /// The resolver skips an id it cannot find, so a dangling reference crashes nothing — it only
    /// means the filter is doing something the window cannot show. That is the part worth
    /// repairing: what the user sees and what routing does have to be the same list.
    /// </remarks>
    private void DropMissingPolicyReferences()
    {
        if (Policies.Count == 0) return;

        var policyIds = new HashSet<Guid>(Policies.Select(p => p.Id));
        Guid fallback = Policies[0].Id;

        foreach (ProcessRule rule in ProcessRules)
        {
            // The arrangement too, ticked or not: it remembers where a policy sat in the editor,
            // and a policy that no longer exists has no place to remember.
            rule.PolicyOrder.RemoveAll(id => !policyIds.Contains(id));
            rule.PolicyIds.RemoveAll(id => !policyIds.Contains(id));

            if (rule.PolicyIds.Count > 0) continue;

            rule.PolicyIds.Add(fallback);
            if (!rule.PolicyOrder.Contains(fallback)) rule.PolicyOrder.Insert(0, fallback);
        }
    }

    /// <summary>
    /// Puts Direct and Block back the way they are defined, and back in the list if they went
    /// missing.
    /// </summary>
    /// <remarks>
    /// Everything about the two except their name is fixed — Direct is the machine's own stack and
    /// Block is the absence of one, so a kind, a URL or a credential on either means nothing.
    /// Repaired rather than merely prevented, because for a while the interface let it happen: the
    /// outbound grid was read-only, which stopped the text cells but not the combo columns, so one
    /// stray click turned Direct into an HTTP proxy with no URL and saving kept it.
    ///
    /// The name is left alone. Policies reference these by id, so renaming one is the user's
    /// business and breaks nothing.
    /// </remarks>
    private void RestoreBuiltInOutbounds()
    {
        foreach (Outbound outbound in Outbounds)
        {
            if (!outbound.IsBuiltIn) continue;

            outbound.Kind = outbound.Id == Outbound.DirectId ? OutboundKind.Direct : OutboundKind.Block;
            outbound.Url = null;
            outbound.Username = null;
            outbound.Password = null;
            outbound.PreSharedKey = null;
            outbound.VpnProtocol = VpnProtocol.Auto;
            outbound.IsEnabled = true;
        }

        // The engine puts them back for itself when they are missing, so their absence never broke
        // routing. It did break the window, which offers only what is in this list: a policy whose
        // outbound is not there reads as an empty cell the user cannot fill back in.
        if (Outbounds.All(o => o.Id != Outbound.DirectId)) Outbounds.Insert(0, Outbound.CreateDirect());
        if (Outbounds.All(o => o.Id != Outbound.BlockId))
            Outbounds.Insert(Math.Min(1, Outbounds.Count), Outbound.CreateBlock());
    }

    // Keeps the LAST of each id rather than the first, matching every table built from these lists:
    // they are filled by assignment, so a repeated id ends up holding whichever came last.
    private static void KeepOneOfEachId<T>(List<T> items, Func<T, Guid> idOf)
    {
        if (items.Count < 2) return;

        var seen = new HashSet<Guid>();
        for (int i = items.Count - 1; i >= 0; i--)
            if (!seen.Add(idOf(items[i]))) items.RemoveAt(i);
    }
}
