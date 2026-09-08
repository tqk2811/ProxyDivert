using System;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Authentications;
using TqkLibrary.Proxy.ProxySources;

namespace ProxyDivert.Core.Outbounds.Builders;

public sealed class HttpProxyOutboundBuilder : IOutboundSourceBuilder
{
    public OutboundKind Kind => OutboundKind.HttpProxy;

    // A tunnel per connection, and nothing held open between them.
    public bool BuildsManagedSource => false;

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
    {
        Uri uri = OutboundUrl.Parse(outbound, "http");
        var source = new HttpProxySource(uri, context.LoggerFactory);
        if (OutboundUrl.HasCredential(outbound))
            source.Credential = new ProxyCredential(outbound.Username!, outbound.Password!);

        // No IPv6 switch handed over. CONNECT gives the upstream a name and the upstream resolves
        // it, so nothing on this side can keep AAAA records out of that answer — the property that
        // used to be set here existed but was read by nobody, which made the switch look enforced
        // when it never was. What Ipv6Support=Disabled does reach is OutboundIpv6Capability in the
        // engine, which refuses an IPv6 literal outright.
        return new OutboundInstance(outbound.Id, context.Signature, source);
    }
}
