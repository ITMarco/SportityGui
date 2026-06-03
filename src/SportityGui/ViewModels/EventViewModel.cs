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
