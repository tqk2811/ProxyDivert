using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Wpf.ViewModels.Conditions;

/// <summary>One condition row: what to look at, how to compare it, and what to compare it with.</summary>
/// <remarks>
/// The subject combo is what merges the two fixed slots the old filter had. It also decides the
/// contents of the comparison combo next to it, because the two lists are genuinely different: a
/// command line is not a path, so "full path" has no meaning there and "contains" is the sensible
/// default rather than "is exactly".
/// </remarks>
public sealed partial class ConditionLeafViewModel : ConditionNodeViewModel
{
    private static readonly Array ProcessMatchers = Enum.GetValues(typeof(ProcessMatcherType));
    private static readonly Array ArgumentMatchers = Enum.GetValues(typeof(ArgumentMatcherType));

    public Array Subjects { get; } = Enum.GetValues(typeof(ConditionSubject));

    [ObservableProperty]
    private ConditionSubject _subject;

    [ObservableProperty]
    private string _pattern = string.Empty;

    /// <summary>Set on a row the user just added, so the view can put the caret in its value box.</summary>
    [ObservableProperty]
    private bool _isNew;

    public ConditionLeafViewModel()
    {
    }

    public ConditionLeafViewModel(ProcessNameCondition model)
    {
        Negate = model.Negate;
        Subject = ConditionSubject.ProcessName;
        _matcher = model.Matcher;
        Pattern = model.Pattern;
    }

    public ConditionLeafViewModel(CommandLineCondition model)
    {
        Negate = model.Negate;
        Subject = ConditionSubject.CommandLine;
        _matcher = model.Matcher;
        Pattern = model.Pattern;
    }

    /// <summary>The comparisons offered for the current subject.</summary>
    public Array Matchers => Subject == ConditionSubject.CommandLine ? ArgumentMatchers : ProcessMatchers;

    private object _matcher = ProcessMatcherType.ExeName;

    /// <summary>The chosen comparison: a ProcessMatcherType or an ArgumentMatcherType.</summary>
    /// <remarks>
    /// Nulls are refused rather than stored. Swapping the subject swaps the whole list out from
    /// under the combo box, and a ComboBox whose SelectedItem is no longer in its ItemsSource
    /// writes null back through the binding on its way past — accepting that would blank the row
    /// for a moment and, worse, leave it blank if the assignment below ever stopped happening.
    /// </remarks>
    public object? Matcher
    {
        get => _matcher;
        set
        {
            if (value is null) return;
            SetProperty(ref _matcher, value);
        }
    }

    partial void OnSubjectChanged(ConditionSubject value)
    {
        OnPropertyChanged(nameof(Matchers));
        Matcher = value == ConditionSubject.CommandLine
            ? ArgumentMatcherType.Contains
            : ProcessMatcherType.ExeName;
    }

    public override ProcessCondition ToModel() => Subject == ConditionSubject.CommandLine
        ? new CommandLineCondition
        {
            Negate = Negate,
            Matcher = _matcher is ArgumentMatcherType argument ? argument : ArgumentMatcherType.Contains,
            Pattern = Pattern ?? string.Empty,
        }
        : new ProcessNameCondition
        {
            Negate = Negate,
            Matcher = _matcher is ProcessMatcherType process ? process : ProcessMatcherType.ExeName,
            Pattern = Pattern ?? string.Empty,
        };
}
