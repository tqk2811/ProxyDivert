using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Wpf.ViewModels.Conditions;

/// <summary>A bracket in the editor: one operator, and the rows under it.</summary>
/// <remarks>
/// The operator lives on the group and nowhere else. That is the whole reason this editor is not
/// an expression box in disguise: there is no "and" or "or" to pick BETWEEN two rows, so there is
/// no precedence for the user to get wrong — the indentation says which bracket a row is in, and
/// the one combo at the top of the bracket says how those rows join.
/// </remarks>
public sealed partial class ConditionGroupViewModel : ConditionNodeViewModel
{
    public ObservableCollection<ConditionNodeViewModel> Children { get; }
        = new ObservableCollection<ConditionNodeViewModel>();

    public Array Operators { get; } = Enum.GetValues(typeof(ConditionOperator));

    [ObservableProperty]
    private ConditionOperator _operator;

    public ConditionGroupViewModel()
    {
        // Both of these turn on and off with something the user just did to a row: ticking one
        // enables "group the ticked rows", and turning NOT on takes "ungroup" away. Ticking and
        // negating both arrive here as a change coming up from a row, so one handler serves.
        Changed += () =>
        {
            GroupSelectedCommand.NotifyCanExecuteChanged();
            UngroupCommand.NotifyCanExecuteChanged();
        };
    }

    public ConditionGroupViewModel(ConditionGroup model) : this()
    {
        Negate = model.Negate;
        Operator = model.Operator;
        foreach (ProcessCondition child in model.Children) Attach(FromModel(child));
    }

    /// <summary>True for the outermost group, which cannot be removed, moved or ungrouped.</summary>
    public bool IsRoot => Parent is null;

    public override ProcessCondition ToModel() => new ConditionGroup
    {
        Negate = Negate,
        Operator = Operator,
        Children = Children.Select(child => child.ToModel()).ToList(),
    };

    public override void ClearTestResult()
    {
        base.ClearTestResult();
        foreach (ConditionNodeViewModel child in Children) child.ClearTestResult();
    }

    // ==== what the buttons on a group row do ====

    [RelayCommand]
    private void AddCondition()
    {
        var leaf = new ConditionLeafViewModel { IsNew = true };
        Attach(leaf);
    }

    [RelayCommand]
    private void AddGroup()
    {
        // A group with one row in it rather than an empty one: an empty bracket is a thing the
        // user then has to fill before it means anything, and it looks like the editor broke.
        var group = new ConditionGroupViewModel { Operator = ConditionOperator.Any };
        group.Attach(new ConditionLeafViewModel { IsNew = true });
        Attach(group);
    }

    /// <summary>Puts the ticked rows of this group into a bracket of their own.</summary>
    /// <remarks>
    /// The one operation that makes a tree editor usable. People write conditions flat, discover
    /// halfway through that two of them belong together, and are not going to delete and retype
    /// them to get a bracket. The new group joins with "any", because wanting a bracket almost
    /// always means wanting an OR inside an AND.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanGroupSelected))]
    private void GroupSelected()
    {
        List<ConditionNodeViewModel> selected = Children.Where(child => child.IsSelected).ToList();
        if (selected.Count < 2) return;

        int index = Children.IndexOf(selected[0]);
        var group = new ConditionGroupViewModel { Operator = ConditionOperator.Any };

        foreach (ConditionNodeViewModel child in selected) Detach(child);
        foreach (ConditionNodeViewModel child in selected)
        {
            child.IsSelected = false;
            group.Attach(child);
        }

        Attach(group, index);
    }

    private bool CanGroupSelected() => Children.Count(child => child.IsSelected) >= 2;

    /// <summary>Dissolves this group into its parent.</summary>
    /// <remarks>
    /// Blocked while the group is negated. "NOT (a OR b)" spread over a parent that joins with
    /// "and" is not the same filter, and quietly changing what someone wrote is worse than making
    /// them turn the NOT off first.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanUngroup))]
    private void Ungroup() => Parent?.Absorb(this);

    private bool CanUngroup() => Parent != null && !Negate;


    // ==== tree surgery ====

    private void Attach(ConditionNodeViewModel node, int index = -1)
    {
        node.Parent = this;
        node.Changed += RaiseChanged;

        if (index < 0) Children.Add(node);
        else Children.Insert(index, node);

        RaiseChanged();
    }

    private void Detach(ConditionNodeViewModel node)
    {
        node.Changed -= RaiseChanged;
        node.Parent = null;
        Children.Remove(node);
    }

    internal void RemoveChild(ConditionNodeViewModel node)
    {
        Detach(node);
        RaiseChanged();
        Parent?.TidyAfterRemoval(this);
    }

    internal void Move(ConditionNodeViewModel node, int delta)
    {
        int index = Children.IndexOf(node);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Children.Count) return;

        Children.Move(index, target);
        RaiseChanged();
    }

    // Deleting rows must not leave brackets behind that mean nothing: an empty group disappears,
    // and a group down to its last row was a bracket around one thing, which is that thing.
    //
    // Only ever on a removal. Doing it whenever a group happens to hold one row would snatch the
    // bracket away the moment it was created, before the user had put the second row in it.
    private void TidyAfterRemoval(ConditionGroupViewModel group)
    {
        if (group.Children.Count == 0)
        {
            Detach(group);
            RaiseChanged();
            Parent?.TidyAfterRemoval(this);
        }
        else if (group.Children.Count == 1 && !group.Negate)
        {
            Absorb(group);
        }
    }

    private void Absorb(ConditionGroupViewModel group)
    {
        int index = Children.IndexOf(group);
        if (index < 0) return;

        List<ConditionNodeViewModel> orphans = group.Children.ToList();
        foreach (ConditionNodeViewModel child in orphans) group.Detach(child);
        Detach(group);

        for (int i = 0; i < orphans.Count; i++) Attach(orphans[i], index + i);
        RaiseChanged();
    }
}
