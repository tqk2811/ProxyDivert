using System.Windows;
using ProxyDivert.Wpf.ViewModels;

namespace ProxyDivert.Wpf.Views;

/// <summary>The filter editor: name, conditions, and what to do with what matches.</summary>
/// <remarks>
/// A window rather than a panel under the list. The Processes tab already carries the filter list
/// and the tree of processes being redirected right now, and a condition tree needs room to be
/// read — squeezing a third resizable thing in there would leave all three too small to use.
/// </remarks>
public partial class ProcessFilterWindow : Window
{
    public ProcessFilterWindow()
    {
        InitializeComponent();
    }

    public ProcessFilterWindow(ProcessFilterViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    // Cancel is the window's own IsCancel button and needs no code; saving is the one that has an
    // answer to give back. The caller writes the edit onto the rule only on true.
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    // The system caption is replaced here as it is on the main window, so its buttons are ours.
    // Closing from the title bar leaves DialogResult alone, which is what discards the edit — the
    // X on a dialog means cancel.
    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
