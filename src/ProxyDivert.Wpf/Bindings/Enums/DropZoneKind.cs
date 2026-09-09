namespace ProxyDivert.Wpf.Bindings.Enums;

/// <summary>What part of the editor an element is, for the purpose of working out a drop.</summary>
/// <remarks>
/// A group is two zones rather than one because its two halves mean different things: dropping on
/// the header is dropping into the bracket, and dropping on the strip under its last row is
/// dropping after the whole bracket. One zone could not tell those apart.
/// </remarks>
public enum DropZoneKind
{
    None,

    /// <summary>A single row: the top half is "above me", the bottom half "below me".</summary>
    Row,

    /// <summary>A group's own row: the top half is "above this group", the bottom half "inside it".</summary>
    GroupHeader,

    /// <summary>The strip below a group's last row: "after this whole group".</summary>
    GroupTail,
}
