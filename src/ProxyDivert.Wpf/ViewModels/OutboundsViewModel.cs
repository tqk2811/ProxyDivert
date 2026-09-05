using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Engine;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn.Enums;
using ProxyDivert.Core.Vpn.Models;
using ProxyDivert.Wpf.Services;

namespace ProxyDivert.Wpf.ViewModels;

// The Outbounds tab: the list of ways out of the machine.
//
// Direct and Block are kept in the list but not editable — a policy has to be able to reference
// them, and letting the user rename or delete them would only produce broken rules.
public sealed partial class OutboundsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<Outbound> Outbounds { get; } = new ObservableCollection<Outbound>();

    public Array Kinds { get; } = new[]
    {
        OutboundKind.HttpProxy, OutboundKind.Socks4, OutboundKind.Socks5, OutboundKind.Vpn,
    };

    public Array Ipv6Supports { get; } = Enum.GetValues(typeof(Ipv6Support));

    // Every value, Auto included: Auto is the answer for all but one case, and that case — running
    // a WireGuard .conf in this process instead of on wireproxy — can only be said by hand.
    public Array VpnProtocols { get; } = Enum.GetValues(typeof(VpnProtocol));

    [ObservableProperty]
    private Outbound? _selected;

    [ObservableProperty]
    private string? _testResult;

    [ObservableProperty]
    private bool _isTesting;

    /// <summary>
    /// The VPN tunnels the engine is holding up. Empty when nothing is running or no VPN outbound
    /// is enabled, which is what hides the strip.
    /// </summary>
    public ObservableCollection<VpnTunnelViewModel> VpnTunnels { get; }
        = new ObservableCollection<VpnTunnelViewModel>();

    public OutboundsViewModel(AppServices services)
    {
        _services = services;
        // The engine supervises tunnels on its own threads and outlives this view model, so the
        // subscription is for the life of the window.
        _services.Engine.VpnStatusChanged += OnVpnStatusChanged;
        Reload();
    }

    public void Reload()
    {
        Outbounds.Clear();
        foreach (Outbound outbound in _services.Config.Outbounds)
            Outbounds.Add(outbound);

        VpnTunnels.Clear();
        foreach (VpnStatus status in _services.Engine.VpnStatuses)
            VpnTunnels.Add(new VpnTunnelViewModel(status));
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
        VpnTunnelViewModel? row = VpnTunnels.FirstOrDefault(t => t.Id == status.OutboundId);

        // A stopped tunnel is one the engine is no longer keeping — the outbound was disabled,
        // deleted, or the engine stopped — so it leaves the strip rather than sitting there greyed.
        if (status.State == VpnConnectionState.Stopped)
        {
            if (row != null) VpnTunnels.Remove(row);
            return;
        }

        if (row is null) VpnTunnels.Add(new VpnTunnelViewModel(status));
        else row.Update(status);
    }

    // True for the two built-ins, which the UI keeps read-only. The grid asks the row itself, so
    // the answer lives on the outbound and this only forwards it.
    public static bool IsBuiltIn(Outbound outbound) => outbound.IsBuiltIn;

    [RelayCommand]
    private void Add()
    {
        var outbound = new Outbound
        {
            Id = Guid.NewGuid(),
            Name = $"proxy {Outbounds.Count(o => !IsBuiltIn(o)) + 1}",
            Kind = OutboundKind.Socks5,
            Url = "socks5://127.0.0.1:1080",
        };
        _services.Config.Outbounds.Add(outbound);
        Outbounds.Add(outbound);
        Selected = outbound;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null || IsBuiltIn(Selected)) return;

        Guid removedId = Selected.Id;
        _services.Config.Outbounds.Remove(Selected);
        Outbounds.Remove(Selected);

        // A policy pointing at a deleted outbound would send its traffic nowhere, and "nowhere"
        // must not quietly become Direct — that is the user's address on the wire. Repointed at
        // Block so the mistake is visible from the first connection instead.
        foreach (RoutingPolicy policy in _services.Config.Policies.Where(p => p.OutboundId == removedId))
            policy.OutboundId = Outbound.BlockId;

        Selected = null;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void Save() => _services.SaveAndApply();

    [RelayCommand]
    private async Task TestAsync()
    {
        Outbound? outbound = Selected;
        if (outbound is null) return;

        IsTesting = true;
        TestResult = null;
        try
        {
            string? error = await RedirectEngine
                .TestOutboundAsync(outbound, wireProxyPath: _services.Config.WireProxyPath)
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
