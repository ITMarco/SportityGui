using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SportityGui.Models;
using SportityGui.ViewModels;

namespace SportityGui;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();

        // Set window taskbar icon from the embedded PNG
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/sportitylogo1.png"));
        }
        catch { /* icon is cosmetic, continue without it */ }

        // Give tree view access to DataContext for context menu commands
        ContentTree.Tag = DataContext;
        DataContextChanged += (_, _) => ContentTree.Tag = DataContext;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            Vm.RefreshCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ContentTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeItemViewModel vm)
            Vm.ItemClickedCommand.Execute(vm);
    }

    private void ContentTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ContentTree.SelectedItem is TreeItemViewModel vm)
        {
            Vm.ItemDoubleClickedCommand.Execute(vm);
            e.Handled = true;
        }
    }

    private void DeleteRecentUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
        {
            Vm.RemoveRecentUrlCommand.Execute(url);
            e.Handled = true; // prevent ComboBox from selecting the item
        }
    }

    private void ChannelEventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is SportityEvent ev)
            Vm.LoadChannelEventCommand.Execute(ev);
    }
}
