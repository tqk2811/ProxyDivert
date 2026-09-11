using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDivert.Core.Routing.Models.Conditions;

namespace ProxyDivert.Wpf.ViewModels.Conditions;

/// <summary>One condition row: what to look at, how to compare it, and what to compare it with.</summary>
/// <remarks>
/// The subject combo is what merges the two fixed slots the old filter had. It also decides the
/// contents of the comparison combo next to it; both lists, and the class a row becomes on Save,
/// come from the <see cref="ConditionSubject"/>, so this row works for a kind of condition it has
/// never heard of.
/// </remarks>
public sealed partial class ConditionLeafViewModel : ConditionNodeViewModel
{
    public IReadOnlyList<ConditionSubject> Subjects => ConditionSubject.All;

    [ObservableProperty]
    private string _pattern = string.Empty;

    /// <summary>Set on a row the user just added, so the view can put the caret in its value box.</summary>
    [ObservableProperty]
    private bool _isNew;

    // Where the caret goes is the window's business, not the filter's; the view clears this once
    // it has moved it, which would otherwise read as an edit the user never made.
    protected override bool IsPartOfTheFilter(string? propertyName)
        => propertyName != nameof(IsNew) && base.IsPartOfTheFilter(propertyName);

    public ConditionLeafViewModel()
    {
    }

    public ConditionLeafViewModel(LeafCondition model)
    {
        Negate = model.Negate;
        _subject = model.Subject;
        _matcher = model.MatcherValue;
        Pattern = model.Pattern;
    }

    private ConditionSubject _subject = ConditionSubject.All[0];

    /// <summary>What this row looks at. Changing it resets the comparison to that subject's default.</summary>
    /// <remarks>Nulls are refused for the same reason as on <see cref="Matcher"/>.</remarks>
    public ConditionSubject? Subject
    {
        get => _subject;
        set
        {
            if (value is null || !SetProperty(ref _subject, value)) return;

            OnPropertyChanged(nameof(Matchers));
            Matcher = value.DefaultMatcher;
        }
    }

    /// <summary>The comparisons offered for the current subject.</summary>
    public IReadOnlyList<object> Matchers => _subject.Matchers;

    private object _matcher = ConditionSubject.All[0].DefaultMatcher;

    /// <summary>The chosen comparison, as a value of whichever enum the subject compares with.</summary>
    /// <remarks>
    /// Nulls are refused rather than stored. Swapping the subject swaps the whole list out from
    /// under the combo box, and a ComboBox whose SelectedItem is no longer in its ItemsSource
    /// writes null back through the binding on its way past — accepting that would blank the row
    /// for a moment and, worse, leave it blank if the assignment above ever stopped happening.
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

    public override ProcessCondition ToModel() => _subject.Create(_matcher, Pattern, Negate);
}
