using System.Diagnostics;
using System.Drawing;
using System.IO;
using ShakeToFindCursor;

internal static partial class Program
{
    // Sample installed Win32 arrows. Paint the captured handles AFTER sampling
    // so measuring their borders cannot itself stall the frame trace.
    private static void TraceDesktopShrink()
    {
        if (!GetPhysicalCursorPos(out var saved)) throw new Exception("Cannot save pointer position.");
        var frames = new List<(double Time, int Width, nint Handle)>();
        try
        {
            var rects = new List<MonitorRect>();
            EnumDisplayMonitors(0, 0, (_, _, r, _) => { rects.Add(System.Runtime.InteropServices.Marshal.PtrToStructure<MonitorRect>(r)); return true; }, 0);
            var rect = rects.First(r => saved.X >= r.Left && saved.X < r.Right && saved.Y >= r.Top && saved.Y < r.Bottom);
            int center = (rect.Left + rect.Right) / 2, y = (rect.Top + rect.Bottom) / 2;
            int a = Math.Min(700, (rect.Right - rect.Left) / 3);
            int left = rects.Min(r => r.Left), top = rects.Min(r => r.Top);
            int w = rects.Max(r => r.Right)-left, h = rects.Max(r => r.Bottom)-top;
            int Width() => CursorHelper.GetCursorSize(CursorHelper.LoadCursor(0, 32512)).Width;
            int normal = Width();
            for (int i = 0; i <= 96; i++)
            {
                int phase = i % 12;
                int x = center - a + (phase <= 6 ? phase : 12 - phase) * a / 3;
                mouse_event(0xC001, (int)((x-left+0.5)*65536/w), (int)((y-top+0.5)*65536/h), 0, 0);
                Thread.Sleep(6);
            }
            var watch = Stopwatch.StartNew();
            double end = 14000;
            int last = -1, peak = 0;
            while (watch.Elapsed.TotalMilliseconds < end)
            {
                int width = Width();
                if (width != last)
                {
                    nint handle = CursorHelper.CopyIcon(CursorHelper.LoadCursor(0, 32512));
                    frames.Add((watch.Elapsed.TotalMilliseconds, CursorHelper.GetCursorSize(handle).Width, handle));
                    last = width; peak = Math.Max(peak,width);
                    if (width == normal && peak > normal * 1.5) end = watch.Elapsed.TotalMilliseconds + 100;
                }
                // A normal Sleep(1) can last a full Windows timer tick and
                // hide intermediate installed sizes. Yield for a short sample slot.
                long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 500;
                while (Stopwatch.GetTimestamp() < deadline) Thread.Yield();
            }
            if (peak < normal * 1.5 || last != normal) throw new Exception($"Incomplete release {normal} -> {peak} -> {last}.");
            int start = frames.FindLastIndex(f => f.Width == peak);
            var lines = new List<string> { "ms,width,drop_pct,elapsed_ms,edge_px,edge_relative" };
            double worst = 0;
            for (int i = start; i < frames.Count; i++)
            {
                var f = frames[i];
                using var image = PaintCursor(f.Handle, f.Width, Color.White);
                double ink = ArrowDiagonalInk(image, f.Width);
                double drop = i == start ? 0 : 100.0*(frames[i-1].Width-f.Width)/frames[i-1].Width;
                double dt = i == start ? 0 : f.Time-frames[i-1].Time;
                worst = Math.Max(worst,drop);
                lines.Add(FormattableString.Invariant($"{f.Time:F1},{f.Width},{drop:F1},{dt:F1},{ink:F3},{ink*normal/f.Width:F3}"));
            }
            string output = Path.GetFullPath("tests/BehaviorTests/obj/desktop-shrink.csv");
            File.WriteAllLines(output, lines);
            Console.WriteLine($"Installed arrow {peak} -> {normal} px: {lines.Count-1} observed sizes; largest observed drop {worst:F1}%. {output}");
            Console.WriteLine(string.Join("\n", lines));
        }
        finally
        {
            foreach (var f in frames) CursorHelper.DestroyIcon(f.Handle);
            SetPhysicalCursorPos(saved.X, saved.Y);
        }
    }

    // Integrated darkness of the right-hand outline on white, across the
    // sloping edge. Normalize by scale to distinguish thickness from resizing.
    private static double ArrowDiagonalInk(Bitmap image, int width)
    {
        double total = 0; int rows = 0;
        for (int y = width*13/100; y <= width*23/100; y++)
        {
            for (int x = Math.Max(3,width/20); x < width; x++)
                total += (255-image.GetPixel(x,y).R)/255.0;
            rows++;
        }
        return total / rows;
    }
}
