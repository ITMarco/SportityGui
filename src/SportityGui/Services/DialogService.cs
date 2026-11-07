using System.Windows;
using Microsoft.Win32;

namespace SportityGui.Services;

/// Dialogs and shell launches used by view models (an interface so they can be scripted in tests).
public interface IDialogService
{
    /// False while the main window is hidden/minimised or another prompt is open.
    bool CanPrompt { get; }
    string? PickFolder(string title, string? initialFolder = null);
    MessageBoxResult Ask(string message, MessageBoxButton buttons, MessageBoxResult fallback,
        MessageBoxImage image = MessageBoxImage.Question);
    void ShowError(string message);
    /// Opens a file with its default application; throws when that fails.
    void OpenFile(string path);
}

/// <summary>
/// Shows folder pickers and message boxes from view models. Prompts are suppressed while the main
/// window is hidden in the tray or minimised (e.g. during background auto-refresh), and only one
/// prompt is shown at a time.
/// </summary>
public sealed class DialogService : IDialogService
{
    private bool _busy;

    private static Window? Owner =>
        Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized } w ? w : null;

    public bool CanPrompt => !_busy && Owner != null;

    public string? PickFolder(string title, string? initialFolder = null)
    {
        if (!CanPrompt) return null;
        _busy = true;
        try
        {
            var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
            var start = PathHelper.NearestExisting(initialFolder);
            if (start != null) dialog.InitialDirectory = start;

            var owner = Owner;
            var ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return ok == true && !string.IsNullOrEmpty(dialog.FolderName) ? dialog.FolderName : null;
        }
        finally { _busy = false; }
    }

    /// Returns <paramref name="fallback"/> without asking when no prompt can be shown.
    public MessageBoxResult Ask(string message, MessageBoxButton buttons, MessageBoxResult fallback,
        MessageBoxImage image = MessageBoxImage.Question)
    {
        if (!CanPrompt) return fallback;
        _busy = true;
        try { return System.Windows.MessageBox.Show(Owner!, message, "SportityGui", buttons, image); }
        finally { _busy = false; }
    }

    public void ShowError(string message)
    {
        if (!CanPrompt) return;
        _busy = true;
        try { System.Windows.MessageBox.Show(Owner!, message, "SportityGui", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; }
    }

    public void OpenFile(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
}
