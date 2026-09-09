using System;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing.Compiled;

/// <summary>One enabled rule of a policy, with its pattern already parsed.</summary>
/// <remarks>
/// The <see cref="RoutingRule"/> it was built from is kept whole, because that is what a decision
/// reports back: "why did this go direct?" is answered with the row the user can see, not with a
/// predicate object.
/// </remarks>
public sealed class CompiledRule
{
    private readonly IHostPredicate _predicate;

    public CompiledRule(RoutingRule source, IHostPredicate predicate)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
    }

    public RoutingRule Source { get; }

    // != on two bools is xor: IsNot inverts the answer, and a rule that cannot be parsed at all
    // matches nothing — so "everything EXCEPT <unparseable>" claims everything, which is what the
    // uninverted rule claiming nothing has to mean.
    public bool IsMatch(RouteTarget target) => _predicate.IsMatch(target) != Source.IsNot;

    public override string ToString() => Source.ToString();
}
