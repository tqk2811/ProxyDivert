using System;
using ProxyDivert.Core.Vpn.Enums;

namespace ProxyDivert.Core.Vpn.Models;

/// <summary>
/// Everything needed to dial one VPN, after the outbound's URL, its configuration file and the
/// credential boxes have been read together. Built by <see cref="VpnProfileReader"/>.
/// </summary>
/// <remarks>
/// The six protocols do not take the same arguments — two are handed a file, four are handed a
/// server and credentials — so this is the union of both, with the reader deciding which half is
/// filled in. Nothing here is validated beyond "present"; a wrong password is the server's answer
/// to give, not ours.
/// </remarks>
public sealed class VpnProfile
{
    public required VpnProtocol Protocol { get; init; }

    /// <summary>The VPN server, for the protocols dialled by address. Null for the file-based ones.</summary>
    public string? Host { get; init; }

    /// <summary>Only meaningful where the protocol has a port to choose: SSTP and SoftEther.</summary>
    public int Port { get; init; }

    /// <summary>The SoftEther virtual hub to join. Null for everything else.</summary>
    public string? Hub { get; init; }

    /// <summary>The .ovpn or .conf file, for the protocols configured by file. Null otherwise.</summary>
    public string? ConfigPath { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>The IPsec group pre-shared key: L2TP/IPsec and IKEv2 only.</summary>
    public string? PreSharedKey { get; init; }

    /// <summary>
    /// The genuine SoftEther watermark blob. Without it a real SoftEther server answers HTTP 403 —
    /// the blob is GPL data that cannot be shipped, so it is a file the user supplies. Only settable
    /// from a .vpn file, because it is the only shape with room for it.
    /// </summary>
    public string? SoftEtherWatermarkPath { get; init; }

    /// <summary>
    /// True when the tunnel is run by the external wireproxy binary rather than in this process.
    /// The two differ in what they can carry, so the router has to know which it is.
    /// </summary>
    public bool RunsOnWireProxy => Protocol == VpnProtocol.WireGuardWireProxy;

    /// <summary>
    /// The same profile pointed at a watermark blob. Used where the outbound named none of its own
    /// and one was found on the machine — the .vpn file's own <c>Watermark =</c> line is read first
    /// and is not overridden here, because a path someone typed beats one that was merely found.
    /// </summary>
    public VpnProfile WithSoftEtherWatermark(string? path)
        => string.IsNullOrWhiteSpace(SoftEtherWatermarkPath) && !string.IsNullOrWhiteSpace(path)
            ? new VpnProfile
            {
                Protocol = Protocol,
                Host = Host,
                Port = Port,
                Hub = Hub,
                ConfigPath = ConfigPath,
                Username = Username,
                Password = Password,
                PreSharedKey = PreSharedKey,
                SoftEtherWatermarkPath = path,
            }
            : this;

    public override string ToString()
        => ConfigPath is not null
            ? $"{Protocol} ({ConfigPath})"
            : $"{Protocol} ({Host}:{Port})";
}
