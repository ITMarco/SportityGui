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

        var client = httpClientFactory.CreateClient("sportity");
        Exception? lastEx = null;

        foreach (var url in new[] { AppInfo.UpdateZipUrlPrimary, AppInfo.UpdateZipUrlFallback })
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    lastEx = new HttpRequestException($"HTTP {(int)response.StatusCode} from {new Uri(url).Host}");
                    continue;
                }

                var ext = Path.GetExtension(new Uri(url).AbsolutePath); // ".exe" or ".zip"
                var localPath = Path.Combine(downloadsFolder, $"SportityGui-v{remoteVersion}{ext}");

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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastEx = ex;
            }
        }

        throw lastEx ?? new InvalidOperationException("Could not download update from any source.");
    }

    // ── Self-install ────────────────────────────────────────────────────────

    private const string OldSuffix = ".old";
    private const string NewSuffix = ".new";

    /// <summary>
    /// True when the running app is a single-file SportityGui exe in a folder we can write to,
    /// so a downloaded update can replace it in place.
    /// </summary>
    public static bool CanSelfInstall(out string reason)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location))
        {
            reason = "this copy of SportityGui is not a single-file build";
            return false;
        }
        if (!PathHelper.CanWrite(Path.GetDirectoryName(exe)!, out var error))
        {
            reason = $"the app folder is not writable ({error})";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Replaces the running exe with the downloaded update (an .exe, or a .zip containing one),
    /// starts the new version once this process has exited, and returns. The caller must then
    /// shut the app down. The previous exe is kept as "&lt;name&gt;.old" until the new version starts.
    /// </summary>
    public static void InstallAndRestart(string downloadedPath)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown app path.");
        var newExe = ExtractExe(downloadedPath);
        var staged = exe + NewSuffix;
        var old = exe + OldSuffix;

        File.Copy(newExe, staged, overwrite: true);
        try
        {
            // A running exe can be renamed but not overwritten: move it aside, then put the new one in place.
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(staged, exe); }
            catch
            {
                File.Move(old, exe); // roll back
                throw;
            }
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
        {
            ArgumentList = { "--wait-pid", Environment.ProcessId.ToString() },
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false
        });
    }

    /// <summary>
    /// Called at startup: waits for the previous version to exit (when started by an update)
    /// and removes the exe it left behind. Returns true when this launch came from an update.
    /// </summary>
    public static bool FinishPendingInstall(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase));
        var afterUpdate = i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out _);
        if (afterUpdate)
        {
            try { System.Diagnostics.Process.GetProcessById(int.Parse(args[i + 1])).WaitForExit(15000); }
            catch { /* already gone */ }
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return afterUpdate;
        var old = exe + OldSuffix;
        for (var attempt = 0; attempt < 5 && File.Exists(old); attempt++)
        {
            try { File.Delete(old); }
            catch { Thread.Sleep(300); }
        }
        return afterUpdate;
    }

    /// Returns the SportityGui exe inside a downloaded update, extracting it when it is a zip.
    private static string ExtractExe(string downloadedPath)
    {
        string exe;
        if (Path.GetExtension(downloadedPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(Path.GetTempPath(), "SportityGui-update");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            System.IO.Compression.ZipFile.ExtractToDirectory(downloadedPath, dir);
            var candidates = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories);
            exe = candidates.FirstOrDefault(f => Path.GetFileName(f).Contains("SportityGui", StringComparison.OrdinalIgnoreCase))
                  ?? candidates.FirstOrDefault()
                  ?? throw new InvalidOperationException("The update zip does not contain an .exe.");
        }
        else
        {
            exe = downloadedPath;
        }

        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
        if (!string.Equals(info.ProductName, "SportityGui", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded file is not a SportityGui build.");
        return exe;
    }
}
