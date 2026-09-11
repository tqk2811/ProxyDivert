using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ProxyDivert.Core.Processes;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>
/// One evaluation of a condition tree against one process: what every condition in it is asked
/// about, and the time they may spend on regular expressions between them.
/// </summary>
/// <remarks>
/// One instance per evaluation, used on one thread and then dropped — the budget and the depth
/// both belong to a single walk of the tree, and sharing either across two would charge one
/// process for another's patterns. It is a class rather than a struct so there is no default value
/// to hand over by accident: a context with no budget in it would throw from the middle of a scan.
/// </remarks>
public sealed class ConditionContext
{
    // Patterns come from a text box and run against every process on the machine, every scan. An
    // expression that backtracks catastrophically would hang the watcher, so it gets a budget
    // rather than the benefit of the doubt.
    //
    // The budget covers the whole filter, not one pattern: a tree of twenty regexes each allowed
    // 100ms would be two seconds per process per scan. What it counts is time spent matching —
    // see RegexBudget for why that distinction is the whole point.
    private static readonly TimeSpan RegexBudgetTotal = TimeSpan.FromMilliseconds(100);

    private readonly RegexBudget _budget = new RegexBudget(RegexBudgetTotal);

    // How many nodes are open above the one being asked. See ProcessCondition.MaxDepth.
    private int _depth;

    public ConditionContext(string processName, string? executablePath, string? commandLine)
    {
        ProcessName = processName ?? string.Empty;
        ExecutablePath = executablePath;
        CommandLine = commandLine;
    }

    /// <summary>The executable name. Always readable.</summary>
    public string ProcessName { get; }

    /// <summary>
    /// The full path, or null when it cannot be read — system processes, and 64-bit processes seen
    /// from a 32-bit host.
    /// </summary>
    public string? ExecutablePath { get; }

    /// <summary>The whole command line, or null when it cannot be read.</summary>
    public string? CommandLine { get; }

    /// <summary>Steps one level into the tree. False when that would go past the limit.</summary>
    internal bool TryEnter()
    {
        if (_depth > ProcessCondition.MaxDepth) return false;

        _depth++;
        return true;
    }

    internal void Leave() => _depth--;

    internal ConditionResult MatchRegex(string pattern, string text)
        => Run(RegexCache.Shared.Get(pattern), text);

    // "*" and "?" as everyone writes them in a file dialog, turned into the regex they mean —
    // once, by the cache, rather than rebuilt for every process on the machine.
    internal ConditionResult MatchWildcard(string pattern, string text)
        => Run(RegexCache.Shared.GetWildcard(pattern), text);

    // Every pattern here came from a text box, so a typo must not take the watcher down — and must
    // not turn into a confident "no" either, because a NOT in front of it would then claim every
    // process on the machine. A pattern that will not compile, or one that runs past the filter
    // budget, is Unknown: the filter simply does not apply.
    private ConditionResult Run(Regex? regex, string text)
    {
        // A pattern that will not compile. RegexCache has already decided that once and remembered
        // it, so this costs nothing per process.
        if (regex is null) return ConditionResult.Unknown;

        if (_budget.Remaining <= TimeSpan.Zero) return ConditionResult.Unknown;

        // Only the match is timed. Building the expression is not matching, it happens once per
        // distinct pattern rather than once per process, and charging it to the budget would make
        // the first process of a scan behave differently from the rest.
        long started = Stopwatch.GetTimestamp();
        try
        {
            return regex.IsMatch(text) ? ConditionResult.Match : ConditionResult.NoMatch;
        }
        catch (RegexMatchTimeoutException)
        {
            return ConditionResult.Unknown;
        }
        finally
        {
            _budget.Spend(Stopwatch.GetElapsedTime(started));
        }
    }
}
