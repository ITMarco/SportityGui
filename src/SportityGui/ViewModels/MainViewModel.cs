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

    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _urlInput = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _autoDownload;
    [ObservableProperty] private EventViewModel? _currentEvent;
    [ObservableProperty] private TreeItemViewModel? _selectedItem;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string _detailMetadata = string.Empty;
    [ObservableProperty] private SportityEvent? _selectedChannelEvent;

    private string _currentChannelCode = string.Empty;
    private string _currentEventName = string.Empty;

    public ObservableCollection<string> RecentUrls { get; } = [];
    public ObservableCollection<SportityEvent> ChannelEvents { get; } = [];

    public bool HasChannelEvents => ChannelEvents.Count > 0;

    public MainViewModel(StateService state, ScraperService scraper, DownloadService downloader)
    {
        _state = state;
        _scraper = scraper;
        _downloader = downloader;

        AutoDownload = state.Preferences.AutoDownload;

        foreach (var url in state.State.RecentUrls)
            RecentUrls.Add(url);
    }

    partial void OnAutoDownloadChanged(bool value)
    {
        _state.Preferences.AutoDownload = value;
        _state.SavePreferences();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var url = UrlInput.Trim();
        if (string.IsNullOrEmpty(url)) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsLoading = true;
        ShowProgress = false;
        ChannelEvents.Clear();
        CurrentEvent = null;
        StatusMessage = "Connecting to Sportity…";

        try
        {
            var mode = ScraperService.DetectMode(url);

            if (mode == ScraperService.UrlMode.Channel)
            {
                StatusMessage = "Loading channel…";
                var channel = await _scraper.ScrapeChannelAsync(url, ct);
                _currentChannelCode = channel.Code;
                foreach (var ev in channel.Events)
                    ChannelEvents.Add(ev);
                OnPropertyChanged(nameof(HasChannelEvents));
                StatusMessage = $"Channel loaded — {channel.Events.Count} event(s) found.";
            }
            else if (mode == ScraperService.UrlMode.Event)
            {
                await LoadEventAsync(url, ct);
            }
            else
            {
                StatusMessage = "Unrecognised URL. Use a Sportity channel or event URL.";
                return;
            }

            _state.AddRecentUrl(url);
            RefreshRecentUrls();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Network error: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            ShowProgress = false;
        }
    }

    [RelayCommand]
    private async Task LoadChannelEventAsync(SportityEvent ev)
    {
        UrlInput = ev.Url;
        await LoadEventAsync(ev.Url, CancellationToken.None, ev.Name);
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusMessage = "Cancelling…";
    }

    [RelayCommand]
    private async Task ItemClickedAsync(TreeItemViewModel? vm)
    {
        if (vm is null) return;
        SelectedItem = vm;

        if (vm.IsFolder)
        {
            vm.IsExpanded = !vm.IsExpanded;
            return;
        }

        vm.MarkRead();

        if (vm.Model is FileItem file)
        {
            if (!vm.IsDownloaded)
            {
                ShowProgress = true;
                StatusMessage = $"Downloading {file.Name}…";
                var prog = new Progress<double>(p =>
                {
                    DownloadProgress = p * 100;
                    StatusMessage = $"Downloading {file.Name} — {p:P0}";
                });
                try
                {
                    var path = await _downloader.DownloadFileAsync(
                        file, GetEventDownloadFolder(), prog);
                    vm.NotifyDownloaded(path);
                    StatusMessage = $"Downloaded: {file.Name}";
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Download failed: {ex.Message}";
                }
                finally { ShowProgress = false; }
            }
            ShowFileMetadata(vm);
        }
        else if (vm.Model is TextItem text)
        {
            await ShowTextContentAsync(text);
        }
    }

    [RelayCommand]
    private void ItemDoubleClicked(TreeItemViewModel? vm)
    {
        if (vm?.IsFile != true || !vm.IsDownloaded || vm.LocalPath is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(vm.LocalPath) { UseShellExecute = true });
        }
        catch { /* file may have moved */ }
    }

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
            await _downloader.DownloadFolderAsync(
                folder, GetEventDownloadFolder(), prog, _cts.Token);
            StatusMessage = $"Folder downloaded: {folder.Name}";
        }
        catch (OperationCanceledException) { StatusMessage = "Download cancelled."; }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { ShowProgress = false; }
    }

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
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                { UseShellExecute = true });
    }

    [RelayCommand]
    private void RemoveRecentUrl(string url)
    {
        _state.RemoveRecentUrl(url);
        RefreshRecentUrls();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (!string.IsNullOrEmpty(UrlInput))
            _ = LoadAsync();
    }

    private async Task LoadEventAsync(string url, CancellationToken ct, string? knownName = null)
    {
        StatusMessage = "Loading event…";
        var items = await _scraper.ScrapeEventAsync(url, ct);

        var (channelCode, eventId) = ScraperService.ParseEventUrl(url);
        if (!string.IsNullOrEmpty(channelCode)) _currentChannelCode = channelCode;

        var eventName = knownName ?? eventId;
        _currentEventName = eventName;

        var ev = new SportityEvent
        {
            Id = eventId,
            Name = eventName,
            ChannelCode = channelCode,
            Url = url,
            Items = items
        };

        CurrentEvent = new EventViewModel(ev, _state);

        if (AutoDownload)
            await AutoDownloadAllAsync(CurrentEvent.DisplayedItems, ct);

        StatusMessage = $"Event loaded — {items.Count} top-level item(s).";
    }

    private string GetEventDownloadFolder() =>
        Path.Combine(
            _state.Preferences.DownloadFolder,
            SanitizeFolderName(_currentChannelCode.Length > 0 ? _currentChannelCode : "Sportity"),
            SanitizeFolderName(_currentEventName.Length > 0 ? _currentEventName : "Event"));

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }

    private async Task AutoDownloadAllAsync(
        IEnumerable<TreeItemViewModel> items, CancellationToken ct)
    {
        foreach (var vm in items)
        {
            ct.ThrowIfCancellationRequested();
            if (vm.Model is FileItem file && !vm.IsDownloaded)
            {
                try
                {
                    var path = await _downloader.DownloadFileAsync(
                        file, GetEventDownloadFolder(), null, ct);
                    vm.NotifyDownloaded(path);
                }
                catch { /* continue with next file */ }
            }
            if (vm.Children.Count > 0)
                await AutoDownloadAllAsync(vm.Children, ct);
        }
    }

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
            return;
        }

        StatusMessage = "Loading message…";
        try
        {
            text.InlineContent = await FetchTextContent(text.ContentUrl);
            DetailText = text.InlineContent ?? string.Empty;
            StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load message: {ex.Message}";
        }
        DetailMetadata = $"Name: {text.Name}";
    }

    private async Task<string?> FetchTextContent(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var html = await _scraper.FetchPublicAsync(url);
        return await _scraper.ExtractTextContentAsync(html);
    }

    private void RefreshRecentUrls()
    {
        RecentUrls.Clear();
        foreach (var url in _state.State.RecentUrls)
            RecentUrls.Add(url);
    }
}
