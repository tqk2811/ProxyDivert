using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn;
using ProxyDivert.Core.Vpn.Client;
using ProxyDivert.Core.Vpn.Models;
using TqkLibrary.Proxy.Vpn.WireProxyCli;

namespace ProxyDivert.Core.Outbounds.Builders;

/// <summary>
/// A VPN, run by one of two engines. Both keep the rest of the machine on its normal network: no
/// TUN adapter, no route table, no second elevation prompt.
/// </summary>
/// <remarks>
/// Which engine is decided by the outbound's URL and its protocol box, and <see cref="VpnProfileReader"/>
/// is the only thing that reads them. A WireGuard .conf still goes to wireproxy, exactly as it did
/// before any of the other protocols existed; everything else is dialled inside this process by
/// TqkLibrary.VpnClient, which also means those tunnels carry UDP.
///
/// Both engines produce an <see cref="TqkLibrary.Proxy.Interfaces.IManagedProxySource"/>, which is
/// what lets the supervisor hold either one open without knowing which it got. It used to work that
/// out by pattern-matching the concrete source and wrapping one of them in an adapter, so the
/// knowledge of how each engine is kept alive lived one layer away from the code that chose it.
/// </remarks>
public sealed class VpnOutboundBuilder : IOutboundSourceBuilder
{
    public OutboundKind Kind => OutboundKind.Vpn;

    // A subprocess or an in-process driver, either way something that is up between requests and
    // costs seconds to establish — the whole reason the supervisor exists.
    public bool BuildsManagedSource => true;

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
    {
        VpnProfile profile = VpnProfileReader.Read(outbound);
        return profile.RunsOnWireProxy
            ? BuildOnWireProxy(outbound, context, profile.ConfigPath!)
            : BuildInProcess(outbound, context, profile);
    }

    // Dialled by TqkLibrary.VpnClient in this process: it owns a whole userspace IP stack, so it is
    // its own tunnel as well as its own source.
    private static IOutboundInstance BuildInProcess(
        Outbound outbound, OutboundBuildContext context, VpnProfile profile)
    {
        var source = new VpnClientProxySource(
            profile, outbound.Ipv6Support != Ipv6Support.Disabled, context.LoggerFactory);

        return new OutboundInstance(
            outbound.Id, context.Signature, source,
            // The tunnel takes the switch too, but it only ever narrows: one that got no global
            // IPv6 stays without one however this is set.
            setIpv6Support: supported => source.IsSupportIpv6 = supported);
    }

    // The wireproxy engine: a subprocess running the WireGuard tunnel in user space and exposing it
    // as a loopback SOCKS5 listener. Two shapes of .conf are accepted:
    //   * the file a VPN provider gives you (only [Interface]/[Peer]) — it is read here and
    //     wireproxy gets a generated copy with a [Socks5] section on a private loopback port;
    //   * a file that already has a [Socks5] section — handed to wireproxy untouched.
    private static IOutboundInstance BuildOnWireProxy(
        Outbound outbound, OutboundBuildContext context, string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"VPN outbound '{outbound.Name}': config file not found.", configPath);

        var options = new WireGuardOptions
        {
            BinaryPath = string.IsNullOrWhiteSpace(context.WireProxyPath) ? null : context.WireProxyPath,
            // wireproxy's SOCKS5 is TCP-only, so UDP must not be advertised: the router downgrades
            // "UDP through this outbound" to Block rather than letting the datagrams out direct.
            IsSupportUdp = false,
            // Taken here rather than through the instance's switch: the subprocess is configured
            // once, when it is started, and cannot be told otherwise afterwards.
            IsSupportIpv6 = outbound.Ipv6Support != Ipv6Support.Disabled,
        };

        string text = File.ReadAllText(configPath);
        IPEndPoint? existingSocks5 = WireGuardConfigParser.ParseSocks5BindAddress(text);
        if (existingSocks5 != null)
        {
            options.ConfigFilePath = configPath;
            options.ExternalSocks5Endpoint = existingSocks5;
        }
        else
        {
            options.Config = WireGuardConfigParser.Parse(text);
            // A loopback SOCKS5 listener with no credentials is usable by every process on the
            // machine — including the ones the user is deliberately keeping OUT of the tunnel.
            // A random per-instance credential closes that without asking the user for anything.
            options.Socks5Username = "pd";
            options.Socks5Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        }

        // No IPv6 switch handed over: everything above went into the subprocess's configuration,
        // and wireproxy is told once, when it starts.
        return new OutboundInstance(
            outbound.Id, context.Signature, new WireGuardProxySource(options, context.LoggerFactory));
    }
}
