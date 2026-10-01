using System.Windows;
using System.Windows.Controls;
using NtfsRecovery.Gui.ViewModels;

namespace NtfsRecovery.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    // TreeView.SelectedItem has no setter, so two-way binding isn't possible directly;
    // this is the standard WPF workaround to push the selection into the view model.
    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel vm && e.NewValue is TreeItemViewModel selected)
            vm.SelectedTreeItem = selected;
    }
}
