using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using ProxyDivert.Core.Routing.Compiled;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing;

// Turns (process, destination) into "which outbound".
//
// The resolver holds an immutable snapshot of the configuration: the compiled policies and the
// outbounds. Editing the config builds a NEW resolver rather than mutating this one, so a
// connection being routed right now can never observe a half-applied rule change.
//
// The pid -> policy assignments are NOT part of that snapshot: they are read live from whoever
// tracks processes. Which process is under redirection changes several times a minute on a machine
// running a browser, and rebuilding the whole table for each one meant re-parsing every pattern of
// every policy sixty times over a browser start. Each pid's list of policy ids is itself replaced
// whole, never edited in place, so a connection still resolves against one coherent list.
//
// A pid with no assignment resolves to the fallback policy (normally "everything direct"), which
// matters because the relay can see a connection from a process the watcher has just dropped.
public sealed class RoutingPolicyResolver
{
    private readonly CompiledRuleSet _ruleSet;
    private readonly IReadOnlyDictionary<Guid, Outbound> _outbounds;
    private readonly IProcessPolicySource _policiesByProcessId;
    private readonly CompiledPolicy _fallbackPolicy;

    // Machine-side secure DNS, worked out once: the policies that turned it on, each with only its
    // domain rules, in the order the user put them (see BuildSystemDnsPolicies).
    private readonly IReadOnlyList<CompiledPolicy> _systemDnsPolicies;

    // Names whose lookup is never taken over: see IsDnsPassThrough.
    private readonly HashSet<string> _dnsPassThroughHosts;

    /// <param name="processRules">
    /// The filters, in the user's order. Only machine-side secure DNS reads them; without them no
    /// query from an untracked process is ever taken over.
    /// </param>
    /// <param name="dohEndpoint">
    /// The DNS over HTTPS server the taken-over queries go to. Its host name joins the names that
    /// are never taken over.
    /// </param>
    /// <param name="extraDnsPassThroughHosts">
    /// More names never taken over: the servers of VPN outbounds configured from a file, which this
    /// class cannot see without reading the file (see VpnServerHostReader).
    /// </param>
    public RoutingPolicyResolver(
        CompiledRuleSet ruleSet,
        IEnumerable<Outbound> outbounds,
        IProcessPolicySource policiesByProcessId,
        RoutingPolicy? fallbackPolicy = null,
        IEnumerable<ProcessRule>? processRules = null,
        string? dohEndpoint = null,
        IEnumerable<string>? extraDnsPassThroughHosts = null)
    {
        _ruleSet = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));
        if (outbounds is null) throw new ArgumentNullException(nameof(outbounds));
        _policiesByProcessId = policiesByProcessId ?? ProcessPolicyMap.Empty;

        // Assignment rather than ToDictionary: two outbounds sharing an id is something a
        // hand-edited file can say, and ToDictionary answers it by throwing — out of the engine's
        // Start, leaving the machine with no redirection at all over a duplicated line. The last
        // one written wins, which is what AppConfig.Normalize keeps and what the compiled rule set
        // does with a repeated policy.
        var byId = new Dictionary<Guid, Outbound>();
        foreach (Outbound outbound in outbounds) byId[outbound.Id] = outbound;
        // The two built-ins always resolve, whether or not the user's list contains them.
        if (!byId.ContainsKey(Outbound.DirectId)) byId[Outbound.DirectId] = Outbound.CreateDirect();
        if (!byId.ContainsKey(Outbound.BlockId)) byId[Outbound.BlockId] = Outbound.CreateBlock();
        _outbounds = byId;

        _fallbackPolicy = CompiledRuleSet.CompilePolicy(fallbackPolicy ?? new RoutingPolicy
        {
            Id = Guid.Empty,
            Name = "Untracked",
            OutboundId = Outbound.DirectId,
        });

        _systemDnsPolicies = BuildSystemDnsPolicies(ruleSet, processRules);
        _dnsPassThroughHosts = BuildDnsPassThroughHosts(_outbounds.Values, dohEndpoint);
        if (extraDnsPassThroughHosts != null)
            foreach (string host in extraDnsPassThroughHosts) AddHostName(_dnsPassThroughHosts, host);
    }

    /// <summary>
    /// Compiles the policies and resolves against a fixed pid map. For callers that have a
    /// configuration and nothing tracking processes — tests, and one-off questions about what a
    /// configuration would do.
    /// </summary>
    public RoutingPolicyResolver(
        IEnumerable<RoutingPolicy> policies,
        IEnumerable<Outbound> outbounds,
        IReadOnlyDictionary<uint, IReadOnlyList<Guid>>? policiesByProcessId,
        RoutingPolicy? fallbackPolicy = null,
        IEnumerable<ProcessRule>? processRules = null,
        string? dohEndpoint = null)
        : this(
            CompiledRuleSet.Compile(policies ?? throw new ArgumentNullException(nameof(policies))),
            outbounds,
            ProcessPolicyMap.From(policiesByProcessId),
            fallbackPolicy,
            processRules,
            dohEndpoint)
    {
    }

    /// <summary>The rules of this configuration that could not be parsed. Empty when all of them can.</summary>
    public IReadOnlyList<RulePatternError> RuleErrors => _ruleSet.Errors;

    /// <summary>
    /// The policies applied to this process, in the order the filter listed them. Empty never
    /// happens: a process nothing claims gets the fallback policy.
    /// </summary>
    public IReadOnlyList<RoutingPolicy> GetPolicies(uint processId)
        => GetCompiledPolicies(processId).Select(p => p.Source).ToList();

    /// <summary>
    /// The policy whose own settings apply to this process — the first one the filter listed. The
    /// rest contribute rules only.
    /// </summary>
    public RoutingPolicy GetPolicy(uint processId) => GetCompiledPolicies(processId)[0].Source;

    private IReadOnlyList<CompiledPolicy> GetCompiledPolicies(uint processId)
    {
        if (!_policiesByProcessId.TryGetPolicyIds(processId, out IReadOnlyList<Guid> ids) || ids.Count == 0)
            return new[] { _fallbackPolicy };

        // A policy the user deleted while its filter still names it is skipped rather than faked:
        // its rules are gone, and pretending otherwise would route by a list nobody can see.
        var found = new List<CompiledPolicy>(ids.Count);
        foreach (Guid id in ids)
            if (_ruleSet.TryGetPolicy(id, out CompiledPolicy? policy)) found.Add(policy!);

        return found.Count > 0 ? found : new[] { _fallbackPolicy };
    }

    // First matching enabled rule wins, across every policy in turn: all of the first policy's
    // rules in their own Order, then the second policy's, and so on. That is what the order in the
    // filter means — one list read end to end, not a merge.
    //
    // Where it goes is the policy's outbound, not the rule's: a rule says which destinations belong
    // to this policy, and everything that belongs to it leaves the same way.
    //
    // Nothing matching anywhere means no policy claimed the connection, and it goes Direct. A
    // policy whose outbound no longer exists, or is disabled, is skipped rather than silently
    // sending the connection direct under that policy's name.
    public RouteDecision Resolve(RouteTarget target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        return Resolve(target, GetCompiledPolicies(target.ProcessId), applyAntiDpi: true);
    }

    // applyAntiDpi: TCP gets the matching policy's anti-DPI overrides; UDP has no ClientHello to
    // split, and an override would only build its outbound a second, identical source.
    private RouteDecision Resolve(RouteTarget target, IReadOnlyList<CompiledPolicy> policies, bool applyAntiDpi)
    {
        foreach (CompiledPolicy policy in policies)
        {
            foreach (CompiledRule rule in policy.Rules)
            {
                if (!rule.IsMatch(target)) continue;

                if (TryGetUsableOutbound(policy.Source.OutboundId, out Outbound? outbound))
                {
                    if (applyAntiDpi)
                        outbound = outbound!.WithAntiDpi(policy.Source.AntiDpiTls, policy.Source.AntiDpiConnect, policy.Source.AntiDpiChunkSize);
                    return new RouteDecision(outbound!, policy.Source, rule.Source);
                }
            }
        }

        return new RouteDecision(_outbounds[Outbound.DirectId], policies[0].Source, null);
    }

    // UDP that is not DNS. The datagram follows the SAME decision its TCP twin would get, and only
    // when that decision is a proxy or a VPN do the UDP settings come into it:
    //
    //   * TCP would go Direct — nothing claimed the destination, or the policy's outbound is Direct
    //     — then the datagram goes direct too, QUIC included. BlockQuic exists to stop a browser
    //     from slipping past the proxy over UDP/443 while its TCP is tunnelled; with no proxy in the
    //     picture there is nothing to slip past, and blocking it only makes the browser spend
    //     seconds retrying QUIC before it falls back to the TCP that was going to work all along.
    //   * TCP would be tunnelled: UdpMode decides. ThroughOutbound rides the tunnel when the
    //     outbound can carry UDP; otherwise the QUIC block applies (a datagram that reaches the
    //     destination direct carries the real source address), then Direct or Block as configured.
    //
    // The UDP settings are read off the first policy, like every setting that is not a rule: a
    // filter listing three policies would otherwise have three answers to "is QUIC blocked".
    public RouteDecision ResolveUdp(RouteTarget target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));

        // Read once, and both the settings and the rules come from the same list: asking twice
        // could see the process re-described in between and answer with one policy's BlockQuic
        // against another policy's rules.
        IReadOnlyList<CompiledPolicy> policies = GetCompiledPolicies(target.ProcessId);
        RoutingPolicy policy = policies[0].Source;

        if (policy.UdpMode == UdpMode.Block)
            return new RouteDecision(_outbounds[Outbound.BlockId], policy, null);

        RouteDecision tcpDecision = Resolve(target, policies, applyAntiDpi: false);
        // Neither of these has an outbound to ride, so UdpMode has nothing left to decide.
        if (!tcpDecision.UsesTunnel) return tcpDecision;

        switch (policy.UdpMode)
        {
            case UdpMode.ThroughOutbound when tcpDecision.Outbound.SupportsUdp:
                return tcpDecision;

            case UdpMode.ThroughOutbound:
            case UdpMode.Direct when policy.BlockQuic && target.Port == 443:
                return new RouteDecision(_outbounds[Outbound.BlockId], policy, tcpDecision.MatchedRule);

            case UdpMode.Direct:
                return new RouteDecision(_outbounds[Outbound.DirectId], policy, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(target), policy.UdpMode, "Unknown UdpMode");
        }
    }

    // ---- secure DNS --------------------------------------------------------------------------
    //
    // Called on the packet pump for every DNS query, so everything below reads the immutable
    // snapshot built in the constructor: no locks, no I/O, nothing allocated beyond the decision.

    /// <summary>
    /// Whether a DNS query is taken over and resolved over DNS over HTTPS, and through which
    /// outbound. Null means leave it alone: it goes out as plain DNS.
    /// </summary>
    /// <param name="processId">The process that sent it, or null when the socket is not known.</param>
    /// <param name="queryName">The name asked about, lower case, without the trailing dot.</param>
    /// <remarks>
    /// A process under redirection is answered by its own policies (<see cref="ResolveProcessDns"/>);
    /// anything else — no pid, or a pid no filter caught, which is where the Windows DNS client
    /// service asking for everyone ends up — by the policies that turned machine-side secure DNS on
    /// (<see cref="ResolveSystemDns"/>). A tracked process whose policies decline is NOT handed on
    /// to the machine side: its filter has spoken.
    /// </remarks>
    public DnsRouteDecision? ResolveDns(uint? processId, string queryName, bool isIpv6 = false)
    {
        if (string.IsNullOrEmpty(queryName) || IsDnsPassThrough(queryName)) return null;

        return processId is uint pid && IsTracked(pid)
            ? ResolveProcessDns(pid, queryName, isIpv6)
            : ResolveSystemDns(queryName);
    }

    /// <summary>
    /// The process side: the query is judged exactly like a UDP connection to port 53 of that name
    /// would be, and taken over only when the policy that claims it turned SecureDnsProcess on.
    /// Nothing claiming it means the process's first policy, sending it Direct — again only if that
    /// policy turned it on. A Block outbound takes nothing over: there is no path to resolve on.
    /// </summary>
    public DnsRouteDecision? ResolveProcessDns(uint processId, string queryName, bool isIpv6 = false)
    {
        if (string.IsNullOrEmpty(queryName) || IsDnsPassThrough(queryName)) return null;

        var target = new RouteTarget(
            processId, isIpv6 ? IPAddress.IPv6Any : IPAddress.Any, 53, queryName, isUdp: true);
        RouteDecision decision = Resolve(target, GetCompiledPolicies(processId), applyAntiDpi: false);

        if (!decision.Policy.SecureDnsProcess || decision.IsBlocked) return null;
        return new DnsRouteDecision(decision.Outbound, decision.Policy, decision.MatchedRule);
    }

    /// <summary>
    /// The machine side: the policies that turned SecureDnsSystem on, read in the user's order —
    /// filter by filter as the list shows them, each filter's ticked policies in its own order, a
    /// policy already read skipped — and within each only its domain rules, in Order. The first
    /// rule that claims the name decides. Nothing claiming it leaves it alone.
    /// </summary>
    public DnsRouteDecision? ResolveSystemDns(string queryName)
    {
        if (string.IsNullOrEmpty(queryName) || IsDnsPassThrough(queryName)) return null;
        if (_systemDnsPolicies.Count == 0) return null;

        var target = new RouteTarget(0, IPAddress.Any, 53, queryName, isUdp: true);
        foreach (CompiledPolicy policy in _systemDnsPolicies)
        {
            foreach (CompiledRule rule in policy.Rules)
            {
                if (!rule.IsMatch(target)) continue;

                // Same as a connection: a policy whose way out is gone or switched off is passed
                // over rather than resolving under its name some other way.
                if (!TryGetUsableOutbound(policy.Source.OutboundId, out Outbound? outbound)) continue;
                if (outbound!.IsBlocked) return null;
                return new DnsRouteDecision(outbound, policy.Source, rule.Source);
            }
        }

        return null;
    }

    /// <summary>
    /// The names whose lookup must always go out as plain DNS: the DoH server itself and every
    /// enabled outbound's server. Taking those over would resolve the way out through the way out,
    /// which cannot work before it is up.
    /// </summary>
    public bool IsDnsPassThrough(string queryName)
        => _dnsPassThroughHosts.Count > 0 && _dnsPassThroughHosts.Contains(NormalizeDnsName(queryName));

    private bool IsTracked(uint processId)
        => _policiesByProcessId.TryGetPolicyIds(processId, out IReadOnlyList<Guid> ids) && ids.Count > 0;

    // Filters top to bottom, a disabled filter skipped; each filter's ticked policies in its order;
    // a policy listed by an earlier filter already has its place. The built-in Default policy is
    // left out whatever its flag says: its one rule matches everything, which for the machine side
    // would mean every lookup on the machine.
    private static IReadOnlyList<CompiledPolicy> BuildSystemDnsPolicies(
        CompiledRuleSet ruleSet, IEnumerable<ProcessRule>? processRules)
    {
        if (processRules is null) return Array.Empty<CompiledPolicy>();

        var seen = new HashSet<Guid>();
        var result = new List<CompiledPolicy>();
        foreach (ProcessRule filter in processRules)
        {
            if (filter is null || !filter.IsEnabled) continue;
            foreach (Guid id in filter.PolicyIds)
            {
                if (!seen.Add(id)) continue;
                if (!ruleSet.TryGetPolicy(id, out CompiledPolicy? policy)) continue;
                if (policy!.Source.IsBuiltIn || !policy.Source.SecureDnsSystem) continue;

                // A negated rule ("not x") says nothing about one name: it would claim nearly every
                // lookup on the machine, so only plain domain rules count here.
                var domainRules = policy.Rules
                    .Where(r => !r.Source.IsNot && IsDomainMatcher(r.Source.Matcher))
                    .ToList();
                if (domainRules.Count > 0) result.Add(new CompiledPolicy(policy.Source, domainRules));
            }
        }
        return result;
    }

    private static bool IsDomainMatcher(HostMatcherType matcher) => matcher switch
    {
        HostMatcherType.Wildcard or HostMatcherType.Equals or HostMatcherType.DomainSuffix
            or HostMatcherType.StartsWith or HostMatcherType.EndsWith or HostMatcherType.Contains
            or HostMatcherType.Regex => true,
        _ => false,
    };

    // Only names: an IP literal is never looked up. A VPN outbound pointing at a configuration file
    // names its server inside the file, which is not read here (no I/O on this path); the engine
    // reads those and hands them in as extraDnsPassThroughHosts.
    private static HashSet<string> BuildDnsPassThroughHosts(IEnumerable<Outbound> outbounds, string? dohEndpoint)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Uri.TryCreate(dohEndpoint, UriKind.Absolute, out Uri? endpoint))
            AddHostName(hosts, endpoint.IdnHost);

        foreach (Outbound outbound in outbounds)
        {
            if (!outbound.IsEnabled) continue;
            AddHostName(hosts, outbound.Address?.Host);
        }
        return hosts;
    }

    private static void AddHostName(HashSet<string> hosts, string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        string name = NormalizeDnsName(host!);
        if (name.Length == 0) return;
        hosts.Add(name);
    }

    // Lower-case ASCII (punycode) form without the root dot; empty for an IP literal, which is never
    // looked up by name.
    private static string NormalizeDnsName(string name)
    {
        string trimmed = name.Trim().Trim('[', ']');
        if (trimmed.Length == 0 || IPAddress.TryParse(trimmed, out _)) return string.Empty;

        string lowered = trimmed.ToLowerInvariant().TrimEnd('.');
        try
        {
            return new System.Globalization.IdnMapping().GetAscii(lowered);
        }
        catch (ArgumentException)
        {
            return lowered;
        }
    }

    private bool TryGetUsableOutbound(Guid id, out Outbound? outbound)
    {
        outbound = null;
        if (!_outbounds.TryGetValue(id, out Outbound? found)) return false;
        if (!found.IsEnabled) return false;
        outbound = found;
        return true;
    }
}
