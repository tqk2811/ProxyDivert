using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Wpf.ViewModels.Conditions;

/// <summary>One row of the filter editor: a group, or a single condition.</summary>
/// <remarks>
/// The editor works on view models rather than on the model tree directly, for two reasons. It
/// carries things the saved filter must not — which rows are ticked for grouping — and it lets
/// Cancel throw the whole thing away, because the model is only rebuilt when the user presses Save.
/// </remarks>
public abstract partial class ConditionNodeViewModel : ObservableObject
{
    protected ConditionNodeViewModel()
    {
        // Almost anything changing on a row means the sentence at the top of the window is out of
        // date, and one subscription here beats remembering to raise it from every setter. Only
        // "almost": a row also carries state the filter itself never sees, and reporting that as
        // an edit meant opening a filter to look at it, ticking a row and unticking it again, and
        // then being asked whether to save changes that were never made.
        PropertyChanged += (_, e) =>
        {
            if (IsPartOfTheFilter(e.PropertyName)) RaiseChanged();
        };
    }

    /// <summary>
    /// Whether changing this property changes the filter. False for the row's own interface
    /// state — anything <see cref="ToModel"/> does not read.
    /// </summary>
    protected virtual bool IsPartOfTheFilter(string? propertyName)
        => propertyName != nameof(IsSelected);

    /// <summary>Raised for any edit anywhere at or below this node.</summary>
    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();

    /// <summary>The group this row sits in. Null only for the root group.</summary>
    public ConditionGroupViewModel? Parent { get; internal set; }

    /// <summary>Turns this row, or this whole group, into "NOT".</summary>
    [ObservableProperty]
    private bool _negate;

    /// <summary>Ticked for "put the ticked rows into a group together". Never saved.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void ToggleNegate() => Negate = !Negate;

    [RelayCommand]
    private void Remove() => Parent?.RemoveChild(this);

    [RelayCommand]
    private void MoveUp() => Parent?.Move(this, -1);

    [RelayCommand]
    private void MoveDown() => Parent?.Move(this, +1);

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
