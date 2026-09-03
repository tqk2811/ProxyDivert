using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Wpf.Services;

namespace ProxyDivert.Wpf.ViewModels;

// The Rules tab: policies and their ordered rule lists.
//
// Order is an explicit number rather than list position, so moving a rule is a renumber of the two
// rows involved instead of a rebuild — and the saved file keeps the order even if something else
// reorders the list.
public sealed partial class RulesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<RoutingPolicy> Policies { get; } = new ObservableCollection<RoutingPolicy>();

    public ObservableCollection<RoutingRule> Rules { get; } = new ObservableCollection<RoutingRule>();

    public ObservableCollection<Outbound> Outbounds { get; } = new ObservableCollection<Outbound>();

    public Array Matchers { get; } = Enum.GetValues(typeof(HostMatcherType));

    public Array UdpModes { get; } = Enum.GetValues(typeof(UdpMode));

    [ObservableProperty]
    private RoutingPolicy? _selectedPolicy;

    [ObservableProperty]
    private RoutingRule? _selectedRule;

    public RulesViewModel(AppServices services)
    {
        _services = services;
        Reload();
    }

    public void Reload()
    {
        Guid? previous = SelectedPolicy?.Id;

        Policies.Clear();
        foreach (RoutingPolicy policy in _services.Config.Policies) Policies.Add(policy);

        Outbounds.Clear();
        foreach (Outbound outbound in _services.Config.Outbounds) Outbounds.Add(outbound);

        SelectedPolicy = Policies.FirstOrDefault(p => p.Id == previous) ?? Policies.FirstOrDefault();
    }

    partial void OnSelectedPolicyChanged(RoutingPolicy? value)
    {
        Rules.Clear();
        if (value is null) return;
        foreach (RoutingRule rule in value.Rules.OrderBy(r => r.Order)) Rules.Add(rule);
    }

    [RelayCommand]
    private void AddPolicy()
    {
        var policy = new RoutingPolicy
        {
            Id = Guid.NewGuid(),
            Name = $"Policy {Policies.Count + 1}",
        };
        _services.Config.Policies.Add(policy);
        Policies.Add(policy);
        SelectedPolicy = policy;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void RemovePolicy()
    {
        RoutingPolicy? policy = SelectedPolicy;
        if (policy is null) return;

        // A process rule pointing at a deleted policy would leave those processes redirected with
        // no rules at all. Keep the last policy so that cannot happen.
        if (Policies.Count <= 1) return;

        _services.Config.Policies.Remove(policy);
        Policies.Remove(policy);

        RoutingPolicy fallback = Policies[0];
        foreach (ProcessRule rule in _services.Config.ProcessRules.Where(r => r.PolicyId == policy.Id))
            rule.PolicyId = fallback.Id;

        SelectedPolicy = fallback;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void AddRule()
    {
        RoutingPolicy? policy = SelectedPolicy;
        if (policy is null) return;

        var rule = new RoutingRule
        {
            Id = Guid.NewGuid(),
            Matcher = HostMatcherType.Wildcard,
            Pattern = "*.example.com",
            OutboundId = _services.Config.Outbounds
                .FirstOrDefault(o => o.Kind != OutboundKind.Direct && o.Kind != OutboundKind.Block)?.Id
                ?? Outbound.DirectId,
            Order = policy.Rules.Count == 0 ? 0 : policy.Rules.Max(r => r.Order) + 1,
        };
        policy.Rules.Add(rule);
        Rules.Add(rule);
        SelectedRule = rule;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void RemoveRule()
    {
        RoutingPolicy? policy = SelectedPolicy;
        if (policy is null || SelectedRule is null) return;

        policy.Rules.Remove(SelectedRule);
        Rules.Remove(SelectedRule);
        SelectedRule = null;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(+1);

    private void Move(int delta)
    {
        RoutingRule? rule = SelectedRule;
        if (rule is null) return;

        int index = Rules.IndexOf(rule);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Rules.Count) return;

        Rules.Move(index, target);
        // Renumber the whole list: gaps and duplicates from earlier edits disappear here.
        for (int i = 0; i < Rules.Count; i++) Rules[i].Order = i;
        SelectedRule = rule;
        _services.SaveAndApply();
    }

    [RelayCommand]
    private void Save() => _services.SaveAndApply();
}
