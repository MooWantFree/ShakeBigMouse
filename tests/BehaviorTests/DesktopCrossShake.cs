using System.Runtime.InteropServices;
using System.Diagnostics;
using ShakeToFindCursor;
using static ShakeToFindCursor.MouseHook;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int x, int y, uint data, nuint extra);

    // End-to-end probe: feed the running tray app real hook events. No detector
    // or renderer is started in this process, so it cannot enlarge its own cursor.
    private static void ProbeRunningAppAcrossScreens()
    {
        var screens = new List<MonitorRect>();
        EnumDisplayMonitors(0, 0, (_, _, r, _) => { screens.Add(Marshal.PtrToStructure<MonitorRect>(r)); return true; }, 0);
        foreach (var a in screens)
        foreach (var b in screens)
        {
            bool horizontal = a.Right == b.Left && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > 4;
            bool vertical = a.Bottom == b.Top && Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > 4;
            if (!horizontal && !vertical) continue;
            int seam = horizontal ? a.Right : a.Bottom;
            int middle = horizontal ? (Math.Max(a.Top,b.Top) + Math.Min(a.Bottom,b.Bottom)) / 2
                : (Math.Max(a.Left,b.Left) + Math.Min(a.Right,b.Right)) / 2;
            int span = horizontal ? Math.Min(a.Right-a.Left,b.Right-b.Left) : Math.Min(a.Bottom-a.Top,b.Bottom-b.Top);
            int amplitude = Math.Min(750, span / 2);
            int left = screens.Min(s => s.Left), top = screens.Min(s => s.Top);
            int w = screens.Max(s => s.Right) - left, h = screens.Max(s => s.Bottom) - top;
            int Width() => CursorHelper.GetCursorSize(CursorHelper.LoadCursor(0, 32512)).Width;
            if (!GetPhysicalCursorPos(out var saved)) throw new Exception("Cannot save pointer position.");
            int baseline = Width(), peak = baseline, crossings = 0;
            nint prior = 0;
            var dpis = new HashSet<uint>();
            try
            {
                for (int i = 0; i <= 80; i++)
                {
                    int phase = i % 16;
                    int pos = seam - amplitude + (phase <= 8 ? phase : 16 - phase) * amplitude / 4;
                    int x = horizontal ? pos : middle, y = horizontal ? middle : pos;
                    mouse_event(0xC001, (int)((x - left + 0.5) * 65536 / w), (int)((y - top + 0.5) * 65536 / h), 0, 0);
                    Thread.Sleep(8);
                    if (!GetPhysicalCursorPos(out var actual)) throw new Exception("Cannot read pointer position.");
                    var display = MonitorDpi.DisplayAt(actual);
                    if (prior != 0 && display.Monitor != prior) crossings++;
                    prior = display.Monitor; dpis.Add(display.Dpi);
                    peak = Math.Max(peak, Width());
                }
                Thread.Sleep(100);
                peak = Math.Max(peak, Width());
            }
            finally { SetPhysicalCursorPos(saved.X, saved.Y); }
            var timer = Stopwatch.StartNew();
            while (Width() != baseline && timer.ElapsedMilliseconds < 14000) Thread.Sleep(30);
            Console.WriteLine($"Desktop hook: {crossings} crossings; DPI [{string.Join(", ",dpis)}]; arrow {baseline} -> {peak} -> {Width()} px");
            if (crossings < 8 || peak <= baseline * 1.5 || Width() != baseline)
                throw new Exception("Cross-screen shakes did not enlarge and restore the running app's cursor.");
            return;
        }
        throw new Exception("This probe requires two adjacent physical monitors.");
    }
}
