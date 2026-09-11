using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using ProxyDivert.Core.Configuration;
using ProxyDivert.Core.Configuration.Models;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using ProxyDivert.Core.Routing.Models.Conditions;
using ProxyDivert.Wpf.ViewModels.Conditions;
using Xunit;

namespace ProxyDivert.Core.Tests;

// A condition row's subject replaced an enum that three places each turned into a class and back
// with a switch of their own. These hold the two lists that still have to agree — the file format
// and the editor's — and the promises the editor now relies on instead of those switches.
public class ConditionSubjectTests
{
    // The file can hold every leaf type in ProcessCondition's attributes; the editor can only offer
    // what is in ConditionSubject.All. A type in the first and not the second loads from a file and
    // can never be picked, and the other way round is a row that cannot be saved.
    [Fact]
    public void Every_leaf_the_file_can_hold_is_offered_by_the_editor()
    {
        Type[] inTheFile = typeof(ProcessCondition)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(attribute => attribute.DerivedType)
            .Where(type => typeof(LeafCondition).IsAssignableFrom(type))
            .OrderBy(type => type.Name)
            .ToArray();

        Type[] inTheEditor = ConditionSubject.All
            .Select(subject => subject.Create(subject.DefaultMatcher, string.Empty, negate: false).GetType())
            .OrderBy(type => type.Name)
            .ToArray();

        Assert.Equal(inTheFile, inTheEditor);
        Assert.Equal(ConditionSubject.All.Count, ConditionSubject.All.Select(s => s.Name).Distinct().Count());
    }

    public static TheoryData<string> Subjects()
    {
        var data = new TheoryData<string>();
        foreach (ConditionSubject subject in ConditionSubject.All) data.Add(subject.Name);
        return data;
    }

    private static ConditionSubject Named(string name) => ConditionSubject.All.Single(s => s.Name == name);

    // Every comparison but the default, so a copy or a round trip that fell back to the default
    // somewhere would show.
    private static object NotTheDefault(ConditionSubject subject)
        => subject.Matchers.First(matcher => !matcher.Equals(subject.DefaultMatcher));

    [Theory]
    [MemberData(nameof(Subjects))]
    public void A_condition_says_which_subject_made_it(string name)
    {
        ConditionSubject subject = Named(name);
        object matcher = NotTheDefault(subject);

        LeafCondition condition = subject.Create(matcher, "pattern", negate: true);

        Assert.Same(subject, condition.Subject);
        Assert.Equal(matcher, condition.MatcherValue);
        Assert.Equal("pattern", condition.Pattern);
        Assert.True(condition.Negate);
        Assert.True(subject.Offers(condition.MatcherValue));
    }

    // Clone is written once, on LeafCondition, through the subject. What it must not lose is
    // everything a row has.
    [Theory]
    [MemberData(nameof(Subjects))]
    public void A_copy_keeps_everything_a_row_has(string name)
    {
        ConditionSubject subject = Named(name);
        LeafCondition original = subject.Create(NotTheDefault(subject), "pattern", negate: true);

        var copy = Assert.IsAssignableFrom<LeafCondition>(original.Clone());

        Assert.NotSame(original, copy);
        Assert.Equal(original.GetType(), copy.GetType());
        Assert.Equal(original.MatcherValue, copy.MatcherValue);
        Assert.Equal(original.Pattern, copy.Pattern);
        Assert.Equal(original.Negate, copy.Negate);
    }

    // What the combo box writes back while its list is being swapped for another subject's.
    [Fact]
    public void A_comparison_from_another_subject_becomes_the_default()
    {
        var condition = (CommandLineCondition)ConditionSubject.CommandLine.Create(
            ProcessMatcherType.FullPath, "x", negate: false);

        Assert.Equal(ArgumentMatcherType.Contains, condition.Matcher);
        Assert.False(ConditionSubject.CommandLine.Offers(ProcessMatcherType.FullPath));
        Assert.False(ConditionSubject.CommandLine.Offers(null));
    }

    // Subject and MatcherValue are how the rest of the program reads a row; the file already says
    // both, through the node's type and its own Matcher. Written out as well, they would be a second
    // copy of the same fact — and "subject" would sit in every leaf of every user's config.
    [Fact]
    public void What_a_row_looks_at_is_not_written_to_the_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ProxyDivertTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "config.json");
        try
        {
            AppConfig config = AppConfig.CreateDefault();
            config.ProcessRules.Add(new ProcessRule
            {
                Id = Guid.NewGuid(),
                Name = "java",
                PolicyIds = { config.Policies[0].Id },
                Condition = new ConditionGroup
                {
                    Children =
                    {
                        ConditionSubject.ProcessName.Create(ProcessMatcherType.ExeName, "java", negate: false),
                        ConditionSubject.CommandLine.Create(ArgumentMatcherType.Regex, "minecraft", negate: true),
                    },
                },
            });

            new ConfigStore(path).Save(config);
            string json = File.ReadAllText(path);

            Assert.DoesNotContain("\"subject\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"matcherValue\"", json, StringComparison.OrdinalIgnoreCase);

            ProcessRule loaded = Assert.Single(new ConfigStore(path).Load().ProcessRules);
            var group = Assert.IsType<ConditionGroup>(loaded.Condition);
            var arguments = Assert.IsType<CommandLineCondition>(group.Children[1]);
            Assert.Same(ConditionSubject.CommandLine, arguments.Subject);
            Assert.Equal(ArgumentMatcherType.Regex, arguments.Matcher);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // ==== the editor row, which now knows subjects only through this type ====

    [Fact]
    public void Switching_a_row_to_another_subject_offers_that_subject_s_comparisons()
    {
        var row = new ConditionLeafViewModel(
            (LeafCondition)ConditionSubject.ProcessName.Create(ProcessMatcherType.FullPath, @"C:\x.exe", negate: false));

        row.Subject = ConditionSubject.CommandLine;

        Assert.Equal(ConditionSubject.CommandLine.Matchers, row.Matchers);
        Assert.Equal(ArgumentMatcherType.Contains, row.Matcher);

        var saved = Assert.IsType<CommandLineCondition>(row.ToModel());
        Assert.Equal(ArgumentMatcherType.Contains, saved.Matcher);
        Assert.Equal(@"C:\x.exe", saved.Pattern);
    }

    // A combo box writes null through its binding when its selection drops out of its list. Taken
    // at its word, the row would have no subject and Save would throw.
    [Fact]
    public void A_row_refuses_to_have_no_subject()
    {
        var row = new ConditionLeafViewModel();

        row.Subject = null;

        Assert.Same(ConditionSubject.All[0], row.Subject);
        Assert.IsAssignableFrom<LeafCondition>(row.ToModel());
    }

    [Theory]
    [MemberData(nameof(Subjects))]
    public void A_row_opened_on_a_saved_condition_saves_it_back_unchanged(string name)
    {
        ConditionSubject subject = Named(name);
        LeafCondition original = subject.Create(NotTheDefault(subject), "pattern", negate: true);

        var saved = Assert.IsAssignableFrom<LeafCondition>(new ConditionLeafViewModel(original).ToModel());

        Assert.Same(subject, saved.Subject);
        Assert.Equal(original.MatcherValue, saved.MatcherValue);
        Assert.Equal("pattern", saved.Pattern);
        Assert.True(saved.Negate);
    }
}
