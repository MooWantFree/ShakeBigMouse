using System.Drawing;
using System.Runtime.InteropServices;

namespace ShakeToFindCursor;

public static partial class CursorHelper
{
    private const uint Arrow = 32512;
    // Charge color plus mask conservatively. The handle limit also bounds many
    // tiny entries, whose USER/GDI overhead is not proportional to pixel area.
    internal const long FrameBudget = 16 * 1024 * 1024;
    internal const int FrameLimit = 128;
    private readonly record struct FrameKey(uint Role, uint Dpi, double Scale);
    private sealed record Entry(FrameKey Key, IntPtr Handle, long Bytes);
    private static readonly LinkedList<Entry> _recent = new();
    private static readonly Dictionary<FrameKey, LinkedListNode<Entry>> _frames = new();
    private static readonly Dictionary<uint, FrameKey> _installed = new();
    private static readonly Dictionary<IntPtr, uint> _sharedRoles = new();
    private static long _frameBytes;
    internal static (int Count, long Bytes, long Installs) Usage
    {
        get { lock (_sync) return (_frames.Count, _frameBytes, _installCount); }
    }
    private static long _installCount;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size, Flags; public IntPtr Handle; public MouseHook.NativePoint Point; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);

    private static FrameKey SelectFrame(uint id, double scale, uint dpi)
    {
        double frame = CachedDpi == dpi && _frameScales.Length > 0
            ? _frameScales[FrameIndex(scale)] : Math.Round(Math.Clamp(scale, 1, 10), 2);
        if (id == Arrow && _sources[id].Artwork?.IsArrow == true && scale <= VectorCursorArtwork.ArrowTail)
        {
            const double start = VectorCursorArtwork.ArrowRest, end = VectorCursorArtwork.ArrowTail;
            int step = (int)Math.Round(64 * Math.Clamp((scale - start) / (end - start), 0, 1));
            frame = step == 0 ? 1 : start + (end - start) * step / 64;
        }
        return new(id, dpi, frame);
    }

    // All access is under _sync. A borrowed handle must be copied before
    // SetSystemCursor takes ownership; an oversize one passes straight through.
    private static IntPtr GetFrame(FrameKey key, Size size, out bool borrowed)
    {
        borrowed = true;
        var source = _sources[key.Role];
        if (key.Scale == 1 && source.Size == size) return source.Handle;
        if (_frames.TryGetValue(key, out var hit))
        {
            _recent.Remove(hit);
            _recent.AddFirst(hit);
            return hit.Value.Handle;
        }

        long width = Math.Max(1, (long)Math.Round(size.Width * key.Scale));
        long height = Math.Max(1, (long)Math.Round(size.Height * key.Scale));
        long bytes = width * height * 5;
        var handle = CreateFrame(source, key.Scale, size);
        if (handle == IntPtr.Zero || bytes > FrameBudget)
        {
            borrowed = false;
            return handle;
        }
        while (_frames.Count >= FrameLimit || _frameBytes + bytes > FrameBudget)
        {
            // Retain small arrow frames (particularly the finely stepped native
            // handoff) ahead of large or infrequently visible roles.
            var oldest = _recent.Last;
            while (oldest != null && oldest.Value.Key.Role == Arrow && oldest.Value.Key.Scale <= 2
                && oldest.Value.Bytes <= 128 * 128 * 5)
                oldest = oldest.Previous;
            oldest ??= _recent.Last!;
            _recent.Remove(oldest);
            _frames.Remove(oldest.Value.Key);
            DestroyIcon(oldest.Value.Handle);
            _frameBytes -= oldest.Value.Bytes;
        }
        _frames.Add(key, _recent.AddFirst(new Entry(key, handle, bytes)));
        _frameBytes += bytes;
        return handle;
    }

    private static void ClearFrameCache()
    {
        foreach (var entry in _recent) DestroyIcon(entry.Handle);
        _frames.Clear();
        _recent.Clear();
        _frameBytes = 0;
        _installed.Clear();
    }

    private static void Install(FrameKey key)
    {
        if (_installed.TryGetValue(key.Role, out var previous) && previous == key) return;
        var size = ScaleNativeSize(_sources[key.Role].Size, _sourceDpi, key.Dpi);
        var handle = GetFrame(key, size, out bool borrowed);
        if (handle == IntPtr.Zero) return;
        var owned = borrowed ? CopyIcon(handle) : handle;
        if (owned == IntPtr.Zero) return;
        if (!SetSystemCursor(owned, key.Role)) { DestroyIcon(owned); return; }
        _installed[key.Role] = key;
        _installCount++;
    }

    public static void ApplyScale(double scale)
    {
        lock (_sync)
        {
            if (_frameScales.Length != 0) ApplyScaleForDpi(scale, CachedDpi);
        }
    }

    internal static void ApplyScaleForDpi(double scale, uint pointerDpi)
    {
        lock (_sync)
        {
            if (_disposed || _sourcesDirty || _sources.Count == 0) return;
            if (_requestedDpi != pointerDpi)
                _ = RefreshPointerDpiAsync(_requestedFactor, pointerDpi);
            if (scale <= 1)
            {
                // Repair every touched role and any new monitor baseline. Unchanged
                // roles are skipped, so native animated cursors keep their phase.
                foreach (uint id in SYSTEM_CURSORS) Install(new FrameKey(id, pointerDpi, 1));
                return;
            }
            // Keep the ordinary arrow ready as the fallback when leaving controls.
            // Private application cursors have no system role to replace.
            Install(SelectFrame(Arrow, scale, pointerDpi));
            var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (GetCursorInfo(ref info) && _sharedRoles.TryGetValue(info.Handle, out uint role) && role != Arrow)
                Install(SelectFrame(role, scale, pointerDpi));
        }
    }

    private static async Task RefreshPointerDpiAsync(double factor, uint dpi)
    {
        try { await RequestCaches(factor, dpi).ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Cursor DPI refresh failed: {0}", ex); }
    }
}
