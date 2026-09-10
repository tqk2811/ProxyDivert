using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDivert.Core.Outbounds.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Vpn;
using ProxyDivert.Core.Vpn.Enums;

namespace ProxyDivert.Wpf.ViewModels;

/// <summary>
/// One row of the Outbounds grid.
/// </summary>
/// <remarks>
/// The grid used to bind straight at the <see cref="Outbound"/> the engine would later be handed a
/// copy of. A plain model raises nothing, so a cell that changed left the rest of the row showing
/// what it showed before, and the tricks that worked around it — taking a row out of the collection
/// and putting it back to force a redraw — are elsewhere in this application for the same reason.
/// Worse, there was nowhere between the cell and the model to stand: nowhere to say that an address
/// cannot be read, and nowhere to refuse an edit to Direct or Block, which is why refusing it ended
/// up in the view's code-behind and in a repair on load.
///
/// So the row is an object. It writes through to the model rather than holding a copy — the
/// configuration stays the single list, and Save still hands the engine a snapshot of it — and it
/// is the one place that knows a row is not the user's to change.
/// </remarks>
public sealed partial class OutboundRowViewModel : ObservableObject
{
    private readonly SoftEtherWatermarkStore? _watermarks;

    /// <param name="watermarks">
    /// Where the SoftEther watermark blob is, so a row that needs one can say so and offer to fetch
    /// it. Null leaves that offer off the row entirely, which is what a test binding a grid wants.
    /// </param>
    public OutboundRowViewModel(Outbound model, SoftEtherWatermarkStore? watermarks = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _watermarks = watermarks;
    }

    /// <summary>The outbound itself, for the commands that work on the configuration.</summary>
    public Outbound Model { get; }

    public Guid Id => Model.Id;

    /// <summary>
    /// Direct and Block. Everything about them except their name is fixed, and a policy points at
    /// them by id, so nothing here is the user's to change.
    /// </summary>
    public bool IsBuiltIn => Model.IsBuiltIn;

    public bool IsEditable => !Model.IsBuiltIn;

    public bool IsVpn => Model.Kind == OutboundKind.Vpn;

    // ==== the cells ====

    public bool IsEnabled
    {
        get => Model.IsEnabled;
        set => Write(Model.IsEnabled, value, v => Model.IsEnabled = v);
    }

    public string Name
    {
        get => Model.Name;
        // A blank name is dropped rather than stored: a row with no text is a row nobody can point
        // at, and every policy that names this outbound would show a gap.
        set
        {
            if (string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
            Write(Model.Name, value, v => Model.Name = v);
        }
    }

    public OutboundKind Kind
    {
        get => Model.Kind;
        set
        {
            if (!Write(Model.Kind, value, v => Model.Kind = v)) return;
            OnPropertyChanged(nameof(IsVpn));
            AddressChanged();
        }
    }

    public string? Url
    {
        get => Model.Url;
        set
        {
            if (Write(Model.Url, value, v => Model.Url = v)) AddressChanged();
        }
    }

    public string? Username
    {
        get => Model.Username;
        set => Write(Model.Username, value, v => Model.Username = v);
    }

    public string? Password
    {
        get => Model.Password;
        set => Write(Model.Password, value, v => Model.Password = v);
    }

    public string? PreSharedKey
    {
        get => Model.PreSharedKey;
        set => Write(Model.PreSharedKey, value, v => Model.PreSharedKey = v);
    }

    public VpnProtocol VpnProtocol
    {
        get => Model.VpnProtocol;
        set
        {
            // The same text means different things depending on this box — a bare "host:443" is a
            // server once a dialled protocol is chosen and a file path until then — so the address
            // is worth another look.
            if (Write(Model.VpnProtocol, value, v => Model.VpnProtocol = v)) AddressChanged();
        }
    }

    public Ipv6Support Ipv6Support
    {
        get => Model.Ipv6Support;
        set => Write(Model.Ipv6Support, value, v => Model.Ipv6Support = v);
    }

    // ==== what is wrong with the address, said where it was typed ====

    /// <summary>
    /// Why this row's address cannot be used, or null when it can. Shown on the cell itself.
    /// </summary>
    /// <remarks>
    /// The alternative was finding out at the first connection through it, with the user already
    /// waiting — a SOCKS proxy with no port, a path with a typo in it. Whether the file is there is
    /// asked here and nowhere else: this runs when a cell is edited, while the routing path must
    /// never touch the disk.
    /// </remarks>
    public string? AddressProblem
    {
        get
        {
            string? problem = Model.AddressProblem;
            if (problem != null) return problem;

            OutboundAddress? address = Model.Address;
            if (address is { IsFile: true } && !File.Exists(address.Path!))
                return $"points at a file that is not there: {address.Path}";

            return null;
        }
    }

    public bool HasAddressProblem => AddressProblem != null;

    /// <summary>
    /// Whether this row is a SoftEther outbound with no watermark blob on the machine — the one
    /// thing a VPN row can be missing that is neither typed in a box nor fixable by typing.
    /// </summary>
    /// <remarks>
    /// Asked here, next to <see cref="AddressProblem"/>, and for the same reason: it reads the disk,
    /// which the routing path must never do, and it is re-asked when a cell is edited so switching
    /// the protocol box to SoftEther offers the download straight away.
    /// </remarks>
    public bool NeedsWatermark
        => _watermarks != null
            && IsVpn
            && VpnProfileReader.IsSoftEther(Model.VpnProtocol, Model.Url)
            && _watermarks.Find() is null;

    // ==== the tunnel, for a VPN row ====

    /// <summary>
    /// The tunnel the engine is holding up for this outbound, or null when there is none.
    /// </summary>
    /// <remarks>
    /// It sits on the row because that is where it belongs. It used to be a separate list keyed by
    /// outbound id, which meant every cell that wanted it did the lookup itself through a
    /// multi-binding carrying the list AND the list's count — the count because a collection that
    /// gains an item raises nothing on the property holding it.
    /// </remarks>
    [ObservableProperty]
    private VpnTunnelViewModel? _tunnel;

    public bool IsConnected => Tunnel != null;

    partial void OnTunnelChanged(VpnTunnelViewModel? value) => OnPropertyChanged(nameof(IsConnected));

    /// <summary>
    /// Re-asks <see cref="NeedsWatermark"/>. For after the blob has been fetched — by this row, by
    /// another one, or by the button on the Settings tab.
    /// </summary>
    public void RefreshWatermarkNeed() => OnPropertyChanged(nameof(NeedsWatermark));

    /// <summary>Re-reads every cell. For when something outside the grid changed the model.</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }

    private void AddressChanged()
    {
        OnPropertyChanged(nameof(AddressProblem));
        OnPropertyChanged(nameof(HasAddressProblem));
        OnPropertyChanged(nameof(NeedsWatermark));
    }

    /// <summary>
    /// Writes one cell through to the model. False when nothing changed, or when the row is one of
    /// the two the user does not own.
    /// </summary>
    /// <remarks>
    /// A refused edit still raises the change. The grid disables what it can and the view cancels
    /// the edit before it starts, but a control that has already taken the keystroke has to be told
    /// to read the property again — otherwise the cell goes on showing something the model never
    /// accepted.
    /// </remarks>
    private bool Write<T>(T current, T value, Action<T> apply, [CallerMemberName] string? property = null)
    {
        if (IsBuiltIn)
        {
            OnPropertyChanged(property);
            return false;
        }

        if (EqualityComparer<T>.Default.Equals(current, value)) return false;

        apply(value);
        OnPropertyChanged(property);
        return true;
    }
}
