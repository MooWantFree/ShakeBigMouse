using System.Threading;

namespace ShakeToFindCursor;

internal static class Program
{
    internal static EventWaitHandle? ShowRequest { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true };
        const string showEventName = "ShakeToFindCursor.Show";
        if (args.Contains("--show", StringComparer.OrdinalIgnoreCase))
        {
            // A command never competes with normal startup for the instance mutex.
            if (EventWaitHandle.TryOpenExisting(showEventName, options, out var request))
            {
                using (request) request.Set();
            }
            return;
        }
        // Create the event before taking the mutex so requests during startup
        // remain pending until the primary instance starts listening.
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset,
            showEventName, options);
        // Keep this name stable across versions, filenames and install paths.
        using var instance = new Mutex(false, "ShakeToFindCursor.Instance", options);
        try
        {
            if (!instance.WaitOne(0)) return;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed; this thread now owns the mutex.
        }

        try
        {
            ShowRequest = showRequest;
            App.Main(); // generated WPF entry point, including exit cleanup
        }
        finally
        {
            ShowRequest = null;
            instance.ReleaseMutex();
        }
    }
}
