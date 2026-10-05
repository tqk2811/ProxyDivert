using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Which DNS queries are taken over onto DNS over HTTPS, and through which outbound.
public class SecureDnsRoutingTests
{
    private const uint Pid = 4321;
    private const uint UntrackedPid = 999;

    private static readonly Guid ProxyAId = Guid.NewGuid();
    private static readonly Guid ProxyBId = Guid.NewGuid();

    private static Outbound Socks5(Guid id, string url = "socks5://127.0.0.1:1080", bool enabled = true) => new Outbound
    {
        Id = id,
        Name = "socks-" + id.ToString("N").Substring(0, 4),
        Kind = OutboundKind.Socks5,
        Url = url,
        IsEnabled = enabled,
    };

    private static RoutingPolicy Policy(
        string name, Guid outboundId, bool process = false, bool system = false, bool fallback = false,
        params RoutingRule[] rules)
    {
        var policy = new RoutingPolicy
        {
            Id = Guid.NewGuid(),
            Name = name,
            OutboundId = outboundId,
            SecureDnsProcess = process,
            SecureDnsSystem = system,
            SecureDnsFallbackToPlain = fallback,
        };
        policy.Rules.AddRange(rules);
        return policy;
    }

    private static RoutingRule Rule(
        HostMatcherType matcher, string pattern, int order = 0, bool isNot = false, bool enabled = true)
        => new RoutingRule
        {
            Id = Guid.NewGuid(),
            Matcher = matcher,
            Pattern = pattern,
            Order = order,
            IsNot = isNot,
            IsEnabled = enabled,
        };

    private static ProcessRule Filter(bool enabled, params RoutingPolicy[] policies) => new ProcessRule
    {
        Id = Guid.NewGuid(),
        Name = "filter",
        IsEnabled = enabled,
        PolicyIds = policies.Select(p => p.Id).ToList(),
    };

    private static RoutingPolicyResolver ProcessResolver(params RoutingPolicy[] pidPolicies)
        => new RoutingPolicyResolver(
            pidPolicies,
            new[] { Socks5(ProxyAId), Socks5(ProxyBId) },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = pidPolicies.Select(p => p.Id).ToArray() });

    private static RoutingPolicyResolver SystemResolver(
        IEnumerable<RoutingPolicy> policies, IEnumerable<ProcessRule> filters, params Outbound[] outbounds)
        => new RoutingPolicyResolver(
            policies,
            outbounds.Length > 0 ? outbounds : new[] { Socks5(ProxyAId), Socks5(ProxyBId) },
            policiesByProcessId: null,
            processRules: filters);

    // ---- process side ----------------------------------------------------------------------------

    [Fact]
    public void Process_query_claimed_by_a_policy_with_the_flag_goes_through_its_outbound()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, process: true, fallback: true,
            rules: Rule(HostMatcherType.DomainSuffix, "example.com"));

        DnsRouteDecision? decision = ProcessResolver(policy).ResolveDns(Pid, "www.example.com");

        Assert.NotNull(decision);
        Assert.Equal(ProxyAId, decision!.Outbound.Id);
        Assert.Same(policy, decision.Policy);
        Assert.True(decision.FallbackToPlainDns);
    }

    [Fact]
    public void Process_query_claimed_by_a_policy_without_the_flag_passes()
    {
        RoutingPolicy off = Policy("off", ProxyAId, process: false, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        RoutingPolicy on = Policy("on", ProxyBId, process: true, rules: Rule(HostMatcherType.Any, ""));

        // The first match decides; a later policy with the flag does not get a second go.
        Assert.Null(ProcessResolver(off, on).ResolveDns(Pid, "example.com"));
    }

    [Fact]
    public void Process_query_nothing_claims_falls_to_the_first_policy_direct_when_it_has_the_flag()
    {
        RoutingPolicy first = Policy("first", ProxyAId, process: true, rules: Rule(HostMatcherType.Equals, "other.org"));

        DnsRouteDecision? decision = ProcessResolver(first).ResolveDns(Pid, "example.com");

        Assert.NotNull(decision);
        Assert.True(decision!.Outbound.IsDirect);
        Assert.Same(first, decision.Policy);
        Assert.Null(decision.MatchedRule);
    }

    [Fact]
    public void Process_query_nothing_claims_passes_when_the_first_policy_has_no_flag()
    {
        RoutingPolicy first = Policy("first", ProxyAId, process: false, rules: Rule(HostMatcherType.Equals, "other.org"));
        RoutingPolicy second = Policy("second", ProxyBId, process: true, rules: Rule(HostMatcherType.Equals, "x.org"));

        Assert.Null(ProcessResolver(first, second).ResolveDns(Pid, "example.com"));
    }

    [Fact]
    public void Process_query_whose_policy_blocks_passes()
    {
        RoutingPolicy policy = Policy("block", Outbound.BlockId, process: true, rules: Rule(HostMatcherType.Any, ""));

        Assert.Null(ProcessResolver(policy).ResolveDns(Pid, "example.com"));
    }

    // The process side judges the query like a UDP/53 flow, so a Protocol rule claims it there.
    [Fact]
    public void Process_query_is_judged_as_udp_to_port_53()
    {
        RoutingPolicy policy = Policy("udp", ProxyAId, process: true, rules: Rule(HostMatcherType.Port, "53"));

        Assert.Equal(ProxyAId, ProcessResolver(policy).ResolveDns(Pid, "example.com")!.Outbound.Id);
    }

    // A tracked process whose policies decline is not handed to the machine side.
    [Fact]
    public void Tracked_process_declining_does_not_fall_to_the_machine_side()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, process: false, system: true,
            rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        var resolver = new RoutingPolicyResolver(
            new[] { policy },
            new[] { Socks5(ProxyAId) },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { policy.Id } },
            processRules: new[] { Filter(true, policy) });

        Assert.Null(resolver.ResolveDns(Pid, "example.com"));
        Assert.NotNull(resolver.ResolveDns(UntrackedPid, "example.com"));
        Assert.NotNull(resolver.ResolveDns(null, "example.com"));
    }

    // ---- machine side ----------------------------------------------------------------------------

    [Fact]
    public void System_query_follows_filter_order_then_policy_order()
    {
        RoutingPolicy a = Policy("a", ProxyAId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        RoutingPolicy b = Policy("b", ProxyBId, system: true, rules: Rule(HostMatcherType.Wildcard, "*.example.com"));

        // b's filter comes first, so b wins although a is listed first in the policies.
        var resolver = SystemResolver(new[] { a, b }, new[] { Filter(true, b), Filter(true, a) });
        Assert.Same(b, resolver.ResolveDns(null, "www.example.com")!.Policy);

        // Within one filter, its own order.
        resolver = SystemResolver(new[] { a, b }, new[] { Filter(true, a, b) });
        Assert.Same(a, resolver.ResolveDns(null, "www.example.com")!.Policy);
    }

    [Fact]
    public void System_query_keeps_a_policy_at_its_first_place()
    {
        RoutingPolicy a = Policy("a", ProxyAId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        RoutingPolicy b = Policy("b", ProxyBId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));

        var resolver = SystemResolver(new[] { a, b }, new[] { Filter(true, a, b), Filter(true, b, a) });

        Assert.Same(a, resolver.ResolveSystemDns("example.com")!.Policy);
    }

    [Fact]
    public void System_query_skips_policies_without_the_flag_and_disabled_filters()
    {
        RoutingPolicy off = Policy("off", ProxyAId, system: false, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        RoutingPolicy hidden = Policy("hidden", ProxyAId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        RoutingPolicy on = Policy("on", ProxyBId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));

        var resolver = SystemResolver(
            new[] { off, hidden, on }, new[] { Filter(true, off), Filter(false, hidden), Filter(true, on) });

        Assert.Same(on, resolver.ResolveSystemDns("example.com")!.Policy);
    }

    [Fact]
    public void System_query_reads_only_domain_rules()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, system: true, rules: new[]
        {
            Rule(HostMatcherType.Any, "", order: 0),
            Rule(HostMatcherType.Port, "53", order: 1),
            Rule(HostMatcherType.Protocol, "udp", order: 2),
            Rule(HostMatcherType.IpCidr, "0.0.0.0/0", order: 3),
        });

        Assert.Null(SystemResolver(new[] { policy }, new[] { Filter(true, policy) }).ResolveSystemDns("example.com"));
    }

    [Theory]
    [InlineData(HostMatcherType.Wildcard, "*.example.com", "a.example.com")]
    [InlineData(HostMatcherType.Equals, "example.com", "example.com")]
    [InlineData(HostMatcherType.DomainSuffix, "example.com", "a.b.example.com")]
    [InlineData(HostMatcherType.StartsWith, "exa", "example.com")]
    [InlineData(HostMatcherType.EndsWith, ".com", "example.com")]
    [InlineData(HostMatcherType.Contains, "ampl", "example.com")]
    [InlineData(HostMatcherType.Regex, "^ex.*\\.com$", "example.com")]
    public void System_query_matches_every_domain_matcher(HostMatcherType matcher, string pattern, string name)
    {
        RoutingPolicy policy = Policy("p", ProxyAId, system: true, rules: Rule(matcher, pattern));

        Assert.NotNull(SystemResolver(new[] { policy }, new[] { Filter(true, policy) }).ResolveSystemDns(name));
    }

    [Fact]
    public void System_query_respects_IsNot_and_disabled_rules()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, system: true, rules: new[]
        {
            Rule(HostMatcherType.DomainSuffix, "example.com", order: 0, enabled: false),
            Rule(HostMatcherType.DomainSuffix, "local", order: 1, isNot: true),
        });
        var resolver = SystemResolver(new[] { policy }, new[] { Filter(true, policy) });

        // The negated rule is not a statement about a name, so it never claims system DNS.
        Assert.Null(resolver.ResolveSystemDns("other.org"));
        Assert.Null(resolver.ResolveSystemDns("printer.local"));
        Assert.Null(resolver.ResolveSystemDns("example.com"));
    }

    [Fact]
    public void System_query_whose_policy_blocks_passes()
    {
        RoutingPolicy policy = Policy("p", Outbound.BlockId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));

        Assert.Null(SystemResolver(new[] { policy }, new[] { Filter(true, policy) }).ResolveSystemDns("example.com"));
    }

    [Fact]
    public void System_query_skips_a_policy_whose_outbound_is_disabled()
    {
        RoutingPolicy a = Policy("a", ProxyAId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        RoutingPolicy b = Policy("b", ProxyBId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));

        var resolver = SystemResolver(
            new[] { a, b }, new[] { Filter(true, a, b) }, Socks5(ProxyAId, enabled: false), Socks5(ProxyBId));

        Assert.Same(b, resolver.ResolveSystemDns("example.com")!.Policy);
    }

    [Fact]
    public void Default_policy_never_takes_over_machine_dns()
    {
        RoutingPolicy builtIn = RoutingPolicy.CreateDefault();
        builtIn.SecureDnsSystem = true;
        builtIn.Rules = new List<RoutingRule> { Rule(HostMatcherType.DomainSuffix, "example.com") };

        Assert.Null(SystemResolver(new[] { builtIn }, new[] { Filter(true, builtIn) }).ResolveSystemDns("example.com"));
    }

    [Fact]
    public void Normalize_turns_machine_dns_off_on_the_default_policy()
    {
        AppConfig config = AppConfig.CreateDefault();
        config.Policies.Single(p => p.IsBuiltIn).SecureDnsSystem = true;

        config.Normalize();

        Assert.False(config.Policies.Single(p => p.IsBuiltIn).SecureDnsSystem);
    }

    // ---- names that always pass ------------------------------------------------------------------

    [Fact]
    public void Outbound_servers_and_the_doh_host_always_pass()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, process: true, system: true, rules: Rule(HostMatcherType.Any, ""));
        policy.Rules.Add(Rule(HostMatcherType.Wildcard, "*", order: 1));
        var outbounds = new[]
        {
            Socks5(ProxyAId, "socks5://proxy.example.net:1080"),
            Socks5(ProxyBId, "socks5://off.example.net:1080", enabled: false),
        };
        var resolver = new RoutingPolicyResolver(
            new[] { policy },
            outbounds,
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { policy.Id } },
            processRules: new[] { Filter(true, policy) },
            dohEndpoint: "https://dns.Example.org/dns-query");

        Assert.True(resolver.IsDnsPassThrough("proxy.example.net"));
        Assert.True(resolver.IsDnsPassThrough("dns.example.org."));
        Assert.False(resolver.IsDnsPassThrough("off.example.net"));

        Assert.Null(resolver.ResolveDns(Pid, "proxy.example.net"));
        Assert.Null(resolver.ResolveDns(null, "dns.example.org"));
        Assert.NotNull(resolver.ResolveDns(Pid, "www.example.com"));
        Assert.NotNull(resolver.ResolveDns(null, "www.example.com"));
    }

    [Fact]
    public void Unicode_outbound_host_passes_by_its_punycode_name()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, system: true, rules: Rule(HostMatcherType.Any, ""));
        var resolver = new RoutingPolicyResolver(
            new[] { policy },
            new[] { Socks5(ProxyAId, "socks5://bücher.example:1080") },
            policiesByProcessId: null,
            processRules: new[] { Filter(true, policy) });

        Assert.True(resolver.IsDnsPassThrough("xn--bcher-kva.example"));
    }

    [Fact]
    public void Bracketed_ip_outbound_host_is_not_added()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, system: true, rules: Rule(HostMatcherType.Any, ""));
        var resolver = new RoutingPolicyResolver(
            new[] { policy },
            new[] { Socks5(ProxyAId, "socks5://[::1]:1080") },
            policiesByProcessId: null,
            processRules: new[] { Filter(true, policy) });

        Assert.False(resolver.IsDnsPassThrough("::1"));
        Assert.False(resolver.IsDnsPassThrough("example.com"));
    }

    // ---- why a query is left alone -------------------------------------------------------------------

    private static void AssertReason(DnsPassReason expected, RoutingPolicyResolver resolver, uint? pid, string name)
    {
        DnsRouteDecision? withOut = resolver.ResolveDns(pid, name, false, out DnsPassReason reason);
        Assert.Equal(expected, reason);
        Assert.Equal(expected == DnsPassReason.None, withOut is not null);

        // The 3-argument overload is the same decision, only without the reason.
        DnsRouteDecision? threeArg = resolver.ResolveDns(pid, name);
        Assert.Equal(withOut is null, threeArg is null);
        if (withOut is not null)
        {
            Assert.Same(withOut.Outbound, threeArg!.Outbound);
            Assert.Same(withOut.Policy, threeArg.Policy);
            Assert.Equal(withOut.Side, threeArg.Side);
        }
    }

    [Fact]
    public void Reason_None_when_taken_over_on_the_process_and_machine_side()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, process: true, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        var resolver = new RoutingPolicyResolver(
            new[] { policy },
            new[] { Socks5(ProxyAId) },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { policy.Id } },
            processRules: new[] { Filter(true, policy) });

        AssertReason(DnsPassReason.None, resolver, Pid, "example.com");
        AssertReason(DnsPassReason.None, resolver, null, "example.com");
    }

    [Fact]
    public void Reason_InvalidName_for_an_empty_name_on_every_path()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, process: true, system: true, rules: Rule(HostMatcherType.Any, ""));
        var resolver = new RoutingPolicyResolver(
            new[] { policy },
            new[] { Socks5(ProxyAId) },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { policy.Id } },
            processRules: new[] { Filter(true, policy) });

        AssertReason(DnsPassReason.InvalidName, resolver, Pid, "");
        AssertReason(DnsPassReason.InvalidName, resolver, null, "");
    }

    [Fact]
    public void Reason_PassThroughHost_for_an_outbound_server_name()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, process: true, rules: Rule(HostMatcherType.Any, ""));
        var resolver = new RoutingPolicyResolver(
            new[] { policy },
            new[] { Socks5(ProxyAId, "socks5://proxy.example.net:1080") },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { policy.Id } });

        AssertReason(DnsPassReason.PassThroughHost, resolver, Pid, "proxy.example.net");
    }

    [Fact]
    public void Reason_ProcessPolicyDeclined_when_the_claiming_policy_has_no_flag()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, process: false, rules: Rule(HostMatcherType.Any, ""));

        AssertReason(DnsPassReason.ProcessPolicyDeclined, ProcessResolver(policy), Pid, "example.com");
    }

    [Fact]
    public void Reason_ProcessBlocked_when_the_claiming_policy_blocks()
    {
        RoutingPolicy policy = Policy("block", Outbound.BlockId, process: true, rules: Rule(HostMatcherType.Any, ""));

        AssertReason(DnsPassReason.ProcessBlocked, ProcessResolver(policy), Pid, "example.com");
    }

    [Fact]
    public void Reason_NoSystemPolicy_when_no_policy_turned_the_machine_side_on()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, process: true, system: false, rules: Rule(HostMatcherType.Any, ""));

        AssertReason(DnsPassReason.NoSystemPolicy, ProcessResolver(policy), UntrackedPid, "example.com");
    }

    [Fact]
    public void Reason_NoMatchingPolicy_when_no_machine_side_rule_claims_the_name()
    {
        RoutingPolicy policy = Policy("p", ProxyAId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        RoutingPolicyResolver resolver = SystemResolver(new[] { policy }, new[] { Filter(true, policy) });

        AssertReason(DnsPassReason.NoMatchingPolicy, resolver, null, "other.org");
    }

    [Fact]
    public void Reason_SystemBlocked_when_the_machine_side_policy_blocks()
    {
        RoutingPolicy policy = Policy("p", Outbound.BlockId, system: true, rules: Rule(HostMatcherType.DomainSuffix, "example.com"));
        RoutingPolicyResolver resolver = SystemResolver(new[] { policy }, new[] { Filter(true, policy) });

        AssertReason(DnsPassReason.SystemBlocked, resolver, null, "www.example.com");
    }

    // A policy's own DoH server has to resolve as plain DNS, or the query for its name would be
    // sent to itself.
    [Fact]
    public void Dns_pass_through_includes_every_policys_own_doh_server()
    {
        RoutingPolicy a = Policy("a", ProxyAId, process: true, system: false, rules: Rule(HostMatcherType.Any, ""));
        a.DohEndpoint = "https://dns.Quad9.net/dns-query";
        RoutingPolicy b = Policy("b", ProxyAId, process: true, system: false, rules: Rule(HostMatcherType.Any, ""));
        b.DohEndpoint = "https://bücher.example/dns-query";
        RoutingPolicy broken = Policy("c", ProxyAId, process: true, system: false, rules: Rule(HostMatcherType.Any, ""));
        broken.DohEndpoint = "not a url";
        var resolver = new RoutingPolicyResolver(
            new[] { a, b, broken },
            new[] { Socks5(ProxyAId, "socks5://proxy.example.net:1080") },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { a.Id } },
            dohEndpoint: "https://global.example.org/dns-query");

        Assert.True(resolver.IsDnsPassThrough("global.example.org"));
        Assert.True(resolver.IsDnsPassThrough("dns.quad9.net"));
        Assert.True(resolver.IsDnsPassThrough("xn--bcher-kva.example"));
        Assert.Null(resolver.ResolveDns(Pid, "dns.quad9.net"));
        Assert.NotNull(resolver.ResolveDns(Pid, "www.example.com"));
    }

    // The pool only takes http(s); a name behind any other scheme must not be exempted from secure
    // DNS, or it would leak to plain DNS for a server nobody will ever query.
    [Fact]
    public void Dns_pass_through_ignores_a_policy_endpoint_that_is_not_http()
    {
        RoutingPolicy a = Policy("a", ProxyAId, process: true, system: false, rules: Rule(HostMatcherType.Any, ""));
        a.DohEndpoint = "ftp://bank.example/dns-query";
        var resolver = new RoutingPolicyResolver(
            new[] { a },
            new[] { Socks5(ProxyAId, "socks5://proxy.example.net:1080") },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { a.Id } },
            dohEndpoint: "https://global.example.org/dns-query");

        Assert.False(resolver.IsDnsPassThrough("bank.example"));
        Assert.NotNull(resolver.ResolveDns(Pid, "bank.example"));
    }

    [Fact]
    public void Dns_pass_through_skips_the_endpoint_of_a_policy_without_secure_dns()
    {
        RoutingPolicy plain = Policy("plain", ProxyAId, process: false, system: false, rules: Rule(HostMatcherType.Any, ""));
        plain.DohEndpoint = "https://dns.unused.example/dns-query";
        var resolver = new RoutingPolicyResolver(
            new[] { plain },
            new[] { Socks5(ProxyAId, "socks5://proxy.example.net:1080") },
            new Dictionary<uint, IReadOnlyList<Guid>> { [Pid] = new[] { plain.Id } },
            dohEndpoint: "https://global.example.org/dns-query");

        Assert.False(resolver.IsDnsPassThrough("dns.unused.example"));
        Assert.True(resolver.IsDnsPassThrough("global.example.org"));
    }
}
