using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SportityGui.Models;

namespace SportityGui.ViewModels;

public partial class PreferencesViewModel : ObservableObject
{
    [ObservableProperty] private string _theme;
    [ObservableProperty] private string _downloadFolder;
    [ObservableProperty] private bool _autoDownload;
    [ObservableProperty] private int _autoRefreshMinutes;
    [ObservableProperty] private bool _minimizeToTray;

    public PreferencesViewModel(AppPreferences prefs)
    {
        _theme = prefs.Theme;
        _downloadFolder = prefs.DownloadFolder;
        _autoDownload = prefs.AutoDownload;
        _autoRefreshMinutes = prefs.AutoRefreshMinutes;
        _minimizeToTray = prefs.MinimizeToTray;
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
    }
}
