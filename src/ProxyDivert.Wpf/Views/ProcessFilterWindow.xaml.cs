using System;
using System.ComponentModel;
using System.Windows;
using ProxyDivert.Wpf.ViewModels;
using ProxyDivert.Wpf.Views.Enums;

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
    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <remarks>
    /// Every way out of the window comes through here — the X, Cancel, Esc, Alt+F4 — which is why
    /// the question is asked here and not on the buttons. Saving is the one exception: it has
    /// already said what to do with the edit.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || DialogResult == true) return;
        if (DataContext is not ProcessFilterViewModel viewModel || !viewModel.IsDirty) return;

        switch (UnsavedChangesWindow.Ask(this))
        {
            case UnsavedChangesChoice.Save:
                // Not DialogResult here: setting it closes the window, and closing a window that is
                // already inside its own Closing throws. The close is called off and asked for again
                // once this one has unwound, and by then DialogResult says not to ask twice.
                e.Cancel = true;
                Dispatcher.BeginInvoke(new Action(() => DialogResult = true));
                break;

            case UnsavedChangesChoice.Discard:
                break;

            default:
                e.Cancel = true;
                break;
        }
    }
}
