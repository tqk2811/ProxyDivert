using System.Collections.Generic;
using System.Linq;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.Localization;

namespace ProxyDivert.Wpf.Helpers;

/// <summary>Reads a condition tree back as one sentence, in the language the window is in.</summary>
/// <remarks>
/// The editor never asks anyone to TYPE an expression — but it must always show one. A tree of
/// indented rows says how the filter is built; it does not say what the filter means, and a person
/// who has just dragged a row into a bracket needs to see, in words, what they now have. It is
/// also what the filter list shows in place of the two columns it used to have.
///
/// Rows with nothing typed in them are left out entirely, so a filter being written does not read
/// as a filter with holes in it.
/// </remarks>
public static class ConditionTextBuilder
{
    public static string Describe(ProcessCondition? condition)
        => Build(condition, bracketed: false) ?? Loc.S("Str.Cond.Nothing");

    private static string? Build(ProcessCondition? condition, bool bracketed)
    {
        switch (condition)
        {
            case null:
                return null;

            case LeafCondition leaf:
            {
                if (string.IsNullOrWhiteSpace(leaf.Pattern)) return null;

                string text = Loc.F(
                    "Str.Cond.LeafFormat",
                    LocalizationManager.EnumText(SubjectOf(leaf)),
                    LocalizationManager.EnumText(MatcherOf(leaf)),
                    leaf.Pattern.Trim());

                return leaf.Negate ? Loc.F("Str.Cond.NotFormat", text) : text;
            }

            case ConditionGroup group:
            {
                List<string> parts = group.Children
                    .Select(child => Build(child, bracketed: true))
                    .Where(part => part != null)
                    .Select(part => part!)
                    .ToList();

                if (parts.Count == 0) return null;

                string joined = string.Join(
                    Loc.S(group.Operator == ConditionOperator.All ? "Str.Cond.AndJoin" : "Str.Cond.OrJoin"),
                    parts);

                // Brackets only where they change the reading: around a negated group always,
                // around a joined group inside another one, and never around the whole sentence.
                if (group.Negate) return Loc.F("Str.Cond.NotFormat", "(" + joined + ")");
                return parts.Count > 1 && bracketed ? "(" + joined + ")" : joined;
            }

            default:
                return null;
        }
    }

    private static ConditionSubject SubjectOf(LeafCondition leaf)
        => leaf is CommandLineCondition ? ConditionSubject.CommandLine : ConditionSubject.ProcessName;

    private static object MatcherOf(LeafCondition leaf) => leaf switch
    {
        CommandLineCondition arguments => arguments.Matcher,
        ProcessNameCondition process => process.Matcher,
        _ => string.Empty,
    };
}
