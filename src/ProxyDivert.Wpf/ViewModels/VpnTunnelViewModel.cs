using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;

namespace ProxyDivert.Wpf.ViewModels;

// One row of the VPN tunnel strip on the Outbounds tab.
//
// The tunnels are held up by the engine whether or not anyone is looking, so this exists purely to
// answer "is my VPN actually up?" — a question that otherwise has no answer until a request
// through it succeeds or hangs.
public sealed partial class VpnTunnelViewModel : ObservableObject
{
    public VpnTunnelViewModel(VpnStatus status)
    {
        Id = status.OutboundId;
        Name = status.OutboundName;
        Update(status);
    }

    /// <summary>The outbound this row is about — what a later status update is matched on.</summary>
    public Guid Id { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The state, localized, plus the reason and attempt count when there is one.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    /// <summary>Drives the dot's colour: green when the tunnel is up, amber otherwise.</summary>
    [ObservableProperty]
    private bool _isUp;

    public void Update(VpnStatus status)
    {
        Name = status.OutboundName;
        IsUp = status.State == VpnConnectionState.Connected;

        string state = Localize(status.State);
        if (status.State == VpnConnectionState.Reconnecting)
        {
            string attempt = Text("Str.Vpn.Attempt");
            state = status.Error is null
                ? $"{state} ({attempt} {status.RetryCount})"
                : $"{state} ({attempt} {status.RetryCount}) — {status.Error}";
        }
        Detail = state;
    }

    private static string Localize(VpnConnectionState state) => state switch
    {
        VpnConnectionState.Connected => Text("Str.Vpn.Connected"),
        VpnConnectionState.Connecting => Text("Str.Vpn.Connecting"),
        VpnConnectionState.Reconnecting => Text("Str.Vpn.Reconnecting"),
        _ => Text("Str.Vpn.Stopped"),
    };

    // The dictionary is swapped when the language changes; a row is rebuilt on the next status
    // change, which for a connected tunnel may be a while — an acceptable trade for not making
    // every row listen to the localization manager.
    private static string Text(string key)
        => Application.Current?.Resources[key] as string ?? key;
}
