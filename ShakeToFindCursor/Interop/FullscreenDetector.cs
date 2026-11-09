using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ShakeToFindCursor;

/// <summary>
/// Detects fullscreen applications and games to disable shake-to-find when appropriate.
/// </summary>
public static class FullscreenDetector
{
    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    // Window styles
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_POPUP = 0x80000000;

    // Extended window styles
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    // MonitorFromWindow flags
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    #endregion

    /// <summary>
    /// Returns true if shake-to-find should be disabled based on current foreground window.
    /// </summary>
    /// <param name="excludedProcesses">Executable paths or legacy process names (case-insensitive)</param>
    /// <param name="disableInFullscreen">Whether to disable when fullscreen apps are detected</param>
    public static bool ShouldDisable(List<string>? excludedProcesses, bool disableInFullscreen,
        List<string>? processNameOnlyRules = null)
    {
        try
        {
            bool checkExclusions = excludedProcesses is { Count: > 0 };
            if (!disableInFullscreen && !checkExclusions) return false;
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            if (disableInFullscreen && IsFullscreenWindow(hwnd)) return true;
            if (!checkExclusions) return false;
            return IsExcludedWindow(hwnd, excludedProcesses!, processNameOnlyRules);
        }
        catch
        {
            // On any error, don't disable (fail-safe)
            return false;
        }
    }

    internal static bool IsExcludedWindow(IntPtr hwnd, List<string> excludedProcesses,
        List<string>? processNameOnlyRules = null)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            string name = GetProcessName(hwnd);
            if (ProcessPaths.Matches(excludedProcesses, name, null, processNameOnlyRules)) return true;
            if (!excludedProcesses.Any(rule => ProcessPaths.Normalize(rule) != null)) return false;
            GetWindowThreadProcessId(hwnd, out uint processId);
            return processId != 0 && ProcessPaths.Matches(excludedProcesses, name,
                ProcessPaths.Read((int)processId), processNameOnlyRules);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns information about the current foreground window.
    /// </summary>
    public static (string ProcessName, bool IsFullscreen, string WindowTitle) GetForegroundInfo()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return (string.Empty, false, string.Empty);

        string processName = GetProcessName(hwnd);
        string windowTitle = GetWindowTitle(hwnd);
        bool isFullscreen = IsFullscreenWindow(hwnd);

        return (processName, isFullscreen, windowTitle);
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
                return string.Empty;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsShellDesktop(IntPtr hwnd)
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) return false;
        if (hwnd == shell) return true;

        // Explorer can also host the desktop in a top-level WorkerW window.
        GetWindowThreadProcessId(shell, out uint shellPid);
        GetWindowThreadProcessId(hwnd, out uint windowPid);
        if (shellPid == 0 || windowPid != shellPid) return false;
        var name = new System.Text.StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString() == "WorkerW";
    }

    internal static bool IsFullscreenWindow(IntPtr hwnd)
    {
        // Get window rect
        if (!GetWindowRect(hwnd, out RECT windowRect))
            return false;

        // Get the monitor this window is on
        IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero)
            return false;

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref monitorInfo))
            return false;

        // Check window style for fullscreen indicators
        uint style = (uint)GetWindowLong(hwnd, GWL_STYLE);

        // Fullscreen exclusive typically has no caption, no border
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool hasBorder = (style & WS_BORDER) != 0 || (style & WS_THICKFRAME) != 0;
        bool isPopup = (style & WS_POPUP) != 0;

        // Check if window covers entire monitor (full bounds, not just work area)
        RECT monitorBounds = monitorInfo.rcMonitor;
        bool coversFullMonitor =
            windowRect.Left <= monitorBounds.Left &&
            windowRect.Top <= monitorBounds.Top &&
            windowRect.Right >= monitorBounds.Right &&
            windowRect.Bottom >= monitorBounds.Bottom;

        if (coversFullMonitor && IsShellDesktop(hwnd)) return false;

        // Also check work area (maximized window without taskbar)
        RECT workArea = monitorInfo.rcWork;
        bool coversWorkArea =
            windowRect.Left <= workArea.Left &&
            windowRect.Top <= workArea.Top &&
            windowRect.Right >= workArea.Right &&
            windowRect.Bottom >= workArea.Bottom;

        // Fullscreen exclusive: covers full monitor, typically no caption/border, or popup style
        if (coversFullMonitor && (!hasCaption || isPopup))
            return true;

        // Borderless fullscreen: covers full monitor with no visible chrome
        if (coversFullMonitor && !hasBorder && !hasCaption)
            return true;

        // Maximized app covering work area is NOT considered fullscreen (user wants shake-to-find)
        // Only return true for true fullscreen (covering taskbar area)
        if (coversFullMonitor && !coversWorkArea)
        {
            // Window extends beyond work area into taskbar space = fullscreen
            return true;
        }

        return false;
    }
}
