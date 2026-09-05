using System.Windows.Controls;
using ProxyDivert.Core.Routing.Models;

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
        if (e.Row.Item is Outbound outbound && outbound.IsBuiltIn) e.Cancel = true;
    }
}
