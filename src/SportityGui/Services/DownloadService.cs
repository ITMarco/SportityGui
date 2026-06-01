using System.IO;
using System.Net.Http;
using SportityGui.Models;

namespace SportityGui.Services;

public class DownloadService(IHttpClientFactory httpClientFactory, StateService stateService)
{
    public async Task<string> DownloadFileAsync(
        FileItem file,
        string downloadFolder,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(downloadFolder);

        var existing = stateService.GetDownloadRecord(file.Id);
        if (existing != null && File.Exists(existing.LocalPath))
            return existing.LocalPath;

        var fileName = SanitizeFileName(file.Name);
        if (!string.IsNullOrEmpty(file.FileExtension))
        {
            var expectedExt = "." + file.FileExtension;
            if (!fileName.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
                fileName += expectedExt;
        }

        var localPath = Path.Combine(downloadFolder, fileName);

        var client = httpClientFactory.CreateClient("sportity");
        using var response = await client.GetAsync(
            file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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

        stateService.RecordDownload(file.Id, localPath);
        return localPath;
    }

    public async Task DownloadFolderAsync(
        FolderItem folder,
        string downloadFolder,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var files = CollectFiles(folder);
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var subFolder = Path.Combine(downloadFolder, SanitizeFileName(folder.Name));
            await DownloadFileAsync(files[i], subFolder, null, ct);
            progress?.Report((double)(i + 1) / files.Count);
        }
    }

    private static List<FileItem> CollectFiles(FolderItem folder)
    {
        var result = new List<FileItem>();
        foreach (var child in folder.Children)
        {
            if (child is FileItem f) result.Add(f);
            else if (child is FolderItem sub) result.AddRange(CollectFiles(sub));
        }
        return result;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }
}
