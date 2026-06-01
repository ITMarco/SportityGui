namespace SportityGui.Models;

public class SportityChannel
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public List<SportityEvent> Events { get; init; } = [];
}
