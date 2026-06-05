namespace SportityGui.Models;

public class AppState
{
    public List<string> RecentUrls { get; set; } = [];
    public Dictionary<string, DateTime> ReadItems { get; set; } = [];
    public Dictionary<string, DateTime> FirstSeenItems { get; set; } = [];
    public Dictionary<string, DownloadRecord> DownloadedFiles { get; set; } = [];
    public List<SavedChannel> Channels { get; set; } = [];
    public HashSet<string> NewItems { get; set; } = [];
}

public class DownloadRecord
{
    public string LocalPath { get; set; } = string.Empty;
    public DateTime DownloadedAt { get; set; }
}
