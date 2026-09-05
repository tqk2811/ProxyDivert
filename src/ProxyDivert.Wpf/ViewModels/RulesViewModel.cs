using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Wpf.Localization;
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

    /// <summary>The row being renamed right now, or null. That row draws a box instead of text.</summary>
    [ObservableProperty]
    private RoutingPolicy? _renamingPolicy;

    /// <summary>What is in the rename box. Only means anything while <see cref="RenamingPolicy"/> is set.</summary>
    [ObservableProperty]
    private string _policyName = string.Empty;

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

    // ==== renaming a policy, in place in the list ====
    //
    // The name is the only thing about a policy said in words, and it is what every process filter
    // shows to say where its traffic goes, so "Policy 2" forever makes the filter list unreadable.
    // Double-clicking the row turns it into a box; the edit lands when the box loses focus or on
    // Enter, and Escape drops it.

    /// <summary>Starts renaming one row. The list draws a box in place of that row's text.</summary>
    [RelayCommand]
    public void BeginRename(RoutingPolicy? policy)
    {
        if (policy is null) return;

        PolicyName = policy.Name;
        RenamingPolicy = policy;
    }

    /// <summary>
    /// Takes what was typed. A blank name is dropped rather than stored — a row with no text is a
    /// row nobody can point at, and every filter that names this policy would show a gap.
    /// </summary>
    [RelayCommand]
    public void CommitRename()
    {
        RoutingPolicy? policy = RenamingPolicy;
        if (policy is null) return;

        RenamingPolicy = null;

        string name = (PolicyName ?? string.Empty).Trim();
        if (name.Length == 0 || name == policy.Name) return;

        policy.Name = name;

        // A RoutingPolicy is plain data with nothing to raise a change, so the list has to be told.
        // Assigning the row back over itself does NOT do it: same reference in and out, so WPF sees
        // no change, keeps the container it already has, and the row goes on showing the old name
        // until the tab is rebuilt. The row has to actually leave the collection for its container
        // to be thrown away and its bindings read again.
        int index = Policies.IndexOf(policy);
        if (index >= 0)
        {
            Policies.RemoveAt(index);
            Policies.Insert(index, policy);
        }

        SelectedPolicy = policy;

        _services.SaveAndApply();
    }

    [RelayCommand]
    public void CancelRename() => RenamingPolicy = null;

    [RelayCommand]
    private void AddPolicy()
    {
        var policy = new RoutingPolicy
        {
            Id = Guid.NewGuid(),
            Name = LocalizationManager.Format("Str.Rules.NewPolicy", Policies.Count + 1),
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

        // A filter can name several policies. The deleted one is taken out of each list, and a
        // filter left with an empty list gets the fallback: catching processes and then having no
        // rules at all would send them out direct, which is the one outcome that must not happen
        // by accident.
        RoutingPolicy fallback = Policies[0];
        foreach (ProcessRule rule in _services.Config.ProcessRules.Where(r => r.PolicyIds.Contains(policy.Id)))
        {
            rule.PolicyIds.Remove(policy.Id);
            if (rule.PolicyIds.Count == 0) rule.PolicyIds.Add(fallback.Id);
        }

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
