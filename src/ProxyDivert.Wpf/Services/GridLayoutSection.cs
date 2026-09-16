namespace ProxyDivert.Wpf.Services;

/// <summary>
/// One grid's column widths, as the view and its view model see them. A handle onto
/// <see cref="GridLayoutStore"/> rather than a copy of anything: the store holds every number under
/// one lock, so a width read here is the width that will be written.
/// </summary>
public sealed class GridLayoutSection
{
    private readonly GridLayoutStore _store;

    internal GridLayoutSection(GridLayoutStore store, string key)
    {
        _store = store;
        Key = key;
    }

    /// <summary>The grid this belongs to, as named where the section was asked for.</summary>
    public string Key { get; }

    /// <summary>The stored width of a column, or null when the user has never dragged it.</summary>
    public double? WidthOf(string columnKey) => _store.WidthOf(Key, columnKey);

    /// <summary>
    /// Remembers how wide a column now is. Cheap to call on every mouse move during a drag — the
    /// store keeps the last value and writes it once the drag stops.
    /// </summary>
    public void Record(string columnKey, double width) => _store.Record(Key, columnKey, width);
}
