using System;
using System.Collections.Generic;
using System.IO;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;
using ProxyDivert.Wpf.ViewModels;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The grid used to bind straight at the model the engine would later be handed a copy of. There
// was nowhere between the cell and the model to stand: nowhere to say an address cannot be read,
// and nowhere to refuse an edit to Direct or Block. This is that place.
public class OutboundRowViewModelTests
{
    private static OutboundRowViewModel Proxy(string url = "socks5://127.0.0.1:1080")
        => new OutboundRowViewModel(new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "work",
            Kind = OutboundKind.Socks5,
            Url = url,
        });

    private static List<string> Watch(OutboundRowViewModel row)
    {
        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        return changed;
    }

    // ==== an edit reaches the configuration ====

    [Fact]
    public void EditingACell_ReachesTheOutboundTheEngineWillBeGivenACopyOf()
    {
        OutboundRowViewModel row = Proxy();

        row.Url = "socks5://10.0.0.1:9050";
        row.Username = "u";

        Assert.Equal("socks5://10.0.0.1:9050", row.Model.Url);
        Assert.Equal("u", row.Model.Username);
    }

    [Fact]
    public void ANameLeftBlank_IsDroppedRatherThanStored()
    {
        OutboundRowViewModel row = Proxy();

        row.Name = "   ";

        Assert.Equal("work", row.Model.Name);
    }

    // ==== Direct and Block ====

    [Fact]
    public void TheTwoBuiltIns_KeepTheirSettingsWhateverIsTypedIntoThem()
    {
        var row = new OutboundRowViewModel(Outbound.CreateDirect());

        row.Url = "http://127.0.0.1:8080";
        row.Kind = OutboundKind.HttpProxy;
        row.IsEnabled = false;
        row.Name = "something else";

        Assert.Null(row.Model.Url);
        Assert.Equal(OutboundKind.Direct, row.Model.Kind);
        Assert.True(row.Model.IsEnabled);
        Assert.Equal("Direct", row.Model.Name);
    }

    // A control that has already taken the keystroke goes on showing what was typed unless it is
    // told to read the property again — so a refused edit still raises the change.
    [Fact]
    public void ARefusedEdit_StillTellsTheCellToReadTheValueAgain()
    {
        var row = new OutboundRowViewModel(Outbound.CreateDirect());
        List<string> changed = Watch(row);

        row.Url = "http://127.0.0.1:8080";

        Assert.Contains(nameof(row.Url), changed);
    }

    [Fact]
    public void TheTwoBuiltIns_TakeNoAddressAndSoHaveNothingToComplainAbout()
    {
        var row = new OutboundRowViewModel(Outbound.CreateBlock());

        Assert.False(row.HasAddressProblem);
        Assert.Null(row.AddressProblem);
    }

    // ==== an address that cannot be used ====

    [Fact]
    public void ASocksProxyWithNoPort_IsReportedOnTheRowRatherThanAtTheFirstConnection()
    {
        OutboundRowViewModel row = Proxy("socks5://127.0.0.1");

        Assert.True(row.HasAddressProblem);
        Assert.Contains("port", row.AddressProblem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FixingTheAddress_ClearsTheComplaintWithoutRebuildingTheRow()
    {
        OutboundRowViewModel row = Proxy("socks5://127.0.0.1");
        List<string> changed = Watch(row);

        row.Url = "socks5://127.0.0.1:1080";

        Assert.False(row.HasAddressProblem);
        Assert.Contains(nameof(row.HasAddressProblem), changed);
    }

    // The routing path must never touch the disk, so whether the file is actually there is asked
    // here — where a cell was just edited — and nowhere else.
    [Fact]
    public void AVpnPointingAtAFileThatIsNotThere_SaysSoOnTheRow()
    {
        var row = new OutboundRowViewModel(new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "office",
            Kind = OutboundKind.Vpn,
            Url = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".conf"),
        });

        Assert.True(row.HasAddressProblem);
    }

    [Fact]
    public void AVpnServerAddress_NamesNoFileAndSoIsNotLookedForOnDisk()
    {
        var row = new OutboundRowViewModel(new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "office",
            Kind = OutboundKind.Vpn,
            Url = "sstp://vpn.example.com:443",
        });

        Assert.False(row.HasAddressProblem);
    }

    // The same text means two different things depending on the protocol box beside it, so that
    // box has to make the row look at the address again.
    [Fact]
    public void PickingTheProtocolByHand_MakesTheRowReadTheAddressAgain()
    {
        var row = new OutboundRowViewModel(new Outbound
        {
            Id = Guid.NewGuid(),
            Name = "office",
            Kind = OutboundKind.Vpn,
            Url = "219.100.37.1:443",
        });
        // Read as a path while nothing says otherwise, and there is no file by that name.
        Assert.True(row.HasAddressProblem);

        row.VpnProtocol = VpnProtocol.Sstp;

        Assert.False(row.HasAddressProblem);
    }

    // ==== the tunnel ====

    [Fact]
    public void ATunnelComingUp_ChangesWhatTheRowSaysItIsOffering()
    {
        OutboundRowViewModel row = Proxy();
        List<string> changed = Watch(row);
        Assert.False(row.IsConnected);

        row.Tunnel = new VpnTunnelViewModel(
            new VpnStatus(row.Id, "office", VpnConnectionState.Connected));

        Assert.True(row.IsConnected);
        Assert.Contains(nameof(row.IsConnected), changed);
    }
}
