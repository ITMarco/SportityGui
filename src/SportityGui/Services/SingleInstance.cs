using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SportityGui.Services;

/// <summary>
/// Allows one running SportityGui per install folder (each folder has its own state.json).
/// A second launch asks the running copy to show its window and then exits.
/// </summary>
internal static class SingleInstance
{
    // Kept in static fields so they live as long as the process; the OS releases them on exit.
    private static Mutex? _mutex;
    private static EventWaitHandle? _showRequest;

    /// <summary>
    /// Returns true when this process is the only instance. Otherwise signals the running instance
    /// to show itself (unless <paramref name="signalExisting"/> is false) and returns false.
    /// </summary>
    /// <param name="wait">How long to wait for another instance to exit, e.g. after a self-update.</param>
    public static bool TryAcquire(TimeSpan wait, bool signalExisting)
    {
        var id = InstanceId();
        _mutex = new Mutex(false, $@"Local\SportityGui-{id}");
        bool owned;
        try { owned = _mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { owned = true; } // previous instance exited without releasing

        var eventName = $@"Local\SportityGui-show-{id}";
        if (!owned)
        {
            if (signalExisting && EventWaitHandle.TryOpenExisting(eventName, out var existing))
                using (existing) existing.Set();
            return false;
        }

        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        return true;
    }

    /// Runs <paramref name="onShowRequested"/> (on a background thread) whenever another launch asks to show the app.
    public static void ListenForShowRequests(Action onShowRequested)
    {
        var handle = _showRequest ?? throw new InvalidOperationException("Instance not acquired.");
        var thread = new Thread(() =>
        {
            while (handle.WaitOne()) onShowRequested();
        })
        { IsBackground = true, Name = "SingleInstance" };
        thread.Start();
    }

    private static string InstanceId()
    {
        var folder = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\').ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
    }
}
