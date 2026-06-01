using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SportityGui.Models;
using SportityGui.Services;

namespace SportityGui.ViewModels;

public partial class TreeItemViewModel : ObservableObject
{
    private readonly StateService _state;

    public TreeItem Model { get; }
    public ObservableCollection<TreeItemViewModel> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private bool _isDownloaded;
    [ObservableProperty] private string? _localPath;

    public string Name => Model.Name;
    public TreeItemType Type => Model.Type;
    public bool IsFolder => Model.Type == TreeItemType.Folder;
    public bool IsFile => Model.Type == TreeItemType.File;
    public bool IsText => Model.Type == TreeItemType.Text;

    public DateTime? FirstSeen => _state.GetFirstSeen(Model.Id);

    public bool HasUnreadChildren =>
        Children.Any(c => !c.IsRead || c.HasUnreadChildren);

    public bool ShowUnreadBadge => !IsRead || (IsFolder && HasUnreadChildren);

    public TreeItemViewModel(TreeItem model, StateService state)
    {
        Model = model;
        _state = state;

        _isRead = state.IsRead(model.Id);
        _isDownloaded = model is FileItem && state.IsDownloaded(model.Id);
        _localPath = (model is FileItem)
            ? state.GetDownloadRecord(model.Id)?.LocalPath
            : null;

        state.RecordFirstSeen(model.Id);

        if (model is FolderItem folder)
        {
            foreach (var child in folder.Children)
                Children.Add(new TreeItemViewModel(child, state));
        }
    }

    public void MarkRead()
    {
        _state.MarkRead(Model.Id);
        IsRead = true;
        OnPropertyChanged(nameof(ShowUnreadBadge));
    }

    public void NotifyDownloaded(string path)
    {
        LocalPath = path;
        IsDownloaded = true;
    }
}
