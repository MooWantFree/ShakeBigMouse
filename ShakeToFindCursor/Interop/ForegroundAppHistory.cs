using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ShakeToFindCursor;

// Observe focus before the tray/menu takes it. Explorer folder windows are
// legitimate targets; Explorer's desktop, taskbar and notification popups aren't.
internal sealed class ForegroundAppHistory : IDisposable
{
    private nint _observed, _window;
    private uint _pid;
    private string? _path;

    private nint _hook;
    private WinEventCallback? _callback;
    private delegate void WinEventCallback(nint hook, uint eventId, nint hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module,
        WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);

    // Register and dispose on the WPF dispatcher. Out-of-context callbacks
    // arrive on this thread, including when focus changes via the keyboard.
    internal bool Watch(Action changed)
    {
        const uint foreground = 3;
        _callback = (_, _, hwnd, _, _, _, _) =>
        {
            Observe(hwnd);
            changed();
        };
        _hook = SetWinEventHook(foreground, foreground, 0, _callback, 0, 0, 0);
        Observe();
        return _hook != 0;
    }

    public void Dispose()
    {
        if (_hook != 0) UnhookWinEvent(_hook);
        _hook = 0;
        _callback = null;
    }

    internal void Observe() => Observe(GetForegroundWindow());

    private void Observe(nint hwnd)
    {
        if (hwnd == _observed) return;
        _observed = hwnd;
        if (hwnd == 0 || !IsWindowVisible(hwnd)) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string name = process.ProcessName;
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                cls.ToString() is not ("CabinetWClass" or "ExploreWClass")) return;
            if (name is "ShellExperienceHost" or "ShellHost" or "StartMenuExperienceHost" or "SearchHost") return;
            (_window, _pid, _path) = (hwnd, pid, ProcessPaths.Read((int)pid));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal string? Target()
    {
        if (_window == 0 || !IsWindowVisible(_window) || IsIconic(_window)) return null;
        GetWindowThreadProcessId(_window, out uint pid);
        return pid == _pid ? _path : null;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder value, int capacity);
}
