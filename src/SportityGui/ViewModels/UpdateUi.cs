using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SportityGui.Services;

namespace SportityGui.ViewModels;

/// What happens after an update has been downloaded, shared by the update banner and Preferences.
internal static class UpdateUi
{
    /// <summary>
    /// Offers to install the downloaded update and restart. When that is declined or not possible,
    /// shows the download in Explorer instead. Returns a status line for the caller.
    /// </summary>
    public static string AfterDownload(string path, string version)
    {
        if (!UpdateService.CanSelfInstall(out var reason))
        {
            Show($"Version {version} has been downloaded to:\n{path}\n\n" +
                 $"It can't be installed automatically because {reason}. " +
                 "Close SportityGui and replace its exe with the downloaded one.",
                 MessageBoxButton.OK, MessageBoxImage.Information);
            Reveal(path);
            return $"Update downloaded: {path}";
        }

        var answer = Show($"Version {version} has been downloaded.\n\nInstall it now? SportityGui will restart.",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            Reveal(path);
            return $"Update downloaded: {path}";
        }

        try
        {
            // Save first: once the running exe has been renamed, this single-file app can no longer
            // load code it hasn't used yet, so saving during shutdown may fail.
            var state = App.Services.GetRequiredService<StateService>();
            state.Save();
            state.SavePreferences();
            UpdateService.InstallAndRestart(path);
        }
        catch (Exception ex)
        {
            Show($"Could not install the update: {ex.Message}\n\n" +
                 "The downloaded file is shown in Explorer; close SportityGui and replace its exe with it.",
                 MessageBoxButton.OK, MessageBoxImage.Warning);
            Reveal(path);
            return $"Update install failed: {ex.Message}";
        }

        Application.Current.Shutdown();
        return $"Installing version {version}…";
    }

    private static MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current.MainWindow;
        return owner is { IsVisible: true }
            ? System.Windows.MessageBox.Show(owner, message, "SportityGui Update", buttons, image)
            : System.Windows.MessageBox.Show(message, "SportityGui Update", buttons, image);
    }

    private static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* not essential */ }
    }
}
