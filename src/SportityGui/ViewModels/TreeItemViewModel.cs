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
    public TreeItemViewModel? Parent { get; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private bool _isDownloaded;
    [ObservableProperty] private bool _showUnreadBadge;   // settable, not computed
    [ObservableProperty] private string? _localPath;

    public string Name => Model.Name;
    public TreeItemType Type => Model.Type;
    public bool IsFolder => Model.Type == TreeItemType.Folder;
    public bool IsFile => Model.Type == TreeItemType.File;
    public bool IsText => Model.Type == TreeItemType.Text;

    public DateTime? FirstSeen => _state.GetFirstSeen(Model.Id);

    public TreeItemViewModel(TreeItem model, StateService state, TreeItemViewModel? parent = null)
    {
        Model = model;
        _state = state;
        Parent = parent;

        _isRead = state.IsRead(model.Id);
        _isDownloaded = model is FileItem && state.IsDownloaded(model.Id);
        _localPath = (model is FileItem) ? state.GetDownloadRecord(model.Id)?.LocalPath : null;

        state.RecordFirstSeen(model.Id);

        if (model is FolderItem folder)
        {
            foreach (var child in folder.Children)
                Children.Add(new TreeItemViewModel(child, state, this));
        }

        // Compute initial badge state after children are built (direct field to skip PropertyChanged in ctor)
        _showUnreadBadge = ComputeBadge();
    }

    private bool ComputeBadge() =>
        (!_isRead && !_isDownloaded) || (IsFolder && Children.Any(c => c.ShowUnreadBadge));

    // Called whenever read/download state changes, and propagated up through Parent chain
    private void UpdateBadge()
    {
        ShowUnreadBadge = ComputeBadge();   // property setter fires PropertyChanged only if value changes
    }

    private void NotifyParentChain()
    {
        var p = Parent;
        while (p != null)
        {
            p.UpdateBadge();
            p = p.Parent;
        }
    }

    public void MarkRead()
    {
        _state.MarkRead(Model.Id);
        IsRead = true;
        UpdateBadge();
        NotifyParentChain();
    }

    public void NotifyDownloaded(string path)
    {
        LocalPath = path;
        IsDownloaded = true;
        UpdateBadge();
        NotifyParentChain();
    }

    // Called by EventViewModel.RefreshAllBadges() as a guaranteed sweep
    public void RefreshBadge() => UpdateBadge();
}
