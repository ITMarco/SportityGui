using System.IO;
using System.Net.Http;

namespace SportityGui.Services;

public class UpdateService(IHttpClientFactory httpClientFactory)
{
    // Returns (HasUpdate, RemoteVersion, Error) — Error is non-null when the check couldn't be completed
    public async Task<(bool HasUpdate, string RemoteVersion, string? Error)> CheckAsync(CancellationToken ct = default)
    {
        string? raw = null;
        string? lastError = null;

        foreach (var url in new[] { AppInfo.UpdateVersionUrlPrimary, AppInfo.UpdateVersionUrlFallback })
        {
            try
            {
                var client = httpClientFactory.CreateClient("sportity");
                var response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    raw = (await response.Content.ReadAsStringAsync(ct)).Trim();
                    lastError = null;
                    break;
                }
                lastError = $"HTTP {(int)response.StatusCode} from {new Uri(url).Host}";
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
            return (false, string.Empty, lastError ?? "Could not reach update server");

        // Prefer proper version comparison; fall back to string inequality if parse fails
        bool hasUpdate;
        if (System.Version.TryParse(raw, out var remote) &&
            System.Version.TryParse(AppInfo.Version, out var current))
            hasUpdate = remote > current;
        else
            hasUpdate = !string.Equals(raw, AppInfo.Version, StringComparison.OrdinalIgnoreCase);

        return (hasUpdate, raw, null);
    }

    public async Task<string> DownloadUpdateAsync(
        string remoteVersion,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var downloadsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloadsFolder);

        var localPath = Path.Combine(downloadsFolder, $"SportityGui-v{remoteVersion}.zip");

        var client = httpClientFactory.CreateClient("sportity");
        using var response = await client.GetAsync(
            AppInfo.UpdateZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0) progress?.Report((double)downloaded / total);
        }

        return localPath;
    }
}
