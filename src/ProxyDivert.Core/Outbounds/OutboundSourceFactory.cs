using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Outbounds.Builders;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Core.Outbounds;

// Builds the instance of an outbound, and nothing else: what comes back belongs to the caller from
// the moment it returns.
//
// It used to keep the instances as well, which is how three different objects came to be able to
// dispose one behind each other's backs. Owning them is OutboundRegistry's job now, and this class
// is stateless — so the "test this outbound" button can build a throwaway instance without any risk
// of disturbing the one live traffic is using.
//
// What each kind actually builds is not here either: one IOutboundSourceBuilder per OutboundKind
// answers that, including Block, whose answer is "nothing - close the connection instead".
public sealed class OutboundSourceFactory
{
    private readonly Dictionary<OutboundKind, IOutboundSourceBuilder> _builders;

    public OutboundSourceFactory(IEnumerable<IOutboundSourceBuilder> builders)
    {
        if (builders is null) throw new ArgumentNullException(nameof(builders));

        _builders = new Dictionary<OutboundKind, IOutboundSourceBuilder>();
        foreach (IOutboundSourceBuilder builder in builders)
        {
            // Two builders claiming one kind is a wiring mistake, and the one that would win is
            // whichever the container happened to enumerate first — so say so here instead.
            if (_builders.TryGetValue(builder.Kind, out IOutboundSourceBuilder? existing))
            {
                throw new InvalidOperationException(
                    $"Two builders claim {builder.Kind}: {existing.GetType().Name} and {builder.GetType().Name}.");
            }
            _builders.Add(builder.Kind, builder);
        }
    }

    /// <summary>
    /// A factory with every kind the application knows how to build, for a caller outside the
    /// container.
    /// </summary>
    public static OutboundSourceFactory CreateDefault() => new OutboundSourceFactory(DefaultBuilders());

    public static IEnumerable<IOutboundSourceBuilder> DefaultBuilders()
    {
        yield return new DirectOutboundBuilder();
        yield return new BlockOutboundBuilder();
        yield return new HttpProxyOutboundBuilder();
        yield return new Socks4OutboundBuilder();
        yield return new Socks5OutboundBuilder();
        yield return new VpnOutboundBuilder();
        yield return new SshOutboundBuilder();
    }

    /// <summary>
    /// Whether this outbound would build something a supervisor can hold open, answered without
    /// building it — asking a VPN would mean dialling it.
    /// </summary>
    /// <remarks>
    /// A kind no builder claims cannot be kept connected either, and there is no reason for the
    /// question to throw: the caller is looping over a whole configuration, and one outbound saved
    /// by a newer version of the application should be skipped rather than stop the loop.
    /// </remarks>
    public bool BuildsManagedSource(Outbound outbound)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        return _builders.TryGetValue(outbound.Kind, out IOutboundSourceBuilder? builder)
            && builder.BuildsManagedSource;
    }

    /// <summary>
    /// Builds an instance, stamped with what it was built from. Throws with a message the user can
    /// act on when the outbound cannot produce one at all.
    /// </summary>
    public IOutboundInstance Create(Outbound outbound, ILoggerFactory? loggerFactory, string? wireProxyPath)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));

        return Create(outbound, new OutboundBuildContext
        {
            Signature = OutboundSignature.Of(outbound, wireProxyPath),
            LoggerFactory = loggerFactory,
            WireProxyPath = wireProxyPath,
        });
    }

    public IOutboundInstance Create(Outbound outbound, OutboundBuildContext context)
    {
        if (outbound is null) throw new ArgumentNullException(nameof(outbound));
        if (context is null) throw new ArgumentNullException(nameof(context));

        IOutboundInstance instance = BuilderFor(outbound).Build(outbound, context);
        // Ipv6Support.Disabled is a statement about this way out, so it belongs on the instance
        // itself: LocalProxySource then filters name lookups down to A records instead of handing
        // the connection an AAAA it cannot use.
        if (outbound.Ipv6Support == Ipv6Support.Disabled) instance.SetIpv6Support(false);
        return instance;
    }

    private IOutboundSourceBuilder BuilderFor(Outbound outbound)
        => _builders.TryGetValue(outbound.Kind, out IOutboundSourceBuilder? builder)
            ? builder
            : throw new ArgumentOutOfRangeException(nameof(outbound), outbound.Kind, "Unknown outbound kind");
}
