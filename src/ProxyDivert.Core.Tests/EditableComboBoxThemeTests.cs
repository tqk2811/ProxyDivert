using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace ProxyDivert.Core.Tests;

// The theme's ComboBox template had no PART_EditableTextBox. An editable combo keeps its text in
// that box and leaves SelectionBoxItem empty, so the DoH endpoint box in Settings showed nothing,
// whether a preset was picked or a URL typed.
[Collection("WPF")]
public class EditableComboBoxThemeTests
{
    [Fact]
    public void An_editable_combo_shows_the_picked_item_as_text()
    {
        WpfHost.RunOnStaThread(() =>
        {
            WpfHost.EnsureApplication();

            var combo = new ComboBox { IsEditable = true, ItemsSource = new[] { "https://1.1.1.1/dns-query" } };
            var window = new Window { Content = combo, Width = 400, Height = 100, ShowInTaskbar = false };
            window.Show();
            try
            {
                combo.SelectedIndex = 0;
                window.UpdateLayout();

                TextBox? box = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
                Assert.NotNull(box);
                Assert.Equal(Visibility.Visible, box!.Visibility);
                Assert.Equal("https://1.1.1.1/dns-query", box.Text);
            }
            finally
            {
                window.Close();
            }
        }, "Editable combo");
    }
}
