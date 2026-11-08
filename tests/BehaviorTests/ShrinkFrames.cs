using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.IO;
using ShakeToFindCursor;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint cursor,
        int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint bitmap);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref DibInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    private static Bitmap PaintCursor(nint cursor, int size, Color background)
    {
        // Draw on an actual initialized destination: a bitmap Graphics.GetHdc()
        // can hand out a temporary write-only surface, so alpha blending there
        // is not a reliable measurement of a border on a light background.
        int side = Math.Max(96, size);
        var info = new DibInfo { Size = 40, Width = side, Height = -side, Planes = 1, BitCount = 32 };
        nint dc = CreateCompatibleDC(0), image = CreateDIBSection(dc, ref info, 0, out _, 0, 0);
        nint previous = SelectObject(dc, image);
        try
        {
            using (var g = Graphics.FromHdc(dc)) g.Clear(background);
            if (!DrawIconEx(dc, 0, 0, cursor, size, size, 0, 0, 3)) throw new Exception("DrawIconEx failed.");
            GdiFlush();
            return Image.FromHbitmap(image);
        }
        finally { SelectObject(dc, previous); CursorHelper.DeleteObject(image); DeleteDC(dc); }
    }

    private static void ProbeShrinkFrames(bool verbose = true)
    {
        string arrow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", "aero_arrow.cur");
        foreach (uint role in new uint[] { 32512, 32513, 32515 })
        {
            nint source = role == 32512 ? CursorHelper.LoadImage(0, arrow, 2, 64, 64, 0x10) :
                CursorHelper.CopyIcon(CursorHelper.LoadCursor(0, (int)role));
            try
            {
                var size = CursorHelper.GetCursorSize(source, out bool mono);
                using var art = VectorCursorArtwork.TryCreate(role, role == 32512 ? arrow : null, source);
                var asset = new CursorHelper.CursorSource(role == 32512 ? arrow : null, source, size, art, mono);
                foreach (int baseWidth in new[] { 48, 64 })
                {
                    var destination = new Size(baseWidth, baseWidth);
                    nint baseFrame = CursorHelper.CreateFrame(asset, 1, destination);
                    try
                    {
                        foreach (var bg in new[] { Color.FromArgb(30, 30, 30), Color.White })
                        {
                            using var original = PaintCursor(baseFrame, baseWidth, bg);
                            bool wholeArrow = role == 32512 && baseWidth == 64 && bg.R == 255;
                            foreach (double scale in CursorHelper.FrameScales(10).Reverse())
                            {
                                if (!wholeArrow && scale > 1.14) continue;
                                int w = (int)Math.Round(baseWidth * scale);
                                nint cursor = CursorHelper.CreateFrame(asset, scale, destination);
                                try
                                {
                                    int canvas = CursorHelper.GetCursorSize(cursor).Width;
                                    using var current = PaintCursor(cursor, canvas, bg);
                                    if (wholeArrow)
                                    {
                                        double thickness = ArrowDiagonalInk(current,w) * baseWidth/w;
                                        double native = ArrowDiagonalInk(original,baseWidth);
                                        if (verbose) Console.WriteLine($"  outline {w}px normalized={thickness:F3} native={native:F3}");
                                        if (Math.Abs(thickness-native) > 0.18)
                                            throw new Exception($"Outline at {w}px changed from {native:F3} to {thickness:F3} normalized pixels.");
                                    }
                                    if (scale > 1.14) continue;
                                    bool handoff = role == 32512 && scale <= VectorCursorArtwork.ArrowTail;
                                    if (handoff && CursorDescription(cursor) != CursorDescription(baseFrame))
                                        throw new Exception($"Canvas or hotspot changed during handoff at {scale:F3}.");
                                    int different = 0, visible = 0, unequal = 0;
                                    for (int y = 0; y < current.Height; y++)
                                    for (int x = 0; x < current.Width; x++)
                                    {
                                        int c = current.GetPixel(x,y).R;
                                        var reference = original.GetPixel(x,y);
                                        if (current.GetPixel(x,y).ToArgb() != reference.ToArgb()) unequal++;
                                        if (Math.Abs(c - reference.R) > 12) different++;
                                        if (Math.Abs(c - bg.R) > 12) visible++;
                                    }
                                    if ((w == baseWidth || (handoff && scale <= VectorCursorArtwork.ArrowRest)) &&
                                        (unequal != 0 || CursorDescription(cursor) != CursorDescription(baseFrame)))
                                        throw new Exception($"Endpoint changed pixels or hotspot for {role} at {w}px.");
                                    if (role == 32512 && scale <= 1.02 && different > Math.Max(24, visible / 4))
                                        throw new Exception($"Visible edge snap at {w}px on background {bg.R}: {different} pixels.");
                                    if (verbose) Console.WriteLine($"{role} bg={bg.R} {scale:F4}x {w}px changed={different} visible={visible}");
                                    if (role == 32512 && bg.R == 255)
                                    {
                                        double ink = ArrowDiagonalInk(current,w), nativeInk = ArrowDiagonalInk(original,baseWidth);
                                        if (verbose) Console.WriteLine($"  edge: handle={ink:F3} source={nativeInk:F3}");
                                        if (scale <= 1.04 && Math.Abs(ink - nativeInk) > 0.2)
                                            throw new Exception($"Terminal outline changed by {ink-nativeInk:F2} px at {w}px.");
                                    }
                                }
                                finally { CursorHelper.DestroyIcon(cursor); }
                            }
                        }
                    }
                    finally { CursorHelper.DestroyIcon(baseFrame); }
                }
            }
            finally { CursorHelper.DestroyIcon(source); }
        }
    }

    private static void VerifyLowerArrowEdge()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", "aero_arrow.cur");
        nint source = CursorHelper.LoadImage(0, path, 2, 64, 64, 0x10);
        try
        {
            using var art = VectorCursorArtwork.TryCreate(32512, path, source);
            var asset = new CursorHelper.CursorSource(path, source, new Size(64, 64), art, false);
            foreach (int side in new[] { 48, 64 })
            {
                var size = new Size(side, side);
                nint baseline = CursorHelper.CreateFrame(asset, 1, size);
                try
                {
                    using var native = PaintCursor(baseline, side, Color.White);
                    int[,] original = InkMap(native, out int bottom);
                    double worst = 0;
                    // Midpoint, both joins, and frames close to the native handoff.
                    foreach (var (scale, zoom) in new[] { (1.06, 1.0375), (1.04, 1.0109375),
                        (1.08, 1.0703125), (1.02125, 1.0000484467), (1.10, 1.10), (1.12, 1.12) })
                    {
                        nint cursor = CursorHelper.CreateFrame(asset, scale, size);
                        try
                        {
                            var description = CursorDescription(cursor);
                            using var frame = PaintCursor(cursor, description.Size.Width, Color.White);
                            var actual = InkMap(frame, out _);
                            var spot = CursorDescription(baseline);
                            const int samples = 16;
                            for (int y = (int)(bottom * .63); y <= Math.Ceiling((bottom + 2) * zoom); y++)
                            for (int x = 0; x < side; x++)
                            {
                                int sum = 0;
                                for (int j = 0; j < samples; j++)
                                for (int i = 0; i < samples; i++)
                                {
                                    int u = (int)Math.Floor(spot.HotX + (x + (i + .5) / samples - description.HotX) / zoom);
                                    int v = (int)Math.Floor(spot.HotY + (y + (j + .5) / samples - description.HotY) / zoom);
                                    if ((uint)u < native.Width && (uint)v < native.Height) sum += original[u, v];
                                }
                                double error = Math.Abs(actual[x, y] - sum / (double)(samples * samples));
                                worst = Math.Max(worst, error);
                                if (error > 16)
                                    throw new Exception($"Lower outline differs from a single native contour by {error:F1}/255 at {scale}x, base {side}px, ({x},{y}).");
                            }
                            VerifyTransparencyMask(cursor, description.Size);
                        }
                        finally { CursorHelper.DestroyIcon(cursor); }
                    }
                    Console.WriteLine($"  {side}px arrow lower outline: maximum coverage error {worst:F1}/255");
                }
                finally { CursorHelper.DestroyIcon(baseline); }
            }
        }
        finally { CursorHelper.DestroyIcon(source); }
    }

    private static int[,] InkMap(Bitmap white, out int bottom)
    {
        bottom = 0;
        var ink = new int[white.Width, white.Height];
        for (int y = 0; y < white.Height; y++)
        for (int x = 0; x < white.Width; x++)
        {
            ink[x, y] = 255 - white.GetPixel(x, y).R;
            if (ink[x, y] > 0) bottom = y;
        }
        return ink;
    }

    private static void VerifyTransparencyMask(nint cursor, Size size)
    {
        if (!CursorHelper.GetIconInfo(cursor, out var info)) throw new Exception("Missing native cursor planes.");
        try
        {
            using var mask = Image.FromHbitmap(info.hbmMask);
            using var rgba = CursorHelper.SnapshotColorCursor(cursor, size);
            for (int y = 0; y < size.Height; y++)
            for (int x = 0; x < size.Width; x++)
                if ((mask.GetPixel(x, y).R == 255) != (rgba.GetPixel(x, y).A == 0))
                    throw new Exception($"Transparency mask differs from the color plane at ({x},{y}).");
        }
        finally { CursorHelper.DeleteObject(info.hbmColor); CursorHelper.DeleteObject(info.hbmMask); }
    }

    private static void VerifyNativeAlpha()
    {
        using var bitmap = new Bitmap(16, 16, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.FromArgb(128, 200, 130, 70));
        nint cursor = CursorHelper.CursorFromBitmap(bitmap, 0, 0);
        try
        {
            foreach (var bg in new[] { Color.Black, Color.White })
            {
                using var drawn = PaintCursor(cursor, 16, bg);
                var c = drawn.GetPixel(5, 5);
                int expected = (200 * 128 + bg.R * 127 + 127) / 255;
                Console.WriteLine($"Alpha on {bg.Name}: expected red {expected}; actual {c.R}");
                if (Math.Abs(c.R - expected) > 1) throw new Exception("Cursor conversion changed premultiplied coverage.");
            }
        }
        finally { CursorHelper.DestroyIcon(cursor); }
    }
}
