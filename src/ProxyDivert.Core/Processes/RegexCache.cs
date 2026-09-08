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
/// Measured on one scan of 301 processes, timed end to end through the matcher:
///
/// <code>
///   conditions   static Regex.IsMatch   kept, interpreted   kept, compiled
///        8              2.63 ms              2.33 ms           1.57 ms
///       20             26.41 ms              6.00 ms           2.91 ms
///       40             41.62 ms              9.29 ms           5.25 ms
/// </code>
///
/// The cliff between eight conditions and twenty is <see cref="Regex.CacheSize"/>, which is fifteen.
/// Past it every call missed the framework's cache and rebuilt the expression from source — and a
/// filter asks about both the process name and its path, so it does not take many conditions to get
/// there. Twenty-six milliseconds of a scan, spent parsing patterns that had already been parsed.
///
/// Raising <see cref="Regex.CacheSize"/> was the other way out and was not taken: it is a
/// process-wide setting this library would be imposing on whatever else is in the process, and it
/// still leaves the lookup cost — hashing a key made of the pattern, the options, the culture and
/// the timeout — which is most of the gap at eight conditions.
///
/// Compiling is worth its own column and not much more: it doubles the throughput of a kept
/// expression, and building all eight cost fifteen milliseconds once. Keeping them is what mattered.
///
/// <see cref="RegexOptions.NonBacktracking"/> was considered and not used. It would make the match
/// deadline unnecessary by guaranteeing linear time, but it refuses lookarounds and backreferences
/// at construction — patterns a user may reasonably type — and it is slower than the compiled engine
/// for the simple ones. The deadline already covers what it would.
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
        // evicted one by one: nothing here is expensive to rebuild — fifteen milliseconds for eight
        // — and the only way to reach the cap is a burst of patterns nobody wants any more, from
        // someone typing in the editor.
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
