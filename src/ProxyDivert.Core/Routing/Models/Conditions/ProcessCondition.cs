using System.Text.Json.Serialization;

namespace ProxyDivert.Core.Routing.Models.Conditions;

/// <summary>One node of a process filter's condition tree: a group, or a single test.</summary>
/// <remarks>
/// The derived types are listed here rather than discovered, because that list IS the file format:
/// the discriminator strings below are written into the user's config and can never be renamed.
/// Adding a new kind of condition later — parent process, account, window title — is one more
/// class and one more line here.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
[JsonDerivedType(typeof(ProcessNameCondition), "process")]
[JsonDerivedType(typeof(CommandLineCondition), "commandLine")]
public abstract class ProcessCondition
{
    /// <summary>Inverts this node's answer — on a group as readily as on a single test.</summary>
    /// <remarks>
    /// One flag on the base rather than an extra operator on groups: "NOT (a OR b)" and "name is
    /// NOT chrome" are the same idea, and the evaluator only has to know about it in one place.
    /// It does not flip <see cref="Enums.ConditionResult.Unknown"/> — see that type for why.
    /// </remarks>
    public bool Negate { get; set; }

    /// <summary>Deep copy, so the editor can work on a scratch tree and throw it away on Cancel.</summary>
    public abstract ProcessCondition Clone();
}
