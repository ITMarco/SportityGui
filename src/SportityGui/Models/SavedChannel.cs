namespace SportityGui.Models;

public enum SavedChannelType { Channel, StandaloneEvent }

public class SavedChannel
{
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsCollapsed { get; set; } = false;
    public int SortOrder { get; set; } = 0;
    public SavedChannelType Type { get; set; } = SavedChannelType.Channel;
    public int AutoRefreshMinutes { get; set; } = 0;
}
