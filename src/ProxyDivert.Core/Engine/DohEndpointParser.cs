using System;
using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// The one definition of a usable DoH endpoint — an absolute http or https URL — shared by the
/// engine, the resolver pool and the routing table, so they cannot disagree about which policy
/// endpoints count.
/// </summary>
public static class DohEndpointParser
{
    /// <summary>True when <paramref name="text"/> (trimmed) is an absolute http(s) URL.</summary>
    public static bool TryParse(string? text, out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(text)
            && Uri.TryCreate(text!.Trim(), UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    /// <summary>
    /// A stable text for "which DoH servers would the secure-DNS policies talk to": the global
    /// endpoint, then the sorted distinct effective endpoints of the policies that use secure DNS
    /// (empty or unusable falls back to the global one). Two configurations with different text need
    /// different resolver pools.
    /// </summary>
    public static string Signature(Uri globalEndpoint, IEnumerable<RoutingPolicy> policies)
    {
        if (globalEndpoint is null) throw new ArgumentNullException(nameof(globalEndpoint));
        if (policies is null) throw new ArgumentNullException(nameof(policies));
        IEnumerable<string> used = policies
            .Where(x => x.SecureDnsProcess || x.SecureDnsSystem)
            .Select(x => TryParse(x.DohEndpoint, out Uri uri) ? uri.AbsoluteUri : globalEndpoint.AbsoluteUri)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
        return globalEndpoint.AbsoluteUri + "\n" + string.Join("\n", used);
    }
}
