using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SportityGui.Models;
using SportityGui.Services;

namespace SportityGui.ViewModels;

public partial class EventViewModel : ObservableObject
{
    private readonly StateService _state;
    private List<TreeItemViewModel> _allItems = [];

    public SportityEvent Event { get; }

    public ObservableCollection<TreeItemViewModel> DisplayedItems { get; } = [];

    [ObservableProperty] private string _filterText = string.Empty;

    public EventViewModel(SportityEvent ev, StateService state)
    {
        Event = ev;
        _state = state;
        Rebuild(ev.Items);
    }

    public void Rebuild(List<TreeItem> items)
    {
        _allItems = items.Select(i => new TreeItemViewModel(i, _state)).ToList();
        _state.Save();
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        DisplayedItems.Clear();
        var filter = FilterText.Trim();

        foreach (var vm in _allItems)
        {
            if (string.IsNullOrEmpty(filter) || MatchesFilter(vm, filter))
                DisplayedItems.Add(vm);
        }
    }

    public void MarkAllRead()
    {
        var ids = new List<string>();
        CollectNonFolderIds(_allItems, ids);
        _state.MarkReadBatch(ids);
        SetReadRecursive(_allItems, read: true);
        RefreshAllBadges();
    }

    public void MarkAllUnread()
    {
        var ids = new List<string>();
        CollectNonFolderIds(_allItems, ids);
        _state.MarkUnreadBatch(ids);
        SetReadRecursive(_allItems, read: false);
        RefreshAllBadges();
    }

    private static void CollectNonFolderIds(IEnumerable<TreeItemViewModel> items, List<string> ids)
    {
        foreach (var vm in items)
        {
            if (!vm.IsFolder) ids.Add(vm.Model.Id);
            CollectNonFolderIds(vm.Children, ids);
        }
    }

    private static void SetReadRecursive(IEnumerable<TreeItemViewModel> items, bool read)
    {
        foreach (var vm in items)
        {
            if (!vm.IsFolder)
            {
                vm.IsRead = read;
                if (!read) { vm.IsDownloaded = false; vm.LocalPath = null; }
            }
            SetReadRecursive(vm.Children, read);
        }
    }

    // Force bottom-up re-evaluation of all badge states after any item interaction
    public void RefreshAllBadges()
    {
        foreach (var vm in _allItems)
            RefreshBadgeRecursive(vm);
    }

    private static void RefreshBadgeRecursive(TreeItemViewModel vm)
    {
        foreach (var child in vm.Children)
            RefreshBadgeRecursive(child);
        vm.RefreshBadge();
    }

    private static bool MatchesFilter(TreeItemViewModel vm, string filter)
    {
        if (vm.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;
        return vm.Children.Any(c => MatchesFilter(c, filter));
    }
}
