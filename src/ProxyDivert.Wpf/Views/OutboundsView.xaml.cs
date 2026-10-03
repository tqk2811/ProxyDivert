using System.Windows.Controls;
using ProxyDivert.Wpf.Bindings;
using ProxyDivert.Wpf.ViewModels;

namespace ProxyDivert.Wpf.Views;

public partial class OutboundsView : UserControl
{
    public OutboundsView()
    {
        InitializeComponent();
    }

    // Direct and Block carry nothing anyone could sensibly change, and a rule points at them by id
    // — renaming one, or handing it a URL, only produces a configuration that no longer does what
    // it says. Refused here rather than by disabling the row: on a fresh configuration those two
    // are the only rows there are, and a whole grid greyed out reads as a broken tab.
    private void Grid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not OutboundRowViewModel row) return;
        // The anti-DPI cells follow their own rule: live on Direct (built-in or not) and the plain
        // proxies, dead everywhere else.
        if (ColumnLayoutBehavior.GetColumnKey(e.Column) is "antiDpi" or "antiDpiChunk")
            e.Cancel = !row.SupportsAntiDpi;
        else if (!row.IsEditable)
            e.Cancel = true;
    }
}
