using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using ShakeToFindCursor;

internal static partial class Program
{
    private static void VerifyVectorArtwork()
    {
        Check("vector artwork keeps large edges sharp, hotspots scaled, and custom themes untouched", () =>
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors");
            string arrowPath = Path.Combine(folder, "aero_arrow.cur");
            if (VectorCursorArtwork.Supports(32512, Path.Combine(Path.GetTempPath(), "aero_arrow.cur")))
                throw new Exception("A custom theme was mistaken for the system theme.");
            if (VectorCursorArtwork.Supports(32512, Path.Combine(folder, "aero_link.cur")))
                throw new Exception("Cursor roles are not checked.");

            // File-backed handles avoid depending on another running utility's
            // modifications of shared system cursors.
            IntPtr handle = CursorHelper.LoadImage(IntPtr.Zero, arrowPath, 2, 64, 64, 0x10);
            try
            {
                using var artwork = VectorCursorArtwork.TryCreate(32512, arrowPath, handle)
                    ?? throw new Exception("Standard arrow did not get vector artwork.");
                using var large = artwork.Render(640, 640, 10);
                int maxSoftRun = 0;
                // Scan across the sloping arrow edge, not along a horizontal
                // antialiased segment (whose entire length can have partial alpha).
                foreach (int y in new[] { large.Height / 5, large.Height / 4, large.Height / 3 })
                {
                    int softRun = 0;
                    for (int x = 0; x < large.Width; x++)
                    {
                        int alpha = large.GetPixel(x, y).A;
                        softRun = alpha is > 8 and < 247 ? softRun + 1 : 0;
                        maxSoftRun = Math.Max(maxSoftRun, softRun);
                    }
                }
                if (maxSoftRun > 4) throw new Exception($"10x edge has {maxSoftRun} soft pixels.");
                var original = CursorDescription(handle);
                IntPtr enlarged = artwork.CreateCursor(640, 640, 10);
                try
                {
                    var actual = CursorDescription(enlarged);
                    if (actual.Size != new Size(640, 640) || actual.HotX != original.HotX * 10 || actual.HotY != original.HotY * 10)
                        throw new Exception("Vector cursor size/hotspot changed.");
                }
                finally { CursorHelper.DestroyIcon(enlarged); }
                Console.WriteLine($"  640px vector arrow: maximum partial-alpha run {maxSoftRun}px");
                Directory.CreateDirectory("tests/BehaviorTests/obj");
                large.Save("tests/BehaviorTests/obj/vector-arrow-640.png", ImageFormat.Png);
            }
            finally { CursorHelper.DestroyIcon(handle); }

            string[] files = ["aero_arrow.cur", "", "aero_busy.ani", "", "aero_up.cur", "aero_nwse.cur",
                "aero_nesw.cur", "aero_ew.cur", "aero_ns.cur", "aero_move.cur", "aero_unavail.cur",
                "aero_link.cur", "aero_working.ani", "aero_helpsel.cur"];
            using var sheet = new Bitmap(7 * 220, 2 * 255);
            using (var graphics = Graphics.FromImage(sheet))
            {
                graphics.Clear(Color.FromArgb(215, 220, 225));
                for (int i = 0; i < files.Length; i++)
                {
                    uint id = CursorHelper.SYSTEM_CURSORS[i];
                    string? path = files[i] == "" ? null : Path.Combine(folder, files[i]);
                    IntPtr source = path == null ? CursorHelper.CopyIcon(CursorHelper.LoadCursor(IntPtr.Zero, (int)id))
                        : CursorHelper.LoadImage(IntPtr.Zero, path, 2, 64, 64, 0x10);
                    try
                    {
                        var size = CursorHelper.GetCursorSize(source, out bool mono);
                        using var artwork = VectorCursorArtwork.TryCreate(id, path, source);
                        if (artwork == null && !mono) throw new Exception($"No artwork for standard role {id}.");
                        var asset = new CursorHelper.CursorSource(path, source, size, artwork, mono);
                        IntPtr cursor = CursorHelper.CreateFrame(asset, 4, new Size(50, 50));
                        if (cursor == IntPtr.Zero) throw new Exception($"Native cursor {id} failed.");
                        using var image = PaintCursor(cursor, 200, Color.FromArgb(215, 220, 225));
                        CursorHelper.DestroyIcon(cursor);
                        graphics.DrawImageUnscaled(image, (i % 7) * 220 + 10, (i / 7) * 255 + 35);
                        graphics.DrawString(CursorHelper.CURSOR_REG_NAMES[id], SystemFonts.DefaultFont, Brushes.Black,
                            (i % 7) * 220 + 10, (i / 7) * 255 + 10);
                        cursor = CursorHelper.CreateFrame(asset, 1.1, new Size(48, 48));
                        if (cursor == IntPtr.Zero) throw new Exception($"Destination DPI tail for {id} failed.");
                        CursorHelper.DestroyIcon(cursor);
                    }
                    finally { CursorHelper.DestroyIcon(source); }
                }
            }
            sheet.Save("tests/BehaviorTests/obj/vector-contact-sheet.png", ImageFormat.Png);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetShellWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint WindowFromPoint(MouseHook.NativePoint point);

    private static void VerifyDesktopFullscreenDetection()
    {
        var desktop = GetShellWindow();
        if (desktop == 0) throw new Exception("This check requires the Windows shell desktop.");
        if (FullscreenDetector.IsFullscreenWindow(desktop))
            throw new Exception("The shell desktop was classified as a fullscreen app.");

        using var form = new System.Windows.Forms.Form {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds
        };
        if (!FullscreenDetector.IsFullscreenWindow(form.Handle))
            throw new Exception("A borderless app covering the monitor was missed.");
    }

    private static void VerifyVisibleRolesAndBudget(uint dpi)
    {
        if (!GetPhysicalCursorPos(out var saved)) throw new Exception("Missing cursor position.");
        using var form = new System.Windows.Forms.Form { Width = 340, Height = 160, TopMost = true,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen, Text = "Cursor role test" };
        try
        {
            using var foreground = new ForegroundAppHistory();
            int notifications = 0;
            if (!foreground.Watch(() => notifications++)) throw new Exception("Cannot register foreground events.");
            form.Show();
            form.Activate();
            var point = form.PointToScreen(new Point(80, 60));
            SetPhysicalCursorPos(point.X, point.Y);
            if (GetForegroundWindow() != form.Handle && WindowFromPoint(new MouseHook.NativePoint(point.X,point.Y)) == form.Handle)
                mouse_event(6,0,0,0,0);
            var wait = Stopwatch.StartNew();
            while ((notifications == 0 || GetForegroundWindow() != form.Handle) && wait.ElapsedMilliseconds < 1500)
            {
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(5);
            }
            if (notifications == 0) throw new Exception("Foreground changes were not delivered.");
            var previousBounds = form.Bounds;
            form.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            form.Activate();
            form.Bounds = System.Windows.Forms.Screen.FromControl(form).Bounds;
            System.Windows.Forms.Application.DoEvents();
            if (!FullscreenDetector.ShouldDisable([], true) || FullscreenDetector.ShouldDisable([], false))
                throw new Exception($"Fullscreen check failed: focus={GetForegroundWindow()==form.Handle}, rect={form.Bounds}, monitor={System.Windows.Forms.Screen.FromControl(form).Bounds}, info={FullscreenDetector.GetForegroundInfo().IsFullscreen}");
            form.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
            form.Bounds = previousBounds;
            System.Windows.Forms.Application.DoEvents();
            if (FullscreenDetector.ShouldDisable([], true)) throw new Exception("Windowed mode still suppressed.");
            point = form.PointToScreen(new Point(80, 60));
            SetPhysicalCursorPos(point.X, point.Y);
            CursorHelper.RequestCaches(10, dpi).GetAwaiter().GetResult();
            foreach (uint role in new uint[] { 32512, 32513, 32649, 32512 })
            {
                var shared = CursorHelper.LoadCursor(0, (int)role);
                using var selected = new System.Windows.Forms.Cursor(shared);
                form.Cursor = selected;
                System.Windows.Forms.Cursor.Current = selected;
                System.Windows.Forms.Application.DoEvents();
                long before = CursorHelper.Usage.Installs;
                CursorHelper.ApplyScaleForDpi(3, dpi);
                if (CursorHelper.GetCursorSize(shared).Width < 2 * CursorHelper.GetCursorSize(
                    CursorHelper.LoadCursor(0, 32516)).Width)
                    throw new Exception($"Visible role {role} missed the scale change.");
                if (CursorHelper.Usage.Installs - before > 2) throw new Exception("Updated inactive roles.");
                long installed = CursorHelper.Usage.Installs;
                CursorHelper.ApplyScaleForDpi(3, dpi);
                if (CursorHelper.Usage.Installs != installed) throw new Exception("Installed identical frames twice.");
            }
            foreach (double scale in CursorHelper.FrameScales(10))
                CursorHelper.ApplyScaleForDpi(scale, dpi);
            var usage = CursorHelper.Usage;
            if (usage.Count > CursorHelper.FrameLimit || usage.Bytes > CursorHelper.FrameBudget)
                throw new Exception($"Cache over budget: {usage}");
            Console.WriteLine($"  Arrow / text / link switches passed, cache {usage.Count} entries, {usage.Bytes/1048576.0:F1} MiB");
        }
        finally { form.Close(); SetPhysicalCursorPos(saved.X, saved.Y); CursorHelper.ApplyScaleForDpi(1, dpi); }
    }

    private static void VerifyColdStart(Dictionary<uint, (Size Size, int HotX, int HotY)> originals, uint sourceDpi)
    {
        using var sourcesReady = new ManualResetEventSlim();
        using var finishBuild = new ManualResetEventSlim();
        var settings = new AppSettings { MinimumShakeDistance = 1283, ScaleWithShakeStrength = false,
            MaxSizeHoldSeconds = 3, MagnificationFactor = 10 };
        var detector = new ShakeDetector(settings);
        var renderer = new NativeTestRenderer(originals[32512].Size, sourceDpi);
        using var animator = new CursorAnimator(detector, settings, renderer);
        void PauseAfterSources()
        {
            if (!CursorHelper.IsCached)
            {
                sourcesReady.Set();
                if (!finishBuild.Wait(10000)) throw new Exception("Cold-start test did not release the builder.");
            }
        }
        CursorHelper.CacheChanged += PauseAfterSources;
        var watch = Stopwatch.StartNew();
        Task build = CursorHelper.RequestCaches(10, sourceDpi);
        try
        {
            // Shake while source preparation is still pending. Sources-ready
            // must wake the real renderer, without any full-cache completion.
            Shake(detector, Environment.TickCount64 - 160);
            if (!sourcesReady.Wait(2000)) throw new Exception("Source preparation timed out.");
            WaitUntil(() => renderer.LastScale > 1.2);
            if (build.IsCompleted || CursorHelper.IsCached || !new SystemCursorRenderer().IsReady)
                throw new Exception("Startup test did not exercise the uncached path.");
            Console.WriteLine($"  Cold start: first visible enlargement {watch.Elapsed.TotalMilliseconds:F1} ms, full builder deliberately paused");
            renderer.ChangeDpi(144);
            WaitUntil(() => renderer.LastDpi == 144 && renderer.LastScale > 2);
            renderer.ChangeDpi(192);
            WaitUntil(() => renderer.LastDpi == 192 && renderer.LastScale > 2);
            detector.SetSuppressed(true);
            renderer.ChangeDpi(sourceDpi);
            WaitUntil(() => renderer.LastDpi == sourceDpi && renderer.LastScale == 1);
            foreach (uint id in CursorHelper.SYSTEM_CURSORS)
                if (CursorDescription(CursorHelper.LoadCursor(IntPtr.Zero, (int)id)) != originals[id])
                    throw new Exception($"Cold shrink did not preserve role {id}.");
        }
        finally
        {
            CursorHelper.CacheChanged -= PauseAfterSources;
            finishBuild.Set();
            build.GetAwaiter().GetResult();
        }
        Console.WriteLine("  Cold-cache growth/cancel across 150% and 200% retained exact native baseline");
    }
}
