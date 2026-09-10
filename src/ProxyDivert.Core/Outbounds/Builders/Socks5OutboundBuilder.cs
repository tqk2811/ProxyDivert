using System;
using System.Net;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Authentications;
using TqkLibrary.Proxy.ProxySources;

namespace ProxyDivert.Core.Outbounds.Builders;

public sealed class Socks5OutboundBuilder : IOutboundSourceBuilder
{
    public OutboundKind Kind => OutboundKind.Socks5;

    // A tunnel per connection, and nothing held open between them.
    public bool BuildsManagedSource => false;

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
    {
        Uri uri = OutboundUrl.Parse(outbound);
        IPEndPoint endPoint = OutboundUrl.ResolveEndPoint(uri);

        Socks5ProxySource source = OutboundUrl.HasCredential(outbound)
            ? new Socks5ProxySource(endPoint, new ProxyCredential(outbound.Username!, outbound.Password!), context.LoggerFactory)
            : new Socks5ProxySource(endPoint, context.LoggerFactory);

        // No IPv6 switch handed over: the destination goes to the upstream as a name and the
        // upstream resolves it. See HttpProxyOutboundBuilder for the same reasoning at length.
        return new OutboundInstance(outbound.Id, context.Signature, source);
    }
}
