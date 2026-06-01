namespace SportityGui.Models;

public class SportityEvent
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ChannelCode { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public List<TreeItem> Items { get; init; } = [];
}
