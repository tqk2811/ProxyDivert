using System;
using System.Threading;
using ProxyDivert.Core.Processes;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The budget a filter evaluation shares between its regular expressions.
//
// These are here because of a test that failed once in a hundred runs and only ever while the
// machine was busy building something else: a filter whose pattern was "^chr.*" stopped matching
// "chrome.exe". The budget was a wall-clock deadline stamped at the start of the evaluation, so
// time the thread spent not running counted against it, and on a loaded machine a pattern that
// takes microseconds could arrive to find its hundred milliseconds already gone.
public class RegexBudgetTests
{
    [Fact]
    public void Waiting_does_not_use_the_budget_up()
    {
        var budget = new RegexBudget(TimeSpan.FromMilliseconds(100));

        // Longer than the whole budget, spent doing anything other than matching.
        Thread.Sleep(150);

        Assert.Equal(TimeSpan.FromMilliseconds(100), budget.Remaining);
    }

    [Fact]
    public void Matching_uses_the_budget_up()
    {
        var budget = new RegexBudget(TimeSpan.FromMilliseconds(100));

        budget.Spend(TimeSpan.FromMilliseconds(60));
        Assert.Equal(TimeSpan.FromMilliseconds(40), budget.Remaining);

        budget.Spend(TimeSpan.FromMilliseconds(60));
        Assert.Equal(TimeSpan.Zero, budget.Remaining);
    }

    // Every pattern in one filter draws on the same budget, which is what stops a tree of twenty
    // expressions from costing twenty times as much on every process on every scan.
    [Fact]
    public void The_budget_is_shared_and_never_goes_negative()
    {
        var budget = new RegexBudget(TimeSpan.FromMilliseconds(100));

        for (int i = 0; i < 20; i++) budget.Spend(TimeSpan.FromMilliseconds(30));

        Assert.Equal(TimeSpan.Zero, budget.Remaining);
    }

    [Fact]
    public void A_negative_measurement_is_ignored()
    {
        var budget = new RegexBudget(TimeSpan.FromMilliseconds(100));

        budget.Spend(TimeSpan.FromMilliseconds(-50));

        Assert.Equal(TimeSpan.FromMilliseconds(100), budget.Remaining);
    }
}
