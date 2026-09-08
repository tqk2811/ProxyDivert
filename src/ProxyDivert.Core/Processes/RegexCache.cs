using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;

namespace ProxyDivert.Core.Processes;

/// <summary>
/// The expressions a process filter is built from, compiled once and kept.
/// </summary>
/// <remarks>
/// These run against every process on the machine on every scan, so where they come from matters.
/// Measured end to end through the matcher, one scan of 301 processes, best of three warm runs —
/// the first run of any of them is about twice the number below, which is what made an earlier
/// single-run version of this table wrong:
///
/// <code>
///   conditions   static Regex.IsMatch   kept, interpreted   kept, compiled
///        8              2.04 ms              1.94 ms           1.04 ms
///       20             24.46 ms              5.32 ms           2.61 ms
///       40             37.58 ms              8.89 ms           5.24 ms
/// </code>
///
/// The cliff between eight conditions and twenty is <see cref="Regex.CacheSize"/>, which is fifteen.
/// Past it every call missed the framework's cache and rebuilt the expression from source — and a
/// filter asks about both the process name and its path, so it does not take many conditions to get
/// there. Twenty-four milliseconds of a scan, spent parsing patterns that had already been parsed.
///
/// Keeping them is what removes that cliff; compiling them roughly halves what is left, at every
/// size, and is the only thing that helps at all below the cliff — at eight conditions a kept
/// interpreted expression is no faster than the framework's own cached one.
///
/// What compiling costs is a couple of milliseconds per distinct pattern, once, and a dynamic
/// method held for as long as the expression is. Patterns come from the configuration, so the live
/// set is the user's rules; the cap below is what keeps the ones produced by someone typing in the
/// editor from accumulating.
///
/// Raising <see cref="Regex.CacheSize"/> was the other way out and was not taken: it is a
/// process-wide setting this library would be imposing on whatever else is in the process, and it
/// gets nothing below the cliff.
///
/// <see cref="RegexOptions.NonBacktracking"/> was considered and not used. It would make the match
/// deadline unnecessary by guaranteeing linear time, but it refuses lookarounds and backreferences
/// at construction — patterns a user may reasonably type — and it cannot be combined with
/// <see cref="RegexOptions.Compiled"/>. The deadline already covers what it would.
/// </remarks>
internal sealed class RegexCache
{
    /// <summary>
    /// How long any one match may run. Owned here rather than passed in, because it is part of the
    /// key the expression is kept under: a timeout that varied per call is exactly what defeated
    /// the framework's own cache, and a caller cannot make that mistake against this one.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The one the matcher uses. An instance rather than a static dictionary so that anything with
    /// its own set of patterns — a test, above all — is not sharing state with whatever else is
    /// running at the same time.
    /// </summary>
    public static RegexCache Shared { get; } = new RegexCache();

    // Compiled: see the remarks above for what it buys and what it costs.
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>
    /// How many expressions are kept. Patterns come from the configuration, so the live set is the
    /// user's rules and small; the cap is there for the editing that produces them — every
    /// keystroke in a pattern box is a new pattern while the user is typing it.
    /// </summary>
    internal const int Capacity = 128;

    // Two of them, because a wildcard is kept under what the user wrote rather than the expression
    // it turns into: the translation then happens once as well, instead of building the same string
    // again for every process on the machine.
    private readonly ConcurrentDictionary<string, Lazy<Regex?>> _expressions =
        new ConcurrentDictionary<string, Lazy<Regex?>>(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<Regex?>> _wildcards =
        new ConcurrentDictionary<string, Lazy<Regex?>>(StringComparer.Ordinal);

    internal int Count => _expressions.Count + _wildcards.Count;

    /// <summary>
    /// The expression for a pattern, or null when the pattern will not compile.
    /// </summary>
    /// <remarks>
    /// A pattern that will not compile is remembered as such. It came from a text box and is asked
    /// about every process on every scan, so re-parsing it to fail again each time is the same
    /// waste as re-parsing a good one.
    /// </remarks>
    public Regex? Get(string pattern)
        => Lookup(_expressions, pattern, static p => Build(p));

    /// <summary>The expression for a wildcard — "*" and "?" as everyone writes them in a file dialog.</summary>
    public Regex? GetWildcard(string wildcard)
        => Lookup(
            _wildcards,
            wildcard,
            static w => Build("^" + Regex.Escape(w).Replace("\\*", ".*").Replace("\\?", ".") + "$"));

    private static Regex? Lookup(
        ConcurrentDictionary<string, Lazy<Regex?>> entries, string key, Func<string, Regex?> build)
    {
        // The hit path, and it is the only one that runs per process per scan: a plain lookup and
        // nothing else. In particular not Count, which on a ConcurrentDictionary takes every bucket
        // lock — measured, that alone made this slower than the framework cache it replaced.
        if (entries.TryGetValue(key, out Lazy<Regex?>? found)) return found.Value;

        // A miss happens once per distinct pattern, so the cap is checked here. Cleared rather than
        // evicted one by one: the only way to reach it is a burst of patterns nobody wants any more,
        // from someone typing in the editor, and rebuilding the few that are still in use costs a
        // couple of milliseconds each.
        if (entries.Count >= Capacity) entries.Clear();

        // Lazy, not a bare GetOrAdd factory: the same pattern is asked about from the scan loop,
        // the socket pump and the editor at once, and GetOrAdd may run its factory on every one of
        // them to keep a single result. Compiling the same expression on three threads to throw two
        // away is the cost this exists to avoid.
        return entries.GetOrAdd(
            key,
            k => new Lazy<Regex?>(() => build(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static Regex? Build(string pattern)
    {
        try { return new Regex(pattern, Options, MatchTimeout); }
        catch (ArgumentException) { return null; }
    }
}
