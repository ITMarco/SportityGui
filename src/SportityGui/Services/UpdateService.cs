using System.IO;
using System.Net.Http;

namespace SportityGui.Services;

public class UpdateService(IHttpClientFactory httpClientFactory)
{
    public async Task<(bool HasUpdate, string RemoteVersion)> CheckAsync(CancellationToken ct = default)
    {
        string? raw = null;

        // Try GitHub raw first, fall back to own server
        foreach (var url in new[] { AppInfo.UpdateVersionUrlPrimary, AppInfo.UpdateVersionUrlFallback })
        {
            try
            {
                var client = httpClientFactory.CreateClient("sportity");
                var response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    raw = (await response.Content.ReadAsStringAsync(ct)).Trim();
                    break;
                }
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(raw))
            return (false, string.Empty);

        if (!System.Version.TryParse(raw, out var remote) ||
            !System.Version.TryParse(AppInfo.Version, out var current))
            return (false, raw);

        return (remote > current, raw);
    }

    public async Task<string> DownloadUpdateAsync(
        string remoteVersion,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var downloadsFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        downloadsFolder = Path.Combine(downloadsFolder, "Downloads");
        Directory.CreateDirectory(downloadsFolder);

        var fileName = $"SportityGui-v{remoteVersion}.zip";
        var localPath = Path.Combine(downloadsFolder, fileName);

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
