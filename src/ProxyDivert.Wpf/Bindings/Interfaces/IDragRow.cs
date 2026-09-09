using ProxyDivert.Wpf.Bindings.Enums;

namespace ProxyDivert.Wpf.Bindings.Interfaces;

/// <summary>A row that can be picked up and dropped somewhere else in the same list or tree.</summary>
/// <remarks>
/// The view works out WHICH row the pointer is over and which half of it; what that means is left
/// to the row itself, because only the view model knows whether the move is legal — a bracket
/// cannot be dropped inside itself, and nothing can be dropped beside the outermost group.
/// </remarks>
public interface IDragRow
{
    /// <summary>Whether dropping <paramref name="source"/> here would do anything legal.</summary>
    bool CanAccept(object source, DropWhere where);

    /// <summary>Carries out the move. Never called without <see cref="CanAccept"/> agreeing first.</summary>
    void Accept(object source, DropWhere where);
}
