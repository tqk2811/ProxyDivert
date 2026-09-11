using System;
using System.IO;
using ProxyDivert.Core.Outbounds.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Ssh;
using TqkLibrary.Proxy.SshNet;

namespace ProxyDivert.Core.Outbounds.Builders;

/// <summary>
/// Out through an SSH server: one authenticated session, each connection a direct-tcpip channel on
/// it. The server resolves the destination's name, so the name never touches this machine's DNS.
/// </summary>
/// <remarks>
/// Built by SSH.NET in this process — no ssh.exe to spawn, keep alive or exclude from redirection.
/// The session is the expensive part and is held between connections, which makes it a managed
/// source the supervisor can bring up ahead of the first request and watch, exactly like a VPN.
/// </remarks>
public sealed class SshOutboundBuilder : IOutboundSourceBuilder
{
    // Asked once per session handshake, never per connection, so a small file behind a lock is fine.
    private readonly SshKnownHostsStore _knownHosts;

    public SshOutboundBuilder(SshKnownHostsStore? knownHosts = null)
    {
        _knownHosts = knownHosts ?? new SshKnownHostsStore();
    }

    public OutboundKind Kind => OutboundKind.Ssh;

    // A session held open between requests, costing a handshake and a login to establish.
    public bool BuildsManagedSource => true;

    public IOutboundInstance Build(Outbound outbound, OutboundBuildContext context)
    {
        OutboundAddress? address = outbound.Address;
        if (address is null)
            throw OutboundAddress.Unreadable($"Outbound '{outbound.Name}'", outbound.Url, outbound.AddressProblem);

        string user = UserOf(outbound, address);
        string? keyPath = string.IsNullOrWhiteSpace(outbound.PrivateKeyPath)
            ? null
            : OutboundAddress.Expand(outbound.PrivateKeyPath);
        if (keyPath != null && !File.Exists(keyPath))
            throw new FileNotFoundException($"SSH outbound '{outbound.Name}': private key file not found.", keyPath);

        string? password = string.IsNullOrEmpty(outbound.Password) ? null : outbound.Password;
        if (keyPath is null && password is null)
        {
            throw new InvalidOperationException(
                $"SSH outbound '{outbound.Name}' has neither a password nor a private key to log in with.");
        }

        var options = new SshNetConnectionOptions(address.Host!, user, address.Port)
        {
            // With a key the password box is the key's passphrase. It is still offered as a login
            // password too: SSH.NET tries the methods in turn, and a server that only takes passwords
            // is better served by that than by a failure that says the key was refused.
            Password = password,
            IdentityFile = keyPath,
            IdentityFilePassphrase = keyPath is null ? null : password,
            HostKeyVerifier = _knownHosts,
        };

        // No IPv6 switch to hand over: the destination travels to the server as a name and is
        // resolved there. What Ipv6Support=Disabled does reach is OutboundIpv6Capability in the
        // engine, which refuses an IPv6 literal before it gets here.
        return new OutboundInstance(
            outbound.Id, context.Signature, new SshNetProxySource(options, context.LoggerFactory));
    }

    // The user box wins; "user@host" in the address is the fallback, because that is how people
    // write a server and typing the name twice would be the application's fault.
    private static string UserOf(Outbound outbound, OutboundAddress address)
    {
        if (!string.IsNullOrWhiteSpace(outbound.Username)) return outbound.Username!.Trim();

        string userInfo = Uri.UnescapeDataString(address.Uri?.UserInfo ?? string.Empty);
        int colon = userInfo.IndexOf(':');
        string user = colon >= 0 ? userInfo.Substring(0, colon) : userInfo;
        if (user.Length > 0) return user;

        throw new InvalidOperationException(
            $"SSH outbound '{outbound.Name}' has no user name. Fill in the user box, or write the address as user@host.");
    }
}
