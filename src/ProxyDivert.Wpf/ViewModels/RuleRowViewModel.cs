using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDivert.Core.Routing.Compiled;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;

namespace ProxyDivert.Wpf.ViewModels;

/// <summary>
/// One row of the rule grid on the Rules tab.
/// </summary>
/// <remarks>
/// A rule is a matcher and a pattern, and the pattern is text until something tries to compile it.
/// A pattern that will not compile matches nothing, on every connection, for as long as it stays in
/// the list — and with Not ticked it claims everything instead. The row looks perfectly ordinary in
/// the grid either way, so the row itself is asked.
///
/// The tab also lists the problems together above the grid, because a filter can name several
/// policies and only one of them is on screen at a time. This is the other half of it: which row.
/// </remarks>
public sealed partial class RuleRowViewModel : ObservableObject
{
    public RuleRowViewModel(RoutingRule model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>The rule itself, for the commands that work on the configuration.</summary>
    public RoutingRule Model { get; }

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

    public HostMatcherType Matcher
    {
        get => Model.Matcher;
        set
        {
            if (value == Model.Matcher) return;
            Model.Matcher = value;
            OnPropertyChanged();
            PatternChanged();
        }
    }

    public string Pattern
    {
        get => Model.Pattern;
        set
        {
            if (value == Model.Pattern) return;
            Model.Pattern = value;
            OnPropertyChanged();
            PatternChanged();
        }
    }

    /// <summary>
    /// Inverts the rule: it claims everything the pattern does NOT match.
    /// </summary>
    /// <remarks>
    /// Which is why a broken pattern is worth saying out loud. Inverted, a pattern that matches
    /// nothing claims every connection the policy sees.
    /// </remarks>
    public bool IsNot
    {
        get => Model.IsNot;
        set
        {
            if (value == Model.IsNot) return;
            Model.IsNot = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Why this pattern can never match, or null when it can. The engine's own words.</summary>
    public string? PatternProblem
    {
        get
        {
            // The same compile the engine does, so what is shown here is what the engine will use
            // rather than a second opinion written to agree with it.
            HostPredicate.Compile(Model.Matcher, Model.Pattern, out string? error);
            return error;
        }
    }

    public bool HasPatternProblem => PatternProblem != null;

    private void PatternChanged()
    {
        OnPropertyChanged(nameof(PatternProblem));
        OnPropertyChanged(nameof(HasPatternProblem));
    }
}
