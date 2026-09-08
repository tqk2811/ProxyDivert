using System;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.ProxySources;

namespace ProxyDivert.Core.Outbounds.Builders;

public sealed class Socks4OutboundBuilder : IOutboundSourceBuilder
{
    public OutboundKind Kind => OutboundKind.Socks4;

    // A tunnel per connection, and nothing held open between them.
    public bool BuildsManagedSource => false;

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
    {
        Uri uri = OutboundUrl.Parse(outbound, "socks4");
        bool isSocks4a = uri.Scheme.Equals("socks4a", StringComparison.OrdinalIgnoreCase);

        // SOCKS4 authenticates with a user id only — there is no password in the protocol. Nor is
        // there an address type for IPv6, so no IPv6 switch is handed over: this way out cannot
        // carry it however anything is configured.
        var source = new Socks4ProxySource(OutboundUrl.ResolveEndPoint(uri), outbound.Username, context.LoggerFactory)
        {
            IsUseSocks4A = isSocks4a,
        };
        return new OutboundInstance(outbound.Id, context.Signature, source);
    }
}
