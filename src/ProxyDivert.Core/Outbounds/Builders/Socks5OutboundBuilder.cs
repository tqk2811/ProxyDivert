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
        Uri uri = OutboundUrl.Parse(outbound, "socks5");
        IPEndPoint endPoint = OutboundUrl.ResolveEndPoint(uri);

        Socks5ProxySource source = OutboundUrl.HasCredential(outbound)
            ? new Socks5ProxySource(endPoint, new ProxyCredential(outbound.Username!, outbound.Password!), context.LoggerFactory)
            : new Socks5ProxySource(endPoint, context.LoggerFactory);

        return new OutboundInstance(
            outbound.Id, context.Signature, source,
            setIpv6Support: supported => source.IsSupportIpv6 = supported);
    }
}
