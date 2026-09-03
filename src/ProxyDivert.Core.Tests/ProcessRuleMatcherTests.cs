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
}
