using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Wpf.ViewModels.Conditions;

/// <summary>One row of the filter editor: a group, or a single condition.</summary>
/// <remarks>
/// The editor works on view models rather than on the model tree directly, for two reasons. It
/// carries things the saved filter must not — which rows are ticked for grouping, how each row
/// answered the last time the filter was tried against a running process — and it lets Cancel
/// throw the whole thing away, because the model is only rebuilt when the user presses Save.
/// </remarks>
public abstract partial class ConditionNodeViewModel : ObservableObject
{
    protected ConditionNodeViewModel()
    {
        // Anything at all changing on a row means the sentence at the top of the window is out of
        // date. One subscription here beats remembering to raise it from every setter.
        PropertyChanged += (_, _) => RaiseChanged();
    }

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

    /// <summary>
    /// How this row answered the last time the filter was tried against a running process; null
    /// before anything has been tried. Never saved.
    /// </summary>
    [ObservableProperty]
    private ConditionResult? _testResult;

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

    /// <summary>Clears the colouring left by a previous try, on this row and everything under it.</summary>
    public virtual void ClearTestResult() => TestResult = null;

    public static ConditionNodeViewModel FromModel(ProcessCondition condition) => condition switch
    {
        ConditionGroup group => new ConditionGroupViewModel(group),
        ProcessNameCondition name => new ConditionLeafViewModel(name),
        CommandLineCondition arguments => new ConditionLeafViewModel(arguments),
        _ => throw new ArgumentOutOfRangeException(
            nameof(condition), condition?.GetType(), "Unknown condition type"),
    };
}
