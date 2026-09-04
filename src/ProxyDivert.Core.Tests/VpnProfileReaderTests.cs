using System;
using System.IO;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The URL box now accepts two quite different things, and getting the wrong one means either a
// tunnel that will not dial or — worse — a UDP flow going out unproxied because the tool thought
// the outbound could carry it. Both halves are pure logic, so both are pinned here.
public sealed class VpnProfileReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "pd-vpn-" + Guid.NewGuid().ToString("N"));

    public VpnProfileReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private static Outbound Vpn(string? url, VpnProtocol protocol = VpnProtocol.Auto) => new Outbound
    {
        Id = Guid.NewGuid(),
        Name = "vpn",
        Kind = OutboundKind.Vpn,
        Url = url,
        VpnProtocol = protocol,
    };

    private string Write(string name, string content)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    // --- the address form ------------------------------------------------------------------------

    [Fact]
    public void An_sstp_url_is_read_as_a_server_with_the_outbounds_own_credentials()
    {
        Outbound outbound = Vpn("sstp://vpn.example.com:8443");
        outbound.Username = "user";
        outbound.Password = "secret";

        VpnProfile profile = VpnProfileReader.Read(outbound);

        Assert.Equal(VpnProtocol.Sstp, profile.Protocol);
        Assert.Equal("vpn.example.com", profile.Host);
        Assert.Equal(8443, profile.Port);
        Assert.Equal("user", profile.Username);
        Assert.Equal("secret", profile.Password);
        Assert.Null(profile.ConfigPath);
    }

    [Fact]
    public void A_url_without_a_port_gets_the_usual_tls_port()
    {
        VpnProfile profile = VpnProfileReader.Read(Vpn("sstp://vpn.example.com"));

        Assert.Equal(443, profile.Port);
    }

    [Fact]
    public void Softether_takes_its_virtual_hub_from_the_path()
    {
        VpnProfile profile = VpnProfileReader.Read(Vpn("softether://vpn.example.com:443/VPNGATE"));

        Assert.Equal(VpnProtocol.SoftEther, profile.Protocol);
        Assert.Equal("VPNGATE", profile.Hub);
    }

    [Fact]
    public void Softether_without_a_hub_is_refused_rather_than_dialled_and_rejected()
    {
        Exception error = Record.Exception(() => VpnProfileReader.Read(Vpn("softether://vpn.example.com")));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("hub", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_pre_shared_key_reaches_the_profile_for_l2tp()
    {
        Outbound outbound = Vpn("l2tp://vpn.example.com");
        outbound.Username = "user";
        outbound.Password = "secret";
        outbound.PreSharedKey = "group-key";

        VpnProfile profile = VpnProfileReader.Read(outbound);

        Assert.Equal(VpnProtocol.L2tpIpsec, profile.Protocol);
        Assert.Equal("group-key", profile.PreSharedKey);
    }

    [Fact]
    public void Choosing_a_dialled_protocol_by_hand_makes_a_bare_host_port_readable()
    {
        // What happens when someone pastes what a VPN list gave them and picks the protocol.
        VpnProfile profile = VpnProfileReader.Read(Vpn("219.100.37.1:443", VpnProtocol.Sstp));

        Assert.Equal(VpnProtocol.Sstp, profile.Protocol);
        Assert.Equal("219.100.37.1", profile.Host);
        Assert.Equal(443, profile.Port);
    }

    [Fact]
    public void An_unknown_scheme_says_so_instead_of_guessing()
    {
        Exception error = Record.Exception(() => VpnProfileReader.Read(Vpn("pptp://vpn.example.com")));

        Assert.IsType<FormatException>(error);
    }

    // --- the file form ---------------------------------------------------------------------------

    [Fact]
    public void A_wireguard_conf_still_goes_to_wireproxy_by_default()
    {
        // The whole point of the default: a configuration made before any of this existed keeps
        // running on exactly the engine it always ran on.
        string path = Write("wg0.conf", "[Interface]\nPrivateKey = x\n[Peer]\nEndpoint = 1.2.3.4:51820\n");

        VpnProfile profile = VpnProfileReader.Read(Vpn(path));

        Assert.Equal(VpnProtocol.WireGuardWireProxy, profile.Protocol);
        Assert.True(profile.RunsOnWireProxy);
        Assert.Equal(path, profile.ConfigPath);
    }

    [Fact]
    public void The_same_conf_runs_in_process_when_the_user_asks_for_it()
    {
        string path = Write("wg0.conf", "[Interface]\nPrivateKey = x\n[Peer]\nEndpoint = 1.2.3.4:51820\n");

        VpnProfile profile = VpnProfileReader.Read(Vpn(path, VpnProtocol.WireGuard));

        Assert.Equal(VpnProtocol.WireGuard, profile.Protocol);
        Assert.False(profile.RunsOnWireProxy);
    }

    [Fact]
    public void An_ovpn_file_is_recognised_by_its_extension()
    {
        string path = Write("jp.ovpn", "client\nremote 1.2.3.4 1194\n");

        VpnProfile profile = VpnProfileReader.Read(Vpn(path));

        Assert.Equal(VpnProtocol.OpenVpn, profile.Protocol);
        Assert.Equal(path, profile.ConfigPath);
    }

    [Fact]
    public void A_missing_file_is_reported_as_a_missing_file()
    {
        Exception error = Record.Exception(
            () => VpnProfileReader.Read(Vpn(Path.Combine(_folder, "nothing-here.conf"))));

        Assert.IsType<FileNotFoundException>(error);
    }

    [Fact]
    public void A_dialled_protocol_pointed_at_a_file_says_what_the_box_should_hold()
    {
        string path = Write("jp.ovpn", "client\nremote 1.2.3.4 1194\n");

        Exception error = Record.Exception(() => VpnProfileReader.Read(Vpn(path, VpnProtocol.Sstp)));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("sstp://", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- the .vpn ini ----------------------------------------------------------------------------

    [Fact]
    public void A_vpn_file_supplies_the_server_and_the_secrets()
    {
        string path = Write("office.vpn",
            "[Vpn]\nProtocol = l2tp\nHost = vpn.example.com\nUser = nam\nPass = pw\nPsk = group\n");

        VpnProfile profile = VpnProfileReader.Read(Vpn(path));

        Assert.Equal(VpnProtocol.L2tpIpsec, profile.Protocol);
        Assert.Equal("vpn.example.com", profile.Host);
        Assert.Equal("nam", profile.Username);
        Assert.Equal("pw", profile.Password);
        Assert.Equal("group", profile.PreSharedKey);
    }

    [Fact]
    public void A_secret_in_the_outbounds_own_box_wins_over_one_written_in_the_file()
    {
        // The box is encrypted at rest and the file is not, so the box has to be the one that counts
        // — otherwise moving a secret into DPAPI would silently have no effect.
        string path = Write("office.vpn",
            "[Vpn]\nProtocol = l2tp\nHost = vpn.example.com\nUser = nam\nPass = from-file\nPsk = from-file\n");
        Outbound outbound = Vpn(path);
        outbound.Password = "from-the-box";
        outbound.PreSharedKey = "psk-from-the-box";

        VpnProfile profile = VpnProfileReader.Read(outbound);

        Assert.Equal("from-the-box", profile.Password);
        Assert.Equal("psk-from-the-box", profile.PreSharedKey);
    }

    [Fact]
    public void A_vpn_file_may_point_at_an_ovpn_next_to_it()
    {
        Write("jp.ovpn", "client\nremote 1.2.3.4 1194\n");
        string path = Write("jp.vpn", "[Vpn]\nProtocol = openvpn\nConfig = jp.ovpn\n");

        VpnProfile profile = VpnProfileReader.Read(Vpn(path));

        Assert.Equal(VpnProtocol.OpenVpn, profile.Protocol);
        Assert.Equal(Path.Combine(_folder, "jp.ovpn"), profile.ConfigPath);
    }

    [Fact]
    public void A_vpn_file_cannot_ask_for_wireproxy()
    {
        // RunsOnWireProxy answers from the URL alone, so a claim buried in a file it never reads
        // would make the router believe the wrong thing about UDP.
        string path = Write("bad.vpn", "[Vpn]\nProtocol = wireproxy\nHost = vpn.example.com\n");

        Exception error = Record.Exception(() => VpnProfileReader.Read(Vpn(path)));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("wireproxy", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- what the router asks on every connection ------------------------------------------------

    [Theory]
    [InlineData(@"D:\vpn\wg0.conf", VpnProtocol.Auto, true)]
    [InlineData(@"D:\vpn\WG0.CONF", VpnProtocol.Auto, true)]
    [InlineData(@"D:\vpn\jp.ovpn", VpnProtocol.Auto, false)]
    [InlineData(@"D:\vpn\office.vpn", VpnProtocol.Auto, false)]
    [InlineData("sstp://vpn.example.com", VpnProtocol.Auto, false)]
    [InlineData(@"D:\vpn\wg0.conf", VpnProtocol.WireGuard, false)]
    [InlineData(@"D:\vpn\jp.ovpn", VpnProtocol.WireGuardWireProxy, true)]
    [InlineData(null, VpnProtocol.Auto, false)]
    public void Which_engine_runs_it_is_decided_without_reading_the_file(
        string? url, VpnProtocol protocol, bool expected)
    {
        // None of these paths exist: that is the test. It runs once per connection, so it must not
        // touch the disk.
        Assert.Equal(expected, VpnProfileReader.RunsOnWireProxy(protocol, url));
    }

    [Fact]
    public void A_wireproxy_vpn_does_not_advertise_udp_but_an_in_process_one_does()
    {
        Outbound onWireProxy = Vpn(@"D:\vpn\wg0.conf");
        Outbound inProcess = Vpn("sstp://vpn.example.com");

        Assert.False(onWireProxy.SupportsUdp);
        Assert.True(inProcess.SupportsUdp);
    }
}
