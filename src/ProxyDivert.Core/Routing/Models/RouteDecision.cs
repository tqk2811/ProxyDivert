using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing.Models;

// The answer the resolver gives for one connection: which outbound, and why. The reason is kept
// because "why did this go direct?" is the question a user actually asks, and reconstructing it
// afterwards from the rule list is guesswork.
public sealed class RouteDecision
{
    public Outbound Outbound { get; }

    // The rule that matched, or null when the policy default applied.
    public RoutingRule? MatchedRule { get; }

    public RoutingPolicy Policy { get; }

    public RouteDecision(Outbound outbound, RoutingPolicy policy, RoutingRule? matchedRule)
    {
        Outbound = outbound;
        Policy = policy;
        MatchedRule = matchedRule;
    }

    public string Reason => MatchedRule != null
        ? $"{Policy.Name}: {MatchedRule.Matcher}:{MatchedRule.Pattern}"
        : "no policy matched";

    public override string ToString() => $"{Outbound.Name} <- {Reason}";
}
