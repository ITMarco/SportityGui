namespace SportityGui.Models;

public enum TreeItemType { Folder, File, Text }

public abstract class TreeItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public abstract TreeItemType Type { get; }
}

public sealed class FolderItem : TreeItem
{
    public override TreeItemType Type => TreeItemType.Folder;
    public List<TreeItem> Children { get; init; } = [];
}

public sealed class FileItem : TreeItem
{
    public override TreeItemType Type => TreeItemType.File;
    public string DownloadUrl { get; init; } = string.Empty;
    public string FileExtension { get; init; } = string.Empty;
}

public sealed class TextItem : TreeItem
{
    public override TreeItemType Type => TreeItemType.Text;
    public string ContentUrl { get; init; } = string.Empty;
    public string? InlineContent { get; set; }
}
