using System;
using System.Collections.Generic;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Routing;

/// <summary>
/// Which outbounds a configuration actually routes traffic to: filter → its policies → their
/// outbound.
/// </summary>
/// <remarks>
/// Two questions are asked of this, and they have to agree. Starting the engine brings up the VPN
/// tunnels the rules are about to need, and the Outbounds tab greys the Disconnect button of a
/// tunnel that traffic is going through — one answer, so the button cannot claim a tunnel is free
/// while the engine is holding it up for a rule.
/// </remarks>
public static class OutboundUsage
{
    /// <summary>
    /// The ids referenced by the enabled filters. A disabled filter routes nothing, and a policy
    /// no filter lists is only a draft, so neither contributes.
    /// </summary>
    public static HashSet<Guid> RoutedOutboundIds(AppConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        var policies = new Dictionary<Guid, RoutingPolicy>();
        foreach (RoutingPolicy policy in config.Policies) policies[policy.Id] = policy;

        var routed = new HashSet<Guid>();
        foreach (ProcessRule rule in config.ProcessRules)
        {
            if (!rule.IsEnabled) continue;
            foreach (Guid policyId in rule.PolicyIds)
            {
                if (policies.TryGetValue(policyId, out RoutingPolicy? policy))
                    routed.Add(policy.OutboundId);
            }
        }
        return routed;
    }
}
