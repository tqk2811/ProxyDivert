namespace ProxyDivert.Core.Routing.Models;

// The answer the resolver gives for one DNS query it takes over: resolve it over DNS over HTTPS
// through this outbound. A query the resolver leaves alone gets no decision at all (null), and goes
// out as plain DNS.
public sealed class DnsRouteDecision
{
    public Outbound Outbound { get; }

    // The policy that took the query over. Its SecureDnsFallbackToPlain says what to do when DoH fails.
    public RoutingPolicy Policy { get; }

    // The rule that matched, or null when the query fell to the first policy of the process with
    // nothing matching.
    public RoutingRule? MatchedRule { get; }

    public DnsRouteDecision(Outbound outbound, RoutingPolicy policy, RoutingRule? matchedRule)
    {
        Outbound = outbound;
        Policy = policy;
        MatchedRule = matchedRule;
    }

    public bool FallbackToPlainDns => Policy.SecureDnsFallbackToPlain;

    public override string ToString()
        => $"{Outbound.Name} <- {Policy.Name}"
           + (MatchedRule != null ? $": {MatchedRule.Matcher}:{MatchedRule.Pattern}" : ": no rule matched");
}
