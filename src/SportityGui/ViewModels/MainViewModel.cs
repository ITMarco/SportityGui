using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
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
    private readonly IDialogService _dialogs;

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

    public MainViewModel(StateService state, ScraperService scraper, DownloadService downloader, TrayService tray, UpdateService updater,
        IDialogService dialogs)
    {
        _dialogs = dialogs;
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
            section = CreateSection(url, eventId, SavedChannelType.StandaloneEvent, channelCode: channelCode,
                downloadFolder: _state.GetEventFolder(url));
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
        {
            var failed = await DownloadAllInCurrentEventAsync(ct);
            if (failed > 0 && !_dialogs.CanPrompt)
                _tray.ShowNotification("SportityGui",
                    $"{failed} file(s) of {displayName} could not be saved. Open SportityGui to choose another download folder.");
        }
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
            // Refresh all sidebar channels in parallel; each channel section also refreshes
            // the center panel when the currently open event belongs to it
            var tasks = new List<Task>();

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

                if (CurrentEvent != null && channel.Events.Any(e => e.Url == CurrentEvent.Event.Url))
                {
                    var eventUrl = CurrentEvent.Event.Url;
                    var (channelCode, eventId) = ScraperService.ParseEventUrl(eventUrl);
                    await LoadEventInCenterAsync(eventUrl, ct, channelCode, eventId, CurrentEvent.Event.Name);
                }

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
            var roots = section.Events.Select(e => ResolveEventFolder(e, section)).ToList();
            if (section.Type == SavedChannelType.Channel)
            {
                if (ChannelBaseFolder(section, section.ChannelCode) is { } channelBase) roots.Add(channelBase);
            }
            else roots.Add(ResolveEventFolder(SectionEvent(section), section));

            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                _state.DeleteDownloadsUnder(root);
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
                autoRefreshMinutes: sc.AutoRefreshMinutes,
                downloadFolder: sc.Type == SavedChannelType.Channel ? sc.DownloadFolder : _state.GetEventFolder(sc.Url));
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
        CurrentEvent?.RefreshAllBadges();

        if (!vm.IsDownloaded && !await DownloadItemAsync(vm))
        {
            ShowFileMetadata(vm);
            return;
        }

        if (vm.LocalPath != null && !TryOpenFile(vm.LocalPath, out var error))
            StatusMessage = $"Could not open {vm.Name}: {error}";

        ShowFileMetadata(vm);
    }

    /// <summary>
    /// View = open the downloaded copy if there is one, otherwise download to the system temp folder
    /// and open it from there. If that fails (and it isn't a network problem), let the user choose a
    /// folder to download the file to, and open it from there.
    /// </summary>
    [RelayCommand]
    private async Task ViewSelectedAsync()
    {
        if (SelectedItem is not { Model: FileItem file } vm) return;
        vm.MarkRead();
        CurrentEvent?.RefreshAllBadges();

        if (vm.IsDownloaded && vm.LocalPath != null && TryOpenFile(vm.LocalPath, out _))
        {
            StatusMessage = $"Viewing: {file.Name}";
            return;
        }

        var prog = new Progress<double>(p =>
        {
            DownloadProgress = p * 100;
            StatusMessage = $"Loading {file.Name} — {p:P0}";
        });

        Exception failure;
        ShowProgress = true;
        try
        {
            var tempPath = await _downloader.DownloadToTempAsync(file, prog);
            OpenFile(tempPath);
            StatusMessage = $"Viewing: {file.Name}";
            return;
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            StatusMessage = $"View failed: {ex.Message}";
            return;
        }
        catch (Exception ex) { failure = ex; }
        finally { ShowProgress = false; }

        // The system temp folder did not work → ask where to download the file instead
        var ev = CurrentEvent?.Event;
        var section = _activeSection;
        var folder = AskFallbackFolder($"Could not view \"{file.Name}\"", failure, CurrentEventFolder());
        if (folder == null)
        {
            StatusMessage = $"View failed: {failure.Message}";
            return;
        }

        var scope = new FallbackScope { Folder = folder };
        ShowProgress = true;
        try
        {
            var path = await DownloadWithFallbackAsync(vm, folder, prog, default, scope);
            OnPropertyChanged(nameof(SelectedItemIsDownloaded));
            ShowFileMetadata(vm);
            OpenFile(path);
            StatusMessage = $"Viewing: {file.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"View failed: {ex.Message}";
            _dialogs.ShowError($"Could not view \"{file.Name}\":\n\n{ex.Message}");
            return;
        }
        finally { ShowProgress = false; }

        await OfferRememberFolderAsync(scope, ev, section);
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (SelectedItem is not { Model: FileItem } vm || vm.IsDownloaded) return;
        if (await DownloadItemAsync(vm))
            ShowFileMetadata(vm);
    }

    [RelayCommand]
    private void OpenSelectedFile()
    {
        if (SelectedItem?.LocalPath is null) return;
        if (!TryOpenFile(SelectedItem.LocalPath, out var error))
            StatusMessage = $"Could not open {SelectedItem.Name}: {error}";
    }

    /// Downloads one file into the current event's folder (with folder fallback). Returns success.
    private async Task<bool> DownloadItemAsync(TreeItemViewModel vm)
    {
        var ev = CurrentEvent?.Event;
        var section = _activeSection;
        var scope = new FallbackScope();

        ShowProgress = true;
        StatusMessage = $"Downloading {vm.Name}…";
        var prog = new Progress<double>(p =>
        {
            DownloadProgress = p * 100;
            StatusMessage = $"Downloading {vm.Name} — {p:P0}";
        });
        try
        {
            await DownloadWithFallbackAsync(vm, CurrentEventFolder(), prog, default, scope);
            OnPropertyChanged(nameof(SelectedItemIsDownloaded));
            StatusMessage = $"Downloaded: {vm.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
            // The user already picked another folder and that failed too → make it visible
            if (scope.Folder != null)
                _dialogs.ShowError($"Could not download \"{vm.Name}\":\n\n{ex.Message}");
            return false;
        }
        finally { ShowProgress = false; }

        await OfferRememberFolderAsync(scope, ev, section);
        return true;
    }

    // ── Folder download ──────────────────────────────────────────────────────


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
    private async Task EventMarkAllUnreadAsync(SportityEvent ev)
    {
        if (ev == null) return;
        try
        {
            await EnsureEventLoadedAsync(ev);
            CurrentEvent?.MarkAllUnread(clearDownloads: false);
            StatusMessage = "All items marked as unread.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    [RelayCommand]
    private void EventOpenInBrowser(SportityEvent ev)
    {
        if (ev == null) return;
        Process.Start(new ProcessStartInfo(ev.Url) { UseShellExecute = true });
    }

    [RelayCommand]
    private void EventOpenFolder(SportityEvent ev)
    {
        if (ev == null) return;
        OpenFolderInExplorer(ResolveEventFolder(ev, FindSection(ev)));
    }

    [RelayCommand]
    private void EventCopyUrl(SportityEvent ev)
    {
        if (ev == null) return;
        System.Windows.Clipboard.SetText(ev.Url);
    }

    [RelayCommand]
    private void MarkSelectedRead()
    {
        if (SelectedItem == null) return;
        SelectedItem.MarkRead();
        CurrentEvent?.RefreshAllBadges();
    }

    [RelayCommand]
    private void MarkSelectedUnread()
    {
        if (SelectedItem == null) return;
        SelectedItem.MarkUnread();
        CurrentEvent?.RefreshAllBadges();
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
            var failed = 0;
            if (CurrentEvent != null)
            {
                failed = await DownloadAllInCurrentEventAsync(ct);
                CurrentEvent.RefreshAllBadges();
            }
            StatusMessage = failed == 0
                ? "All documents downloaded."
                : $"{failed} document(s) could not be downloaded.";
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

            _state.DeleteDownloadsUnder(CurrentEventFolder());
            CurrentEvent.MarkAllUnread();
            OnPropertyChanged(nameof(SelectedItemIsDownloaded));
            StatusMessage = "All documents removed and items reset to unread.";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
    }

    // ── Context menu actions ─────────────────────────────────────────────────

    [RelayCommand]
    private void CopyUrl()
    {
        if (SelectedItem?.Model is FileItem f)
            System.Windows.Clipboard.SetText(f.DownloadUrl);
        else if (SelectedItem?.Model is TextItem t)
            System.Windows.Clipboard.SetText(t.ContentUrl);
    }

    [RelayCommand]
    private void OpenContainingFolder()
    {
        if (SelectedItem?.LocalPath is null) return;
        var dir = Path.GetDirectoryName(SelectedItem.LocalPath);
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
        bool isCollapsed = false, string? channelCode = null, int? autoRefreshMinutes = null,
        string? downloadFolder = null)
    {
        var section = new ChannelSectionViewModel(url, name, type, isCollapsed, channelCode, downloadFolder);
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
                AutoRefreshMinutes = s.AutoRefreshMinutes,
                // Standalone events keep their folder in the per-event map instead
                DownloadFolder = s.Type == SavedChannelType.Channel ? s.DownloadFolder : null
            })
            .ToList();
        _state.Save();
    }

    private async Task<int> DownloadAllInCurrentEventAsync(CancellationToken ct)
    {
        if (CurrentEvent == null) return 0;
        var ev = CurrentEvent.Event;
        var section = _activeSection;
        var scope = new FallbackScope();
        var failed = await DownloadAllAsync(CurrentEvent.DisplayedItems, ResolveEventFolder(ev, section), scope, ct);
        await OfferRememberFolderAsync(scope, ev, section);
        return failed;
    }

    /// Downloads all files below <paramref name="items"/>; returns the number of files that failed.
    private async Task<int> DownloadAllAsync(IEnumerable<TreeItemViewModel> items, string folder,
        FallbackScope scope, CancellationToken ct)
    {
        int failed = 0;
        foreach (var vm in items.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (vm.Model is FileItem && !vm.IsDownloaded)
            {
                try { await DownloadWithFallbackAsync(vm, folder, null, ct, scope); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { failed++; }
            }
            if (vm.Children.Count > 0)
                failed += await DownloadAllAsync(vm.Children, folder, scope, ct);
        }
        return failed;
    }

    // ── Download fallback (folder picker when the target folder can't be written) ──

    /// Remembers the user's fallback choice for one user action (a single file or a bulk
    /// download), so a bulk download asks at most once.
    private sealed class FallbackScope
    {
        public string? Folder;
        public bool Declined;
    }

    private static bool IsNetworkError(Exception ex) =>
        ex is HttpRequestException or HttpIOException
        || ex is TaskCanceledException { InnerException: TimeoutException };

    /// <summary>
    /// Downloads the file of <paramref name="vm"/> into <paramref name="targetFolder"/>. If writing
    /// fails because of the local file system (permissions, file in use…), asks once per scope for
    /// another folder and retries there; later files in the same scope go straight to that folder.
    /// </summary>
    private async Task<string> DownloadWithFallbackAsync(TreeItemViewModel vm, string targetFolder,
        IProgress<double>? progress, CancellationToken ct, FallbackScope scope)
    {
        var file = (FileItem)vm.Model;
        string path;
        try
        {
            path = await _downloader.DownloadFileAsync(file, scope.Folder ?? targetFolder, progress, ct);
        }
        catch (LocalFileException ex) when (scope.Folder == null && !scope.Declined)
        {
            var chosen = AskFallbackFolder($"Could not save \"{file.Name}\"", ex, targetFolder);
            if (chosen == null) { scope.Declined = true; throw; }
            scope.Folder = chosen;
            path = await _downloader.DownloadFileAsync(file, chosen, progress, ct);
        }
        vm.NotifyDownloaded(path);
        return path;
    }

    private string? AskFallbackFolder(string what, Exception ex, string? initialFolder)
    {
        var reason = ShortReason(ex);
        StatusMessage = $"{what}: {ex.Message}";
        return PickWritableFolder($"{what} ({reason}) — choose a folder to download to", initialFolder);
    }

    /// A short, path-free explanation for dialog titles.
    private static string ShortReason(Exception ex)
    {
        var inner = ex is LocalFileException { InnerException: { } i } ? i : ex;
        const int sharingViolation = unchecked((int)0x80070020), lockViolation = unchecked((int)0x80070021);
        return inner switch
        {
            UnauthorizedAccessException => "access denied",
            DirectoryNotFoundException => "folder not found",
            PathTooLongException => "path too long",
            IOException io when io.HResult is sharingViolation or lockViolation => "the file is open in another program",
            IOException io when io.HResult == unchecked((int)0x80070070) => "disk is full",
            _ => inner.Message.TrimEnd('.')
        };
    }

    /// After a fallback folder was used, offer to make it the event's download folder.
    private async Task OfferRememberFolderAsync(FallbackScope scope, SportityEvent? ev, ChannelSectionViewModel? section)
    {
        if (scope.Folder == null || ev == null) return;
        if (PathHelper.SameFolder(ResolveEventFolder(ev, section), scope.Folder)) return;

        var answer = _dialogs.Ask(
            $"Use this folder for all downloads of \"{ev.Name}\" from now on?\n\n{scope.Folder}",
            MessageBoxButton.YesNo, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            await ChangeEventFolderAsync(ev, section, scope.Folder);
    }

    /// Folder picker that only accepts folders SportityGui can actually write to.
    private string? PickWritableFolder(string title, string? initialFolder)
    {
        while (true)
        {
            var picked = _dialogs.PickFolder(title, initialFolder);
            if (picked == null) return null;
            if (PathHelper.CanWrite(picked, out var error)) return picked;
            _dialogs.ShowError($"SportityGui cannot write to\n{picked}\n\n{error}\n\nPlease choose another folder.");
            initialFolder = picked;
        }
    }

    // ── Download folder resolution ──────────────────────────────────────────
    // Event folder = custom event folder
    //             ?? (custom channel folder ?? DownloadFolder/ChannelCode) / EventName
    //             ?? DownloadFolder/EventName   (when no channel code is known)

    private ChannelSectionViewModel? FindSection(SportityEvent ev) =>
        Channels.FirstOrDefault(s => s.Events.Contains(ev))
        ?? Channels.FirstOrDefault(s => s.Events.Any(e => e.Url == ev.Url))
        ?? Channels.FirstOrDefault(s => s.Type == SavedChannelType.StandaloneEvent && s.Url == ev.Url);

    /// The event of a standalone-event section (synthesised if it hasn't loaded yet).
    private static SportityEvent SectionEvent(ChannelSectionViewModel section) =>
        section.Events.FirstOrDefault(e => e.Url == section.Url)
        ?? new SportityEvent
        {
            Id = ScraperService.ParseEventUrl(section.Url).eventId,
            Name = section.Name,
            ChannelCode = section.ChannelCode,
            Url = section.Url
        };

    /// Base folder holding a channel's event folders; null when no channel code is known.
    private string? ChannelBaseFolder(ChannelSectionViewModel? section, string channelCode)
    {
        if (section is { Type: SavedChannelType.Channel, HasCustomFolder: true })
            return section.DownloadFolder;
        var code = !string.IsNullOrEmpty(section?.ChannelCode) ? section.ChannelCode : channelCode;
        return string.IsNullOrEmpty(code)
            ? null
            : Path.Combine(_state.Preferences.DownloadFolder, PathHelper.SanitizeName(code));
    }

    private string ResolveEventFolder(SportityEvent ev, ChannelSectionViewModel? section)
    {
        if (_state.GetEventFolder(ev.Url) is { } custom) return custom;
        var eventName = PathHelper.SanitizeName(string.IsNullOrEmpty(ev.Name) ? "Event" : ev.Name);
        return Path.Combine(ChannelBaseFolder(section, ev.ChannelCode) ?? _state.Preferences.DownloadFolder, eventName);
    }

    private string CurrentEventFolder() =>
        CurrentEvent != null
            ? ResolveEventFolder(CurrentEvent.Event, _activeSection)
            : _state.Preferences.DownloadFolder;

    // ── Custom download folders (sidebar context menus) ─────────────────────

    [RelayCommand]
    private async Task EventSetFolderAsync(SportityEvent ev)
    {
        if (ev == null) return;
        var section = FindSection(ev);
        var picked = PickWritableFolder($"Choose the download folder for \"{ev.Name}\"", ResolveEventFolder(ev, section));
        if (picked != null)
            await ChangeEventFolderAsync(ev, section, picked);
    }

    [RelayCommand]
    private async Task EventResetFolderAsync(SportityEvent ev)
    {
        if (ev == null) return;
        if (_state.GetEventFolder(ev.Url) == null)
        {
            StatusMessage = $"\"{ev.Name}\" already uses the default download folder.";
            return;
        }
        await ChangeEventFolderAsync(ev, FindSection(ev), null);
    }

    [RelayCommand]
    private async Task SectionSetFolderAsync(ChannelSectionViewModel section)
    {
        if (section == null) return;
        if (section.Type == SavedChannelType.StandaloneEvent)
        {
            await EventSetFolderAsync(SectionEvent(section));
            return;
        }
        var current = ChannelBaseFolder(section, section.ChannelCode) ?? _state.Preferences.DownloadFolder;
        var picked = PickWritableFolder(
            $"Choose the download folder for channel \"{section.Name}\" (each event gets its own subfolder)", current);
        if (picked != null)
            await ChangeChannelFolderAsync(section, picked);
    }

    [RelayCommand]
    private async Task SectionResetFolderAsync(ChannelSectionViewModel section)
    {
        if (section == null) return;
        if (section.Type == SavedChannelType.StandaloneEvent)
        {
            await EventResetFolderAsync(SectionEvent(section));
            return;
        }
        if (!section.HasCustomFolder)
        {
            StatusMessage = $"\"{section.Name}\" already uses the default download folder.";
            return;
        }
        await ChangeChannelFolderAsync(section, null);
    }

    [RelayCommand]
    private void SectionOpenFolder(ChannelSectionViewModel section)
    {
        if (section == null) return;
        OpenFolderInExplorer(section.Type == SavedChannelType.StandaloneEvent
            ? ResolveEventFolder(SectionEvent(section), section)
            : ChannelBaseFolder(section, section.ChannelCode) ?? _state.Preferences.DownloadFolder);
    }

    private async Task ChangeEventFolderAsync(SportityEvent ev, ChannelSectionViewModel? section, string? newCustom)
    {
        var previous = _state.GetEventFolder(ev.Url);
        var oldFolder = ResolveEventFolder(ev, section);
        _state.SetEventFolder(ev.Url, newCustom);
        var newFolder = ResolveEventFolder(ev, section);

        if (!await OfferMoveFilesAsync(oldFolder, newFolder, []))
        {
            _state.SetEventFolder(ev.Url, previous);
            StatusMessage = "Download folder not changed.";
            return;
        }

        if (section is { Type: SavedChannelType.StandaloneEvent })
            section.DownloadFolder = newCustom;
        AfterFolderChange();
        StatusMessage = $"\"{ev.Name}\" now downloads to {newFolder}";
    }

    private async Task ChangeChannelFolderAsync(ChannelSectionViewModel section, string? newCustom)
    {
        var previous = section.DownloadFolder;
        var oldBase = ChannelBaseFolder(section, section.ChannelCode);
        section.DownloadFolder = newCustom;
        var newBase = ChannelBaseFolder(section, section.ChannelCode);

        // Events with their own custom folder are not affected by the channel folder
        var exclude = section.Events.Select(e => _state.GetEventFolder(e.Url)).OfType<string>().ToList();
        if (oldBase != null && newBase != null && !await OfferMoveFilesAsync(oldBase, newBase, exclude))
        {
            section.DownloadFolder = previous;
            StatusMessage = "Download folder not changed.";
            return;
        }

        PersistChannels();
        AfterFolderChange();
        StatusMessage = $"Channel \"{section.Name}\" now downloads to {newBase}";
    }

    /// Called after the default download folder was changed in Preferences.
    public async Task OnDefaultDownloadFolderChangedAsync(string oldFolder, string newFolder)
    {
        // Channels/events with a custom folder are not affected by the default folder
        var exclude = Channels
            .Where(s => s.Type == SavedChannelType.Channel && s.HasCustomFolder)
            .Select(s => s.DownloadFolder!)
            .Concat(_state.State.EventDownloadFolders.Values)
            .ToList();

        if (!await OfferMoveFilesAsync(oldFolder, newFolder, exclude))
        {
            _state.Preferences.DownloadFolder = oldFolder;
            _state.SavePreferences();
            StatusMessage = "Download folder not changed.";
            return;
        }
        AfterFolderChange();
    }

    /// <summary>
    /// Asks whether downloaded files under <paramref name="oldRoot"/> should move to
    /// <paramref name="newRoot"/> (keeping their relative paths). Returns false when the user
    /// cancels, meaning the folder change should be reverted.
    /// </summary>
    private async Task<bool> OfferMoveFilesAsync(string oldRoot, string newRoot, IReadOnlyCollection<string> excludeRoots)
    {
        if (PathHelper.SameFolder(oldRoot, newRoot)) return true;

        var files = _state.GetDownloadsUnder(oldRoot)
            .Where(d => File.Exists(d.Record.LocalPath)
                        && !PathHelper.IsUnder(d.Record.LocalPath, newRoot)
                        && !excludeRoots.Any(x => PathHelper.IsUnder(d.Record.LocalPath, x)))
            .ToList();
        if (files.Count == 0) return true;

        var answer = _dialogs.Ask(
            $"{files.Count} downloaded file(s) are in\n{oldRoot}\n\nMove them to\n{newRoot}?\n\n" +
            "Yes — move the files\nNo — leave the files where they are\nCancel — keep the old folder",
            MessageBoxButton.YesNoCancel, MessageBoxResult.No);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer != MessageBoxResult.Yes) return true;

        var jobs = files
            .Select(f => (f.Id, Source: f.Record.LocalPath,
                          Target: Path.Combine(newRoot, Path.GetRelativePath(oldRoot, f.Record.LocalPath))))
            .ToList();

        StatusMessage = $"Moving {jobs.Count} file(s)…";
        DownloadProgress = 0;
        ShowProgress = true;
        var progress = new Progress<double>(p => DownloadProgress = p * 100);
        try
        {
            var (moved, errors) = await Task.Run(() => MoveFiles(jobs, oldRoot, progress));
            _state.UpdateDownloadPaths(moved);

            if (errors.Count == 0)
                StatusMessage = $"Moved {moved.Count} file(s) to {newRoot}";
            else
            {
                StatusMessage = $"Moved {moved.Count} file(s); {errors.Count} could not be moved.";
                _dialogs.ShowError($"{errors.Count} file(s) could not be moved and stay in the old folder:\n\n"
                                   + string.Join("\n", errors.Take(10))
                                   + (errors.Count > 10 ? "\n…" : string.Empty));
            }
        }
        finally { ShowProgress = false; }
        return true;
    }

    private static (List<(string Id, string NewPath)> Moved, List<string> Errors) MoveFiles(
        List<(string Id, string Source, string Target)> jobs, string oldRoot, IProgress<double> progress)
    {
        var moved = new List<(string, string)>();
        var errors = new List<string>();
        for (int i = 0; i < jobs.Count; i++)
        {
            var (id, source, target) = jobs[i];
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                target = PathHelper.UniqueFilePath(target);
                File.Move(source, target);
                moved.Add((id, target));
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(source)}: {ex.Message}"); }
            progress.Report((i + 1.0) / jobs.Count);
        }

        // Remove folders left empty below the old root (never above it)
        foreach (var dir in jobs.Select(j => Path.GetDirectoryName(j.Source)).Distinct())
        {
            var d = dir;
            while (d != null && PathHelper.IsUnder(d, oldRoot))
            {
                try
                {
                    if (!Directory.Exists(d) || Directory.EnumerateFileSystemEntries(d).Any()) break;
                    Directory.Delete(d);
                }
                catch { break; }
                d = Path.GetDirectoryName(d);
            }
        }
        return (moved, errors);
    }

    private void AfterFolderChange()
    {
        CurrentEvent?.ReloadDownloadState();
        CurrentEvent?.RefreshAllBadges();
        OnPropertyChanged(nameof(SelectedItemIsDownloaded));
        if (SelectedItem is { IsFile: true } vm) ShowFileMetadata(vm);
    }

    // ── Shell helpers ────────────────────────────────────────────────────────

    private void OpenFile(string path) => _dialogs.OpenFile(path);

    private bool TryOpenFile(string path, out string error)
    {
        try
        {
            if (!File.Exists(path)) { error = "file not found"; return false; }
            OpenFile(path);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private void OpenFolderInExplorer(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { StatusMessage = $"Could not open folder: {ex.Message}"; }
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
            ShowProgress = false;
            StatusMessage = UpdateUi.AfterDownload(path, _updateRemoteVersion);
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
        else if (CurrentEvent != null)
            meta.AppendLine($"Saves to:    {CurrentEventFolder()}");
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
