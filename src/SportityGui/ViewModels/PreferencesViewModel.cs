using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SportityGui.Models;
using SportityGui.Services;

namespace SportityGui.ViewModels;

public partial class PreferencesViewModel : ObservableObject
{
    private readonly UpdateService _updater;

    [ObservableProperty] private string _theme;
    [ObservableProperty] private string _downloadFolder;
    [ObservableProperty] private bool _autoDownload;
    [ObservableProperty] private int _autoRefreshMinutes;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startMinimizedToTray;
    [ObservableProperty] private bool _checkForUpdatesAtStartup;

    public PreferencesViewModel(AppPreferences prefs, UpdateService updater)
    {
        _updater = updater;
        _theme = prefs.Theme;
        _downloadFolder = prefs.DownloadFolder;
        _autoDownload = prefs.AutoDownload;
        _autoRefreshMinutes = prefs.AutoRefreshMinutes;
        _minimizeToTray = prefs.MinimizeToTray;
        _startMinimizedToTray = prefs.StartMinimizedToTray;
        _checkForUpdatesAtStartup = prefs.CheckForUpdatesAtStartup;
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Download Folder",
            InitialDirectory = DownloadFolder
        };
        if (dialog.ShowDialog() == true)
            DownloadFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void ResetFolder()
    {
        DownloadFolder = AppPreferences.DefaultDownloadFolder;
    }

    public void ApplyTo(AppPreferences prefs)
    {
        prefs.Theme = Theme;
        prefs.DownloadFolder = DownloadFolder;
        prefs.AutoDownload = AutoDownload;
        prefs.AutoRefreshMinutes = AutoRefreshMinutes;
        prefs.MinimizeToTray = MinimizeToTray;
        prefs.StartMinimizedToTray = StartMinimizedToTray;
        prefs.CheckForUpdatesAtStartup = CheckForUpdatesAtStartup;
    }

    [RelayCommand]
    private async Task CheckForUpdatesNow()
    {
        try
        {
            var (hasUpdate, remoteVersion) = await _updater.CheckAsync();
            var msg = hasUpdate
                ? $"Version {remoteVersion} is available (you have {AppInfo.Version})."
                : $"You are up to date (version {AppInfo.Version}).";
            System.Windows.MessageBox.Show(msg, "SportityGui Update Check",
                System.Windows.MessageBoxButton.OK,
                hasUpdate ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Update check failed: {ex.Message}", "SportityGui",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
