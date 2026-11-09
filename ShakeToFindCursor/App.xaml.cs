using ShakeToFindCursor.Localization;
using System;
using System.IO;
using System.Windows;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ShakeToFindCursor;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _notifyIcon;
    private TrayMenu? _trayMenu;
    private System.Drawing.Icon? _trayIcon;
    private Action? _refreshTray;
    private bool _hookFailed;
    private volatile bool _isEnabled = true;
    private SettingsWindow? _settingsWindow;
    private RegisteredWaitHandle? _showRequests;
    private int _previewVersion;
    private volatile bool _previewPending;
    private readonly ForegroundAppHistory _recentApp = new();

    // Shared by the mouse hook and the active-animation foreground check.
    private readonly object _suppressionGate = new();
    private System.Windows.Threading.DispatcherTimer? _foregroundTimer;
    // Throttle the expensive foreground/fullscreen check on ordinary mouse moves.
    private int _activeCheckQueued;
    private bool _watchingForeground;
    private long _lastFsCheckTicks;
    private bool _cachedShouldDisable;
    private const long FsCheckIntervalMs = 300;
    private System.Windows.Threading.DispatcherTimer? _appearanceTimer;
    private uint _cursorDpi = 96;
    private string _themeSignature = "";
    private volatile bool _exiting;

    private string _crashDir = "";

    public static AppSettings CurrentSettings { get; private set; } = new AppSettings();
    public static ShakeDetector? Detector { get; private set; }
    public static CursorAnimator? Animator { get; private set; }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        _crashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShakeToFindCursor");
        Directory.CreateDirectory(_crashDir);

        // Route WinForms message-loop exceptions to our handler instead of letting the
        // ThreadExceptionDialog appear — it can itself crash loading stock icons.
        WinForms.Application.SetUnhandledExceptionMode(WinForms.UnhandledExceptionMode.CatchException);
        WinForms.Application.ThreadException += (s, args) => LogAndRestore("ThreadException", args.Exception);

        // Always restore the system cursors on any crash path so the user is never left
        // with a permanently enlarged cursor.
        AppDomain.CurrentDomain.UnhandledException += (s, args) => LogAndRestore("AppDomain", args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogAndRestore("UnobservedTask", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += (s, args) =>
        {
            LogAndRestore("Dispatcher", args.Exception);
            args.Handled = true; // background utility: log + restore, then stay alive
        };

        CurrentSettings = AppSettings.Load();
        UiText.Current.Apply(CurrentSettings.Language);

        Detector = new ShakeDetector(CurrentSettings);
        Animator = new CursorAnimator(Detector, CurrentSettings);
        _cursorDpi = MonitorDpi.AtPointer();
        _themeSignature = CursorHelper.GetThemeSignature();
        _ = RefreshCursorCacheAsync();
        _appearanceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _appearanceTimer.Tick += (_, _) =>
        {
            _appearanceTimer.Stop();
            MonitorDpi.Invalidate();
            Volatile.Write(ref _cursorDpi, MonitorDpi.AtPointer());
            string signature = CursorHelper.GetThemeSignature();
            bool changed = signature != _themeSignature;
            _themeSignature = signature;
            _ = RefreshCursorCacheAsync(changed);
        };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _trayIcon = LoadTrayIcon();
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true
        };

        var contextMenu = _trayMenu = new TrayMenu();
        contextMenu.Toggle.Click += (s, ev) =>
        {
            lock (_suppressionGate)
            {
                _isEnabled = !_isEnabled;
                Detector?.SetSuppressed(!_isEnabled || _cachedShouldDisable || MouseHook.IsButtonDown);
            }
            _refreshTray?.Invoke();
        };

        contextMenu.Settings.Click += (s, ev) => OpenSettings();
        _notifyIcon.MouseDoubleClick += (s, ev) =>
        {
            if (ev.Button == WinForms.MouseButtons.Left) OpenSettings();
        };

        contextMenu.ExitApp.Click += (s, ev) => Shutdown();
        _refreshTray = () =>
        {
            var text = UiText.Current;
            contextMenu.RefreshText(_isEnabled);
            _notifyIcon.Text = text[_hookFailed ? "TrayHookFailed" : _isEnabled ? "AppName" : "TrayDisabled"];
        };
        _refreshTray();
        _notifyIcon.ContextMenuStrip = contextMenu;

        MouseHook.MouseMoved += OnMouseMoved;
        MouseHook.ButtonStateChanged += OnMouseButtonStateChanged;
        if (!MouseHook.Start())
        {
            WriteCrashLog("MouseHook", "SetWindowsHookEx failed — shake detection is inactive.");
            _hookFailed = true;
            _refreshTray();
            _notifyIcon.ShowBalloonTip(5000, UiText.Current["AppName"],
                UiText.Current["HookError"],
                WinForms.ToolTipIcon.Warning);
        }
        // A keyboard switch to an excluded/fullscreen app must cancel an active
        // enlargement even if the pointer remains stationary.
        _foregroundTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _foregroundTimer.Tick += (_, _) =>
        {
            if (!_watchingForeground) _recentApp.Observe();
            _settingsWindow?.RefreshExternalTarget();
            if (Detector.Energy > 0)
            {
                RefreshSuppression(force: true);
                // Re-evaluate the visible cursor even during a stationary hold.
                Animator?.Wake();
            }
            else
            {
                Interlocked.Exchange(ref _activeCheckQueued, 0);
                if (_watchingForeground && _settingsWindow == null) _foregroundTimer.Stop();
                // A shake on the hook thread may have raced the stop.
                if (Detector.Energy > 0) ScheduleActiveChecks();
            }
        };
        _watchingForeground = _recentApp.Watch(() =>
        {
            if (_exiting) return;
            _settingsWindow?.RefreshExternalTarget();
            RefreshSuppression(force: true);
            if (Detector.Energy > 0) Animator?.Wake();
        });
        if (!_watchingForeground) _foregroundTimer.Start();
        Detector.StateChanged += ScheduleActiveChecks;
        ScheduleActiveChecks();
        if (Program.ShowRequest is { } showRequest)
        {
            _showRequests = ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) =>
            {
                if (!_exiting && !Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(ShowPointer));
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }

        // Warm up WPF and the settings window now, while idle, so the first real "open
        // Settings" doesn't trigger a one-time JIT/allocation burst whose GC pause would
        // briefly suspend the mouse-hook thread and stutter system-wide input.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(PrewarmSettingsWindow));
    }

    private void OpenSettings()
    {
        if (_exiting) return;
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _recentApp.Observe();
            _settingsWindow = new SettingsWindow(CurrentSettings, SaveSettings, _recentApp.Target,
                PreviewPointer, CancelPreview);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _foregroundTimer?.Start();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void ShowPointer()
    {
        if (_exiting || Detector == null) return;
        CancelPreview();
        RefreshSuppression(force: true);
        uint dpi = MonitorDpi.AtPointer();
        if (dpi != Volatile.Read(ref _cursorDpi))
        {
            Volatile.Write(ref _cursorDpi, dpi);
            _ = RefreshCursorCacheAsync();
        }
        Detector.Show(Environment.TickCount64);
    }

    internal static void PreviewSettings(AppSettings settings)
    {
        if (Current is App app) app.PreviewPointer(settings);
    }

    internal static void CancelSettingsPreview()
    {
        if (Current is App app) app.CancelPreview();
    }

    private async void PreviewPointer(AppSettings draft)
    {
        if (_exiting || Animator == null) return;
        int version = ++_previewVersion;
        _previewPending = true;
        Animator.CancelPreview();
        try
        {
            uint dpi = MonitorDpi.AtPointer();
            Volatile.Write(ref _cursorDpi, dpi);
            await CursorHelper.RequestCaches(draft.MagnificationFactor, dpi);
            if (_exiting || version != _previewVersion) return;
            await Animator.PreviewAsync(draft);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Preview", ex);
            if (!_exiting && version == _previewVersion)
                _notifyIcon?.ShowBalloonTip(3000, UiText.Current["AppName"],
                    UiText.Current["ErrorPreview"], WinForms.ToolTipIcon.Warning);
        }
        finally
        {
            if (!_exiting && version == _previewVersion)
            {
                _previewPending = false;
                _ = RefreshCursorCacheAsync();
            }
        }
    }

    private void CancelPreview()
    {
        if (!_previewPending) return;
        ++_previewVersion;
        _previewPending = false;
        Animator?.CancelPreview();
        if (!_exiting) _ = RefreshCursorCacheAsync();
    }

    private void ScheduleActiveChecks()
    {
        if (_exiting || Detector?.Energy is not > 0 || Interlocked.Exchange(ref _activeCheckQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_exiting) _foregroundTimer?.Start(); }));
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(
            "ShakeToFindCursor.Assets.TrayIcon.ico")
            ?? throw new InvalidOperationException("The tray icon resource is missing.");
        using var icon = new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)icon.Clone();
    }

    private void PrewarmSettingsWindow()
    {
        try
        {
            var warm = new SettingsWindow(CurrentSettings, SaveSettings)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Opacity = 0
            };
            warm.Show();
            // Let one render pass complete (warms the render/JIT path), then discard it.
            warm.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { try { warm.Close(); } catch { } }));
        }
        catch { }
    }

    private void OnMouseMoved(object? sender, MouseHook.NativePoint point)
    {
        if (Detector == null) return;

        var display = MonitorDpi.DisplayAt(point);
        uint dpi = display.Dpi;
        if (dpi != Volatile.Read(ref _cursorDpi))
        {
            Volatile.Write(ref _cursorDpi, dpi);
            Animator?.Wake();
            Dispatcher.BeginInvoke(new Action(() => { if (!_exiting) _ = RefreshCursorCacheAsync(); }));
        }
        RefreshSuppression();
        Detector.AddSample(point, Environment.TickCount64, dpi, display.DiagonalDip, display.Monitor);
    }

    private void OnMouseButtonStateChanged(object? sender, EventArgs e)
    {
        if (MouseHook.IsButtonDown && _previewPending)
        {
            int version = Volatile.Read(ref _previewVersion);
            Dispatcher.BeginInvoke(new Action(() => { if (version == _previewVersion) CancelPreview(); }));
        }
        // Release is handled on the next move, after checking enabled/exclusion
        // state. A button-up must never accidentally re-enable the detector.
        lock (_suppressionGate)
        {
            if (MouseHook.IsButtonDown) Detector?.SetSuppressed(true);
        }
    }

    private void RefreshSuppression(bool force = false)
    {
        lock (_suppressionGate)
        {
            long now = Environment.TickCount64;
            if (force || now - _lastFsCheckTicks >= FsCheckIntervalMs)
            {
                _lastFsCheckTicks = now;
                _cachedShouldDisable = FullscreenDetector.ShouldDisable(
                    CurrentSettings.ExcludedProcesses,
                    CurrentSettings.DisableInFullscreen,
                    CurrentSettings.ProcessNameOnlyExclusions);
            }
            Detector?.SetSuppressed(!_isEnabled || _cachedShouldDisable || MouseHook.IsButtonDown);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _exiting = true;
        CancelPreview();
        _showRequests?.Unregister(null);
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _appearanceTimer?.Stop();
        _foregroundTimer?.Stop();
        _recentApp.Dispose();
        if (Detector != null) Detector.StateChanged -= ScheduleActiveChecks;
        MouseHook.Stop();
        MouseHook.MouseMoved -= OnMouseMoved;
        MouseHook.ButtonStateChanged -= OnMouseButtonStateChanged;
        Animator?.Dispose();
        CursorHelper.Shutdown();

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
    }

    internal static void SaveSettings(AppSettings next)
    {
        ((App)Current).CancelPreview();
        var previous = CurrentSettings;
        next.Save(previous);
        CurrentSettings = next;
        UiText.Current.Apply(next.Language);
        ((App)Current)._refreshTray?.Invoke();
        if (next.MagnificationFactor != previous.MagnificationFactor)
            _ = ((App)Current).RefreshCursorCacheAsync();
        Detector?.UpdateSettings(CurrentSettings);
        Animator?.UpdateSettings(CurrentSettings);
    }

    internal async Task RefreshCursorCacheAsync(bool refreshSources = false)
    {
        if (_exiting) return;
        try
        {
            await CursorHelper.RequestCaches(CurrentSettings.MagnificationFactor,
                Volatile.Read(ref _cursorDpi), refreshSources);
            Animator?.UpdateSettings(CurrentSettings);
        }
        catch (Exception ex) { WriteCrashLog("CursorCache", ex); }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueAppearanceRefresh();
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => QueueAppearanceRefresh();
    private void QueueAppearanceRefresh()
    {
        if (_exiting || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_exiting || _appearanceTimer == null) return;
            _appearanceTimer.Stop();
            _appearanceTimer.Start();
        }));
    }

    private void WriteCrashLog(string source, object? info)
    {
        try
        {
            string path = Path.Combine(_crashDir, "crash.log");
            // Roll the log if it grows past ~512 KB so it can't accumulate forever.
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTime.Now} [{source}]\n{info}\n\n");
        }
        catch { }
    }

    private void LogAndRestore(string source, object? info)
    {
        WriteCrashLog(source, info);
        try { CursorHelper.RestoreThemeCursors(); } catch { }
    }
}
