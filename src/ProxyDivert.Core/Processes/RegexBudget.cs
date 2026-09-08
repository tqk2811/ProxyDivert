using System;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// How much time one filter evaluation may spend matching regular expressions, counted across every
/// pattern in the tree.
/// </summary>
/// <remarks>
/// Time <b>spent</b>, not time elapsed. The version this replaces stamped a deadline of "now plus a
/// hundred milliseconds" when the evaluation began and compared it against the wall clock, which
/// charged the budget for everything that happened in between — including the thread simply not
/// being scheduled. Measured on this machine: with the cores busy, forty-seven of those hundred
/// milliseconds were gone before the first pattern ran, and no regex work had happened at all. Load
/// the machine a little more and a trivially cheap expression comes back <c>Unknown</c>, which means
/// "cannot tell", so the user's filter quietly stops applying and nothing anywhere says why.
///
/// What the budget is for is a pattern that backtracks catastrophically — that is regex work, and
/// this counts it. Being descheduled is not.
///
/// One instance per evaluation, shared by every pattern in the tree, so a filter with twenty
/// expressions in it stays bounded as a whole rather than twenty times over. Not thread-safe, and
/// does not need to be: an evaluation runs on one thread.
/// </remarks>
internal sealed class RegexBudget
{
    private readonly TimeSpan _total;
    private TimeSpan _spent;

    public RegexBudget(TimeSpan total)
    {
        _total = total > TimeSpan.Zero ? total : TimeSpan.Zero;
    }

    /// <summary>What is left. Zero once the evaluation has used the budget up.</summary>
    public TimeSpan Remaining => _spent < _total ? _total - _spent : TimeSpan.Zero;

    /// <summary>Records time actually spent inside a match.</summary>
    public void Spend(TimeSpan amount)
    {
        if (amount > TimeSpan.Zero) _spent += amount;
    }
}
