using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.ViewModels;

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

    /// <summary>Anti-DPI TLS for what this policy matches: ticked, unticked, or (null) as the outbound says.</summary>
    public bool? AntiDpiTls
    {
        get => Model.AntiDpiTls;
        set
        {
            if (value == Model.AntiDpiTls) return;
            Model.AntiDpiTls = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Anti-DPI CONNECT, the same three ways.</summary>
    public bool? AntiDpiConnect
    {
        get => Model.AntiDpiConnect;
        set
        {
            if (value == Model.AntiDpiConnect) return;
            Model.AntiDpiConnect = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Bytes per piece for this policy, or null to use the outbound's. The box is text so it can be
    /// left empty; anything that is not a number of at least 1 empties it.
    /// </summary>
    public string AntiDpiChunkSize
    {
        get => Model.AntiDpiChunkSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            int? parsed = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1
                ? n
                : null;
            if (parsed != Model.AntiDpiChunkSize) Model.AntiDpiChunkSize = parsed;
            // Raised even when unchanged, so a rejected entry is wiped from the box.
            OnPropertyChanged();
        }
    }

    /// <summary>Secure DNS for the DNS queries the matched app sends itself.</summary>
    public bool SecureDnsProcess
    {
        get => Model.SecureDnsProcess;
        set
        {
            if (value == Model.SecureDnsProcess) return;
            Model.SecureDnsProcess = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Secure DNS for the names Windows resolves on behalf of apps, when a domain rule of this policy matches.</summary>
    public bool SecureDnsSystem
    {
        get => Model.SecureDnsSystem;
        set
        {
            if (value == Model.SecureDnsSystem) return;
            Model.SecureDnsSystem = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Send the query as plain DNS when DoH fails, instead of answering an error.</summary>
    public bool SecureDnsFallbackToPlain
    {
        get => Model.SecureDnsFallbackToPlain;
        set
        {
            if (value == Model.SecureDnsFallbackToPlain) return;
            Model.SecureDnsFallbackToPlain = value;
            OnPropertyChanged();
        }
    }

    /// <summary>This policy's own DoH server; empty uses the one in Settings.</summary>
    public string? DohEndpoint
    {
        get => Model.DohEndpoint;
        set
        {
            string? normalized = string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
            if (normalized == Model.DohEndpoint) return;
            Model.DohEndpoint = normalized;
            OnPropertyChanged();
        }
    }

    public bool IsBuiltIn => Model.IsBuiltIn;

    public override string ToString() => Name;
}
