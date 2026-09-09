using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Bindings.Enums;
using ProxyDivert.Wpf.Bindings.Interfaces;

namespace ProxyDivert.Wpf.ViewModels.Conditions;

/// <summary>One row of the filter editor: a group, or a single condition.</summary>
/// <remarks>
/// The editor works on view models rather than on the model tree directly, for two reasons. It
/// carries things the saved filter must not — which row the caret belongs in — and it lets Cancel
/// throw the whole thing away, because the model is only rebuilt when the user presses Save.
/// </remarks>
public abstract partial class ConditionNodeViewModel : ObservableObject, IDragRow
{
    protected ConditionNodeViewModel()
    {
        // Almost anything changing on a row means the sentence at the top of the window is out of
        // date, and one subscription here beats remembering to raise it from every setter. Only
        // "almost": a row also carries state the filter itself never sees, and reporting that as
        // an edit meant opening a filter to look at it and then being asked whether to save
        // changes that were never made.
        PropertyChanged += (_, e) =>
        {
            if (IsPartOfTheFilter(e.PropertyName)) RaiseChanged();
        };
    }

    /// <summary>
    /// Whether changing this property changes the filter. False for the row's own interface
    /// state — anything <see cref="ToModel"/> does not read.
    /// </summary>
    protected virtual bool IsPartOfTheFilter(string? propertyName) => true;

    /// <summary>Raised for any edit anywhere at or below this node.</summary>
    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();

    /// <summary>The group this row sits in. Null only for the root group.</summary>
    public ConditionGroupViewModel? Parent { get; internal set; }

    /// <summary>Turns this row, or this whole group, into "NOT".</summary>
    [ObservableProperty]
    private bool _negate;

    [RelayCommand]
    private void ToggleNegate() => Negate = !Negate;

    [RelayCommand]
    private void Remove() => Parent?.RemoveChild(this);

    [RelayCommand]
    private void MoveUp() => Parent?.Move(this, -1);

    [RelayCommand]
    private void MoveDown() => Parent?.Move(this, +1);

    // ==== being dropped on ====

    /// <summary>Where a row dropped on this one would land: which group takes it, and at what index.</summary>
    /// <remarks>
    /// Null for a drop that would mean nothing, which is also how the view knows to draw no line.
    /// </remarks>
    protected virtual (ConditionGroupViewModel Group, int Index)? LandingFor(DropWhere where)
    {
        // The outermost group is the tree itself: there is no "beside" it to drop into.
        if (Parent is null) return null;

        int index = Parent.Children.IndexOf(this);
        return where switch
        {
            DropWhere.Before => (Parent, index),
            DropWhere.After => (Parent, index + 1),
            _ => null, // only a group has an inside
        };
    }

    public bool CanAccept(object source, DropWhere where) => Landing(source, where) != null;

    public void Accept(object source, DropWhere where)
    {
        if (source is not ConditionNodeViewModel dragged) return;
        if (Landing(source, where) is not { } landing) return;

        landing.Group.MoveInto(dragged, landing.Index);
    }

    private (ConditionGroupViewModel Group, int Index)? Landing(object source, DropWhere where)
    {
        if (source is not ConditionNodeViewModel dragged) return null;

        // A row dropped on itself, and the outermost group, which is not going anywhere.
        if (ReferenceEquals(dragged, this) || dragged.Parent is null) return null;

        if (LandingFor(where) is not { } landing) return null;

        // A bracket cannot be dropped inside itself: the branch being cut out is the branch it
        // would be pasted into, and what is left is a loop hanging off nothing.
        return landing.Group.IsAtOrBelow(dragged) ? null : landing;
    }

    /// <summary>True if this row is <paramref name="node"/>, or sits somewhere inside it.</summary>
    internal bool IsAtOrBelow(ConditionNodeViewModel node)
    {
        for (ConditionNodeViewModel? at = this; at != null; at = at.Parent)
            if (ReferenceEquals(at, node)) return true;

        return false;
    }

    /// <summary>Rebuilds the saved form of this row.</summary>
    public abstract ProcessCondition ToModel();

    public static ConditionNodeViewModel FromModel(ProcessCondition condition) => condition switch
    {
        ConditionGroup group => new ConditionGroupViewModel(group),
        ProcessNameCondition name => new ConditionLeafViewModel(name),
        CommandLineCondition arguments => new ConditionLeafViewModel(arguments),
        _ => throw new ArgumentOutOfRangeException(
            nameof(condition), condition?.GetType(), "Unknown condition type"),
    };
}
