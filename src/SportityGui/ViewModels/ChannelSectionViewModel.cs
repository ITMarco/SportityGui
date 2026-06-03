using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SportityGui.Models;
using SportityGui.Services;

namespace SportityGui.ViewModels;

public partial class ChannelSectionViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isCollapsed;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private SportityEvent? _selectedEvent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoRefreshDisplay))]
    private int _autoRefreshMinutes;

    [ObservableProperty] private string _refreshCountdown = string.Empty;

    private DispatcherTimer? _timer;
    private int _countdownSec;

    public string Url { get; }
    public string ChannelCode { get; }
    public SavedChannelType Type { get; }
    public ObservableCollection<SportityEvent> Events { get; } = [];

    public int EventCount => Events.Count;
    public string AutoRefreshDisplay => AutoRefreshMinutes == 0 ? "Off" : $"{AutoRefreshMinutes} min";

    internal Action<ChannelSectionViewModel>? OnRemoveRequested { get; set; }
    internal Action<ChannelSectionViewModel>? OnRefreshRequested { get; set; }

    public ChannelSectionViewModel(string url, string name, SavedChannelType type,
        bool isCollapsed = false, string? channelCode = null)
    {
        Url = url;
        ChannelCode = channelCode ?? ScraperService.ParseChannelCode(url);
        _name = name;
        Type = type;
        _isCollapsed = isCollapsed;
        Events.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EventCount));
    }

    partial void OnAutoRefreshMinutesChanged(int value) => RestartTimer();

    public void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
        RefreshCountdown = string.Empty;
    }

    private void RestartTimer()
    {
        StopTimer();
        if (AutoRefreshMinutes <= 0) return;

        _countdownSec = AutoRefreshMinutes * 60;
        UpdateCountdown();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _countdownSec--;
        if (_countdownSec <= 0)
        {
            RefreshCountdown = string.Empty;
            OnRefreshRequested?.Invoke(this);
            _countdownSec = AutoRefreshMinutes * 60;
        }
        else
        {
            UpdateCountdown();
        }
    }

    private void UpdateCountdown()
    {
        var m = _countdownSec / 60;
        var s = _countdownSec % 60;
        RefreshCountdown = $"Auto-refresh in {m}:{s:D2}";
    }

    public void SetEvents(IEnumerable<SportityEvent> events)
    {
        Events.Clear();
        foreach (var ev in events)
            Events.Add(ev);
        OnPropertyChanged(nameof(EventCount));
    }

    public void SetLoading(bool value) => IsLoading = value;

    public void TrySelectEvent(string? eventUrl)
    {
        SelectedEvent = string.IsNullOrEmpty(eventUrl)
            ? null
            : Events.FirstOrDefault(e => e.Url == eventUrl);
    }

    [RelayCommand]
    private void ToggleCollapse() => IsCollapsed = !IsCollapsed;

    [RelayCommand]
    private void Remove() => OnRemoveRequested?.Invoke(this);

    [RelayCommand]
    private void Refresh() => OnRefreshRequested?.Invoke(this);
}
