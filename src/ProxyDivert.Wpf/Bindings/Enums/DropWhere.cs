namespace ProxyDivert.Wpf.Bindings.Enums;

/// <summary>Where a dragged row would land relative to the row it is hovering over.</summary>
public enum DropWhere
{
    /// <summary>Nowhere: this row will not take the dragged one.</summary>
    None,

    /// <summary>Immediately above the hovered row, in the same group.</summary>
    Before,

    /// <summary>Immediately below the hovered row, in the same group.</summary>
    After,

    /// <summary>As the first row inside the hovered group.</summary>
    Inside,
}
