using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Wpf.ViewModels;

/// <summary>
/// One policy in the list on the Rules tab.
/// </summary>
/// <remarks>
/// A <see cref="RoutingPolicy"/> is plain data with nothing to raise a change, so renaming one used
/// to mean taking the row out of the collection and putting it straight back — the only way to make
/// the list throw its container away and read the name again. It worked, and it cost the rest of
/// the tab: removing a row unselects it, unselecting a policy empties the rule grid, and the rule
/// the user had picked was gone by the time the new name appeared.
///
/// The row raises its own changes, so the list is never disturbed.
/// </remarks>
public sealed partial class PolicyRowViewModel : ObservableObject
{
    public PolicyRowViewModel(RoutingPolicy model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>The policy itself, for the commands that work on the configuration.</summary>
    public RoutingPolicy Model { get; }

    public Guid Id => Model.Id;

    /// <summary>
    /// What the user calls this policy. It is what every process filter shows to say where its
    /// traffic goes, so a blank one would leave a gap in each of them, and is dropped.
    /// </summary>
    public string Name
    {
        get => Model.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Model.Name) return;
            Model.Name = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Where a connection matching one of this policy's rules goes.</summary>
    public Guid OutboundId
    {
        get => Model.OutboundId;
        set
        {
            if (value == Model.OutboundId) return;
            Model.OutboundId = value;
            OnPropertyChanged();
        }
    }

    public UdpMode UdpMode
    {
        get => Model.UdpMode;
        set
        {
            if (value == Model.UdpMode) return;
            Model.UdpMode = value;
            OnPropertyChanged();
        }
    }

    public bool BlockQuic
    {
        get => Model.BlockQuic;
        set
        {
            if (value == Model.BlockQuic) return;
            Model.BlockQuic = value;
            OnPropertyChanged();
        }
    }

    public override string ToString() => Name;
}
