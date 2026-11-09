using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ShakeToFindCursor;
using static ShakeToFindCursor.MouseHook;

internal static partial class Program
{
    private static int _passed;

    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }

    private static void Run(string[] args)
    {
        if (args.Contains("--render-exclusions")) { RenderExclusionModes(); return; }
        if (args.Contains("--control-styles")) { Check("styled controls preserve dropdown, selection and toggle behavior", VerifyControlStyles); return; }
        if (args.Contains("--path-rule-target"))
        {
            using var form = new System.Windows.Forms.Form { Text = "Path exclusion test", Width = 300, Height = 120 };
            System.Windows.Forms.Application.Run(form);
            return;
        }
        if (args.Contains("--tray-menu")) { VerifyTrayMenu(); return; }
        if (args.Contains("--lower-edge")) { VerifyLowerArrowEdge(); return; }
        if (args.Contains("--alpha-check")) { VerifyNativeAlpha(); return; }
        if (args.Contains("--desktop-shrink-trace")) { TraceDesktopShrink(); return; }
        if (args.Contains("--shrink-frames")) { ProbeShrinkFrames(); return; }
        if (args.Contains("--desktop-cross-shake")) { ProbeRunningAppAcrossScreens(); return; }
        Check("fullscreen detection allows the shell desktop and recognizes a borderless fullscreen app", VerifyDesktopFullscreenDetection);
        Check("decimal editors preview draft settings without saving and reject invalid values", VerifyDraftPreview);
        Check("preview completes, cancels and restores the saved animation configuration", VerifyPreviewAnimation);
        Check("default settings height fits the complete English and Chinese shake page", VerifySettingsLayout);
        Check("path exclusions distinguish live same-name executables and preserve legacy rules", VerifyPathExclusions);
        Check("per-app name-only mode preserves paths, survives saving and restores exact matching", VerifyExclusionModes);
        Check("reset preserves saved and unsaved startup choices; only Apply or Confirm saves them", VerifyStartupPreference);
        Check("styled controls preserve dropdown, selection and toggle behavior", VerifyControlStyles);
        Check("manual show reaches maximum without motion, renews its hold, and respects cancellation", () =>
        {
            foreach (bool proportional in new[] { true, false })
            foreach (double hold in new[] { 0.0, 2.0 })
            foreach (double duration in new[] { 0.5, 1.0, 8.0 })
            {
                var detector = new ShakeDetector(new AppSettings
                    { ScaleWithShakeStrength = proportional, MaxSizeHoldSeconds = hold, CommandLineDurationSeconds = duration });
                detector.Show(1000);
                Near(detector.GetState(1000).Energy, 1);
                Near(detector.GetState(1000).HoldRemainingMs, duration * 1000);
                Near(detector.Tick(1500), 1);
                detector.Show(1800);
                Near(detector.Tick(2200), 1);
                Near(detector.Tick(20000), 0);
                detector.Show(21000);
                detector.SetSuppressed(true);
                Near(detector.GetState(21000).HoldRemainingMs, 0);
                detector.Show(22000);
                Near(detector.Tick(22000), 0);
                detector.SetSuppressed(false);
                detector.Show(23000);
                Near(detector.Tick(23500), 1);
            }
        });
        Check("command-line duration defaults for old settings and survives save with bounded values", () =>
        {
            Near(AppSettings.FromJson("{}").CommandLineDurationSeconds, 1);
            Near(AppSettings.FromJson("""{"CommandLineDurationSeconds":-1}""").CommandLineDurationSeconds, .5);
            Near(AppSettings.FromJson("""{"CommandLineDurationSeconds":120}""").CommandLineDurationSeconds, 30);
            var settings = new AppSettings { CommandLineDurationSeconds = 7.5, MaxSizeHoldSeconds = 2 };
            var loaded = AppSettings.FromJson(JsonSerializer.Serialize(settings));
            Near(loaded.CommandLineDurationSeconds, 7.5);
            Near(loaded.MaxSizeHoldSeconds, 2);
            settings.CommandLineDurationSeconds = double.NaN;
            Near(settings.CommandLineDurationSeconds, 1);
        });
        Check("shake threshold combines logical distance, pointer monitor diagonal and rolling interval", () =>
        {
            static bool Triggers(int minimum, int window, int percent, double diagonal, uint dpi = 96, bool cross = false)
            {
                var detector = new ShakeDetector(new AppSettings { MinimumShakeDistance = minimum,
                    ShakeWindowMs = window, ShakeSensitivityPercent = percent, ScaleWithShakeStrength = false });
                int[] xs = [0, 100, 0, 100, 0];
                for (int i = 0; i < xs.Length; i++)
                    detector.AddSample(new NativePoint((int)(xs[i] * dpi / 96), 0), 1000 + i * 40,
                        dpi, diagonal, cross && i >= 3 ? 2 : 1);
                return detector.Energy > 0;
            }
            if (!Triggers(350, 350, 10, 2000) || Triggers(450, 350, 10, 2000) ||
                Triggers(350, 80, 10, 2000) || Triggers(350, 350, 30, 2000) ||
                !Triggers(350, 350, 30, 1000, 192) || !Triggers(350, 350, 10, 2000, cross: true))
                throw new Exception("Independent thresholds or same-DPI monitor crossing failed.");
        });
        Check("legacy sensitivity migrates once; explicit detection settings round trip", () =>
        {
            var old = AppSettings.FromJson("""{"Sensitivity":4}""");
            if (old.MinimumShakeDistance != 1283 || old.ShakeWindowMs != 350 || old.ShakeSensitivityPercent != 40)
                throw new Exception("Legacy threshold was lost.");
            var current = AppSettings.FromJson("""{"Sensitivity":4,"MinimumShakeDistance":750,"ShakeWindowMs":520,"ShakeSensitivityPercent":24}""");
            string json = JsonSerializer.Serialize(current);
            var roundTrip = AppSettings.FromJson(json);
            if (roundTrip.MinimumShakeDistance != 750 || roundTrip.ShakeWindowMs != 520 ||
                roundTrip.ShakeSensitivityPercent != 24 || json.Contains("\"Sensitivity\""))
                throw new Exception("Explicit detection settings did not round trip.");
        });
        Check("small backtracking cannot turn a single return into a shake", () =>
        {
            foreach (double hold in new[] { 0.0, 1.5 })
            {
                var detector = Create(hold);
                Replay(detector, [0, 700, 699, 0, 1]);
                Near(detector.Energy, 0);
            }
        });

        Check("repeated small jitter and straight travel cannot trigger", () =>
        {
            var jitter = Create();
            var straight = Create();
            for (int i = 0; i < 350; i++)
            {
                jitter.AddTestSample(new NativePoint(i % 2 == 0 ? 0 : 5, 0), 1000 + i);
                straight.AddTestSample(new NativePoint(i * 20, 0), 1000 + i);
                Near(jitter.Energy, 0);
                Near(straight.Energy, 0);
            }
        });

        Check("deliberate shakes work horizontally, vertically and diagonally at different sample rates", () =>
        {
            foreach (int interval in new[] { 1, 2, 8 })
            foreach (var direction in new[] { (1, 0), (0, 1), (1, 1) })
            {
                var detector = Create();
                for (int t = 0; t <= 320; t += interval)
                {
                    int phase = t % 160;
                    int x = (phase <= 80 ? phase : 160 - phase) * 10;
                    detector.AddTestSample(new NativePoint(x * direction.Item1, x * direction.Item2), 1000 + t);
                }
                Near(detector.Energy, 1);
            }
        });

        Check("small individual deltas accumulate into a deliberate shake", () =>
        {
            var detector = new ShakeDetector(new AppSettings { MinimumShakeDistance = 450, ShakeSensitivityPercent = 10, MaxSizeHoldSeconds = 1, ScaleWithShakeStrength = false });
            for (int i = 0; i <= 640; i++)
            {
                int phase = i % 320;
                int x = phase <= 160 ? phase : 320 - phase;
                detector.AddTestSample(new NativePoint(x, 0), 1000 + i / 4);
            }
            Near(detector.Energy, 1);
        });

        Check("zero-hold release is identical while stationary or moving straight", () =>
        {
            foreach (int delta in new[] { -20, -1, 0, 1 })
            {
                var still = Create(0);
                var moving = Create(0);
                Shake(still);
                Shake(moving);
                for (int i = 1; i <= 30; i++)
                {
                    long time = 1160 + i * 20;
                    moving.AddTestSample(new NativePoint(i * delta, 0), time);
                    Near(moving.Energy, still.Tick(time));
                }
            }
        });

        Check("a slow reversal cannot renew a hold using stale shake history", () =>
        {
            var detector = Create(1);
            Shake(detector);
            for (int i = 1; i <= 15; i++)
                detector.AddTestSample(new NativePoint(i, 0), 1160 + i * 20);
            Near(detector.Tick(2300), Math.Exp(-1));
        });

        Check("a fresh deliberate shake replenishes continuous energy", () =>
        {
            var detector = Create(0);
            Shake(detector);
            Near(detector.Tick(1300), Math.Exp(-1));
            Shake(detector, 1400);
            Near(detector.Energy, 1);
        });

        Check("hold timing and renewal remain intact", () =>
        {
            var detector = Create(1);
            Shake(detector);
            Near(detector.Tick(2160), 1);
            Near(detector.Tick(2300), Math.Exp(-1));
            Shake(detector, 2400);
            Near(detector.Tick(3560), 1);
            Near(detector.Tick(3700), Math.Exp(-1));
        });

        Check("suppression clears history and prevents dragging from retriggering", () =>
        {
            var detector = Create();
            Shake(detector);
            detector.SetSuppressed(true);
            Shake(detector, 1200);
            Near(detector.Energy, 0);
            detector.SetSuppressed(false);
            detector.AddTestSample(new NativePoint(800, 0), 1400);
            Near(detector.Energy, 0);
            Shake(detector, 1800);
            Near(detector.Energy, 1);
        });

        Check("mode and hold duration are independent in all four combinations", () =>
        {
            foreach (bool proportional in new[] { true, false })
            foreach (double hold in new[] { 0.0, 1.0 })
            {
                var detector = new ShakeDetector(new AppSettings
                {
                    MinimumShakeDistance = 450, ShakeSensitivityPercent = 10, ShrinkSpeed = 1, ScaleWithShakeStrength = proportional, MaxSizeHoldSeconds = hold
                });
                Replay(detector, [0, 150, 0, 150, 0]);
                double expected = proportional ? 150.0 / 540 : 1;
                Near(detector.Energy, expected);
                Near(detector.Tick(1300), hold == 0 ? expected * Math.Exp(-1) : expected);
                if (hold > 0) Near(detector.Tick(2300), expected * Math.Exp(-1));
            }
        });

        Check("legacy settings preserve their mode, explicit mode survives save and reload", () =>
        {
            if (AppSettings.FromJson("""{"MaxSizeHoldSeconds":2}""").ScaleWithShakeStrength) throw new Exception("Legacy hold changed mode.");
            if (!AppSettings.FromJson("{\"MaxSizeHoldSeconds\":0}").ScaleWithShakeStrength)
                throw new Exception("Legacy continuous mode was lost.");
            foreach (bool mode in new[] { true, false })
            {
                var settings = new AppSettings { ScaleWithShakeStrength = mode, MaxSizeHoldSeconds = 1.5 };
                var loaded = AppSettings.FromJson(JsonSerializer.Serialize(settings));
                if (loaded.ScaleWithShakeStrength != mode) throw new Exception("Mode did not round trip.");
                Near(loaded.MaxSizeHoldSeconds, 1.5);
            }
        });

        Check("equivalent logical shakes produce the same energy across DPI scales and negative origins", () =>
        {
            foreach (uint dpi in new[] { 96u, 120u, 144u, 192u, 288u })
            {
                var detector = new ShakeDetector(new AppSettings { MinimumShakeDistance = 450, ShakeSensitivityPercent = 10, MaxSizeHoldSeconds = 1 });
                int[] xs = [0, 160, 0, 160, 0];
                for (int i = 0; i < xs.Length; i++)
                    detector.AddTestSample(new NativePoint(-4000 + (int)(xs[i] * dpi / 96), -1000), 1000 + i * 40, dpi);
                Near(detector.Energy, 190.0 / 540);
            }
        });

        Check("crossing DPI boundaries does not create a false reversal or cancel a hold", () =>
        {
            var detector = Create(1);
            Shake(detector);
            detector.AddTestSample(new NativePoint(-4000, -2000), 1200, 192);
            Near(detector.Energy, 1);
            Near(detector.Tick(2300), Math.Exp(-1));
            detector.SetSuppressed(true);
            detector.SetSuppressed(false);
            for (int i = 0; i < 10; i++)
                detector.AddTestSample(new NativePoint(i % 2 == 0 ? -4000 : 4000, 0), 2400 + i * 40, i % 2 == 0 ? 96u : 192u);
            Near(detector.Energy, 0);
        });

        Check("repeated turns across a seam trigger, straight crossings and desktop warps do not", () =>
        {
            foreach (var pair in new[] { (192u, 192u), (144u, 192u), (192u, 144u) })
            foreach (bool vertical in new[] { false, true })
            {
                var detector = new ShakeDetector(new AppSettings { MinimumShakeDistance = 1100,
                    ShakeSensitivityPercent = 10, ShakeWindowMs = 350, MaxSizeHoldSeconds = 2, ScaleWithShakeStrength = false });
                long t = 1000;
                void Feed(int position)
                {
                    bool left = position < 0;
                    detector.AddSample(vertical ? new NativePoint(-500, position) : new NativePoint(position, -500),
                        t, left ? pair.Item1 : pair.Item2, left ? 1600 : 2200, left ? 1 : 2);
                    t += 8;
                }
                for (int x = -500; x <= 500; x += 100) Feed(x);
                Near(detector.Energy, 0);
                for (int x = 400; x >= -500; x -= 100) Feed(x);
                Near(detector.Energy, 0);
                for (int x = -400; x <= 500; x += 100) Feed(x);
                for (int x = 400; x >= -500; x -= 100) Feed(x);
                Near(detector.Energy, 1);
                detector.SetSuppressed(true);
                detector.SetSuppressed(false);
                for (int i = 0; i < 10; i++) Feed(i % 2 == 0 ? -5000 : 5000);
                Near(detector.Energy, 0);
            }
        });

        Check("native cursor sizing preserves custom dimensions and scales with monitor DPI", () =>
        {
            var native = new System.Drawing.Size(96, 80);
            if (CursorHelper.ScaleNativeSize(native, 192, 192) != native ||
                CursorHelper.ScaleNativeSize(native, 192, 96) != new System.Drawing.Size(48, 40) ||
                CursorHelper.ScaleNativeSize(native, 192, 144) != new System.Drawing.Size(72, 60))
                throw new Exception("Native dimensions were replaced by nominal cursor metrics.");
        });

        Check("monitor scaling agrees with effective DPI under the executable manifest", () =>
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, rect, _) =>
            {
                var bounds = Marshal.PtrToStructure<MonitorRect>(rect);
                if (GetDpiForMonitor(monitor, 0, out uint actualDpi, out _) != 0)
                    throw new Exception("Cannot read effective DPI.");
                uint selected = MonitorDpi.AtPoint(new NativePoint((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2));
                if (selected != actualDpi)
                    throw new Exception($"Monitor selected {selected} DPI instead of {actualDpi}; check the Windows compatibility manifest.");
                return true;
            }, IntPtr.Zero);
        });

        Check("8 kHz input has bounded storage and evaluation cost", () =>
        {
            var detector = Create();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 40000; i++)
            {
                int phase = i % 1280;
                int x = phase <= 640 ? phase : 1280 - phase;
                detector.AddTestSample(new NativePoint(x * 2, 0), 1000 + i / 8);
                if (detector.BufferedSegmentCount > 89) throw new Exception("History exceeded its bound.");
            }
            if (detector.EvaluationCount > 1250) throw new Exception("Input was not rate bounded.");
            Console.WriteLine($"  40,000 events: {detector.EvaluationCount} evaluations, {watch.Elapsed.TotalMilliseconds:F1} ms total");
        });

        Check("animator sleeps during a hold, wakes on cancellation, and stops before dispose returns", () =>
        {
            var settings = new AppSettings { MinimumShakeDistance = 1283, ScaleWithShakeStrength = false,
                MaxSizeHoldSeconds = 3, MagnificationFactor = 5 };
            var detector = new ShakeDetector(settings);
            var renderer = new FakeRenderer();
            var animator = new CursorAnimator(detector, settings, renderer);
            try
            {
                Shake(detector, Environment.TickCount64 - 160);
                WaitUntil(() => renderer.LastScale == 5);
                Thread.Sleep(40);
                long before = animator.IterationCount;
                Thread.Sleep(200);
                if (animator.IterationCount - before > 1) throw new Exception("Holding still polls.");
                renderer.NotifyChanged();
                WaitUntil(() => animator.IterationCount > before);
                detector.SetSuppressed(true);
                WaitUntil(() => renderer.LastScale == 1);
                if (renderer.Restores != 0) throw new Exception("Cancellation reloaded the system cursor theme.");
                detector.SetSuppressed(false);
                Shake(detector, Environment.TickCount64 - 160);
                WaitUntil(() => renderer.LastScale > 1.1);
            }
            finally { animator.Dispose(); }
            int applied = renderer.Applies;
            Thread.Sleep(40);
            if (renderer.Applies != applied || renderer.LastScale != 1 || renderer.Restores != 1)
                throw new Exception("Animation continued after disposal.");
        });

        Check("normal shrink ends on a visible base frame without reloading the theme", () =>
        {
            var settings = new AppSettings { MinimumShakeDistance = 1283, ScaleWithShakeStrength = false,
                MaxSizeHoldSeconds = 0.7, MagnificationFactor = 2 };
            var detector = new ShakeDetector(settings);
            var renderer = new FakeRenderer();
            var animator = new CursorAnimator(detector, settings, renderer);
            try
            {
                Shake(detector, Environment.TickCount64 - 160);
                WaitUntil(() => renderer.LastScale == 2);
                WaitUntil(() => renderer.LastScale == 1);
                if (renderer.Restores != 0) throw new Exception("Shrink completion reloaded the theme.");
                int applied = renderer.Applies;
                Thread.Sleep(50);
                if (renderer.Applies != applied) throw new Exception("Idle cursor still animates.");
                renderer.NotifyChanged();
                WaitUntil(() => renderer.Applies > applied);
                Near(renderer.LastScale, 1);
                if (renderer.Restores != 0) throw new Exception("Idle cache refresh reloaded the theme.");
            }
            finally { animator.Dispose(); }
            if (renderer.Restores != 1) throw new Exception("Exit while idle did not restore the native theme once.");
        });

        Check("cache readiness wakes a pending enlargement", () =>
        {
            var settings = new AppSettings { MinimumShakeDistance = 1283, ScaleWithShakeStrength = false, MaxSizeHoldSeconds = 3 };
            var detector = new ShakeDetector(settings);
            var renderer = new FakeRenderer { Ready = false };
            using var animator = new CursorAnimator(detector, settings, renderer);
            Shake(detector, Environment.TickCount64 - 160);
            Thread.Sleep(40);
            if (renderer.Applies != 0) throw new Exception("Applied an unready cache.");
            renderer.Ready = true;
            renderer.NotifyChanged();
            WaitUntil(() => renderer.LastScale > 1.1);
        });

        Check("changing hold duration applies without changing proportional mode", () =>
        {
            var settings = new AppSettings { MinimumShakeDistance = 450, ShakeSensitivityPercent = 10, ShrinkSpeed = 1, MaxSizeHoldSeconds = 2 };
            var detector = new ShakeDetector(settings);
            Replay(detector, [0, 150, 0, 150, 0]);
            double peak = detector.Energy;
            detector.Tick(1600);
            settings.MaxSizeHoldSeconds = 0.5;
            detector.UpdateSettings(settings);
            Near(detector.Tick(1800), peak * Math.Exp(-1));
            settings.MaxSizeHoldSeconds = 10;
            detector.UpdateSettings(settings);
            Near(detector.Tick(1940), peak * Math.Exp(-2));
        });

        // Exercise the real hook button routing and App handler without installing
        // a global hook, starting the application, or changing system cursors.
        var app = new App();
        var handler = (EventHandler)typeof(App)
            .GetMethod("OnMouseButtonStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(EventHandler), app);
        MouseHook.ButtonStateChanged += handler;
        try
        {
            Check("all five buttons cancel the hold without any mouse movement", () =>
            {
                foreach (var button in new[] { (0x201, 0x202, 0u), (0x204, 0x205, 0u),
                    (0x207, 0x208, 0u), (0x20B, 0x20C, 1u), (0x20B, 0x20C, 2u) })
                {
                    var detector = Create();
                    typeof(App).GetProperty(nameof(App.Detector))!.SetValue(null, detector);
                    Shake(detector);
                    SendButton(button.Item1, button.Item3);
                    Near(detector.Energy, 0);
                    SendButton(button.Item2, button.Item3);
                    Near(detector.Tick(1200), 0);
                    if (MouseHook.IsButtonDown) throw new Exception("Button remained held after release.");
                }
            });

            Check("releasing one of two buttons keeps dragging suppressed", () =>
            {
                var detector = Create();
                typeof(App).GetProperty(nameof(App.Detector))!.SetValue(null, detector);
                Shake(detector);
                SendButton(0x201);
                SendButton(0x204);
                SendButton(0x202);
                if (!MouseHook.IsButtonDown) throw new Exception("Other button was lost.");
                Shake(detector, 1200);
                Near(detector.Energy, 0);
                SendButton(0x205);
            });
        }
        finally
        {
            MouseHook.ButtonStateChanged -= handler;
        }
        VerifyVectorArtwork();
        Check("native cursor preserves translucent colors on black and white", VerifyNativeAlpha);
        Check("10x release does not repay a delayed frame in one large shrink step", () =>
        {
            var settings = new AppSettings { MinimumShakeDistance = 450, ScaleWithShakeStrength = false,
                MaxSizeHoldSeconds = 0.65, ShrinkSpeed = 1, MagnificationFactor = 10 };
            var detector = new ShakeDetector(settings);
            double last = 1, largest = 0, largestTailStep = 0;
            int handoffUpdates = 0;
            bool stalled = false;
            var renderer = new FakeRenderer { OnFrame = scale =>
            {
                if (scale < last)
                {
                    largest = Math.Max(largest, 1 - scale / last);
                    if (last > VectorCursorArtwork.ArrowRest && scale < VectorCursorArtwork.ArrowTail)
                    {
                        handoffUpdates++;
                        largestTailStep = Math.Max(largestTailStep,
                            Math.Min(last, VectorCursorArtwork.ArrowTail) - Math.Max(scale, VectorCursorArtwork.ArrowRest));
                    }
                    if (!stalled) { stalled = true; Thread.Sleep(65); }
                }
                last = scale;
            }};
            using (var animator = new CursorAnimator(detector, settings, renderer))
            {
                Shake(detector, Environment.TickCount64-160);
                WaitUntil(() => renderer.LastScale == 10);
                WaitUntil(() => renderer.LastScale < 10); // do not charge the configured hold to release
                WaitUntil(() => renderer.LastScale == 1, 2500);
            }
            if (!stalled || largest > 0.04) throw new Exception($"Delayed shrink dropped {largest:P1}.");
            if (handoffUpdates < 30 || largestTailStep > 0.0027)
                throw new Exception($"Handoff skipped steps: {handoffUpdates} updates, largest {largestTailStep:F4}x.");
        });
        Check("shrink speed scales the visible release and defaults to 2.5x", () =>
        {
            Near(AppSettings.FromJson("{}").ShrinkSpeed, 2.5);
            Near(AppSettings.FromJson("""{"ShrinkSpeed":0}""").ShrinkSpeed, 0.5);
            Near(AppSettings.FromJson("""{"ShrinkSpeed":5}""").ShrinkSpeed, 3);
            double Measure(double speed)
            {
                var settings = new AppSettings { MaxSizeHoldSeconds = .6, MagnificationFactor = 3,
                    ScaleWithShakeStrength = false, ShrinkSpeed = speed };
                Near(AppSettings.FromJson(JsonSerializer.Serialize(settings)).ShrinkSpeed, speed);
                var detector = new ShakeDetector(settings);
                var renderer = new FakeRenderer();
                using var animator = new CursorAnimator(detector, settings, renderer);
                Shake(detector, Environment.TickCount64 - 160);
                WaitUntil(() => renderer.LastScale == 3);
                WaitUntil(() => renderer.LastScale < 2.9);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                WaitUntil(() => renderer.LastScale == 1, 7000);
                return elapsed.Elapsed.TotalMilliseconds;
            }
            double slow = Measure(.5), fast = Measure(3);
            Console.WriteLine($"  Visible shrink at 0.5x: {slow:F0} ms; at 3x: {fast:F0} ms");
            if (slow < 2 * fast) throw new Exception("Shrink speed barely changed the visible animation.");
        });
        Check("lower arrow edge follows a single native outline at both DPIs", VerifyLowerArrowEdge);
        Check("shrink meets native pixels and hotspots on light and dark backgrounds at 150% and 200%", () => ProbeShrinkFrames(false));
        Check("English and Chinese catalogs match; language switches and saves immediately without committing drafts", () =>
        {
            var ui = ShakeToFindCursor.Localization.UiText.Current;
            var resources = ShakeToFindCursor.Localization.UiText.Resources;
            var en = System.Globalization.CultureInfo.GetCultureInfo("en");
            var zh = System.Globalization.CultureInfo.GetCultureInfo("zh-Hans");
            var neutral = resources.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, false)!;
            var chinese = resources.GetResourceSet(zh, true, false) ?? throw new Exception("Missing Chinese satellite.");
            var keys = neutral.Cast<System.Collections.DictionaryEntry>().Select(x => (string)x.Key).ToHashSet();
            if (!keys.SetEquals(chinese.Cast<System.Collections.DictionaryEntry>().Select(x => (string)x.Key)))
                throw new Exception("Language catalogs have different keys.");
            foreach (var key in keys)
                if (System.Text.CompositeFormat.Parse(neutral.GetString(key)!).MinimumArgumentCount !=
                    System.Text.CompositeFormat.Parse(chinese.GetString(key)!).MinimumArgumentCount)
                    throw new Exception($"Translation placeholder mismatch: {key}");
            if (!ShakeToFindCursor.Localization.UiText.Resolve("system", new("zh-CN")).Equals(zh) ||
                !ShakeToFindCursor.Localization.UiText.Resolve("system", new("fr-FR")).Equals(en) ||
                !ShakeToFindCursor.Localization.UiText.Resolve("unknown", new("zh-CN")).Equals(en))
                throw new Exception("System language fallback failed.");
            ui.Apply("en");
            try
            {
                AppSettings saved = new() { Language = "en", MinimumShakeDistance = 800 };
                int writes = 0;
                var window = new SettingsWindow(saved, draft => { saved = draft; writes++; ui.Apply(saved.Language); });
                window.SliderMinimumShakeDistance.Value = 1200;
                window.InputShrinkSpeed.Text = "invalid";
                window.LanguagePicker.SelectedValue = "zh-Hans";
                if (saved.Language != "zh-Hans" || !Equals(window.BtnApply.Content, chinese.GetString("Apply")) ||
                    window.Title != chinese.GetString("AppName") || writes != 1)
                    throw new Exception("Language did not switch and save on selection.");
                if (saved.MinimumShakeDistance != 800 || saved.ShrinkSpeed != 2.5 ||
                    window.SliderMinimumShakeDistance.Value != 1200 || window.InputShrinkSpeed.Text != "invalid" ||
                    window.BtnApply.IsEnabled || window.BtnConfirm.IsEnabled)
                    throw new Exception("Changing language saved or discarded another draft.");
                if (!window.ErrorShrinkSpeed.Text.Contains("数值"))
                    throw new Exception("Existing validation error did not switch language.");
                var picker = new ProcessPickerWindow();
                if (picker.Title != chinese.GetString("PickerTitle")) throw new Exception("Picker did not use saved language.");
                picker.Close();
                window.LanguagePicker.SelectedValue = "en";
                if (!Equals(window.BtnConfirm.Content, neutral.GetString("Confirm")) ||
                    !window.ErrorShrinkSpeed.Text.StartsWith("Enter a number"))
                    throw new Exception("English controls and validation did not switch immediately.");
                window.InputShrinkSpeed.Text = "2.5";
                if (!window.BtnApply.IsEnabled) throw new Exception("An unrelated valid draft was lost.");
                window.Close();
                if (saved.Language != "en" || ui.Culture != en || writes != 2 || saved.MinimumShakeDistance != 800)
                    throw new Exception("Closing reverted the saved language or committed other edits.");
                var loaded = AppSettings.FromJson(JsonSerializer.Serialize(saved));
                if (loaded.Language != "en" || AppSettings.FromJson("{}").Language != "system")
                    throw new Exception("Language preference did not round trip.");
            }
            finally { ui.Apply("system"); }
        });
        Check("settings edit, reset and close never save; only Apply and Confirm commit", () =>
        {
            var saved = new AppSettings { Language = "zh-Hans", MinimumShakeDistance = 800, ExcludedProcesses = ["example"], ShrinkSpeed = 1.5, CommandLineDurationSeconds = 7.5 };
            int writes = 0;
            bool closed = false;
            var window = new SettingsWindow(saved, draft => { saved = draft; writes++; });
            window.Closed += (_, _) => closed = true;
            void Click(SettingsWindow target, string name) =>
                ((System.Windows.Controls.Button)target.FindName(name)).RaiseEvent(
                    new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Near(window.SliderShrinkSpeed.Value, 1.5);
            Near(window.SliderCommandLineDuration.Value, 7.5);
            window.SliderCommandLineDuration.Value = .5;
            window.SliderShrinkSpeed.Value = .5;
            if (!window.BtnApply.IsEnabled || writes != 0) throw new Exception("Speed edit should remain a draft.");
            window.SliderMinimumShakeDistance.Value = 3;
            Click(window, "BtnReset");
            Near(window.SliderShrinkSpeed.Value, 2.5);
            Near(window.SliderCommandLineDuration.Value, 1);
            Near(saved.CommandLineDurationSeconds, 7.5);
            if (!Equals(window.LanguagePicker.SelectedValue, "zh-Hans") || saved.Language != "zh-Hans")
                throw new Exception("Reset must preserve the user's readable language.");
            Near(saved.ShrinkSpeed, 1.5);
            if (writes != 0 || saved.MinimumShakeDistance != 800 || saved.ExcludedProcesses.Count != 1)
                throw new Exception("Editing or resetting changed saved settings.");
            Click(window, "BtnApply");
            if (closed || writes != 1 || saved.MinimumShakeDistance != new AppSettings().MinimumShakeDistance || saved.ExcludedProcesses.Count != 0)
                throw new Exception("Apply did not commit the draft and keep the window open.");
            Near(saved.ShrinkSpeed, 2.5);
            Near(saved.CommandLineDurationSeconds, 1);
            window.SliderCommandLineDuration.Value = 10;
            window.SliderShrinkSpeed.Value = .7;
            window.SliderMinimumShakeDistance.Value = 9;
            window.Close();
            if (!closed || writes != 1 || saved.MinimumShakeDistance == 9) throw new Exception("Close committed an edit.");
            closed = false;
            var reopened = new SettingsWindow(saved, draft => { saved = draft; writes++; });
            reopened.Closed += (_, _) => closed = true;
            if (reopened.SliderMinimumShakeDistance.Value != saved.MinimumShakeDistance) throw new Exception("Reopen loaded a discarded edit.");
            Near(reopened.SliderShrinkSpeed.Value, 2.5);
            Near(reopened.SliderCommandLineDuration.Value, 1);
            reopened.SliderCommandLineDuration.Value = 4.5;
            reopened.SliderShrinkSpeed.Value = 3;
            reopened.SliderMinimumShakeDistance.Value = 7;
            reopened.SliderShakeWindowMs.Value = 510;
            reopened.SliderShakeSensitivityPercent.Value = 34;
            Click(reopened, "BtnConfirm");
            Near(saved.ShrinkSpeed, 3);
            Near(saved.CommandLineDurationSeconds, 4.5);
            if (!closed || writes != 2 || saved.MinimumShakeDistance != 7 || saved.ShakeWindowMs != 510 ||
                saved.ShakeSensitivityPercent != 34) throw new Exception("Confirm did not commit and close.");
        });
        Check("numeric drafts, target exclusions and save buttons track reversible edits", () =>
        {
            var saved = new AppSettings();
            string? target = @"C:\Windows\System32\notepad.exe";
            int writes = 0;
            var w = new SettingsWindow(saved, draft => { saved = draft; writes++; }, () => target);
            void Click(System.Windows.Controls.Button b) => b.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (w.BtnApply.IsEnabled || !w.BtnConfirm.IsEnabled || w.BtnRemoveApp.IsEnabled ||
                w.EmptyExclusions.Visibility != System.Windows.Visibility.Visible || !w.CurrentAppLabel.Text.Contains("notepad"))
                throw new Exception("Incorrect clean/empty/target state.");
            w.InputDistance.Text = "1283"; w.InputInterval.Text = "351"; w.InputFactor.Text = "39";
            if (w.SliderMinimumShakeDistance.Value != 1283 || w.SliderShakeWindowMs.Value != 351 ||
                w.SliderShakeSensitivityPercent.Value != 39 || !w.BtnApply.IsEnabled || writes != 0)
                throw new Exception("Exact numeric draft failed.");
            w.InputInterval.Text = "9999";
            if (w.BtnApply.IsEnabled || w.BtnConfirm.IsEnabled || w.ErrorInterval.Visibility != System.Windows.Visibility.Visible)
                throw new Exception("Invalid input could be saved.");
            Click(w.BtnReset);
            if (w.BtnApply.IsEnabled || !w.BtnConfirm.IsEnabled || writes != 0) throw new Exception("Reset did not restore clean state.");
            Click(w.BtnAddCurrentApp);
            if (w.BtnAddCurrentApp.IsEnabled || !w.BtnApply.IsEnabled || w.ListExcludedApps.Items.Count != 1)
                throw new Exception("Target not added exactly once.");
            w.ListExcludedApps.SelectedIndex = 0;
            if (!w.BtnRemoveApp.IsEnabled) throw new Exception("Cannot remove selection.");
            Click(w.BtnRemoveApp);
            if (w.BtnApply.IsEnabled || w.BtnRemoveApp.IsEnabled) throw new Exception("Reverting list edit stayed dirty.");
            target = null; w.RefreshExternalTarget();
            if (w.BtnAddCurrentApp.IsEnabled) throw new Exception("Empty target enabled.");
            w.SliderMinimumShakeDistance.Value = 1200;
            if (w.InputDistance.Text != "1200") throw new Exception("Slider did not sync to editor.");
            Click(w.BtnApply);
            if (writes != 1 || w.BtnApply.IsEnabled || saved.MinimumShakeDistance != 1200)
                throw new Exception("Apply did not establish a clean baseline.");
            Click(w.BtnConfirm);
            if (writes != 1) throw new Exception("Clean Confirm wrote settings.");
        });
        if (args.Contains("--render-settings"))
        {
            var window = new SettingsWindow();
            var content = (System.Windows.FrameworkElement)window.Content;
            int width = (int)window.Width, height = (int)window.Height;
            content.Measure(new System.Windows.Size(width, height));
            content.Arrange(new System.Windows.Rect(0, 0, width, height));
            content.UpdateLayout();
            content.UpdateLayout();
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(content);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output = System.IO.File.Create("tests/BehaviorTests/obj/settings-preview.png");
            encoder.Save(output);
        }
        if (args.Contains("--native-cache"))
        {
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
            Check("native cache preserves the desktop baseline after enlargement and rejects stale DPI frames", () =>
            {
                CursorHelper.RestoreThemeCursors();
                uint dpi = MonitorDpi.SystemDpi;
                var originals = CursorHelper.SYSTEM_CURSORS.ToDictionary(id => id,
                    id => CursorDescription(CursorHelper.LoadCursor(IntPtr.Zero, (int)id)));
                var originalPixels = ArrowPixels();
                try
                {
                    VerifyColdStart(originals, dpi);
                    CursorHelper.RequestCaches(2, 96);
                    CursorHelper.RequestCaches(2, 192);
                    CursorHelper.RequestCaches(2, 144).GetAwaiter().GetResult();
                    if (!CursorHelper.IsCached || CursorHelper.CachedDpi != 144) throw new Exception("Stale cache published.");
                    CursorHelper.ApplyScaleForDpi(1, 144);
                    AssertDesktopSize(originals[32512].Size, dpi, 144, 1);
                    long generation = CursorHelper.CacheGeneration;
                    CursorHelper.RequestCaches(2, 144, refreshSources: true).GetAwaiter().GetResult();
                    if (CursorHelper.CacheGeneration <= generation) throw new Exception("Theme did not refresh.");
                    uint staleDpi = dpi == 144 ? 96u : 144u;
                    CursorHelper.RequestCaches(2, staleDpi).GetAwaiter().GetResult();
                    CursorHelper.ApplyScaleForDpi(1, dpi);
                    if (CursorDescription(CursorHelper.LoadCursor(IntPtr.Zero, 32512)) != originals[32512])
                        throw new Exception("A stale DPI baseline was installed.");
                    CursorHelper.RequestCaches(2, dpi).GetAwaiter().GetResult();
                    CursorHelper.ApplyScaleForDpi(2, dpi);
                    var enlarged = CursorDescription(CursorHelper.LoadCursor(IntPtr.Zero, 32512));
                    if (enlarged.Size.Width <= originals[32512].Size.Width)
                        throw new Exception("Native enlargement did not reach the desktop.");
                    CursorHelper.ApplyScaleForDpi(1, dpi);
                    foreach (uint id in CursorHelper.SYSTEM_CURSORS)
                        if (CursorDescription(CursorHelper.LoadCursor(IntPtr.Zero, (int)id)) != originals[id])
                            throw new Exception($"Cursor {id} did not return to its native size and hotspot.");
                    if (ArrowPixels() != originalPixels)
                        throw new Exception("Arrow artwork changed after shrinking despite matching bitmap dimensions.");
                    Console.WriteLine($"  Native arrow pixel hash and visible bounds preserved: {originalPixels.Bounds}; host={Environment.ProcessPath}");
                    Console.WriteLine($"  System DPI: {dpi}, pointer DPI: {MonitorDpi.AtPointer()}; arrow: {originals[32512].Size} -> {enlarged.Size} -> {CursorHelper.GetCursorSize(CursorHelper.LoadCursor(IntPtr.Zero, 32512))}");
                    VerifyVisibleRolesAndBudget(dpi);
                    VerifyDpiTransitions(originals, dpi);
                    if (args.Contains("--native-monitors")) VerifyPhysicalMonitors(originals[32512].Size, dpi);
                }
                finally { CursorHelper.Shutdown(); CursorHelper.RestoreThemeCursors(); }
            });
        }
        Console.WriteLine($"All {_passed} behavior checks passed.");
    }

    private static void AddTestSample(this ShakeDetector detector, NativePoint point, long time, uint dpi = 96)
        => detector.AddSample(point, time, dpi, 2000, 1);

    private static ShakeDetector Create(double hold = 1.5) => new(new AppSettings
    {
        MinimumShakeDistance = 1283,
        ShrinkSpeed = 1,
        ScaleWithShakeStrength = hold == 0,
        MaxSizeHoldSeconds = hold
    });

    private static (System.Drawing.Size Size, int HotX, int HotY) CursorDescription(IntPtr handle)
    {
        if (!CursorHelper.GetIconInfo(handle, out var info)) throw new Exception("Native cursor missing.");
        try { return (CursorHelper.GetCursorSize(handle), info.xHotspot, info.yHotspot); }
        finally { CursorHelper.DeleteObject(info.hbmColor); CursorHelper.DeleteObject(info.hbmMask); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DibInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Color;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref DibInfo info, uint usage);

    private static (string Hash, System.Drawing.Rectangle Bounds) ArrowPixels()
    {
        IntPtr arrow = CursorHelper.LoadCursor(IntPtr.Zero, 32512);
        var size = CursorHelper.GetCursorSize(arrow);
        if (!CursorHelper.GetIconInfo(arrow, out var icon)) throw new Exception("Arrow bitmap missing.");
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            var info = new DibInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
            byte[] pixels = new byte[size.Width * size.Height * 4];
            if (GetDIBits(dc, icon.hbmColor, 0, (uint)size.Height, pixels, ref info, 0) != size.Height)
                throw new Exception("Cannot read native arrow pixels.");
            int left = size.Width, top = size.Height, right = -1, bottom = -1;
            for (int y = 0; y < size.Height; y++)
            for (int x = 0; x < size.Width; x++)
                if (pixels[(y * size.Width + x) * 4 + 3] != 0)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
            var bounds = right < left ? System.Drawing.Rectangle.Empty
                : System.Drawing.Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            return (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)), bounds);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
            CursorHelper.DeleteObject(icon.hbmColor);
            CursorHelper.DeleteObject(icon.hbmMask);
        }
    }

    private static void VerifyDpiTransitions(
        Dictionary<uint, (System.Drawing.Size Size, int HotX, int HotY)> originals, uint sourceDpi)
    {
        var gate = typeof(CursorHelper).GetField("_sync", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        double slowest = 0;
        int transitions = 0;
        foreach (uint from in new[] { 96u, 144u, 192u })
        foreach (uint to in new[] { 96u, 144u, 192u })
        {
            if (from == to) continue;
            CursorHelper.RequestCaches(2, from).GetAwaiter().GetResult();
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                if (GetGuiResources(process.Handle, 0) > 4000)
                    throw new Exception($"One cache leaves insufficient room for a second DPI cache: {GuiResources()}");
            // Deliberately keep the builder waiting: correctness must not depend
            // on a full destination cache finishing before the animation does.
            lock (gate)
            {
                foreach (double scale in new[] { 1.0, 1.3, 2.0, 1.6, 1.05, 1.0 })
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    CursorHelper.ApplyScaleForDpi(scale, to);
                    slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds);
                    AssertDesktopSize(originals[32512].Size, sourceDpi, to, scale);
                    transitions++;
                }
                // Return to the cached screen before the pending build completes.
                CursorHelper.ApplyScaleForDpi(1, from);
                AssertDesktopSize(originals[32512].Size, sourceDpi, from, 1);
            }
        }
        Console.WriteLine($"  Mixed-DPI pending-cache matrix: {transitions} frames, 100%/150%/200% both directions; slowest handoff {slowest:F1} ms");
        Console.WriteLine($"  Cache resources: {GuiResources()}");

        CursorHelper.RequestCaches(2, sourceDpi).GetAwaiter().GetResult();
        var settings = new AppSettings { MinimumShakeDistance = 1283, ScaleWithShakeStrength = false,
            MaxSizeHoldSeconds = 1.5, MagnificationFactor = 2 };
        var detector = new ShakeDetector(settings);
        var renderer = new NativeTestRenderer(originals[32512].Size, sourceDpi);
        using var animator = new CursorAnimator(detector, settings, renderer);
        Shake(detector, Environment.TickCount64 - 160);
        WaitUntil(() => renderer.LastScale > 1 && renderer.LastScale < 1.9);
        renderer.ChangeDpi(96);
        WaitUntil(() => renderer.LastDpi == 96 && renderer.LastScale == 2);
        renderer.ChangeDpi(144);
        WaitUntil(() => renderer.LastDpi == 144);
        Near(detector.Energy, 1);
        WaitUntil(() => renderer.LastScale < 1.9 && renderer.LastScale > 1);
        renderer.ChangeDpi(192);
        WaitUntil(() => renderer.LastDpi == 192);
        renderer.ChangeDpi(96);
        WaitUntil(() => renderer.LastDpi == 96 && renderer.LastScale == 1);
        renderer.ChangeDpi(192);
        WaitUntil(() => renderer.LastDpi == 192 && renderer.LastScale == 1);
        Console.WriteLine("  Real animator + Win32 renderer: crossed during growth, hold, shrink, and idle; correct final baseline");
    }

    private static void AssertDesktopSize(System.Drawing.Size original, uint sourceDpi, uint dpi, double scale)
    {
        var baseline = CursorHelper.ScaleNativeSize(original, sourceDpi, dpi);
        var actual = CursorHelper.GetCursorSize(CursorHelper.LoadCursor(IntPtr.Zero, 32512));
        double tolerance = scale == 1 ? 0 : Math.Max(2, baseline.Width * scale * 0.03);
        // The stock arrow fits on its destination monitor's native canvas
        // during the handoff. Its visible pixels are tested in ShrinkFrames.
        if (actual == baseline && scale <= VectorCursorArtwork.ArrowTail + .01) return;
        if (Math.Abs(actual.Width - baseline.Width * scale) > tolerance ||
            Math.Abs(actual.Height - baseline.Height * scale) > tolerance)
        {
            throw new Exception($"DPI {dpi}, scale {scale:F3}: expected baseline {baseline}, got {actual}. " +
                $"Cache DPI {CursorHelper.CachedDpi}, usage {CursorHelper.Usage}, {GuiResources()}");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct MonitorRect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);

    private static void VerifyPhysicalMonitors(System.Drawing.Size original, uint sourceDpi)
    {
        if (!GetPhysicalCursorPos(out var saved)) throw new Exception("Cannot save pointer position.");
        IntPtr oldContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var monitors = new List<(NativePoint Center, uint Dpi)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, rect, _) =>
            {
                var bounds = Marshal.PtrToStructure<MonitorRect>(rect);
                if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) != 0) throw new Exception("Cannot read monitor scaling.");
                monitors.Add((new NativePoint((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2),
                    dpi));
                return true;
            }, IntPtr.Zero);
            if (monitors.Count < 2) throw new Exception("Physical crossing requires at least two monitors.");
            // Capture shared cursors while the pointer is away from the primary
            // screen: their native dimensions still belong to the system DPI.
            var secondary = monitors[1];
            SetPhysicalCursorPos(secondary.Center.X, secondary.Center.Y);
            CursorHelper.RequestCaches(2, secondary.Dpi, refreshSources: true).GetAwaiter().GetResult();
            var settings = new AppSettings { MinimumShakeDistance = 1283, ScaleWithShakeStrength = false,
                MaxSizeHoldSeconds = 1.5, MagnificationFactor = 2 };
            var detector = new ShakeDetector(settings);
            var renderer = new NativeTestRenderer(original, sourceDpi, physicalPointer: true);
            using var animator = new CursorAnimator(detector, settings, renderer);
            void Move(int index)
            {
                var target = monitors[index % monitors.Count];
                if (!SetPhysicalCursorPos(target.Center.X, target.Center.Y)) throw new Exception("Cannot move pointer.");
                if (MonitorDpi.AtPointer() != target.Dpi) throw new Exception("Physical pointer selected the wrong monitor DPI.");
                renderer.ChangeDpi(target.Dpi); // wake the renderer as the app's hook does
            }
            foreach (int start in new[] { 0, 1 })
            {
                Move(start);
                Shake(detector, Environment.TickCount64 - 160);
                WaitUntil(() => renderer.LastScale > 1 && renderer.LastScale < 1.9);
                Move(1 - start);
                WaitUntil(() => renderer.LastScale == 2);
                Move(start);
                WaitUntil(() => renderer.LastScale < 1.9 && renderer.LastScale > 1);
                Move(1 - start);
                WaitUntil(() => renderer.LastScale == 1);
                foreach (int index in Enumerable.Range(0, monitors.Count))
                {
                    Move(index);
                    WaitUntil(() => renderer.LastDpi == monitors[index].Dpi && renderer.LastScale == 1);
                    AssertDesktopSize(original, sourceDpi, monitors[index].Dpi, 1);
                }
            }
            Console.WriteLine($"  Physical crossing: {monitors.Count} monitors, DPI [{string.Join(", ", monitors.Select(m => m.Dpi))}]; growth/hold/shrink/idle passed both directions, including source capture on secondary monitor");
        }
        finally
        {
            SetPhysicalCursorPos(saved.X, saved.Y);
            SetThreadDpiAwarenessContext(oldContext);
        }
    }

    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flag);
    private static string GuiResources()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return $"GDI={GetGuiResources(process.Handle, 0)}, USER={GetGuiResources(process.Handle, 1)}";
    }

    private sealed class NativeTestRenderer : ICursorRenderer
    {
        private readonly System.Drawing.Size _original;
        private readonly uint _sourceDpi;
        private readonly SystemCursorRenderer _inner;
        private readonly Func<uint> _readDpi;
        private uint _dpi, _lastDpi, _appliedDpi;
        private double _lastScale = 1;
        private event Action? _changed;
        public NativeTestRenderer(System.Drawing.Size original, uint sourceDpi, bool physicalPointer = false)
        {
            _original = original;
            _sourceDpi = _dpi = sourceDpi;
            _readDpi = physicalPointer ? MonitorDpi.AtPointer : () => Volatile.Read(ref _dpi);
            _inner = new SystemCursorRenderer(() => _appliedDpi = _readDpi());
        }
        public uint LastDpi => Volatile.Read(ref _lastDpi);
        public double LastScale => Volatile.Read(ref _lastScale);
        public bool IsReady => _inner.IsReady;
        public event Action? Changed
        {
            add { _changed += value; _inner.Changed += value; }
            remove { _changed -= value; _inner.Changed -= value; }
        }
        public void ChangeDpi(uint dpi) { Volatile.Write(ref _dpi, dpi); _changed?.Invoke(); }
        public void Apply(double scale)
        {
            _inner.Apply(scale);
            uint dpi = _appliedDpi;
            AssertDesktopSize(_original, _sourceDpi, dpi, scale);
            Volatile.Write(ref _lastScale, scale);
            Volatile.Write(ref _lastDpi, dpi);
        }
        public void Restore() => _inner.Restore();
    }

    private static void Shake(ShakeDetector detector, long start = 1000) =>
        Replay(detector, [0, 800, 0, 800, 0], start);

    private static void Replay(ShakeDetector detector, int[] xs, long start = 1000)
    {
        for (int i = 0; i < xs.Length; i++)
            detector.AddTestSample(new NativePoint(xs[i], 0), start + i * 40);
    }

    private static void SendButton(int message, uint xButton = 0)
    {
        var type = typeof(MouseHook).GetNestedType("MSLLHOOKSTRUCT", BindingFlags.NonPublic)!;
        var data = Activator.CreateInstance(type)!;
        type.GetField("mouseData")!.SetValue(data, xButton << 16);
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(type));
        try
        {
            Marshal.StructureToPtr(data, pointer, false);
            typeof(MouseHook).GetMethod("HookCallback", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [0, new IntPtr(message), pointer]);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void Check(string name, Action check)
    {
        check();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void Near(double actual, double expected)
    {
        if (Math.Abs(actual - expected) > 1e-9)
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void WaitUntil(Func<bool> predicate, int timeoutMs = 2000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new Exception("Animation timed out.");
            Thread.Sleep(5);
        }
    }

    private sealed class FakeRenderer : ICursorRenderer
    {
        public Action<double>? OnFrame { get; init; }
        public volatile bool Ready = true;
        public bool IsReady => Ready;
        public event Action? Changed;
        private double _lastScale = 1;
        private int _applies, _restores;
        public double LastScale => Volatile.Read(ref _lastScale);
        public int Applies => Volatile.Read(ref _applies);
        public int Restores => Volatile.Read(ref _restores);
        public void Apply(double scale) { OnFrame?.Invoke(scale); Volatile.Write(ref _lastScale, scale); Interlocked.Increment(ref _applies); }
        public void Restore() { Volatile.Write(ref _lastScale, 1); Interlocked.Increment(ref _restores); }
        public void NotifyChanged() => Changed?.Invoke();
    }
}
