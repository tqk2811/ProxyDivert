using System;
using System.Linq;
using System.Net;
using ProxyDivert.Core.Routing.Compiled;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// Compiling a configuration is the moment a pattern is looked at. Before there was one, an
// unusable pattern matched nothing on every connection and said so nowhere.
public class CompiledRuleSetTests
{
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

    private static RoutingPolicy Policy(params RoutingRule[] rules)
    {
        var policy = new RoutingPolicy { Id = Guid.NewGuid(), Name = "test" };
        policy.Rules.AddRange(rules);
        return policy;
    }

    private static RouteTarget Target(string? host, string address = "93.184.216.34", int port = 443, bool isUdp = false)
        => new RouteTarget(1234, IPAddress.Parse(address), port, host, isUdp);

    [Fact]
    public void APatternThatCannotBeParsed_IsNamedInsteadOfQuietlyMatchingNothing()
    {
        RoutingPolicy policy = Policy(Rule(HostMatcherType.Regex, "([unclosed"));

        CompiledRuleSet set = CompiledRuleSet.Compile(new[] { policy });

        RulePatternError error = Assert.Single(set.Errors);
        Assert.Equal(policy.Id, error.PolicyId);
        Assert.Equal("([unclosed", error.Pattern);
        // And it still matches nothing, which is the only safe answer for a rule nobody can read.
        Assert.True(set.TryGetPolicy(policy.Id, out CompiledPolicy? compiled));
        Assert.False(compiled!.Rules[0].IsMatch(Target("anything")));
    }

    [Theory]
    [InlineData(HostMatcherType.IpCidr, "not-an-ip")]
    [InlineData(HostMatcherType.IpCidr, "10.0.0.0/99")]
    [InlineData(HostMatcherType.Port, "https")]
    [InlineData(HostMatcherType.Port, "80-http")]
    [InlineData(HostMatcherType.Protocol, "icmp")]
    [InlineData(HostMatcherType.Equals, "   ")]
    public void EveryKindOfUnusablePattern_IsReported(HostMatcherType matcher, string pattern)
    {
        CompiledRuleSet set = CompiledRuleSet.Compile(new[] { Policy(Rule(matcher, pattern)) });

        Assert.Single(set.Errors);
    }

    [Fact]
    public void AnInvertedRuleWithAnUnusablePattern_ClaimsEverything()
    {
        // "everything EXCEPT <nonsense>" is everything. That is what it has always done, and the
        // reason it is worth a test is that the rule looks narrow on screen while claiming the lot
        // — which is exactly why the compile error above has to be visible somewhere.
        CompiledRuleSet set = CompiledRuleSet.Compile(
            new[] { Policy(Rule(HostMatcherType.IpCidr, "not-an-ip", isNot: true)) });

        Assert.True(set.TryGetPolicy(set.Policies.Single().Id, out CompiledPolicy? compiled));
        Assert.True(compiled!.Rules[0].IsMatch(Target(null)));
        Assert.Single(set.Errors);
    }

    [Fact]
    public void ADisabledRule_IsLeftOutRatherThanSkippedPerConnection()
    {
        RoutingPolicy policy = Policy(
            Rule(HostMatcherType.Equals, "kept.example"),
            Rule(HostMatcherType.Regex, "([unclosed", enabled: false));

        CompiledRuleSet set = CompiledRuleSet.Compile(new[] { policy });

        Assert.True(set.TryGetPolicy(policy.Id, out CompiledPolicy? compiled));
        Assert.Equal("kept.example", Assert.Single(compiled!.Rules).Source.Pattern);
        // A row that is switched off is not a row with a mistake in it.
        Assert.Empty(set.Errors);
    }

    [Fact]
    public void RulesAreSortedOnce_NotOnEveryConnection()
    {
        RoutingPolicy policy = Policy(
            Rule(HostMatcherType.Equals, "second", order: 2),
            Rule(HostMatcherType.Equals, "first", order: 1));

        CompiledRuleSet set = CompiledRuleSet.Compile(new[] { policy });

        Assert.True(set.TryGetPolicy(policy.Id, out CompiledPolicy? compiled));
        Assert.Equal(new[] { "first", "second" }, compiled!.Rules.Select(r => r.Source.Pattern));
    }

    [Fact]
    public void ACidrWrittenOffItsBoundary_StillMeansTheNetwork()
    {
        // "10.1.2.3/8" is what a user types when they mean 10.0.0.0/8. Masking at compile time
        // makes that true once instead of depending on which side is masked at match time.
        CompiledRuleSet set = CompiledRuleSet.Compile(
            new[] { Policy(Rule(HostMatcherType.IpCidr, "10.1.2.3/8")) });
        CompiledRule rule = set.Policies.Single().Rules[0];

        Assert.True(rule.IsMatch(Target(null, "10.9.9.9")));
        Assert.False(rule.IsMatch(Target(null, "11.9.9.9")));
        Assert.Empty(set.Errors);
    }

    [Fact]
    public void APortRangeTypedBackwards_StillCoversIt()
    {
        CompiledRuleSet set = CompiledRuleSet.Compile(
            new[] { Policy(Rule(HostMatcherType.Port, "8100-8000")) });
        CompiledRule rule = set.Policies.Single().Rules[0];

        Assert.True(rule.IsMatch(Target(null, port: 8050)));
        Assert.False(rule.IsMatch(Target(null, port: 8200)));
    }

    [Fact]
    public void TwoPoliciesSharingAnId_DoNotStopTheEngineFromStarting()
    {
        // A hand-edited configuration file can produce this. Refusing to compile it would leave the
        // machine with no redirection at all, which is worse than routing by one of the two.
        Guid shared = Guid.NewGuid();
        var first = new RoutingPolicy { Id = shared, Name = "first" };
        var second = new RoutingPolicy { Id = shared, Name = "second" };

        CompiledRuleSet set = CompiledRuleSet.Compile(new[] { first, second });

        Assert.True(set.TryGetPolicy(shared, out CompiledPolicy? compiled));
        Assert.Equal("second", compiled!.Source.Name);
    }
}
