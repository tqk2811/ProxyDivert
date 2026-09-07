using System.Windows;
using ProxyDivert.Wpf.Views.Enums;

namespace ProxyDivert.Wpf.Views;

/// <summary>Asks what to do with an edit that is about to be closed: save it, drop it, or go back.</summary>
public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow()
    {
        InitializeComponent();
    }

    /// <summary>What the user answered. <see cref="UnsavedChangesChoice.Cancel"/> until they do.</summary>
    public UnsavedChangesChoice Choice { get; private set; } = UnsavedChangesChoice.Cancel;

    /// <summary>Puts the question on screen over <paramref name="owner"/> and waits for the answer.</summary>
    public static UnsavedChangesChoice Ask(Window owner)
    {
        var window = new UnsavedChangesWindow { Owner = owner };
        window.ShowDialog();
        return window.Choice;
    }

    // Closing the question any other way — Esc, the owner going away — leaves Choice at Cancel,
    // which keeps the editor open. Losing an edit must never be the answer nobody chose.
    private void Cancel_Click(object sender, RoutedEventArgs e) => Answer(UnsavedChangesChoice.Cancel);

    private void Discard_Click(object sender, RoutedEventArgs e) => Answer(UnsavedChangesChoice.Discard);

    private void Save_Click(object sender, RoutedEventArgs e) => Answer(UnsavedChangesChoice.Save);

    private void Answer(UnsavedChangesChoice choice)
    {
        Choice = choice;
        DialogResult = choice != UnsavedChangesChoice.Cancel;
    }
}
