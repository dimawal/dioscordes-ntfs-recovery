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

    private void ContentsDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not DataGrid { SelectedItem: FileListItemViewModel item })
            return;

        if (item.Node.IsDirectory)
            return; // folder navigation was removed; double-click only opens the content preview

        var (metadata, image, text, unsupportedReason) = vm.BuildPreviewContent(item);
        new PreviewWindow(item.Name, metadata, image, text, unsupportedReason) { Owner = this }.ShowDialog();
    }
}
