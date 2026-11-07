using System.IO;
using System.Net.Http;
using SportityGui.Models;

namespace SportityGui.Services;

/// <summary>
/// Thrown when a download failed because of the local file system (access denied, file in use,
/// invalid path, disk full…) rather than the network. Picking another folder may help.
/// </summary>
public sealed class LocalFileException(string path, Exception inner)
    : Exception($"Cannot write '{path}': {inner.Message}", inner)
{
    public string Path { get; } = path;
}

public class DownloadService(IHttpClientFactory httpClientFactory, StateService stateService) : IDisposable
{
    private readonly Dictionary<string, string> _tempPaths = [];

    public static string TempRoot => Path.Combine(Path.GetTempPath(), "SportityGui");

    /// Downloads a file permanently into <paramref name="downloadFolder"/> and records it in state.
    public async Task<string> DownloadFileAsync(
        FileItem file,
        string downloadFolder,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var existing = stateService.GetDownloadRecord(file.Id);
        if (existing != null && File.Exists(existing.LocalPath))
            return existing.LocalPath;

        var localPath = Path.Combine(downloadFolder, BuildFileName(file));
        await DownloadToPathAsync(file.DownloadUrl, localPath, progress, ct);

        stateService.RecordDownload(file.Id, localPath);
        return localPath;
    }

    /// <summary>
    /// Downloads a file into the system temp folder for viewing. Each file gets its own subfolder,
    /// so a copy that is still open in a viewer (e.g. from a previous session) never blocks a new download.
    /// An already completed temp copy is reused.
    /// </summary>
    public async Task<string> DownloadToTempAsync(
        FileItem file,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (_tempPaths.TryGetValue(file.Id, out var cached) && File.Exists(cached))
            return cached;

        var localPath = Path.Combine(TempRoot, PathHelper.SanitizeName(file.Id), BuildFileName(file));

        // Downloads are written to ".part" and renamed when complete, so an existing file is always whole.
        if (!File.Exists(localPath))
            await DownloadToPathAsync(file.DownloadUrl, localPath, progress, ct);

        _tempPaths[file.Id] = localPath;
        return localPath;
    }

    private async Task DownloadToPathAsync(string url, string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("sportity");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        var partPath = localPath + ".part";

        FileStream fileStream;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LocalFileException(localPath, ex);
        }

        try
        {
            await using (fileStream)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    try { await fileStream.WriteAsync(buffer.AsMemory(0, read), ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new LocalFileException(localPath, ex);
                    }
                    downloaded += read;
                    if (total > 0) progress?.Report((double)downloaded / total);
                }
            }

            try { File.Move(partPath, localPath, overwrite: true); }
            catch (Exception ex) { throw new LocalFileException(localPath, ex); }
        }
        catch
        {
            try { File.Delete(partPath); } catch { }
            throw;
        }
    }

    private static string BuildFileName(FileItem file)
    {
        var fileName = PathHelper.SanitizeName(file.Name);
        if (string.IsNullOrEmpty(fileName)) fileName = PathHelper.SanitizeName(file.Id);
        if (!string.IsNullOrEmpty(file.FileExtension))
        {
            var expectedExt = "." + file.FileExtension;
            if (!fileName.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
                fileName += expectedExt;
        }
        return fileName;
    }

    /// Best-effort cleanup of the temp view folder, including leftovers from earlier sessions.
    /// Files still open in another application stay behind and are reused next time.
    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var f in Directory.EnumerateFiles(TempRoot, "*", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { }
            foreach (var d in Directory.EnumerateDirectories(TempRoot))
                try { Directory.Delete(d, recursive: false); } catch { }
        }
        catch { }
    }
}
