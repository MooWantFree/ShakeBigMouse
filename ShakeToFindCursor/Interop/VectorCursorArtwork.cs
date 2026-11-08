using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ShakeToFindCursor;

/// <summary>Size-independent artwork for the Windows white/Aero cursor family.
/// Custom themes never pass the allowlist. Native pixels and hotspots remain
/// authoritative at scale 1; these paths are only used during enlargement.</summary>
internal sealed class VectorCursorArtwork : IDisposable
{
    private readonly uint _role;
    private readonly Bitmap _native;
    private readonly Rectangle _bounds;
    private readonly Point _hotspot;
    private readonly object _gate = new();
    private float _stroke = 1.15f;
    internal const double ArrowRest = 1.02, ArrowTail = 1.10;
    internal bool IsArrow => _role == 32512;
    private NativeArrowRaster? _nativeArrow;
    private NativeArrowRaster? _destinationArrow;

    private VectorCursorArtwork(uint role, Bitmap native, Rectangle bounds, Point hotspot)
    {
        _role = role; _native = native; _bounds = bounds; _hotspot = hotspot;
    }

    internal static bool Supports(uint role, string? path)
    {
        string? name = role switch
        {
            32512 => "aero_arrow", 32514 => "aero_busy", 32516 => "aero_up",
            32642 => "aero_nwse", 32643 => "aero_nesw", 32644 => "aero_ew",
            32645 => "aero_ns", 32646 => "aero_move", 32648 => "aero_unavail",
            32649 => "aero_link", 32650 => "aero_working", 32651 => "aero_helpsel",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(path)) return role is 32513 or 32515;
        if (name == null) return false;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase))
            return false;
        string stem = Path.GetFileNameWithoutExtension(path);
        return string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(stem, name + "_l", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stem, name + "_xl", StringComparison.OrdinalIgnoreCase);
    }

    internal static VectorCursorArtwork? TryCreate(uint role, string? path, IntPtr handle)
    {
        if (!Supports(role, path) || !CursorHelper.GetIconInfo(handle, out var info)) return null;
        try
        {
            Size size = CursorHelper.GetCursorSize(handle);
            if (size.IsEmpty) return null;
            // AND/XOR masks depend on the pixels behind the cursor. Native
            // mask scaling preserves that behavior; an ARGB snapshot cannot.
            if (info.hbmColor == IntPtr.Zero) return null;
            var bitmap = CursorHelper.SnapshotColorCursor(handle, size);
            int left = size.Width, top = size.Height, right = -1, bottom = -1;
            for (int y = 0; y < size.Height; y++)
            for (int x = 0; x < size.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A >= 128)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
            }
            if (right < left) { bitmap.Dispose(); return null; }
            var artwork = new VectorCursorArtwork(role, bitmap, Rectangle.FromLTRB(left, top, right + 1, bottom + 1),
                new Point(info.xHotspot, info.yHotspot));
            if (role == 32512) artwork.MatchArrowStroke();
            return artwork;
        }
        finally { CursorHelper.DeleteObject(info.hbmColor); CursorHelper.DeleteObject(info.hbmMask); }
    }

    private void MatchArrowStroke()
    {
        // Measure the outer diagonal against white, stopping at the opaque
        // white interior. Match coverage rather than counting black pixels.
        double Ink(Bitmap image, int factor)
        {
            double sum = 0; int rows = 0;
            int first = (_bounds.Top + _bounds.Height / 4) * factor;
            int last = (_bounds.Top + 2 * _bounds.Height / 5) * factor;
            for (int y = first; y <= last && y < image.Height; y++)
            {
                double row = 0;
                for (int x = image.Width - 1; x >= 0; x--)
                {
                    Color c = image.GetPixel(x, y);
                    if (c.A > 245 && c.R > 245) { sum += row; rows++; break; }
                    row += c.A * (255 - c.R) / (255.0 * 255);
                }
            }
            return rows > 0 ? sum / (rows * factor) : 0;
        }
        double target = Ink(_native, 1);
        if (target <= 0) return;
        using var sample = Render(_native.Width * 2, _native.Height * 2, 2);
        double actual = Ink(sample, 2);
        if (actual > 0) _stroke = (float)Math.Clamp(_stroke * target / actual, 0.25, 3);
    }

    internal Bitmap Render(int width, int height, double scale, Bitmap? baseline = null, Point? baseHotspot = null)
    {
        lock (_gate)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float sx = width / (float)_native.Width, sy = height / (float)_native.Height;
            using var outline = Shape(_role);
            using var pen = new Pen(Color.Black, _stroke) { LineJoin = LineJoin.Round };
            RectangleF bounds = outline.GetBounds(null, pen);
            float shapeScaleX = _bounds.Width * sx / bounds.Width;
            float shapeScaleY = _bounds.Height * sy / bounds.Height;
            PointF anchor = _role switch
            {
                32512 or 32650 or 32651 => new(1, 1),
                32513 => new(9, 15), 32514 => new(14, 14),
                32516 => new(15, 1), 32649 => new(13, 0),
                _ => new(15, 15)
            };
            // Align the actual geometric tip/center, not merely the bitmap's
            // padding. The native hotspot is also the visible pointing location.
            using var transform = new Matrix(
                shapeScaleX, 0, 0, shapeScaleY,
                _hotspot.X * sx - anchor.X * shapeScaleX,
                _hotspot.Y * sy - anchor.Y * shapeScaleY);
            g.Transform = transform;
            if (_role == 32514)
            {
                g.FillPath(Brushes.White, outline);
                using var blue = new Pen(Color.FromArgb(0, 120, 215), 1.15f);
                g.DrawPath(blue, outline);
            }
            else
            {
                g.FillPath(Brushes.White, outline);
                if (_role == 32512)
                {
                    // Fill a geometric stroke. A thin transformed GDI+ pen can
                    // switch abruptly to a cosmetic hairline at intermediate sizes.
                    using var ink = (GraphicsPath)outline.Clone();
                    using var precision = new Matrix(64, 0, 0, 64, 0, 0);
                    ink.Transform(precision);
                    using var wide = new Pen(Color.Black, 64 * _stroke) { LineJoin = LineJoin.Round };
                    ink.Widen(wide, null, 0.25f);
                    precision.Invert();
                    ink.Transform(precision);
                    g.FillPath(Brushes.Black, ink);
                }
                else g.DrawPath(pen, outline);
                if (_role == 32650)
                {
                    using var ring = new GraphicsPath(FillMode.Alternate);
                    ring.AddEllipse(21, 2, 9, 9); ring.AddEllipse(23, 4, 5, 5);
                    using var blue = new Pen(Color.FromArgb(0, 120, 215), 1.15f);
                    g.DrawPath(blue, ring);
                }
            }
            g.ResetTransform();
            // Finish the shape change before the last few pixels of motion.
            // A rounded canvas width must not snap the artwork by a whole pixel.
            float nativeOpacity = (float)SmoothStep((1.6 - scale) / 0.48);
            if (nativeOpacity > 0)
            {
                g.Dispose();
                var destSpot = new Point((int)Math.Round(_hotspot.X * sx), (int)Math.Round(_hotspot.Y * sy));
                using var native = IsArrow
                    ? ArrowRaster(baseline, baseHotspot).Render(width, height, destSpot, scale)
                    : new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                if (!IsArrow)
                {
                    using var ng = Graphics.FromImage(native);
                    double ease = SmoothStep((scale - 1) / 0.10);
                    int baseW = (int)Math.Round(width / scale), baseH = (int)Math.Round(height / scale);
                    float w = (float)(baseW + (width - baseW) * ease);
                    float h = (float)(baseH + (height - baseH) * ease);
                    ng.PixelOffsetMode = PixelOffsetMode.Half;
                    ng.InterpolationMode = InterpolationMode.Bilinear;
                    // Use an explicit pixel source rectangle; bitmap DPI is not geometry.
                    var pixels = baseline ?? _native;
                    var spot = baseHotspot ?? _hotspot;
                    float x = (float)Math.Round(_hotspot.X * sx) - spot.X * w / pixels.Width;
                    float y = (float)Math.Round(_hotspot.Y * sy) - spot.Y * h / pixels.Height;
                    ng.DrawImage(pixels, new RectangleF(x, y, w, h),
                        new RectangleF(0, 0, pixels.Width, pixels.Height), GraphicsUnit.Pixel);
                }
                // Interpolate premultiplied pixels, rather than source-over two
                // translucent images (which would dim an opaque white interior).
                Blend(bitmap, native, nativeOpacity);
            }
            return bitmap;
        }
    }

    private NativeArrowRaster ArrowRaster(Bitmap? baseline, Point? hotspot)
    {
        if (baseline == null) return _nativeArrow ??= new NativeArrowRaster(_native, _hotspot);
        var spot = hotspot ?? _hotspot;
        if (_destinationArrow?.Size != baseline.Size || _destinationArrow.Hotspot != spot)
            _destinationArrow = new NativeArrowRaster(baseline, spot);
        return _destinationArrow;
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static void Blend(Bitmap vector, Bitmap native, float amount)
    {
        var rect = new Rectangle(0, 0, vector.Width, vector.Height);
        var a = vector.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var b = native.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var av = new byte[a.Stride * a.Height];
                var bv = new byte[b.Stride * b.Height];
                Marshal.Copy(a.Scan0, av, 0, av.Length); Marshal.Copy(b.Scan0, bv, 0, bv.Length);
                for (int i = 0; i < av.Length; i++) av[i] = (byte)Math.Round(av[i] * (1 - amount) + bv[i] * amount);
                Marshal.Copy(av, 0, a.Scan0, av.Length);
            }
            finally { native.UnlockBits(b); }
        }
        finally { vector.UnlockBits(a); }
    }

    internal IntPtr CreateCursor(int width, int height, double scale, IntPtr baseline = default)
    {
        using var pixels = baseline == IntPtr.Zero ? null : CursorHelper.SnapshotColorCursor(baseline, CursorHelper.GetCursorSize(baseline));
        Point? spot = null;
        if (baseline != IntPtr.Zero && CursorHelper.GetIconInfo(baseline, out var info))
        {
            spot = new Point(info.xHotspot, info.yHotspot);
            CursorHelper.DeleteObject(info.hbmColor); CursorHelper.DeleteObject(info.hbmMask);
        }
        if (IsArrow && scale <= ArrowTail)
        {
            lock (_gate)
            {
                var raster = ArrowRaster(pixels, spot);
                double t = Math.Clamp((scale - ArrowRest) / (ArrowTail - ArrowRest), 0, 1);
                // Move one silhouette about its hotspot. Crossfading two sizes
                // draws two displaced tail outlines at the same time.
                double zoom = 1 + (ArrowTail - 1) * t * t * (2 - t);
                Point hotspot = raster.Hotspot;
                if (raster.FitsNativeCanvas(zoom))
                {
                    width = raster.Size.Width;
                    height = raster.Size.Height;
                }
                else hotspot = new Point((int)Math.Round(_hotspot.X * width / (double)_native.Width),
                    (int)Math.Round(_hotspot.Y * height / (double)_native.Height));
                using var frame = raster.Render(width, height, hotspot, zoom);
                return CursorHelper.CursorFromBitmap(frame, hotspot.X, hotspot.Y);
            }
        }
        using var bitmap = Render(width, height, scale, pixels, spot);
        return CursorHelper.CursorFromBitmap(bitmap,
            (int)Math.Round(_hotspot.X * width / (double)_native.Width),
            (int)Math.Round(_hotspot.Y * height / (double)_native.Height));
    }

    private static GraphicsPath Shape(uint role)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        void Polygon(params PointF[] points) => path.AddPolygon(points);
        switch (role)
        {
            case 32512: // arrow
            case 32651: // help arrow and question mark
            case 32650: // working arrow and ring
                Polygon(new(1, 1), new(1, 23), new(7, 18), new(12, 28), new(16, 26), new(11, 17), new(20, 17));
                if (role == 32650) { path.AddEllipse(21, 2, 9, 9); path.AddEllipse(23, 4, 5, 5); }
                if (role == 32651)
                {
                    path.StartFigure();
                    path.AddBezier(22, 7, 22, -1, 32, -1, 32, 6);
                    path.AddBezier(32, 6, 32, 10, 28, 10, 28, 13);
                    path.AddLine(28, 13, 25, 13);
                    path.AddBezier(25, 13, 25, 9, 29, 8, 29, 6);
                    path.AddBezier(29, 6, 29, 2, 25, 2, 25, 7);
                    path.CloseFigure(); path.AddEllipse(25, 16, 2, 2);
                }
                break;
            case 32513: // text I-beam
                Polygon(new(4, 1), new(9, 2), new(14, 1), new(14, 3), new(10, 4), new(10, 26),
                    new(14, 27), new(14, 29), new(9, 28), new(4, 29), new(4, 27), new(8, 26), new(8, 4), new(4, 3));
                break;
            case 32514: path.AddEllipse(1, 1, 26, 26); path.AddEllipse(6, 6, 16, 16); break;
            case 32515:
                Polygon(new(14, 1), new(16, 1), new(16, 14), new(29, 14), new(29, 16), new(16, 16),
                    new(16, 29), new(14, 29), new(14, 16), new(1, 16), new(1, 14), new(14, 14));
                break;
            case 32516:
                Polygon(new(15, 1), new(27, 13), new(19, 13), new(19, 29), new(11, 29), new(11, 13), new(3, 13));
                break;
            case 32642: case 32643: case 32644: case 32645:
                Polygon(new(1, 15), new(8, 8), new(8, 12), new(22, 12), new(22, 8), new(29, 15),
                    new(22, 22), new(22, 18), new(8, 18), new(8, 22));
                using (var rotate = new Matrix())
                {
                    rotate.RotateAt(role == 32642 ? 45 : role == 32643 ? -45 : role == 32645 ? 90 : 0, new PointF(15, 15));
                    path.Transform(rotate);
                }
                break;
            case 32646:
                Polygon(new(15, 1), new(21, 7), new(17, 7), new(17, 13), new(23, 13), new(23, 9),
                    new(29, 15), new(23, 21), new(23, 17), new(17, 17), new(17, 23), new(21, 23),
                    new(15, 29), new(9, 23), new(13, 23), new(13, 17), new(7, 17), new(7, 21),
                    new(1, 15), new(7, 9), new(7, 13), new(13, 13), new(13, 7), new(9, 7));
                break;
            case 32648:
                path.AddEllipse(1, 1, 28, 28); path.AddEllipse(5, 5, 20, 20);
                // A separate figure would XOR the ring. Union via winding fill.
                path.Reset();
                path.FillMode = FillMode.Winding;
                path.AddArc(1, 1, 28, 28, 0, 360); path.CloseFigure();
                path.AddArc(5, 5, 20, 20, 0, -360); path.CloseFigure();
                Polygon(new(4, 7), new(7, 4), new(26, 23), new(23, 26));
                break;
            case 32649:
                path.AddBezier(10, 16, 8, 14, 5, 13, 4, 16);
                path.AddBezier(4, 16, 3, 18, 9, 24, 11, 29);
                path.AddLine(11, 29, 23, 29);
                path.AddBezier(23, 29, 26, 25, 28, 21, 28, 16);
                path.AddBezier(28, 16, 28, 12, 25, 12, 24, 14);
                path.AddBezier(24, 14, 24, 10, 21, 10, 20, 12);
                path.AddBezier(20, 12, 20, 8, 17, 8, 16, 11);
                path.AddLine(16, 11, 16, 3);
                path.AddBezier(16, 3, 16, -1, 10, -1, 10, 3);
                path.AddLine(10, 3, 10, 16); path.CloseFigure();
                break;
        }
        return path;
    }

    public void Dispose() { lock (_gate) { _native.Dispose(); } }
}
