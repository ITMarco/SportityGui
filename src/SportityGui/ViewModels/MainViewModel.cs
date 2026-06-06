using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SportityGui.Models;
using SportityGui.Services;

namespace SportityGui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly StateService _state;
    private readonly ScraperService _scraper;
    private readonly DownloadService _downloader;
    private readonly TrayService _tray;
    private readonly UpdateService _updater;

    private CancellationTokenSource? _cts;
    private ChannelSectionViewModel? _activeSection;

    [ObservableProperty] private string _urlInput = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _autoDownload;
    [ObservableProperty] private int _autoRefreshMinutes;           // global default for new sections
    [ObservableProperty] private string _refreshCountdownDisplay = string.Empty; // proxied from active section
    [ObservableProperty] private EventViewModel? _currentEvent;
    [ObservableProperty] private TreeItemViewModel? _selectedItem;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string _detailMetadata = string.Empty;
    [ObservableProperty] private bool _updateBannerVisible;
    [ObservableProperty] private string _updateBannerText = string.Empty;
    private string _updateRemoteVersion = string.Empty;

    public string? CurrentEventUrl => CurrentEvent?.Event.Url;
    public bool SelectedItemIsFile => SelectedItem?.IsFile == true;
    public bool SelectedItemIsDownloaded => SelectedItem?.IsDownloaded == true;

    public ObservableCollection<string> RecentUrls { get; } = [];
    public ObservableCollection<ChannelSectionViewModel> Channels { get; } = [];
    public bool HasChannels => Channels.Count > 0;

    public MainViewModel(StateService state, ScraperService scraper, DownloadService downloader, TrayService tray, UpdateService updater)
    {
        _state = state;
        _scraper = scraper;
        _downloader = downloader;
        _tray = tray;
        _updater = updater;

        // Initialise fields directly to avoid triggering save-on-load
        _autoDownload = state.Preferences.AutoDownload;
        _autoRefreshMinutes = state.Preferences.AutoRefreshMinutes;

        foreach (var url in state.State.RecentUrls)
            RecentUrls.Add(url);

        // Persist channel order after drag-and-drop reordering
        Channels.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
                PersistChannels();
            OnPropertyChanged(nameof(HasChannels));
        };
    }

    partial void OnAutoDownloadChanged(bool value)
    {
        _state.Preferences.AutoDownload = value;
        _state.SavePreferences();
    }

    partial void OnAutoRefreshMinutesChanged(int value)
    {
        _state.Preferences.AutoRefreshMinutes = value;
        _state.SavePreferences();
    }

    partial void OnCurrentEventChanged(EventViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentEventUrl));
    }

    partial void OnSelectedItemChanged(TreeItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedItemIsFile));
        OnPropertyChanged(nameof(SelectedItemIsDownloaded));
        if (value is null)
        {
            DetailMetadata = string.Empty;
            DetailText = string.Empty;
        }
    }

    // ── Load command ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        var rawInput = UrlInput.Trim();
        if (string.IsNullOrEmpty(rawInput)) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsLoading = true;
        ShowProgress = false;
        StatusMessage = "Connecting to Sportity…";

        try
        {
            // M9: normalise / smart shorthand
            var (url, isShorthand) = NormalizeInput(rawInput);
            var mode = ScraperService.DetectMode(url);

            if (mode == ScraperService.UrlMode.Unknown)
            {
                StatusMessage = "Unrecognised URL. Use a Sportity channel or event URL, or just a channel code.";
                return;
            }

            if (mode == ScraperService.UrlMode.Channel)
            {
                StatusMessage = "Loading channel…";
                try
                {
                    await LoadOrRefreshChannelAsync(url, ct);
                }
                catch (HttpRequestException) when (isShorthand)
                {
                    // Channel not found — try same word as event id
                    var eventUrl = $"https://webapp.sportity.com/event/{rawInput}/{rawInput}";
                    if (ScraperService.DetectMode(eventUrl) == ScraperService.UrlMode.Event)
                    {
                        StatusMessage = "Channel not found, trying as event…";
                        try { await LoadOrRefreshEventAsync(eventUrl, ct); }
                        catch (HttpRequestException)
                        {
                            StatusMessage = $"'{rawInput}' not found as a channel or event. Enter the full URL.";
                            return;
                        }
                    }
                    else
                    {
                        StatusMessage = $"'{rawInput}' not found. Enter the full Sportity URL.";
                        return;
                    }
                }
            }
            else
            {
                StatusMessage = "Loading event…";
                await LoadOrRefreshEventAsync(url, ct);
            }

            _state.AddRecentUrl(rawInput);
            RefreshRecentUrls();
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (HttpRequestException ex) { StatusMessage = $"Network error: {ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally
        {
            IsLoading = false;
            ShowProgress = false;
        }
    }

    private static (string url, bool isShorthand) NormalizeInput(string input)
    {
        if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return (input, false);

        // Single word (no slashes, dots, spaces) → try as channel code
        if (!input.Contains('/') && !input.Contains('.') && !input.Contains(' '))
            return ($"https://webapp.sportity.com/channel/{input}", true);

        return ("https://" + input, false);
    }

    // ── Channel load / refresh ───────────────────────────────────────────────

    private async Task LoadOrRefreshChannelAsync(string url, CancellationToken ct)
    {
        var existing = Channels.FirstOrDefault(s => s.Url == url);
        ChannelSectionViewModel section;

        if (existing != null)
        {
            if (Channels.IndexOf(existing) != 0)
            {
                Channels.Remove(existing);
                Channels.Insert(0, existing);
            }
            section = existing;
        }
        else
        {
            section = CreateSection(url, ScraperService.ParseChannelCode(url), SavedChannelType.Channel);
            Channels.Insert(0, section);
            OnPropertyChanged(nameof(HasChannels));
            PersistChannels();
        }

        await RefreshSectionAsync(section, ct);

        StatusMessage = section.HasError
            ? "Could not load channel."
            : $"Channel loaded — {section.EventCount} event(s).";
    }

    // ── Event load / refresh ─────────────────────────────────────────────────

    private async Task LoadOrRefreshEventAsync(string url, CancellationToken ct)
    {
        var (channelCode, eventId) = ScraperService.ParseEventUrl(url);

        // Find if this event already lives in a channel section
        var hostSection = Channels.FirstOrDefault(s =>
            s.Type == SavedChannelType.Channel && s.Events.Any(e => e.Url == url));

        // Find existing standalone section
        var standalone = Channels.FirstOrDefault(s =>
            s.Type == SavedChannelType.StandaloneEvent && s.Url == url);

        ChannelSectionViewModel? section = standalone;

        if (standalone != null)
        {
            if (Channels.IndexOf(standalone) != 0)
            {
                Channels.Remove(standalone);
                Channels.Insert(0, standalone);
            }
        }
        else if (hostSection == null)
        {
            // Create new standalone section
            section = CreateSection(url, eventId, SavedChannelType.StandaloneEvent, channelCode: channelCode);
            Channels.Insert(0, section);
            OnPropertyChanged(nameof(HasChannels));
        }

        SetActiveSection(section ?? hostSection);

        // Load event into center panel
        section?.SetLoading(true);
        try
        {
            await LoadEventInCenterAsync(url, ct, channelCode, eventId);

            // Update standalone section with real event name + sidebar entry
            if (section != null && CurrentEvent != null)
            {
                var realName = CurrentEvent.Event.Name;
                section.Name = realName;
                section.SetEvents([new SportityEvent
                {
                    Id = eventId, Name = realName,
                    ChannelCode = channelCode, Url = url
                }]);
                section.TrySelectEvent(url);
            }

            // Update selection across all sections
            foreach (var s in Channels) s.TrySelectEvent(null);
            if (hostSection != null) hostSection.TrySelectEvent(url);
            else section?.TrySelectEvent(url);

            PersistChannels();
            StatusMessage = $"Event loaded — {CurrentEvent?.DisplayedItems.Count ?? 0} top-level item(s).";
        }
        finally
        {
            section?.SetLoading(false);
        }
    }

    // ── Left-panel event click ───────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadChannelEventAsync(SportityEvent ev)
    {
        if (ev == null) return;

        var hostSection = Channels.FirstOrDefault(s => s.Events.Any(e => e.Url == ev.Url));
        SetActiveSection(hostSection);

        var (channelCode, eventId) = ScraperService.ParseEventUrl(ev.Url);
        // Single-event channel: URL is a channel URL, not an event URL
        if (string.IsNullOrEmpty(channelCode) && string.IsNullOrEmpty(eventId))
        {
            channelCode = ScraperService.ParseChannelCode(ev.Url);
            eventId = ev.Name; // use the known display name
        }

        var ct = (_cts = new CancellationTokenSource()).Token;

        IsLoading = true;
        ShowProgress = false;
        StatusMessage = "Loading event…";
        try
        {
            await LoadEventInCenterAsync(ev.Url, ct, channelCode, eventId, ev.Name);

            foreach (var s in Channels) s.TrySelectEvent(null);
            hostSection?.TrySelectEvent(ev.Url);

            PersistChannels();
            StatusMessage = $"Event loaded — {CurrentEvent?.DisplayedItems.Count ?? 0} top-level item(s).";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (HttpRequestException ex) { StatusMessage = $"Network error: {ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally
        {
            IsLoading = false;
            ShowProgress = false;
        }
    }

    private async Task LoadEventInCenterAsync(string url, CancellationToken ct,
        string channelCode, string eventId, string? knownName = null)
    {
        var (items, pageTitle) = await _scraper.ScrapeEventAsync(url, ct);
        var (newNames, newIds, hadExisting) = FindNewItems(items);

        // Prefer known sidebar name → page title → event id (UUID fallback)
        var displayName = !string.IsNullOrEmpty(knownName) ? knownName
            : !string.IsNullOrEmpty(pageTitle) ? pageTitle
            : eventId;

        var ev = new SportityEvent { Id = eventId, Name = displayName, ChannelCode = channelCode, Url = url, Items = items };
        CurrentEvent = new EventViewModel(ev, _state);

        if (hadExisting && newIds.Count > 0)
        {
            ApplyNewBadges(CurrentEvent.DisplayedItems, newIds);
            CurrentEvent.RefreshAllBadges();
            var msg = newNames.Count == 1
                ? $"New file '{newNames[0]}' was added for event {displayName}"
                : $"New file '{newNames[0]}' and {newNames.Count - 1} more were added for event {displayName}";
            _tray.ShowNotification("SportityGui", msg);
        }

        if (AutoDownload)
            await AutoDownloadAllAsync(CurrentEvent.DisplayedItems, ct);
    }

    // ── Cancel ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusMessage = "Cancelling…";
    }

    // ── Refresh ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshCurrentEventAsync()
    {
        if (CurrentEvent == null) return;
        var url = CurrentEvent.Event.Url;
        var ct = (_cts = new CancellationTokenSource()).Token;
        IsLoading = true;
        StatusMessage = "Refreshing event…";
        try
        {
            var (channelCode, eventId) = ScraperService.ParseEventUrl(url);
            var currentName = CurrentEvent.Event.Name;
            await LoadEventInCenterAsync(url, ct, channelCode, eventId, currentName);
            StatusMessage = $"Refreshed — {CurrentEvent?.DisplayedItems.Count ?? 0} item(s).";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; ShowProgress = false; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var ct = (_cts = new CancellationTokenSource()).Token;
        IsLoading = true;
        StatusMessage = "Refreshing all…";
        try
        {
            // Refresh the currently open event (if any) and all sidebar channels in parallel
            var tasks = new List<Task>();

            if (CurrentEvent != null)
            {
                var url = CurrentEvent.Event.Url;
                var (channelCode, eventId) = ScraperService.ParseEventUrl(url);
                var currentName = CurrentEvent.Event.Name;
                tasks.Add(LoadEventInCenterAsync(url, ct, channelCode, eventId, currentName));
            }

            tasks.AddRange(Channels.ToList().Select(s => RefreshSectionAsync(s, ct)));

            await Task.WhenAll(tasks);
            StatusMessage = CurrentEvent != null
                ? $"Refreshed — {CurrentEvent.DisplayedItems.Count} item(s)."
                : "All channels refreshed.";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; ShowProgress = false; }
    }

    public async Task RefreshAllChannelsAsync()
    {
        if (Channels.Count == 0) return;
        StatusMessage = "Refreshing all channels…";
        var tasks = Channels.ToList().Select(s => RefreshSectionAsync(s, CancellationToken.None));
        await Task.WhenAll(tasks);
        StatusMessage = "All channels refreshed.";
    }

    public async Task RefreshSectionAsync(ChannelSectionViewModel section, CancellationToken ct = default)
    {
        section.IsLoading = true;
        section.HasError = false;
        try
        {
            if (section.Type == SavedChannelType.Channel)
            {
                var previousUrls = section.Events.Select(e => e.Url).ToHashSet();
                var channel = await _scraper.ScrapeChannelAsync(section.Url, ct);
                section.Name = channel.Name;
                section.SetEvents(channel.Events);

                var newEvents = channel.Events.Where(e => !previousUrls.Contains(e.Url)).ToList();
                if (previousUrls.Count > 0 && newEvents.Count > 0)
                    _tray.ShowNotification("SportityGui",
                        newEvents.Count == 1
                            ? $"New event '{newEvents[0].Name}' in {section.Name}"
                            : $"{newEvents.Count} new events in {section.Name}");
            }
            else
            {
                // StandaloneEvent: refresh items in the center panel if this is the active section
                if (_activeSection == section && CurrentEvent != null)
                {
                    var url = section.Url;
                    var (channelCode, eventId) = ScraperService.ParseEventUrl(url);
                    var knownName = CurrentEvent.Event.Name;
                    await LoadEventInCenterAsync(url, ct, channelCode, eventId, knownName);
                    section.TrySelectEvent(url);
                }
            }
            PersistChannels();
        }
        catch (OperationCanceledException) { /* swallow */ }
        catch (Exception ex)
        {
            section.HasError = true;
            section.ErrorMessage = $"⚠ {ex.Message}";
        }
        finally { section.IsLoading = false; }
    }

    // ── Channel removal (M8-6) ───────────────────────────────────────────────

    public void RemoveChannel(ChannelSectionViewModel section, bool deleteFiles)
    {
        if (deleteFiles)
        {
            var channelCode = section.ChannelCode.Length > 0 ? section.ChannelCode
                : SanitizeFolderName(section.Name.Length > 0 ? section.Name : "Sportity");
            var folder = Path.Combine(_state.Preferences.DownloadFolder, SanitizeFolderName(channelCode));
            _state.DeleteChannelFiles(folder);
        }

        section.StopTimer();
        Channels.Remove(section);
        OnPropertyChanged(nameof(HasChannels));

        if (_activeSection == section)
        {
            SetActiveSection(null);
            CurrentEvent = null;
            SelectedItem = null;
        }

        PersistChannels();
    }

    // ── Per-section refresh interval (M8-7) ─────────────────────────────────

    // Proxy displayed in the toolbar — shows the active section's value (or global default if none)
    public string ChannelAutoRefreshDisplay =>
        _activeSection?.AutoRefreshDisplay
        ?? (AutoRefreshMinutes == 0 ? "Off" : $"{AutoRefreshMinutes} min");

    private void SetActiveSection(ChannelSectionViewModel? section)
    {
        if (_activeSection == section) return;
        if (_activeSection != null)
            _activeSection.PropertyChanged -= OnActiveSectionPropertyChanged;
        _activeSection = section;
        if (_activeSection != null)
            _activeSection.PropertyChanged += OnActiveSectionPropertyChanged;
        OnPropertyChanged(nameof(ChannelAutoRefreshDisplay));
        RefreshCountdownDisplay = _activeSection?.RefreshCountdown ?? string.Empty;
    }

    private void OnActiveSectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelSectionViewModel.AutoRefreshDisplay)
                           or nameof(ChannelSectionViewModel.AutoRefreshMinutes))
            OnPropertyChanged(nameof(ChannelAutoRefreshDisplay));
        else if (e.PropertyName == nameof(ChannelSectionViewModel.RefreshCountdown))
            RefreshCountdownDisplay = _activeSection?.RefreshCountdown ?? string.Empty;
    }

    [RelayCommand]
    private void IncreaseInterval()
    {
        if (_activeSection != null)
        {
            _activeSection.AutoRefreshMinutes = _activeSection.AutoRefreshMinutes switch
            { 0 => 5, < 30 => _activeSection.AutoRefreshMinutes + 5, _ => _activeSection.AutoRefreshMinutes + 15 };
            PersistChannels();
        }
        else
        {
            AutoRefreshMinutes = AutoRefreshMinutes switch { 0 => 5, < 30 => AutoRefreshMinutes + 5, _ => AutoRefreshMinutes + 15 };
        }
    }

    [RelayCommand]
    private void DecreaseInterval()
    {
        if (_activeSection != null)
        {
            _activeSection.AutoRefreshMinutes = _activeSection.AutoRefreshMinutes switch
            { <= 5 => 0, <= 30 => _activeSection.AutoRefreshMinutes - 5, _ => _activeSection.AutoRefreshMinutes - 15 };
            PersistChannels();
        }
        else
        {
            AutoRefreshMinutes = AutoRefreshMinutes switch { <= 5 => 0, <= 30 => AutoRefreshMinutes - 5, _ => AutoRefreshMinutes - 15 };
        }
    }

    // ── Startup restoration (M8-8) ───────────────────────────────────────────

    public async Task RestoreChannelsAsync()
    {
        var saved = _state.State.Channels.OrderBy(c => c.SortOrder).ToList();
        if (saved.Count == 0) return;

        foreach (var sc in saved)
        {
            // Always derive channel code from URL — sc.Name is the display title, not the code
            var section = CreateSection(sc.Url, sc.Name, sc.Type, sc.IsCollapsed,
                channelCode: null,
                autoRefreshMinutes: sc.AutoRefreshMinutes);
            Channels.Add(section);
        }
        OnPropertyChanged(nameof(HasChannels));

        // Refresh all in parallel (background — don't block UI)
        var tasks = Channels.ToList().Select(s => RefreshSectionAsync(s, CancellationToken.None));
        await Task.WhenAll(tasks);
    }

    // ── Item interaction (M8-10) ─────────────────────────────────────────────

    [RelayCommand]
    private async Task ItemClickedAsync(TreeItemViewModel? vm)
    {
        if (vm is null) return;
        SelectedItem = vm;

        if (vm.IsFolder) return;

        vm.MarkRead();

        if (vm.Model is FileItem)
            ShowFileMetadata(vm);
        else if (vm.Model is TextItem text)
            await ShowTextContentAsync(text);

        // Re-evaluate all folder badges after any item interaction
        CurrentEvent?.RefreshAllBadges();
    }

    [RelayCommand]
    private async Task ItemDoubleClickedAsync(TreeItemViewModel? vm)
    {
        if (vm?.IsFile != true) return;
        vm.MarkRead();

        if (!vm.IsDownloaded)
        {
            ShowProgress = true;
            StatusMessage = $"Downloading {vm.Name}…";
            var prog = new Progress<double>(p =>
            {
                DownloadProgress = p * 100;
                StatusMessage = $"Downloading {vm.Name} — {p:P0}";
            });
            try
            {
                var path = await _downloader.DownloadFileAsync(
                    (FileItem)vm.Model, GetEventDownloadFolder(), prog);
                vm.NotifyDownloaded(path);
                OnPropertyChanged(nameof(SelectedItemIsDownloaded));
                StatusMessage = $"Downloaded: {vm.Name}";
            }
            catch (Exception ex) { StatusMessage = $"Download failed: {ex.Message}"; }
            finally { ShowProgress = false; }
        }

        if (vm.IsDownloaded && vm.LocalPath != null)
        {
            try { Process.Start(new ProcessStartInfo(vm.LocalPath) { UseShellExecute = true }); }
            catch { /* file may have moved */ }
        }

        ShowFileMetadata(vm);
    }

    [RelayCommand]
    private async Task ViewSelectedAsync()
    {
        if (SelectedItem?.Model is not FileItem file) return;
        SelectedItem.MarkRead();
        ShowProgress = true;
        var prog = new Progress<double>(p =>
        {
            DownloadProgress = p * 100;
            StatusMessage = $"Loading {file.Name} — {p:P0}";
        });
        try
        {
            var path = await _downloader.ViewFileAsync(file, prog, _cts?.Token ?? default);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusMessage = $"Viewing: {file.Name}";
        }
        catch (Exception ex) { StatusMessage = $"View failed: {ex.Message}"; }
        finally { ShowProgress = false; }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (SelectedItem?.Model is not FileItem file || SelectedItem.IsDownloaded) return;
        var vm = SelectedItem;

        ShowProgress = true;
        StatusMessage = $"Downloading {file.Name}…";
        var prog = new Progress<double>(p =>
        {
            DownloadProgress = p * 100;
            StatusMessage = $"Downloading {file.Name} — {p:P0}";
        });
        try
        {
            var path = await _downloader.DownloadFileAsync(file, GetEventDownloadFolder(), prog);
            vm.NotifyDownloaded(path);
            OnPropertyChanged(nameof(SelectedItemIsDownloaded));
            ShowFileMetadata(vm);
            StatusMessage = $"Downloaded: {file.Name}";
        }
        catch (Exception ex) { StatusMessage = $"Download failed: {ex.Message}"; }
        finally { ShowProgress = false; }
    }

    [RelayCommand]
    private void OpenSelectedFile()
    {
        if (SelectedItem?.LocalPath is null) return;
        try { Process.Start(new ProcessStartInfo(SelectedItem.LocalPath) { UseShellExecute = true }); }
        catch { /* file may have moved */ }
    }

    // ── Folder download ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DownloadFolderAsync(TreeItemViewModel? vm)
    {
        if (vm?.Model is not FolderItem folder) return;

        ShowProgress = true;
        StatusMessage = $"Downloading folder: {folder.Name}…";
        var prog = new Progress<double>(p =>
        {
            DownloadProgress = p * 100;
            StatusMessage = $"Downloading {folder.Name} — {p:P0}";
        });
        try
        {
            _cts = new CancellationTokenSource();
            await _downloader.DownloadFolderAsync(folder, GetEventDownloadFolder(), prog, _cts.Token);
            StatusMessage = $"Folder downloaded: {folder.Name}";
        }
        catch (OperationCanceledException) { StatusMessage = "Download cancelled."; }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { ShowProgress = false; }
    }

    // ── Event context menu (sidebar right-click) ─────────────────────────────

    private async Task EnsureEventLoadedAsync(SportityEvent ev)
    {
        if (CurrentEvent?.Event.Url == ev.Url) return;
        await LoadChannelEventAsync(ev);
    }

    [RelayCommand]
    private async Task EventMarkAllReadAsync(SportityEvent ev)
    {
        if (ev == null) return;
        try
        {
            await EnsureEventLoadedAsync(ev);
            CurrentEvent?.MarkAllRead();
            StatusMessage = "All items marked as read.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task EventDownloadAllAsync(SportityEvent ev)
    {
        if (ev == null) return;
        var ct = (_cts = new CancellationTokenSource()).Token;
        ShowProgress = true;
        StatusMessage = $"Downloading all documents for '{ev.Name}'…";
        try
        {
            await EnsureEventLoadedAsync(ev);
            if (CurrentEvent != null)
            {
                await AutoDownloadAllAsync(CurrentEvent.DisplayedItems, ct);
                CurrentEvent.RefreshAllBadges();
            }
            StatusMessage = "All documents downloaded.";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (Exception ex) { StatusMessage = $"Download error: {ex.Message}"; }
        finally { ShowProgress = false; }
    }

    [RelayCommand]
    private async Task EventRemoveAllAsync(SportityEvent ev)
    {
        if (ev == null) return;
        try
        {
            await EnsureEventLoadedAsync(ev);
            if (CurrentEvent == null) return;

            _state.RemoveEventFiles(GetEventDownloadFolder());
            CurrentEvent.MarkAllUnread();
            OnPropertyChanged(nameof(SelectedItemIsDownloaded));
            StatusMessage = "All documents removed and items reset to unread.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    // ── Context menu actions ─────────────────────────────────────────────────

    [RelayCommand]
    private void CopyUrl(TreeItemViewModel? vm)
    {
        if (vm?.Model is FileItem f)
            System.Windows.Clipboard.SetText(f.DownloadUrl);
        else if (vm?.Model is TextItem t)
            System.Windows.Clipboard.SetText(t.ContentUrl);
    }

    [RelayCommand]
    private void OpenContainingFolder(TreeItemViewModel? vm)
    {
        if (vm?.LocalPath is null) return;
        var dir = Path.GetDirectoryName(vm.LocalPath);
        if (dir != null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void RemoveRecentUrl(string url)
    {
        _state.RemoveRecentUrl(url);
        RefreshRecentUrls();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ChannelSectionViewModel CreateSection(string url, string name, SavedChannelType type,
        bool isCollapsed = false, string? channelCode = null, int? autoRefreshMinutes = null)
    {
        var section = new ChannelSectionViewModel(url, name, type, isCollapsed, channelCode);
        section.OnRefreshRequested = s => _ = RefreshSectionAsync(s);
        section.AutoRefreshMinutes = autoRefreshMinutes ?? _state.Preferences.AutoRefreshMinutes;
        return section;
    }

    public void PersistChannels()
    {
        _state.State.Channels = Channels
            .Select((s, i) => new SavedChannel
            {
                Url = s.Url,
                Name = s.Name,
                IsCollapsed = s.IsCollapsed,
                SortOrder = i,
                Type = s.Type,
                AutoRefreshMinutes = s.AutoRefreshMinutes
            })
            .ToList();
        _state.Save();
    }

    private string GetEventDownloadFolder()
    {
        var channelCode = _activeSection?.ChannelCode ?? string.Empty;
        var eventName = CurrentEvent?.Event.Name ?? string.Empty;
        if (string.IsNullOrEmpty(eventName)) eventName = "Event";

        // When a channel code is known, organise as: base / channel / event
        // When no channel code, organise as: base / event  (no spurious fallback folder)
        return string.IsNullOrEmpty(channelCode)
            ? Path.Combine(_state.Preferences.DownloadFolder, SanitizeFolderName(eventName))
            : Path.Combine(_state.Preferences.DownloadFolder, SanitizeFolderName(channelCode), SanitizeFolderName(eventName));
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }

    private async Task AutoDownloadAllAsync(IEnumerable<TreeItemViewModel> items, CancellationToken ct)
    {
        foreach (var vm in items)
        {
            ct.ThrowIfCancellationRequested();
            if (vm.Model is FileItem file && !vm.IsDownloaded)
            {
                try
                {
                    var path = await _downloader.DownloadFileAsync(file, GetEventDownloadFolder(), null, ct);
                    vm.NotifyDownloaded(path);
                }
                catch { /* continue */ }
            }
            if (vm.Children.Count > 0)
                await AutoDownloadAllAsync(vm.Children, ct);
        }
    }

    public async Task CheckForUpdateAsync()
    {
        try
        {
            var (hasUpdate, remoteVersion, _) = await _updater.CheckAsync();
            if (hasUpdate)
            {
                _updateRemoteVersion = remoteVersion;
                UpdateBannerText = $"Version {remoteVersion} is available (you have {AppInfo.Version}). Download now?";
                UpdateBannerVisible = true;
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        UpdateBannerVisible = false;
        ShowProgress = true;
        StatusMessage = "Downloading update…";
        var prog = new Progress<double>(p =>
        {
            DownloadProgress = p * 100;
            StatusMessage = $"Downloading update — {p:P0}";
        });
        try
        {
            var path = await _updater.DownloadUpdateAsync(_updateRemoteVersion, prog);
            StatusMessage = $"Update downloaded: {path}";
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception ex) { StatusMessage = $"Update download failed: {ex.Message}"; }
        finally { ShowProgress = false; }
    }

    [RelayCommand]
    private void DismissUpdateBanner() => UpdateBannerVisible = false;

    private void ShowFileMetadata(TreeItemViewModel vm)
    {
        var firstSeen = vm.FirstSeen;
        var record = _state.GetDownloadRecord(vm.Model.Id);
        var meta = new System.Text.StringBuilder();
        meta.AppendLine($"Name:        {vm.Name}");
        if (firstSeen.HasValue)
            meta.AppendLine($"First seen:  {firstSeen.Value.ToLocalTime():g}");
        if (record != null)
        {
            meta.AppendLine($"Downloaded:  {record.DownloadedAt.ToLocalTime():g}");
            meta.AppendLine($"Local path:  {record.LocalPath}");
        }
        DetailMetadata = meta.ToString();
        DetailText = string.Empty;
    }

    private async Task ShowTextContentAsync(TextItem text)
    {
        if (text.InlineContent != null)
        {
            DetailText = text.InlineContent;
            DetailMetadata = $"Name: {text.Name}";
            return;
        }

        StatusMessage = "Loading message…";
        try
        {
            var html = await _scraper.FetchPublicAsync(text.ContentUrl);
            text.InlineContent = await _scraper.ExtractTextContentAsync(html);
            DetailText = text.InlineContent ?? string.Empty;
            StatusMessage = "Ready";
        }
        catch (Exception ex) { StatusMessage = $"Could not load message: {ex.Message}"; }
        DetailMetadata = $"Name: {text.Name}";
    }

    private void RefreshRecentUrls()
    {
        RecentUrls.Clear();
        foreach (var url in _state.State.RecentUrls)
            RecentUrls.Add(url);
    }

    private (List<string> Names, HashSet<string> NewIds, bool HadExisting) FindNewItems(IEnumerable<TreeItem> items)
    {
        var names  = new List<string>();
        var newIds = new HashSet<string>();
        bool hadExisting = false;
        ScanItems(items, names, newIds, ref hadExisting);
        return (names, newIds, hadExisting);
    }

    private void ScanItems(IEnumerable<TreeItem> items, List<string> newNames, HashSet<string> newIds, ref bool hadExisting)
    {
        foreach (var item in items)
        {
            if (item is FolderItem folder)
                ScanItems(folder.Children, newNames, newIds, ref hadExisting);
            else if (_state.GetFirstSeen(item.Id) == null)
            {
                newNames.Add(item.Name);
                newIds.Add(item.Id);
            }
            else
                hadExisting = true;
        }
    }

    private static void ApplyNewBadges(IEnumerable<TreeItemViewModel> vms, HashSet<string> newIds)
    {
        foreach (var vm in vms)
        {
            if (newIds.Contains(vm.Model.Id))
                vm.NotifyNew();
            if (vm.Children.Count > 0)
                ApplyNewBadges(vm.Children, newIds);
        }
    }
}
