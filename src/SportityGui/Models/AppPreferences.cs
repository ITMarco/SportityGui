namespace SportityGui.Models;

public class AppPreferences
{
    public bool AutoDownload { get; set; } = false;
    public string DownloadFolder { get; set; } = DefaultDownloadFolder;
    public int AutoRefreshMinutes { get; set; } = 0;
    public string Theme { get; set; } = "Light";
    public bool MinimizeToTray { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = false;
    public bool CheckForUpdatesAtStartup { get; set; } = true;

    public static string DefaultDownloadFolder =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "SportityGui");
}
