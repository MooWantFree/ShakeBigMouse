using System.Runtime.InteropServices;
using static ShakeToFindCursor.MouseHook;

namespace ShakeToFindCursor;

internal static class MonitorDpi
{
    private static readonly object Gate = new();
    private static RECT _bounds;
    internal readonly record struct Display(nint Monitor, uint Dpi, double DiagonalDip);
    private static Display _display = new(0, 96, 0);

    public static void Invalidate() { lock (Gate) _bounds = default; }

    // Shared stock cursor images are loaded at the system DPI, independent of
    // the monitor currently containing the pointer. Explicit thread awareness
    // also makes this read reliable in diagnostic/test hosts without a manifest.
    public static uint SystemDpi
    {
        get
        {
            IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try { return Math.Clamp(GetDpiForSystem(), 96u, 768u); }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }
    }

    // Cache bounds and low-level hook points use physical desktop coordinates.
    // GetCursorPos can be virtualized by the calling thread's DPI context.
    public static uint AtPointer() => GetPhysicalCursorPos(out var point) ? AtPoint(point) : 96;

    public static uint AtPoint(NativePoint point) => DisplayAt(point).Dpi;

    internal static Display DisplayAt(NativePoint point)
    {
        lock (Gate)
        {
            if (point.X >= _bounds.Left && point.X < _bounds.Right &&
                point.Y >= _bounds.Top && point.Y < _bounds.Bottom) return _display;
            IntPtr monitor = MonitorFromPoint(point, 2);
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            _bounds = GetMonitorInfo(monitor, ref info) ? info.Monitor : default;
            // This API takes an HMONITOR directly, independent of the DPI
            // awareness of whatever application's window happens to be under it.
            uint dpi = GetScaleFactorForMonitor(monitor, out int percent) == 0
                ? (uint)Math.Clamp((int)Math.Round(percent * 96.0 / 100), 96, 768) : 96;
            double w = _bounds.Right - (double)_bounds.Left, h = _bounds.Bottom - (double)_bounds.Top;
            return _display = new(monitor, dpi, Math.Sqrt(w * w + h * h) * 96 / dpi);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor, Work;
        public uint Flags;
    }
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
}
