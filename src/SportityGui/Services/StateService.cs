using System.IO;
using System.Text.Json;
using ModernWpf;
using SportityGui.Models;

namespace SportityGui.Services;

public class StateService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string BaseDir => AppContext.BaseDirectory;
    private static string StatePath => Path.Combine(BaseDir, "state.json");
    private static string PrefsPath => Path.Combine(BaseDir, "preferences.json");

    public AppState State { get; private set; } = new();
    public AppPreferences Preferences { get; private set; } = new();

    public void Load()
    {
        State = LoadJson<AppState>(StatePath) ?? new AppState();
        Preferences = LoadJson<AppPreferences>(PrefsPath) ?? new AppPreferences();
        ApplyTheme();
    }

    public void Save()
    {
        SaveJson(StatePath, State);
    }

    public void SavePreferences()
    {
        SaveJson(PrefsPath, Preferences);
        ApplyTheme();
    }

    public void AddRecentUrl(string url)
    {
        State.RecentUrls.Remove(url);
        State.RecentUrls.Insert(0, url);
        if (State.RecentUrls.Count > 20)
            State.RecentUrls.RemoveAt(State.RecentUrls.Count - 1);
        Save();
    }

    public void RemoveRecentUrl(string url)
    {
        State.RecentUrls.Remove(url);
        Save();
    }

    public bool IsRead(string id) => State.ReadItems.ContainsKey(id);

    public void MarkRead(string id)
    {
        State.ReadItems.TryAdd(id, DateTime.UtcNow);
        Save();
    }

    public DateTime? GetFirstSeen(string id) =>
        State.FirstSeenItems.TryGetValue(id, out var dt) ? dt : null;

    public void RecordFirstSeen(string id)
    {
        State.FirstSeenItems.TryAdd(id, DateTime.UtcNow);
    }

    public bool IsDownloaded(string id) =>
        State.DownloadedFiles.TryGetValue(id, out var r) &&
        File.Exists(r.LocalPath);

    public DownloadRecord? GetDownloadRecord(string id) =>
        State.DownloadedFiles.TryGetValue(id, out var r) ? r : null;

    public void DeleteChannelFiles(string channelFolderPath)
    {
        var prefix = channelFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        var toRemove = State.DownloadedFiles
            .Where(kvp => kvp.Value.LocalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in toRemove)
        {
            try { File.Delete(State.DownloadedFiles[key].LocalPath); } catch { }
            State.DownloadedFiles.Remove(key);
        }
        Save();
    }

    public void RecordDownload(string id, string localPath)
    {
        State.DownloadedFiles[id] = new DownloadRecord
        {
            LocalPath = localPath,
            DownloadedAt = DateTime.UtcNow
        };
        Save();
    }

    private void ApplyTheme()
    {
        ApplicationTheme? theme = Preferences.Theme switch
        {
            "Dark" => ApplicationTheme.Dark,
            "System" => null,
            _ => ApplicationTheme.Light,
        };
        ThemeManager.Current.ApplicationTheme = theme;
    }

    private static T? LoadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch { return default; }
    }

    private static void SaveJson<T>(string path, T obj)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch { /* best-effort */ }
    }
}
