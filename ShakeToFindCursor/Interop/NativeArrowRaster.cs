using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ShakeToFindCursor;

// Each native pixel occupies a unit square. During enlargement a destination
// pixel overlaps at most two squares per axis. Integrate their coverage to keep
// a thin edge moving continuously, including its original antialiasing.
internal sealed class NativeArrowRaster
{
    internal Size Size { get; }
    internal Point Hotspot { get; }
    private readonly byte[] _pixels;
    private readonly Rectangle _ink;

    internal NativeArrowRaster(Bitmap bitmap, Point hotspot)
    {
        Size = bitmap.Size;
        Hotspot = hotspot;
        // Transparent border supplies the sample just outside the native canvas.
        int stride = (Size.Width + 2) * 4;
        _pixels = new byte[stride * (Size.Height + 2)];
        var data = bitmap.LockBits(new Rectangle(Point.Empty, Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        int left = Size.Width, top = Size.Height, right = 0, bottom = 0;
        try
        {
            for (int y = 0; y < Size.Height; y++)
            {
                int row = (y + 1) * stride + 4;
                Marshal.Copy(data.Scan0 + y * data.Stride, _pixels, row, Size.Width * 4);
                for (int x = 0; x < Size.Width; x++)
                {
                    if (_pixels[row + x * 4 + 3] == 0) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x + 1);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        _ink = Rectangle.FromLTRB(left, top, right, bottom);
    }

    internal bool FitsNativeCanvas(double zoom) =>
        Hotspot.X + (_ink.Left - Hotspot.X) * zoom >= 0 &&
        Hotspot.Y + (_ink.Top - Hotspot.Y) * zoom >= 0 &&
        Hotspot.X + (_ink.Right - Hotspot.X) * zoom <= Size.Width &&
        Hotspot.Y + (_ink.Bottom - Hotspot.Y) * zoom <= Size.Height;

    private readonly record struct Sample(int First, int Second, double SecondWeight);

    private static Sample[] MapAxis(int length, int sourceLength, int hotspot, int destinationHotspot, double zoom)
    {
        var samples = new Sample[length];
        for (int i = 0; i < length; i++)
        {
            double start = hotspot + (i - destinationHotspot) / zoom;
            int first = (int)Math.Floor(start);
            double secondWeight = Math.Clamp(1 - (first + 1 - start) * zoom, 0, 1);
            samples[i] = new(Math.Clamp(first + 1, 0, sourceLength + 1),
                Math.Clamp(first + 2, 0, sourceLength + 1), secondWeight);
        }
        return samples;
    }

    internal Bitmap Render(int width, int height, Point hotspot, double zoom)
    {
        var xs = MapAxis(width, Size.Width, Hotspot.X, hotspot.X, zoom);
        var ys = MapAxis(height, Size.Height, Hotspot.Y, hotspot.Y, zoom);
        var output = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = output.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, output.PixelFormat);
        try
        {
            var row = new byte[width * 4];
            int stride = (Size.Width + 2) * 4;
            for (int y = 0; y < height; y++)
            {
                var sy = ys[y];
                for (int x = 0; x < width; x++)
                {
                    var sx = xs[x];
                    for (int c = 0; c < 4; c++)
                    {
                        int left = sx.First * 4 + c, right = sx.Second * 4 + c;
                        double upper = _pixels[sy.First * stride + left] * (1 - sx.SecondWeight)
                            + _pixels[sy.First * stride + right] * sx.SecondWeight;
                        double lower = _pixels[sy.Second * stride + left] * (1 - sx.SecondWeight)
                            + _pixels[sy.Second * stride + right] * sx.SecondWeight;
                        row[x * 4 + c] = (byte)Math.Round(upper + (lower - upper) * sy.SecondWeight);
                    }
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { output.UnlockBits(data); }
        return output;
    }
}
