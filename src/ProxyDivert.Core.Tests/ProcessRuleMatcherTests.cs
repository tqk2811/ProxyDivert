using System;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Routing.Enums;
using ProxyDivert.Core.Routing.Models;
using Xunit;

namespace ProxyDivert.Core.Tests;

public class ProcessRuleMatcherTests
{
    private static ProcessRule Rule(ProcessMatcherType matcher, string pattern) => new ProcessRule
    {
        Id = Guid.NewGuid(),
        Matcher = matcher,
        Pattern = pattern,
        PolicyId = Guid.NewGuid(),
    };

    [Theory]
    [InlineData("chrome", "chrome", true)]
    [InlineData("chrome.exe", "chrome", true)]      // extension on the rule side
    [InlineData("chrome", "chrome.exe", true)]      // extension on the process side
    [InlineData("CHROME", "chrome", true)]
    [InlineData("chrome", "chromium", false)]
    public void ExeName_ignores_the_extension_and_case(string pattern, string processName, bool expected)
        => Assert.Equal(expected, ProcessRuleMatcher.IsMatch(Rule(ProcessMatcherType.ExeName, pattern), processName, null));

    [Fact]
    public void ExeName_also_matches_against_the_file_name_of_the_path()
        => Assert.True(ProcessRuleMatcher.IsMatch(
            Rule(ProcessMatcherType.ExeName, "client.exe"), "someProcess", @"C:\Games\Foo\client.exe"));

    [Theory]
    [InlineData(@"C:\Games\Foo\client.exe", @"C:\Games\Foo\client.exe", true)]
    [InlineData(@"c:\games\foo\client.exe", @"C:\Games\Foo\client.exe", true)]
    [InlineData(@"C:/Games/Foo/client.exe", @"C:\Games\Foo\client.exe", true)]  // pasted with slashes
    [InlineData(@"C:\Games\Bar\client.exe", @"C:\Games\Foo\client.exe", false)]
    public void FullPath_normalises_separators_and_case(string pattern, string path, bool expected)
        => Assert.Equal(expected, ProcessRuleMatcher.IsMatch(Rule(ProcessMatcherType.FullPath, pattern), "client", path));

    [Theory]
    [InlineData(@"C:\Games\*\client.exe", @"C:\Games\Foo\client.exe", true)]
    [InlineData(@"C:\Games\*\client.exe", @"D:\Games\Foo\client.exe", false)]
    [InlineData(@"*\client.exe", @"C:\Games\Foo\client.exe", true)]
    public void Wildcard_matches_the_full_path(string pattern, string path, bool expected)
        => Assert.Equal(expected, ProcessRuleMatcher.IsMatch(Rule(ProcessMatcherType.Wildcard, pattern), "client", path));

    [Fact]
    public void Path_matchers_do_not_match_a_process_whose_path_is_unreadable()
    {
        // System processes and (from a 32-bit host) 64-bit processes report no path.
        Assert.False(ProcessRuleMatcher.IsMatch(Rule(ProcessMatcherType.FullPath, @"C:\x\y.exe"), "y", null));
        Assert.False(ProcessRuleMatcher.IsMatch(Rule(ProcessMatcherType.Wildcard, @"C:\x\*.exe"), "y", null));
    }

    [Fact]
    public void Disabled_rule_never_matches()
    {
        ProcessRule rule = Rule(ProcessMatcherType.ExeName, "chrome");
        rule.IsEnabled = false;

        Assert.False(ProcessRuleMatcher.IsMatch(rule, "chrome", null));
    }

    // ==== the second condition: the command line ====

    private static ProcessRule WithArgument(ArgumentMatcherType matcher, string? pattern)
    {
        ProcessRule rule = Rule(ProcessMatcherType.ExeName, "java");
        rule.ArgumentMatcher = matcher;
        rule.ArgumentPattern = pattern;
        return rule;
    }

    // A rule written before arguments existed says nothing about them, and must keep matching
    // exactly what it used to.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_argument_pattern_is_not_a_condition(string? pattern)
    {
        Assert.True(ProcessRuleMatcher.IsMatch(WithArgument(ArgumentMatcherType.Contains, pattern), "java", null, null));
        Assert.False(ProcessRuleMatcher.NeedsCommandLine(WithArgument(ArgumentMatcherType.Contains, pattern)));
    }

    [Theory]
    [InlineData("minecraft", @"C:\jre\java.exe -Dminecraft.launcher", true)]
    [InlineData("MINECRAFT", @"C:\jre\java.exe -Dminecraft.launcher", true)]  // case-insensitive
    [InlineData("eclipse", @"C:\jre\java.exe -Dminecraft.launcher", false)]
    public void Contains_looks_anywhere_in_the_command_line(string pattern, string commandLine, bool expected)
        => Assert.Equal(expected, ProcessRuleMatcher.IsMatch(
            WithArgument(ArgumentMatcherType.Contains, pattern), "java", null, commandLine));

    [Theory]
    [InlineData("*-Dminecraft*", @"C:\jre\java.exe -Dminecraft.launcher", true)]
    [InlineData("*-Declipse*", @"C:\jre\java.exe -Dminecraft.launcher", false)]
    [InlineData("*java.exe*", @"C:\jre\java.exe -Dminecraft.launcher", true)]
    public void Wildcard_spans_the_whole_command_line(string pattern, string commandLine, bool expected)
        => Assert.Equal(expected, ProcessRuleMatcher.IsMatch(
            WithArgument(ArgumentMatcherType.Wildcard, pattern), "java", null, commandLine));

    [Theory]
    [InlineData("java.exe -jar app.jar", "java.exe -jar app.jar", true)]
    [InlineData("java.exe -jar app.jar", "  java.exe -jar app.jar  ", true)]   // padding is not meant
    [InlineData("java.exe -jar app.jar", "java.exe -jar other.jar", false)]
    public void Exact_compares_the_whole_command_line(string pattern, string commandLine, bool expected)
        => Assert.Equal(expected, ProcessRuleMatcher.IsMatch(
            WithArgument(ArgumentMatcherType.Exact, pattern), "java", null, commandLine));


    // A rule that asks about the command line has said the process is not enough on its own, so a
    // command line it cannot read is a "no". The other direction would redirect the very processes
    // the rule was written to leave alone.
    [Fact]
    public void An_unreadable_command_line_does_not_match_a_rule_that_asks_about_one()
    {
        Assert.False(ProcessRuleMatcher.IsMatch(
            WithArgument(ArgumentMatcherType.Contains, "minecraft"), "java", null, commandLine: null));
        Assert.True(ProcessRuleMatcher.NeedsCommandLine(
            WithArgument(ArgumentMatcherType.Contains, "minecraft")));
    }

    // Both halves are one condition: the process must pass the name test as well.
    [Fact]
    public void The_argument_condition_is_ANDed_with_the_process_condition()
    {
        ProcessRule rule = WithArgument(ArgumentMatcherType.Contains, "minecraft");

        Assert.True(ProcessRuleMatcher.IsMatch(rule, "java", null, "java.exe -Dminecraft"));
        Assert.False(ProcessRuleMatcher.IsMatch(rule, "python", null, "python.exe -Dminecraft"));
        Assert.False(ProcessRuleMatcher.IsMatch(rule, "java", null, "java.exe -Declipse"));
    }

    // A disabled rule needs nothing looked up for it.
    [Fact]
    public void A_disabled_rule_does_not_make_the_engine_read_command_lines()
    {
        ProcessRule rule = WithArgument(ArgumentMatcherType.Contains, "minecraft");
        rule.IsEnabled = false;

        Assert.False(ProcessRuleMatcher.NeedsCommandLine(rule));
    }
}
