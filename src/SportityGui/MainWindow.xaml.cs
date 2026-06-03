using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using SportityGui.Services;
using SportityGui.ViewModels;

namespace SportityGui;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/sportitylogo1.png"));
        }
        catch { }

        ContentTree.Tag = DataContext;
        DataContextChanged += (_, _) => ContentTree.Tag = DataContext;
    }

    // M8-8: Restore saved channels once the window is first rendered
    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Prompt for download folder if not set (first run or user cleared it)
        var stateService = App.Services.GetRequiredService<StateService>();
        if (string.IsNullOrWhiteSpace(stateService.Preferences.DownloadFolder))
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose a folder where SportityGui will save downloaded files",
                Multiselect = false
            };
            stateService.Preferences.DownloadFolder =
                dialog.ShowDialog(this) == true && !string.IsNullOrEmpty(dialog.FolderName)
                    ? dialog.FolderName
                    : Models.AppPreferences.DefaultDownloadFolder;
            stateService.SavePreferences();
        }

        await Vm.RestoreChannelsAsync();
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
            e.Handled = true;
        }
    }

    // M8-6: Remove channel button (click on ✕ in channel header)
    private void RemoveChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        // Walk up to find the ChannelSectionViewModel DataContext
        var section = FindChannelSection(el);
        if (section == null) return;

        var dlg = new RemoveChannelDialog(section.Name) { Owner = this };
        if (dlg.ShowDialog() == true)
            Vm.RemoveChannel(section, dlg.DeleteFiles);
    }

    private static ChannelSectionViewModel? FindChannelSection(DependencyObject element)
    {
        var current = element;
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.DataContext is ChannelSectionViewModel csv)
                return csv;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            var prefs = App.Services.GetRequiredService<StateService>().Preferences;
            if (prefs.MinimizeToTray)
            {
                Hide();
                App.Services.GetRequiredService<TrayService>().ShowTrayIcon();
            }
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var stateService = App.Services.GetRequiredService<StateService>();
        var vm = new PreferencesViewModel(stateService.Preferences);
        var window = new PreferencesWindow(vm) { Owner = this };
        if (window.ShowDialog() == true)
        {
            vm.ApplyTo(stateService.Preferences);
            stateService.SavePreferences();
            Vm.AutoDownload = stateService.Preferences.AutoDownload;
            Vm.AutoRefreshMinutes = stateService.Preferences.AutoRefreshMinutes;
        }
    }
}
