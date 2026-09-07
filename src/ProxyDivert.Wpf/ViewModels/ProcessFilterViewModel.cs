using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Helpers;
using ProxyDivert.Wpf.Localization;
using ProxyDivert.Wpf.ViewModels.Conditions;

namespace ProxyDivert.Wpf.ViewModels;

/// <summary>The filter window: a name, a tree of conditions, and what to do with what matches.</summary>
/// <remarks>
/// It edits a copy. The tree is cloned on the way in and only written back in
/// <see cref="ApplyTo"/>, so closing the window without saving leaves the filter exactly as it was
/// — including for a filter the engine is running against right now.
/// </remarks>
public sealed partial class ProcessFilterViewModel : ObservableObject
{
    public ProcessFilterViewModel(ProcessRule rule, IEnumerable<RoutingPolicy> policies)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));

        // The fields, not the properties: assigning through the properties before the window is
        // even on screen would mark the filter as edited by the act of opening it.
        _name = rule.Name;
        _includeChildren = rule.IncludeChildren;

        BuildPolicyList(rule, policies);

        Root = new ConditionGroupViewModel(RootGroupOf(rule));
        Root.Changed += OnTreeChanged;
        _summary = ConditionTextBuilder.Describe(Root.ToModel());
    }

    /// <summary>The outermost group. Everything the editor shows hangs off this.</summary>
    public ConditionGroupViewModel Root { get; }

    /// <summary>
    /// Every policy there is, ticked or not, in the order that decides priority: the rules of a
    /// ticked policy are tried before those of every ticked policy below it.
    /// </summary>
    /// <remarks>
    /// One list rather than "available" and "chosen" side by side. A policy keeps its place when it
    /// is unticked, so trying one out and putting it back does not cost the arrangement, and the
    /// order is a property of the whole list rather than of a second one that has to be kept in
    /// step. The rank number is only drawn next to the ticked rows, which is where order means
    /// anything.
    /// </remarks>
    public ObservableCollection<PolicyChoice> Policies { get; } = new ObservableCollection<PolicyChoice>();

    /// <summary>
    /// True once the user has changed anything in the window. What the close button asks about:
    /// closing a filter that was only looked at must not stop to ask.
    /// </summary>
    /// <remarks>
    /// Set by the edits themselves rather than worked out by comparing the filter with what it was.
    /// A change put back by hand still counts as an edit, which is the answer every editor gives
    /// and the safe one: the question it leads to is one the user can answer with "don't save".
    /// </remarks>
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _includeChildren;

    partial void OnNameChanged(string value) => IsDirty = true;

    partial void OnIncludeChildrenChanged(bool value) => IsDirty = true;

    /// <summary>The policies the user ticked, in priority order, read back as one line.</summary>
    [ObservableProperty]
    private string _policySummary = string.Empty;

    /// <summary>The whole filter read back as one sentence. Rebuilt on every edit.</summary>
    [ObservableProperty]
    private string _summary;

    /// <summary>Writes the edited filter back onto the rule. Called only when the user saves.</summary>
    public void ApplyTo(ProcessRule rule)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));

        rule.Name = string.IsNullOrWhiteSpace(Name) ? DefaultName() : Name.Trim();
        rule.Condition = Root.ToModel();
        rule.IncludeChildren = IncludeChildren;
        rule.PolicyIds = ChosenPolicyIds();
        // The whole list, not just the ticked part: that is what the window has to come back up as.
        rule.PolicyOrder = Policies.Select(c => c.Policy.Id).ToList();
    }

    // Ticking nothing would leave the filter catching processes and then having no rules to route
    // them by — they would fall through to the untracked default, which is Direct. That is the one
    // outcome a redirector must not produce by accident, so the first policy stands in.
    private List<Guid> ChosenPolicyIds()
    {
        List<Guid> chosen = Policies.Where(p => p.IsSelected).Select(p => p.Policy.Id).ToList();
        if (chosen.Count == 0 && Policies.Count > 0) chosen.Add(Policies[0].Policy.Id);
        return chosen;
    }

    // The order the rule was left in — every row, ticked or not — then everything it does not
    // mention, so the whole set is there to be ticked without a second list to go to.
    //
    // The ticked ids follow the saved order rather than lead it: a filter saved before the order
    // was kept has none, and then they are the order, which is how the window used to open.
    // Anything still missing is a policy created since, and it goes at the end.
    private void BuildPolicyList(ProcessRule rule, IEnumerable<RoutingPolicy> policies)
    {
        List<RoutingPolicy> all = policies.ToList();
        var chosen = new HashSet<Guid>(rule.PolicyIds);

        foreach (Guid id in rule.PolicyOrder.Concat(rule.PolicyIds))
        {
            RoutingPolicy? policy = all.FirstOrDefault(p => p.Id == id);
            if (policy != null && !Policies.Any(c => c.Policy.Id == id))
                Policies.Add(new PolicyChoice(policy) { IsSelected = chosen.Contains(id) });
        }

        // Whatever is left is a policy the filter has never named, so it comes up unticked.
        foreach (RoutingPolicy policy in all)
            if (!Policies.Any(c => c.Policy.Id == policy.Id))
                Policies.Add(new PolicyChoice(policy));

        // Only the tick: Rank is written by RenumberPolicies itself, and reacting to it would both
        // loop back into it and call opening the window an edit.
        foreach (PolicyChoice choice in Policies)
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(PolicyChoice.IsSelected)) return;
                RenumberPolicies();
                IsDirty = true;
            };

        RenumberPolicies();
    }

    [RelayCommand]
    private void MovePolicyUp(PolicyChoice? choice) => MovePolicy(choice, -1);

    [RelayCommand]
    private void MovePolicyDown(PolicyChoice? choice) => MovePolicy(choice, +1);

    private void MovePolicy(PolicyChoice? choice, int delta)
    {
        if (choice is null) return;

        int index = Policies.IndexOf(choice);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Policies.Count) return;

        Policies.Move(index, target);
        RenumberPolicies();
        IsDirty = true;
    }

    // The number shown against a ticked row, and the sentence under the list. Both are derived from
    // the list, so they are recomputed rather than maintained.
    private void RenumberPolicies()
    {
        int rank = 0;
        foreach (PolicyChoice choice in Policies)
            choice.Rank = choice.IsSelected ? ++rank : 0;

        PolicySummary = string.Join(
            " → ",
            Policies.Where(c => c.IsSelected).Select(c => c.Policy.Name));
    }

    /// <summary>One policy in the list: whether this filter uses it, and where in the order.</summary>
    public sealed partial class PolicyChoice : ObservableObject
    {
        public PolicyChoice(RoutingPolicy policy) => Policy = policy;

        public RoutingPolicy Policy { get; }

        public string Name => Policy.Name;

        [ObservableProperty]
        private bool _isSelected;

        /// <summary>1 for the first policy tried, 2 for the next; 0 while the row is not ticked.</summary>
        [ObservableProperty]
        private int _rank;
    }

    private void OnTreeChanged()
    {
        Summary = ConditionTextBuilder.Describe(Root.ToModel());
        IsDirty = true;
    }

    // A filter with no name is still a row in a list that has to say something. The first thing
    // the user typed is what they would have called it anyway.
    private string DefaultName()
    {
        string? pattern = FirstPattern(Root.ToModel());
        return string.IsNullOrWhiteSpace(pattern) ? Loc.S("Str.Process.UnnamedFilter") : pattern!.Trim();
    }

    private static string? FirstPattern(ProcessCondition? condition) => condition switch
    {
        LeafCondition leaf when !string.IsNullOrWhiteSpace(leaf.Pattern) => leaf.Pattern,
        ConditionGroup group => group.Children.Select(FirstPattern).FirstOrDefault(p => p != null),
        _ => null,
    };

    // A filter written by hand into the config file can have a single condition at its root, and
    // the editor only knows how to show groups. Wrapping it changes nothing about what it matches.
    private static ConditionGroup RootGroupOf(ProcessRule rule) => rule.Condition switch
    {
        ConditionGroup group => (ConditionGroup)group.Clone(),
        ProcessCondition condition => new ConditionGroup { Children = { condition.Clone() } },
        _ => ConditionGroup.CreateDefault(),
    };

}
