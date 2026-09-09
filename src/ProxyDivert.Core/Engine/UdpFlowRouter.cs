using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using ProxyDivert.Core.Engine.Interfaces;
using ProxyDivert.Core.Outbounds;
using ProxyDivert.Core.Routing.Models;
using TqkLibrary.Proxy.Interfaces;
using TqkLibrary.WinDivert.Redirect.Models;
using TqkLibrary.WinDivert.SecureDns.Interfaces;

namespace ProxyDivert.Core.Engine;

/// <summary>
/// The two questions UDP asks: whether a flow should be pulled into the relay at all, and where
/// each datagram of the ones that are goes.
/// </summary>
/// <remarks>
/// They belong together because they must give the same answer. The first is asked on the PACKET
/// path, once per flow, before anything has been claimed; the second on the relay, per datagram.
/// A flow the first says Direct never reaches the second — and that is not a shortcut but the whole
/// point, written out at <see cref="ShouldRedirect"/>.
/// </remarks>
internal sealed class UdpFlowRouter
{
    private readonly IResolverSource _resolvers;
    private readonly IReverseDnsTable _reverseDns;
    private readonly UdpProxyForwarder _forwarder;
    private readonly OutboundRegistry _outbounds;
    private readonly OutboundIpv6Capability _ipv6Capability;
    private readonly ILogger _logger;

    public UdpFlowRouter(
        IResolverSource resolvers,
        IReverseDnsTable reverseDns,
        UdpProxyForwarder forwarder,
        OutboundRegistry outbounds,
        OutboundIpv6Capability ipv6Capability,
        ILogger<UdpFlowRouter> logger)
    {
        _resolvers = resolvers ?? throw new ArgumentNullException(nameof(resolvers));
        _reverseDns = reverseDns ?? throw new ArgumentNullException(nameof(reverseDns));
        _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
        _outbounds = outbounds ?? throw new ArgumentNullException(nameof(outbounds));
        _ipv6Capability = ipv6Capability ?? throw new ArgumentNullException(nameof(ipv6Capability));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Asked on the packet path, before a UDP flow is redirected at all.
    //
    // A datagram routed Direct must never reach the relay: the relay forwards from its own socket,
    // on a port nothing can map back to the process, so the query leaves and the answer is lost.
    // That is what broke DNS — a browser with its own resolver got no answers at all. Leaving the
    // flow untouched is the only thing that actually delivers "direct": the datagram goes out of
    // the process's own socket and the reply comes straight back to it.
    //
    // The cost is stated plainly: a passed-through flow carries the machine's real address, which
    // for DNS is the same exposure SystemSniff already accepts by definition. A user who wants
    // their DNS tunnelled says so with a rule — a Protocol "udp" rule, or a Port "53" one — and
    // the flow then resolves to an outbound instead of Direct and comes back here as "redirect".
    //
    // Block still goes through the relay: it is claimed by NAT and dropped there, so nothing about
    // it reaches the wire. Passing a blocked datagram would leak the very thing it must not.
    public bool ShouldRedirect(uint processId, IPAddress destination, ushort destinationPort, bool isIpv6)
    {
        string? host = _reverseDns.Resolve(destination);
        var target = new RouteTarget(processId, destination, destinationPort, host, isUdp: true);

        RouteDecision decision = _resolvers.Resolver.ResolveUdp(target);
        if (!decision.IsDirect) return true;

        _logger.LogDebug("udp pid={Pid} -> {Target} left direct, unredirected ({Reason})",
            processId, target, decision.Reason);
        return false;
    }

    // Returning the payload lets the relay send it out directly; returning null means "handled or
    // dropped — do not send". Anything that cannot be tunnelled is dropped rather than leaked.
    public byte[]? HandleDatagram(RedirectedUdpDatagram datagram, CancellationToken ct)
    {
        if (datagram is null) throw new ArgumentNullException(nameof(datagram));

        try
        {
            string? host = _reverseDns.Resolve(datagram.OriginalDestination.Address);
            var target = new RouteTarget(
                datagram.ProcessId,
                datagram.OriginalDestination.Address,
                datagram.OriginalDestination.Port,
                host,
                isUdp: true);

            RouteDecision decision = _resolvers.Resolver.ResolveUdp(target);

            if (decision.IsBlocked) return null;

            if (decision.IsDirect)
            {
                // Normally unreachable: ShouldRedirect keeps a Direct flow away from the relay
                // entirely. It is still reached when the answer changed between the packet path and
                // here — a DNS answer landing in between gives the flow a name it did not have, and
                // a rule that then claims it. Forwarding from the relay's socket is all that is left
                // at this point, and its reply has nowhere to go, so the sender sees one lost
                // datagram and retries.
                _logger.LogDebug(
                    "udp pid={Pid} -> {Destination} resolved Direct after it was already redirected; "
                    + "forwarding without a reply path, the sender will retry",
                    datagram.ProcessId, datagram.OriginalDestination);
                return datagram.Payload;
            }

            bool isIpv6 = datagram.OriginalDestination.AddressFamily == AddressFamily.InterNetworkV6;
            // A UDP datagram carries no name to fall back on, so an outbound without an IPv6 route
            // has nothing to send it over. Dropping is the safe answer: letting it out direct would
            // expose the real address.
            if (isIpv6 && !_ipv6Capability.AllowsIpv6(decision.Outbound))
            {
                _logger.LogDebug(
                    "udp pid={Pid} -> {Destination} dropped: {Outbound} has no IPv6 route",
                    datagram.ProcessId, datagram.OriginalDestination, decision.Outbound.Name);
                return null;
            }

            IProxySource source = _outbounds.GetOrCreate(decision.Outbound).Source;
            bool queued = _forwarder.Send(
                decision.Outbound.Id, source,
                (ushort)datagram.OriginalSource.Port,
                datagram.OriginalDestination,
                datagram.Payload,
                isIpv6);
            if (!queued)
                _logger.LogDebug("udp pid={Pid} -> {Destination} dropped, the tunnel is not ready yet", datagram.ProcessId, datagram.OriginalDestination);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "udp pid={Pid} routing failed, dropping the datagram", datagram.ProcessId);
            return null;
        }
    }
}
