using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Wpf.ViewModels;

/// <summary>
/// One row of the process-filter grid.
/// </summary>
/// <remarks>
/// A filter is a name, a condition tree and the policies to apply, and only the first and the last
/// of those fit in a cell — the tree is edited in a window of its own. That is what made the row
/// need an object: a <see cref="ProcessRule"/> raises nothing, so an edit made behind the dialog
/// left the cell showing the sentence the tree used to read as, and the way round it was to take
/// the row out of the collection and put it straight back.
/// </remarks>
public sealed partial class ProcessFilterRowViewModel : ObservableObject
{
    public ProcessFilterRowViewModel(ProcessRule model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>The filter itself, for the commands that work on the configuration.</summary>
    public ProcessRule Model { get; }

    public Guid Id => Model.Id;

    public bool IsEnabled
    {
        get => Model.IsEnabled;
        set
        {
            if (value == Model.IsEnabled) return;
            Model.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// What the user calls this filter. It is the only part of it that reads at a glance, the
    /// condition tree being one click away, so a blank one leaves the row unidentifiable.
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

    public bool IncludeChildren
    {
        get => Model.IncludeChildren;
        set
        {
            if (value == Model.IncludeChildren) return;
            Model.IncludeChildren = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The condition tree, shown in the cell as the sentence it reads as.</summary>
    public ProcessCondition? Condition => Model.Condition;

    /// <summary>The policies this filter applies, in the order their rules are tried.</summary>
    public IReadOnlyList<Guid> PolicyIds => Model.PolicyIds;

    /// <summary>
    /// Re-reads every cell. For the two the dialog changes behind the grid's back.
    /// </summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
