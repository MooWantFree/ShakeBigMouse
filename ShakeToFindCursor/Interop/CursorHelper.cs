using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ShakeToFindCursor;

public static partial class CursorHelper
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern IntPtr CopyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern IntPtr CopyImage(IntPtr original, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetSystemCursor(IntPtr hcur, uint id);
    [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO icon);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    private const uint SPI_SETCURSORS = 0x0057;
    private const int SM_CXCURSOR = 13;
    private const int SM_CYCURSOR = 14;

    private const uint IMAGE_CURSOR = 2;
    private const uint LR_LOADFROMFILE = 0x00000010;

    public static readonly uint[] SYSTEM_CURSORS = new uint[]
    {
        32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651
    };

    public static readonly Dictionary<uint, string> CURSOR_REG_NAMES = new()
    {
        { 32512, "Arrow" }, { 32513, "IBeam" }, { 32514, "Wait" }, { 32515, "Crosshair" },
        { 32516, "UpArrow" }, { 32642, "SizeNWSE" }, { 32643, "SizeNESW" },
        { 32644, "SizeWE" }, { 32645, "SizeNS" }, { 32646, "SizeAll" }, { 32648, "No" },
        { 32649, "Hand" }, { 32650, "AppStarting" }, { 32651, "Help" }
    };

    private static readonly object _sync = new();
    internal sealed record CursorSource(string? Path, IntPtr Handle, Size Size, VectorCursorArtwork? Artwork, bool Monochrome);
    private static readonly Dictionary<uint, CursorSource> _sources = new();
    private static uint _sourceDpi = 96;
    public const int ScaleSteps = 64;
    private static double[] _frameScales = Array.Empty<double>();
    private static bool _isCached, _building, _disposed, _sourcesDirty = true;
    private static double _requestedFactor = -1;
    private static double _cachedFactor = -1;
    private static uint _requestedDpi = 96;
    private static long _requestVersion;
    private static Task _buildTask = Task.CompletedTask;
    private static int _lastBaseIndex;
    public static bool IsCached { get { lock (_sync) return _isCached; } }
    public static bool IsReady { get { lock (_sync) return !_disposed && !_sourcesDirty && _sources.Count == SYSTEM_CURSORS.Length; } }
    public static event Action? CacheChanged;
    internal static long CacheGeneration { get; private set; }
    internal static uint CachedDpi { get; private set; }

    // One builder, latest request wins. Neither cursor animation nor the mouse
    // hook waits for GDI asset generation. Stale results are freed, not published.
    public static Task RequestCaches(double factor, uint dpi, bool refreshSources = false)
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            factor = Math.Clamp(factor, 1, 10);
            dpi = Math.Clamp(dpi, 96u, 768u);
            if (!refreshSources && factor == _requestedFactor && dpi == _requestedDpi
                && (_building || (_isCached && CachedDpi == dpi && _cachedFactor == factor))) return _buildTask;
            _requestedFactor = factor;
            _requestedDpi = dpi;
            _sourcesDirty |= refreshSources;
            if (refreshSources) _isCached = false;
            _requestVersion++;
            if (!_building)
            {
                _building = true;
                _buildTask = Task.Run(BuildPendingCaches);
            }
            return _buildTask;
        }
    }

    private static void BuildPendingCaches()
    {
        while (true)
        {
            double factor;
            uint dpi;
            long version;
            try
            {
                bool captured = false;
                lock (_sync)
                {
                    if (_disposed) { _building = false; return; }
                    factor = _requestedFactor;
                    dpi = _requestedDpi;
                    version = _requestVersion;
                    if (_sourcesDirty)
                    {
                        // Never magnify an already magnified shared system cursor.
                        RestoreThemeCursors();
                        CaptureSources();
                        _sourcesDirty = false;
                        captured = true;
                    }
                }
                // The first shake can render immediately from these sources.
                if (captured) CacheChanged?.Invoke();
            }
            catch
            {
                lock (_sync) _building = false;
                throw;
            }

            try
            {
                var scales = FrameScales(factor);
                lock (_sync)
                {
                    if (_disposed || version != _requestVersion) continue;
                    int largest = _sources.Values.Select(source => ScaleNativeSize(source.Size, _sourceDpi, dpi))
                        .Max(size => Math.Max(size.Width, size.Height));
                    _lastBaseIndex = Array.FindLastIndex(scales, s => Math.Round(largest * s) == largest);
                    _frameScales = scales;
                    CachedDpi = dpi;
                    _cachedFactor = factor;
                    // The originals are already pinned. Warm just the small arrow
                    // frames; all roles and DPI variants share one bounded LRU.
                }
                foreach (double scale in scales)
                {
                    if (scale > 2) break;
                    lock (_sync)
                    {
                        if (_disposed || version != _requestVersion) break;
                        var key = SelectFrame(Arrow, scale, dpi);
                        var size = ScaleNativeSize(_sources[Arrow].Size, _sourceDpi, dpi);
                        if (key.Scale > 1 && Math.Max(size.Width, size.Height) * key.Scale <= 128)
                            GetFrame(key, size, out _);
                    }
                }
                lock (_sync)
                {
                    if (_disposed || version != _requestVersion) continue;
                    _isCached = true;
                    CacheGeneration++;
                    _building = false;
                }
                CacheChanged?.Invoke();
                return;
            }
            catch
            {
                lock (_sync) _building = false;
                throw;
            }
        }
    }

    internal static double[] FrameScales(double factor)
    {
        var scales = Enumerable.Range(0, ScaleSteps)
            .Select(i => 1 + (factor - 1) * Math.Pow(i / (double)(ScaleSteps - 1), 1.8)).ToList();
        for (int i = 2; i <= 10; i++)
        {
            double s = 1 + i / 100.0;
            if (s < factor && scales.All(other => Math.Abs(s - other) > .004)) scales.Add(s);
        }
        scales.Sort();
        return scales.ToArray();
    }

    private static bool IsRestArrow(CursorSource source, double scale) =>
        source.Artwork?.IsArrow == true && scale <= VectorCursorArtwork.ArrowRest;

    private static void CaptureSources()
    {
        ClearFrameCache();
        _sharedRoles.Clear();
        _frameScales = Array.Empty<double>();
        _isCached = false;
        foreach (var source in _sources.Values)
        {
            source.Artwork?.Dispose();
            if (source.Handle != IntPtr.Zero) DestroyIcon(source.Handle);
        }
        _sources.Clear();
        _sourceDpi = MonitorDpi.SystemDpi;
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
        using var accessibility = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
        bool standardWhite = Convert.ToInt32(accessibility?.GetValue("CursorType", 0)) == 0;
        foreach (uint id in SYSTEM_CURSORS)
        {
            string? path = CURSOR_REG_NAMES.TryGetValue(id, out var name) ? key?.GetValue(name) as string : null;
            if (!string.IsNullOrWhiteSpace(path)) path = Environment.ExpandEnvironmentVariables(path);
            IntPtr shared = LoadCursor(IntPtr.Zero, (int)id);
            IntPtr original = CopyIcon(shared);
            if (original == IntPtr.Zero) throw new InvalidOperationException($"Cannot capture native cursor {id}.");
            Size size = GetCursorSize(original, out bool monochrome);
            if (size.IsEmpty) size = new Size(GetSystemMetricsForDpi(SM_CXCURSOR, _sourceDpi),
                GetSystemMetricsForDpi(SM_CYCURSOR, _sourceDpi));
            var artwork = standardWhite ? VectorCursorArtwork.TryCreate(id, path, original) : null;
            _sharedRoles.TryAdd(shared, id);
            _installed[id] = new FrameKey(id, _sourceDpi, 1);
            _sources[id] = new CursorSource(path, original, size, artwork, monochrome);
        }
    }

    internal static Size ScaleNativeSize(Size original, uint sourceDpi, uint targetDpi) => new(
        Math.Max(1, (int)Math.Round(original.Width * (double)GetSystemMetricsForDpi(SM_CXCURSOR, targetDpi)
            / Math.Max(1, GetSystemMetricsForDpi(SM_CXCURSOR, sourceDpi)))),
        Math.Max(1, (int)Math.Round(original.Height * (double)GetSystemMetricsForDpi(SM_CYCURSOR, targetDpi)
            / Math.Max(1, GetSystemMetricsForDpi(SM_CYCURSOR, sourceDpi)))));

    internal static Size GetCursorSize(IntPtr handle) => GetCursorSize(handle, out _);

    internal static Size GetCursorSize(IntPtr handle, out bool monochrome)
    {
        monochrome = false;
        if (handle == IntPtr.Zero || !GetIconInfo(handle, out var info)) return Size.Empty;
        try
        {
            monochrome = info.hbmColor == IntPtr.Zero;
            IntPtr bitmap = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
            if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var data) == 0) return Size.Empty;
            return new Size(data.Width, info.hbmColor != IntPtr.Zero ? data.Height : data.Height / 2);
        }
        finally { DeleteObject(info.hbmColor); DeleteObject(info.hbmMask); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);

    // System broadcasts may also follow our own restore calls. Compare relevant
    // persisted settings/assets so those broadcasts cannot start a rebuild loop.
    public static string GetThemeSignature()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
        using var accessibility = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
        var signature = new System.Text.StringBuilder();
        foreach (string name in key?.GetValueNames() ?? Array.Empty<string>())
        {
            object? value = key?.GetValue(name);
            signature.Append(name).Append('=').Append(value).Append(';');
            if (value is string path && System.IO.File.Exists(Environment.ExpandEnvironmentVariables(path)))
                signature.Append(System.IO.File.GetLastWriteTimeUtc(Environment.ExpandEnvironmentVariables(path)).Ticks);
        }
        foreach (string name in new[] { "CursorSize", "CursorType", "CursorColor" })
            signature.Append(name).Append('=').Append(accessibility?.GetValue(name)).Append(';');
        signature.Append(GetSystemMetricsForDpi(SM_CXCURSOR, 96));
        return signature.ToString();
    }

    public static void Shutdown()
    {
        Task build;
        lock (_sync) { _disposed = true; build = _buildTask; }
        try { build.GetAwaiter().GetResult(); } catch { }
        lock (_sync)
        {
            ClearFrameCache();
            _sharedRoles.Clear();
            foreach (var source in _sources.Values)
            {
                source.Artwork?.Dispose();
                if (source.Handle != IntPtr.Zero) DestroyIcon(source.Handle);
            }
            _sources.Clear();
            _isCached = false;
        }
    }

    private static IntPtr GenerateFrame(string? path, IntPtr fallback, double scale, int width, int height)
    {
        int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        IntPtr cursor = string.IsNullOrWhiteSpace(path) ? IntPtr.Zero
            : LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, targetWidth, targetHeight, LR_LOADFROMFILE);
        if (cursor == IntPtr.Zero)
            return fallback == IntPtr.Zero ? IntPtr.Zero : MagnifyCursorFallback(fallback, targetWidth, targetHeight);
        // An ANI contains many bitmap frames. Retaining a full ANI at every
        // scale exhausts the process GDI quota while two DPI caches coexist.
        // Enlarged frames use a snapshot; scale 1 keeps the native source copy.
        try
        {
            if (!GetIconInfo(cursor, out var info)) return IntPtr.Zero;
            try { info.fIcon = false; return CreateIconIndirect(ref info); }
            finally { DeleteObject(info.hbmColor); DeleteObject(info.hbmMask); }
        }
        finally { DestroyIcon(cursor); }
    }

    internal static IntPtr CreateFrame(CursorSource source, double scale, Size size)
    {
        var target = new Size(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
        if (target == size || IsRestArrow(source, scale)) return CreateBaseFrame(source, size);
        if (source.Monochrome && !IsAnimated(source))
            return CopyImage(source.Handle, IMAGE_CURSOR, target.Width, target.Height, 0);
        if (source.Artwork == null) return GenerateFrame(source.Path, source.Handle, scale, size.Width, size.Height);
        // On a different DPI the endpoint is the actual destination base frame,
        // which may differ from resampling the startup monitor's snapshot.
        IntPtr baseline = size != source.Size && scale < 1.6 ? CreateBaseFrame(source, size) : IntPtr.Zero;
        try { return source.Artwork.CreateCursor(target.Width, target.Height, scale, baseline); }
        finally { if (baseline != IntPtr.Zero) DestroyIcon(baseline); }
    }

    private static bool IsAnimated(CursorSource source) =>
        string.Equals(System.IO.Path.GetExtension(source.Path), ".ani", StringComparison.OrdinalIgnoreCase);

    private static IntPtr CreateBaseFrame(CursorSource source, Size size)
    {
        if (size == source.Size) return CopyIcon(source.Handle);
        // Keep the theme's animation at normal size on other DPI screens too.
        // Only one native ANI per role/DPI is retained, never one per scale step.
        if (IsAnimated(source))
        {
            IntPtr animated = LoadImage(IntPtr.Zero, source.Path!, IMAGE_CURSOR, size.Width, size.Height, LR_LOADFROMFILE);
            if (animated != IntPtr.Zero) return animated;
        }
        return source.Monochrome ? CopyImage(source.Handle, IMAGE_CURSOR, size.Width, size.Height, 0)
            : MagnifyCursorFallback(source.Handle, size.Width, size.Height);
    }

    private static IntPtr MagnifyCursorFallback(IntPtr hCursor, int newW, int newH)
    {
        IntPtr hSafeCopy = CopyIcon(hCursor);
        if (hSafeCopy == IntPtr.Zero) return IntPtr.Zero;

        try
        {
            // Extract hotspot from the safe copy, not the shared system handle
            if (!GetIconInfo(hSafeCopy, out ICONINFO origInfo))
                return IntPtr.Zero;

            int hotX = origInfo.xHotspot;
            int hotY = origInfo.yHotspot;

            // Get bitmap data then immediately release the GDI objects
            Bitmap originalBmp;
            try
            {
                using Icon tmpIcon = Icon.FromHandle(hSafeCopy);
                originalBmp = tmpIcon.ToBitmap();
            }
            finally
            {
                DeleteObject(origInfo.hbmColor);
                DeleteObject(origInfo.hbmMask);
            }

            double scaleX = newW / (double)originalBmp.Width;
            double scaleY = newH / (double)originalBmp.Height;

            using (originalBmp)
            using (Bitmap newBmp = new Bitmap(newW, newH, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(newBmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(originalBmp, 0, 0, newW, newH);
                }

                return CursorFromBitmap(newBmp, (int)(hotX * scaleX), (int)(hotY * scaleY));
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            DestroyIcon(hSafeCopy);
        }
    }

    private static int FrameIndex(double scale)
    {
        int index = Array.BinarySearch(_frameScales, Math.Clamp(scale, _frameScales[0], _frameScales[^1]));
        if (index >= 0) return index <= _lastBaseIndex ? 0 : index;
        index = ~index;
        if (index >= _frameScales.Length) return _frameScales.Length - 1;
        if (index > 0 && Math.Abs(_frameScales[index - 1] - scale) <= Math.Abs(_frameScales[index] - scale)) index--;
        return index <= _lastBaseIndex ? 0 : index;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DibHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref DibHeader header,
        uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes,
        uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr cursor,
        int width, int height, uint step, IntPtr brush, uint flags);

    // DrawIconEx needs a real destination for alpha compositing. Recover each
    // pixel's premultiplied color from black, and its opacity from white-black.
    // This also works for legacy color cursors whose alpha is supplied by a mask.
    internal static Bitmap SnapshotColorCursor(IntPtr cursor, Size size)
    {
        int w = size.Width, h = size.Height;
        var header = new DibHeader { Size = 40, Width = 2 * w, Height = -h, Planes = 1, BitCount = 32 };
        IntPtr dc = CreateCompatibleDC(0), image = CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
        IntPtr previous = IntPtr.Zero;
        try
        {
            if (dc == 0 || image == 0 || (previous = SelectObject(dc, image)) == 0 ||
                !PatBlt(dc, 0, 0, w, h, 0x00000042) || !PatBlt(dc, w, 0, w, h, 0x00FF0062) ||
                !DrawIconEx(dc, 0, 0, cursor, w, h, 0, 0, 3) || !DrawIconEx(dc, w, 0, cursor, w, h, 0, 0, 3))
                throw new InvalidOperationException("Cannot capture native cursor pixels.");
            GdiFlush();
            var pixels = new int[checked(2 * w * h)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * 2 * w + x, black = pixels[i], white = pixels[i + w];
                int alpha = Math.Clamp(255 - ((white & 255) - (black & 255)), 0, 255);
                pixels[y * w + x] = (black & 0x00ffffff) | (alpha << 24);
            }
            var result = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(pixels, 0, data.Scan0, w * h); }
            finally { result.UnlockBits(data); }
            return result;
        }
        finally { if (previous != 0) SelectObject(dc, previous); if (image != 0) DeleteObject(image); if (dc != 0) DeleteDC(dc); }
    }

    internal static IntPtr CursorFromBitmap(Bitmap bitmap, int hotX, int hotY)
    {
        // CreateIconIndirect premultiplies the color plane. Supply straight BGRA
        // here; passing the renderer's PArgb bytes darkens translucent edges twice.
        int w = bitmap.Width, h = bitmap.Height, stride = checked(w * 4);
        var header = new DibHeader { Size = (uint)Marshal.SizeOf<DibHeader>(), Width = w, Height = -h,
            Planes = 1, BitCount = 32 };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var pixels, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        IntPtr mask = IntPtr.Zero;
        try
        {
            int maskStride = ((w + 15) / 16) * 2;
            var andMask = new byte[checked(maskStride * h)];
            var row = new int[w];
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
                    Marshal.Copy(row, 0, pixels + y * stride, w);
                    // The stock color arrow sets its AND bit on transparent
                    // pixels only. Keep the mask and alpha silhouettes consistent.
                    for (int x = 0; x < w; x++)
                        if ((row[x] >>> 24) == 0)
                            andMask[y * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
            finally { bitmap.UnlockBits(data); }
            mask = CreateBitmap(w, h, 1, 1, andMask);
            if (mask == IntPtr.Zero) return IntPtr.Zero;
            var info = new ICONINFO { hbmColor = color, hbmMask = mask,
                xHotspot = Math.Clamp(hotX, 0, w - 1), yHotspot = Math.Clamp(hotY, 0, h - 1) };
            return CreateIconIndirect(ref info);
        }
        finally { if (mask != IntPtr.Zero) DeleteObject(mask); DeleteObject(color); }
    }

    public static void RestoreThemeCursors()
    {
        lock (_sync)
        {
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            _installed.Clear();
        }
    }
}
