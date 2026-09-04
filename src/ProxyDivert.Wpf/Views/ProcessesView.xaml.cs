using System.Windows;
using System.Windows.Controls;
using ProxyDivert.Wpf.ViewModels;

namespace ProxyDivert.Wpf.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView()
    {
        InitializeComponent();
    }

    // TreeView.SelectedItem is read-only, so it cannot be bound the way a DataGrid's is; forwarding
    // the change here is the whole of the workaround.
    private void AppliedProcesses_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ProcessesViewModel viewModel)
            viewModel.SelectedProcess = e.NewValue as ProcessesViewModel.AppliedProcessNode;
    }
}
