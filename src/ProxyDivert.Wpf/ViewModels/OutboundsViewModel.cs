using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Routing;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;
using ProxyDivert.Wpf.Services;

namespace ProxyDivert.Wpf.ViewModels;

// The Outbounds tab: the list of ways out of the machine.
//
// Each row is an OutboundRowViewModel over the configuration's own Outbound. Direct and Block are
// in the list — a policy has to be able to reference them — and the row is what refuses to let them
// be edited.
public sealed partial class OutboundsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<OutboundRowViewModel> Outbounds { get; }
        = new ObservableCollection<OutboundRowViewModel>();

    public Array Kinds { get; } = new[]
    {
        OutboundKind.HttpProxy, OutboundKind.Socks4, OutboundKind.Socks5, OutboundKind.Vpn,
    };

    public Array Ipv6Supports { get; } = Enum.GetValues(typeof(Ipv6Support));

    // Every value, Auto included: Auto is the answer for all but one case, and that case — running
    // a WireGuard .conf in this process instead of on wireproxy — can only be said by hand.
    public Array VpnProtocols { get; } = Enum.GetValues(typeof(VpnProtocol));

    [ObservableProperty]
    private OutboundRowViewModel? _selected;

    [ObservableProperty]
    private string? _testResult;

    [ObservableProperty]
    private bool _isTesting;

    public OutboundsViewModel(AppServices services)
    {
        _services = services;
        // The keeper supervises tunnels on its own threads and outlives this view model, so the
        // subscription is for the life of the window.
        _services.Vpn.StatusChanged += OnVpnStatusChanged;
        Reload();
    }

    public void Reload()
    {
        // Held across the refill, since a tab switch calls this and the selection is the user's
        // place in the list.
        Guid? previous = Selected?.Id;

        Outbounds.Clear();
        foreach (Outbound outbound in _services.Config.Outbounds)
            Outbounds.Add(new OutboundRowViewModel(outbound));

        Selected = previous is null ? null : Outbounds.FirstOrDefault(r => r.Id == previous);

        foreach (VpnStatus status in _services.Vpn.Statuses) ApplyVpnStatus(status);
        ToggleVpnCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Re-asks whether each Connect button may be pressed. Called when redirection is switched on
    /// or off, which is what decides whether a tunnel a filter routes through may be taken down.
    /// </summary>
    public void RefreshVpnCommands() => ToggleVpnCommand.NotifyCanExecuteChanged();

    // Connect and Disconnect are the same button: which one it is depends on the tunnel, not on
    // which control was pressed. The engine is never told — a tunnel going up or down changes
    // nothing about how a connection is routed.
    [RelayCommand(CanExecute = nameof(CanToggleVpn))]
    private void ToggleVpn(OutboundRowViewModel? row)
    {
        if (row is null) return;

        _services.SetVpnConnectedAsync(row.Model, !row.IsConnected);
        // The tunnel answers on its own thread a moment from now; until then the button would
        // still offer what it offered before.
        ToggleVpnCommand.NotifyCanExecuteChanged();
    }

    private bool CanToggleVpn(OutboundRowViewModel? row)
    {
        if (row is null || !_services.Vpn.CanKeep(row.Model) || !row.IsEnabled) return false;

        // Connecting is always allowed. Disconnecting is not, while redirection is on and a filter
        // routes through this tunnel: taking it down would leave every connection that filter
        // catches failing at a tunnel that is no longer there, with nothing on screen to say why.
        // Switch redirection off, or point the filter elsewhere, and the button comes back.
        if (!row.IsConnected) return true;
        return !_services.Engine.IsRunning
            || !OutboundUsage.RoutedOutboundIds(_services.Config).Contains(row.Id);
    }

    // Called from the tunnel's supervision thread. BeginInvoke, never Invoke: the thread that
    // saves the configuration is the UI thread, and it can be inside the keeper's Sync while this
    // arrives — a synchronous marshal would have the two waiting on each other.
    private void OnVpnStatusChanged(VpnStatus status)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => ApplyVpnStatus(status)));
    }

    private void ApplyVpnStatus(VpnStatus status)
    {
        // A status for an outbound that is no longer in the list — deleted while its tunnel was
        // coming down — has no row to land on, and needs none.
        OutboundRowViewModel? row = Outbounds.FirstOrDefault(r => r.Id == status.OutboundId);
        if (row is null) return;

        // A stopped tunnel is one the keeper is no longer holding up — switched off, disabled or
        // deleted — so the cell goes back to offering Connect rather than sitting there greyed.
        if (status.State == VpnConnectionState.Stopped) row.Tunnel = null;
        else if (row.Tunnel is null) row.Tunnel = new VpnTunnelViewModel(status);
        else row.Tunnel.Update(status);

        // Which button the row offers follows the tunnel, so it is re-asked here rather than only
        // when the user clicks something.
        ToggleVpnCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Add()
    {
        var outbound = new Outbound
        {
            Id = Guid.NewGuid(),
            Name = $"proxy {Outbounds.Count(r => !r.IsBuiltIn) + 1}",
            Kind = OutboundKind.Socks5,
            Url = "socks5://127.0.0.1:1080",
        };
        _services.Config.Outbounds.Add(outbound);

        var row = new OutboundRowViewModel(outbound);
        Outbounds.Add(row);
        Selected = row;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null) return;

        // The configuration repoints at Block every policy that used this way out — "nowhere" must
        // not quietly become Direct, which is the user's own address on the wire — and refuses the
        // two built-ins outright. Both answers belong to the configuration, not to this grid.
        if (!_services.Config.RemoveOutbound(Selected.Id)) return;

        Outbounds.Remove(Selected);
        Selected = null;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void Save() => _services.SaveAndApply();

    [RelayCommand]
    private async Task TestAsync()
    {
        OutboundRowViewModel? row = Selected;
        if (row is null) return;

        IsTesting = true;
        TestResult = null;
        try
        {
            string? error = await _services.OutboundTester
                .TestAsync(row.Model, wireProxyPath: _services.Config.WireProxyPath)
                .ConfigureAwait(true);
            TestResult = error is null
                ? (string)Application.Current.Resources["Str.Outbound.TestOk"]
                : $"{Application.Current.Resources["Str.Outbound.TestFailed"]} {error}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}
