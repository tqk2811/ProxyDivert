using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Bindings.Enums;

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
        // Turning NOT on takes "ungroup" away, and that arrives here as an edit coming up from a row.
        Changed += () => UngroupCommand.NotifyCanExecuteChanged();
    }

    // Whether a bracket may be dissolved is a question about BOTH operators, so changing this one
    // answers it differently for every bracket directly inside it. Their own edits re-ask for
    // them; this one is not theirs.
    partial void OnOperatorChanged(ConditionOperator value)
    {
        foreach (ConditionGroupViewModel child in Children.OfType<ConditionGroupViewModel>())
            child.UngroupCommand.NotifyCanExecuteChanged();
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

    /// <summary>Dissolves this group into its parent.</summary>
    /// <remarks>
    /// Offered only where it cannot change what the filter matches, which rules out two cases.
    ///
    /// A negated group: "NOT (a OR b)" spread over a parent that joins with "and" is a different
    /// filter. And a group that joins its rows differently from the parent it would fall into:
    /// dissolving "x AND (a OR b)" throws the inner operator away and leaves "x AND a AND b",
    /// which matches far less — the rows stay on screen looking exactly as they did, so nothing
    /// tells the user their filter has stopped catching what it used to.
    ///
    /// One row is always safe: a row joins with nothing, so there is no operator to lose.
    ///
    /// Quietly changing what someone wrote is worse than making them take the bracket apart row by
    /// row, which is still available.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanUngroup))]
    private void Ungroup()
    {
        // Asked again here, not only through CanExecute: a command runs when it is invoked, and a
        // button whose enabled state has not caught up yet would invoke it.
        if (!CanUngroup()) return;
        Parent?.Absorb(this);
    }

    private bool CanUngroup()
        => Parent != null
        && !Negate
        && (Children.Count <= 1 || Operator == Parent.Operator);


    // ==== being dropped on ====

    /// <remarks>
    /// A group is the only row with an inside, and the only one a drop can mean two things on.
    /// </remarks>
    protected override (ConditionGroupViewModel Group, int Index)? LandingFor(DropWhere where)
    {
        // The lower half of a group row is the bracket itself, so a row dropped there goes in at
        // the top of it — right where the pointer is.
        if (where == DropWhere.Inside) return (this, 0);

        // Nothing sits beside the outermost group, so the strip under its last row cannot mean
        // "after me". It means the end of the tree, which is the one place it could sensibly be.
        if (IsRoot) return where == DropWhere.After ? (this, Children.Count) : null;

        return base.LandingFor(where);
    }

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

    /// <summary>Takes a row from wherever it is and puts it in this group at <paramref name="index"/>.</summary>
    /// <remarks>
    /// What a drag ends in. Within one group it is a move rather than a remove and an add, so the
    /// row keeps its place in the list rather than being rebuilt at the end of it and jumping.
    /// </remarks>
    public void MoveInto(ConditionNodeViewModel node, int index)
    {
        ConditionGroupViewModel? source = node.Parent;
        if (source is null || IsAtOrBelow(node)) return;

        if (ReferenceEquals(source, this))
        {
            int from = Children.IndexOf(node);
            if (from < 0) return;

            // The gap the row is asked to fill is measured with the row still in the list, and
            // taking it out closes one place ahead of it.
            if (index > from) index--;

            index = Math.Clamp(index, 0, Children.Count - 1);
            if (index == from) return;

            Children.Move(from, index);
            RaiseChanged();
            return;
        }

        source.Detach(node);
        Attach(node, Math.Clamp(index, 0, Children.Count));
        source.PruneIfEmpty();
    }

    // Dragging the last row out of a bracket leaves a bracket around nothing, which means nothing
    // and would be saved as a group with no children.
    //
    // Only emptiness, deliberately. The other half of the rule a deletion applies — one row left,
    // so the bracket is noise — would dissolve a bracket the user is halfway through filling, and
    // could swallow the very bracket the row was just dropped into.
    private void PruneIfEmpty()
    {
        if (Children.Count > 0 || Parent is null) return;

        ConditionGroupViewModel parent = Parent;
        parent.Detach(this);
        parent.RaiseChanged();
        parent.PruneIfEmpty();
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
