using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Routing.Compiled;
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
//
// Both lists hold row view models over the configuration's own objects. A policy and a rule are
// plain data with nothing to raise a change, and the tab used to work around that by taking a row
// out of its collection and putting it straight back.
public sealed partial class RulesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<PolicyRowViewModel> Policies { get; }
        = new ObservableCollection<PolicyRowViewModel>();

    public ObservableCollection<RuleRowViewModel> Rules { get; }
        = new ObservableCollection<RuleRowViewModel>();

    public ObservableCollection<Outbound> Outbounds { get; } = new ObservableCollection<Outbound>();

    public Array Matchers { get; } = Enum.GetValues(typeof(HostMatcherType));

    public Array UdpModes { get; } = Enum.GetValues(typeof(UdpMode));

    [ObservableProperty]
    private PolicyRowViewModel? _selectedPolicy;

    [ObservableProperty]
    private RuleRowViewModel? _selectedRule;

    /// <summary>The row being renamed right now, or null. That row draws a box instead of text.</summary>
    [ObservableProperty]
    private PolicyRowViewModel? _renamingPolicy;

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
        foreach (RoutingPolicy policy in _services.Config.Policies)
            Policies.Add(new PolicyRowViewModel(policy));

        Outbounds.Clear();
        foreach (Outbound outbound in _services.Config.Outbounds) Outbounds.Add(outbound);

        SelectedPolicy = Policies.FirstOrDefault(p => p.Id == previous) ?? Policies.FirstOrDefault();
        CheckPatterns();
    }

    /// <summary>
    /// The rules whose pattern cannot be parsed, one per line, or null while every pattern is
    /// usable.
    /// </summary>
    /// <remarks>
    /// Such a rule matches nothing, on every connection, for as long as it stays in the list — and
    /// with Not ticked it claims everything instead. The row says so on its own cell; this is the
    /// list of all of them, because a filter can name several policies and only one of them is on
    /// screen at a time.
    /// </remarks>
    [ObservableProperty]
    private string? _patternProblems;

    public bool HasPatternProblems => !string.IsNullOrEmpty(PatternProblems);

    partial void OnPatternProblemsChanged(string? value) => OnPropertyChanged(nameof(HasPatternProblems));

    private void CheckPatterns()
    {
        // The same compile the engine does, so what is shown here is what the engine will use — not
        // a second opinion written to agree with it.
        IReadOnlyList<RulePatternError> errors = CompiledRuleSet.Compile(_services.Config.Policies).Errors;
        PatternProblems = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
    }

    // Every command that edits a rule ends here, so the check happens on each of them rather than
    // only when the tab is opened again.
    private void SaveAndApply()
    {
        _services.SaveAndApply();
        CheckPatterns();
    }

    partial void OnSelectedPolicyChanged(PolicyRowViewModel? value)
    {
        Rules.Clear();
        SelectedRule = null;
        if (value is null) return;

        foreach (RoutingRule rule in value.Model.Rules.OrderBy(r => r.Order))
            Rules.Add(new RuleRowViewModel(rule));
    }

    // ==== renaming a policy, in place in the list ====
    //
    // The name is the only thing about a policy said in words, and it is what every process filter
    // shows to say where its traffic goes, so "Policy 2" forever makes the filter list unreadable.
    // Double-clicking the row turns it into a box; the edit lands when the box loses focus or on
    // Enter, and Escape drops it.

    /// <summary>Starts renaming one row. The list draws a box in place of that row's text.</summary>
    [RelayCommand]
    public void BeginRename(PolicyRowViewModel? policy)
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
        PolicyRowViewModel? policy = RenamingPolicy;
        if (policy is null) return;

        RenamingPolicy = null;

        string name = (PolicyName ?? string.Empty).Trim();
        if (name.Length == 0 || name == policy.Name) return;

        // The row raises the change itself, so the list redraws where it stands. It used to be
        // taken out of the collection and put back — the only way to make WPF read a plain model
        // again — and that unselected the policy, which emptied the rule grid underneath.
        policy.Name = name;

        SaveAndApply();
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

        var row = new PolicyRowViewModel(policy);
        Policies.Add(row);
        SelectedPolicy = row;
        SaveAndApply();
    }

    [RelayCommand]
    private void RemovePolicy()
    {
        PolicyRowViewModel? policy = SelectedPolicy;
        if (policy is null) return;

        // The configuration takes the policy out of every filter that named it, and refuses when
        // this is the last one — a filter must always have somewhere to point, because catching
        // processes and then having no rules at all sends them out direct rather than stopping.
        // None of that is the grid's business; the grid only follows what happened.
        if (!_services.Config.RemovePolicy(policy.Id)) return;

        Policies.Remove(policy);
        SelectedPolicy = Policies.FirstOrDefault();
        SaveAndApply();
    }

    [RelayCommand]
    private void AddRule()
    {
        PolicyRowViewModel? policy = SelectedPolicy;
        if (policy is null) return;

        var rule = new RoutingRule
        {
            Id = Guid.NewGuid(),
            Matcher = HostMatcherType.Wildcard,
            Pattern = "*.example.com",
            Order = policy.Model.Rules.Count == 0 ? 0 : policy.Model.Rules.Max(r => r.Order) + 1,
        };
        policy.Model.Rules.Add(rule);

        var row = new RuleRowViewModel(rule);
        Rules.Add(row);
        SelectedRule = row;
        SaveAndApply();
    }

    [RelayCommand]
    private void RemoveRule()
    {
        PolicyRowViewModel? policy = SelectedPolicy;
        RuleRowViewModel? rule = SelectedRule;
        if (policy is null || rule is null) return;

        policy.Model.Rules.Remove(rule.Model);
        Rules.Remove(rule);
        SelectedRule = null;
        SaveAndApply();
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(+1);

    private void Move(int delta)
    {
        RuleRowViewModel? rule = SelectedRule;
        if (rule is null) return;

        int index = Rules.IndexOf(rule);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Rules.Count) return;

        Rules.Move(index, target);
        // Renumber the whole list: gaps and duplicates from earlier edits disappear here.
        for (int i = 0; i < Rules.Count; i++) Rules[i].Model.Order = i;
        SelectedRule = rule;
        SaveAndApply();
    }

    [RelayCommand]
    private void Save() => SaveAndApply();
}
