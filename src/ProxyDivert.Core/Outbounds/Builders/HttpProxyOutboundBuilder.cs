using System;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Authentications;
using TqkLibrary.Proxy.ProxySources;

namespace ProxyDivert.Core.Outbounds.Builders;

public sealed class HttpProxyOutboundBuilder : IOutboundSourceBuilder
{
    public OutboundKind Kind => OutboundKind.HttpProxy;

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
    {
        Uri uri = OutboundUrl.Parse(outbound, "http");
        var source = new HttpProxySource(uri, context.LoggerFactory);
        if (OutboundUrl.HasCredential(outbound))
            source.Credential = new ProxyCredential(outbound.Username!, outbound.Password!);

        return new OutboundInstance(
            outbound.Id, context.Signature, source,
            setIpv6Support: supported => source.IsSupportIpv6 = supported);
    }
}
