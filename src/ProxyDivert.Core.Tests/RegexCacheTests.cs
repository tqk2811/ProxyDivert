using System;
using System.Text.RegularExpressions;
using ProxyDivert.Core.Processes;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The expressions a process filter is built from, kept rather than rebuilt.
//
// One scan of 301 processes against twenty conditions took 26.41ms before and 2.91ms after. The
// number that mattered is Regex.CacheSize, which is fifteen: past it every call missed the
// framework's own cache and parsed the pattern again, and a filter asks about both the process
// name and its path, so an ordinary ruleset gets there easily.
public class RegexCacheTests
{
    [Fact]
    public void The_same_pattern_is_built_once_and_kept()
    {
        var cache = new RegexCache();

        Regex? first = cache.Get("^chr.*");
        Regex? again = cache.Get("^chr.*");

        Assert.NotNull(first);
        Assert.Same(first, again);
    }

    // Not for speed alone: it is the difference between a pattern that has to be re-parsed on
    // every process and one that does not.
    [Fact]
    public void Patterns_are_compiled()
    {
        var cache = new RegexCache();

        Regex? regex = cache.Get("^chr.*");

        Assert.NotNull(regex);
        Assert.True(regex!.Options.HasFlag(RegexOptions.Compiled));
        Assert.Equal(RegexCache.MatchTimeout, regex.MatchTimeout);
    }

    // A pattern from a text box that will not compile is a decision to remember, not one to make
    // again for every process on the machine.
    [Fact]
    public void A_pattern_that_will_not_compile_is_remembered_as_such()
    {
        var cache = new RegexCache();

        Assert.Null(cache.Get("(unclosed"));
        Assert.Null(cache.Get("(unclosed"));
        Assert.Equal(1, cache.Count);
    }

    // The wildcard the user wrote is the key, so the translation into a regular expression happens
    // once as well rather than building the same string again for every process.
    [Fact]
    public void A_wildcard_is_kept_under_what_the_user_wrote()
    {
        var cache = new RegexCache();

        Regex? first = cache.GetWildcard(@"C:\Games\*.exe");
        Regex? again = cache.GetWildcard(@"C:\Games\*.exe");

        Assert.Same(first, again);
        Assert.Equal(1, cache.Count);
        Assert.True(first!.IsMatch(@"C:\Games\client.exe"));
        Assert.False(first.IsMatch(@"C:\Games\client.dll"));
    }

    // A wildcard and a regular expression that happen to be the same string are different
    // questions, and must not answer each other.
    [Fact]
    public void A_wildcard_and_a_regex_that_read_the_same_do_not_share_an_entry()
    {
        var cache = new RegexCache();

        Regex? asRegex = cache.Get("a.b");
        Regex? asWildcard = cache.GetWildcard("a.b");

        Assert.NotSame(asRegex, asWildcard);
        // "." is a literal in a wildcard and "any character" in an expression.
        Assert.True(asRegex!.IsMatch("axb"));
        Assert.False(asWildcard!.IsMatch("axb"));
        Assert.True(asWildcard.IsMatch("a.b"));
    }

    // Patterns are the user's rules, so the live set is small - but every keystroke in a pattern
    // box is a new pattern while it is being typed, and those must not pile up for ever.
    [Fact]
    public void Editing_does_not_let_the_cache_grow_without_end()
    {
        var cache = new RegexCache();

        for (int i = 0; i < RegexCache.Capacity * 3; i++) cache.Get($"^pattern{i}.*");

        Assert.True(
            cache.Count <= RegexCache.Capacity,
            $"the cache holds {cache.Count} entries, past its cap of {RegexCache.Capacity}");
    }
}
