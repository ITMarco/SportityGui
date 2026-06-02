using System.Windows;
using SportityGui.ViewModels;

namespace SportityGui;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow(PreferencesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
