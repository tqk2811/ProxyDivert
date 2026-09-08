using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.ProxySources;

namespace ProxyDivert.Core.Outbounds.Builders;

/// <summary>
/// Out through the machine's own stack, which is what "no proxy" means here.
/// </summary>
public sealed class DirectOutboundBuilder : IOutboundSourceBuilder
{
    public OutboundKind Kind => OutboundKind.Direct;

    // A tunnel per connection, and nothing held open between them.
    public bool BuildsManagedSource => false;

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
    {
        var source = new LocalProxySource();
        return new OutboundInstance(
            outbound.Id, context.Signature, source,
            setIpv6Support: supported => source.AllowIpv6 = supported);
    }
}
