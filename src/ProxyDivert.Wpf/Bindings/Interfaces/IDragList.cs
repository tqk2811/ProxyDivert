using ProxyDivert.Wpf.Bindings.Enums;

namespace ProxyDivert.Wpf.Bindings.Interfaces;

/// <summary>A list that answers drops on behalf of its rows.</summary>
/// <remarks>
/// The other half of <see cref="IDragRow"/>, for the lists whose rows cannot answer for themselves:
/// the process filters are the saved model objects, straight out of the configuration, and they
/// have no business knowing which list they are being drawn in or that a window exists at all.
/// So the view model implements this, and the rows stay what they are.
/// </remarks>
public interface IDragList
{
    /// <summary>Whether dropping <paramref name="dragged"/> at <paramref name="target"/> is legal.</summary>
    bool CanAccept(object dragged, object target, DropWhere where);

    /// <summary>Carries out the move. Never called without <see cref="CanAccept"/> agreeing first.</summary>
    void Accept(object dragged, object target, DropWhere where);
}
