using System;
using System.Net;
using ProxyDivert.Core.Outbounds.Models;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Outbounds.Builders;

/// <summary>
/// Reading the address of a proxy outbound. Shared by the three proxy builders, which is the only
/// reason it is not a private method on one of them.
/// </summary>
internal static class OutboundUrl
{
    public static bool HasCredential(Outbound outbound)
        => !string.IsNullOrEmpty(outbound.Username) && !string.IsNullOrEmpty(outbound.Password);

    /// <summary>
    /// The proxy's address, already read. The scheme a bare "host:port" is missing comes from the
    /// outbound's own kind, so there is nothing left for the three builders to agree on.
    /// </summary>
    public static Uri Parse(Outbound outbound)
    {
        OutboundAddress? address = outbound.Address;
        if (address is null)
            throw OutboundAddress.Unreadable(
                $"Outbound '{outbound.Name}'", outbound.Url, outbound.AddressProblem);

        return address.Uri!;
    }

    // SOCKS sources take an endpoint rather than a URI, so a host name has to be resolved here.
    // This lookup uses the machine's own DNS: it resolves the PROXY's address, not the traffic's
    // destination, so it reveals nothing about what the user is browsing.
    public static IPEndPoint ResolveEndPoint(Uri uri)
    {
        if (uri.Port <= 0)
            throw new FormatException($"Proxy URL must include a port: {uri}");

        // Uri.Host keeps the brackets of an IPv6 literal ("[::1]"), which IPAddress.TryParse
        // rejects — without trimming them an IPv6 proxy address would be sent to the resolver as
        // if it were a host name.
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? ip))
            return new IPEndPoint(ip, uri.Port);

        IPAddress[] addresses = Dns.GetHostAddresses(uri.Host);
        if (addresses.Length == 0)
            throw new InvalidOperationException($"Cannot resolve proxy host '{uri.Host}'.");
        return new IPEndPoint(addresses[0], uri.Port);
    }
}
