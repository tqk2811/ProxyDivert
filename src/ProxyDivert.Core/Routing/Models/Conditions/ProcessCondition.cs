using System;
using System.Text.Json.Serialization;
using ProxyDivert.Core.Routing.Enums;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>One node of a process filter's condition tree: a group, or a single test.</summary>
/// <remarks>
/// The derived types are listed here rather than discovered, because that list IS the file format:
/// the discriminator strings below are written into the user's config and can never be renamed.
/// Adding a new kind of condition later — parent process, account, window title — is one more
/// class, one more line here, and one more entry in <see cref="ConditionSubject"/> so the editor
/// can offer it.
///
/// Each node answers for itself, and a leaf says what it looks at through its subject. The
/// evaluator used to be a separate class with a switch over these types, and so did the editor
/// and the sentence describing a filter, which is how "one more class" became six edits.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
[JsonDerivedType(typeof(ProcessNameCondition), "process")]
[JsonDerivedType(typeof(CommandLineCondition), "commandLine")]
public abstract class ProcessCondition
{
    /// <summary>How deep a tree is walked before the answer becomes Unknown.</summary>
    /// <remarks>
    /// A tree this deep is not something the editor can build — it would be a hand-edited config
    /// file, and the recursion has to stop somewhere short of the stack.
    /// </remarks>
    public const int MaxDepth = 16;

    /// <summary>Inverts this node's answer — on a group as readily as on a single test.</summary>
    /// <remarks>
    /// One flag on the base rather than an extra operator on groups: "NOT (a OR b)" and "name is
    /// NOT chrome" are the same idea, and the evaluator only has to know about it in one place.
    /// It does not flip <see cref="Enums.ConditionResult.Unknown"/> — see that type for why.
    /// </remarks>
    public bool Negate { get; set; }

    /// <summary>Deep copy, so the editor can work on a scratch tree and throw it away on Cancel.</summary>
    public abstract ProcessCondition Clone();

    /// <summary>What this node — or the whole subtree under it — says about one process.</summary>
    /// <remarks>
    /// In four states, not two; see <see cref="ConditionResult"/>. Whether a filter applies is a
    /// separate question with a stricter answer — only Match counts — and that one is asked by
    /// <see cref="Processes.ProcessRuleMatcher"/>.
    /// </remarks>
    public ConditionResult Evaluate(ConditionContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        // Stopping means "cannot tell", never "yes".
        if (!context.TryEnter()) return ConditionResult.Unknown;

        ConditionResult result;
        try
        {
            result = Answer(context);
        }
        finally
        {
            context.Leave();
        }

        return Negate ? Invert(result) : result;
    }

    /// <summary>This node's own answer, before <see cref="Negate"/> is applied to it.</summary>
    /// <remarks>
    /// Only this assembly can implement it: the set of node types is closed by the file format
    /// above, so a type declared anywhere else could never be saved or loaded.
    /// </remarks>
    private protected abstract ConditionResult Answer(ConditionContext context);

    // NOT leaves alone the two states it has no answer for. Flipping Unknown is the bug this whole
    // four-state business exists to prevent: "argument does not contain X" would then be true for
    // every process whose command line cannot be read, which is most of the system.
    private static ConditionResult Invert(ConditionResult result) => result switch
    {
        ConditionResult.Match => ConditionResult.NoMatch,
        ConditionResult.NoMatch => ConditionResult.Match,
        _ => result,
    };
}
