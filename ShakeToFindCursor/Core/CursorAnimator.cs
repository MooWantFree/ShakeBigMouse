using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShakeToFindCursor;

internal interface ICursorRenderer
{
    bool IsReady { get; }
    event Action? Changed;
    void Apply(double scale);
    void Restore();
}

internal sealed class SystemCursorRenderer : ICursorRenderer
{
    private readonly Func<uint> _pointerDpi;
    public SystemCursorRenderer() : this(MonitorDpi.AtPointer) { }
    internal SystemCursorRenderer(Func<uint> pointerDpi) => _pointerDpi = pointerDpi;
    public bool IsReady => CursorHelper.IsReady;
    public event Action? Changed
    {
        add => CursorHelper.CacheChanged += value;
        remove => CursorHelper.CacheChanged -= value;
    }
    public void Apply(double scale) => CursorHelper.ApplyScaleForDpi(scale, _pointerDpi());
    public void Restore() => CursorHelper.RestoreThemeCursors();
}

/// <summary>Event-driven animation; no polling or busy-spin while idle or holding.</summary>
public sealed class CursorAnimator : IDisposable
{
    private readonly ShakeDetector _detector;
    private readonly ICursorRenderer _renderer;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private sealed record PreviewState(ShakeDetector Detector, double MaxScale, TaskCompletionSource Completion);
    private PreviewState? _preview;
    private double _maxScale;
    private int _disposed;
    private long _iterations;
    private const double FollowTauMs = 55;
    // Limit relative motion per displayed update. Time spent installing cursors
    // or waiting on a late timer must not be repaid in one large shrink step.
    private const double ShrinkTauMs = 340;
    private const double MaxShrinkStepMs = 10;
    internal long IterationCount => Interlocked.Read(ref _iterations);

    public CursorAnimator(ShakeDetector detector, AppSettings settings)
        : this(detector, settings, new SystemCursorRenderer()) { }

    internal CursorAnimator(ShakeDetector detector, AppSettings settings, ICursorRenderer renderer)
    {
        _detector = detector;
        _renderer = renderer;
        _maxScale = Math.Max(1, settings.MagnificationFactor);
        _detector.StateChanged += Wake;
        _renderer.Changed += Wake;
        _worker = Task.Run(RunAsync);
    }

    public void UpdateSettings(AppSettings settings)
    {
        Volatile.Write(ref _maxScale, Math.Max(1, settings.MagnificationFactor));
        Wake();
    }

    internal Task PreviewAsync(AppSettings settings)
    {
        var detector = new ShakeDetector(settings);
        detector.Show(Environment.TickCount64);
        var preview = new PreviewState(detector, settings.MagnificationFactor,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Interlocked.Exchange(ref _preview, preview)?.Completion.TrySetResult();
        if (Volatile.Read(ref _disposed) != 0) CancelPreview();
        Wake();
        return preview.Completion.Task;
    }

    internal void CancelPreview()
    {
        if (Interlocked.Exchange(ref _preview, null) is not { } preview) return;
        preview.Completion.TrySetResult();
        Wake();
    }

    public void Wake()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _signal.Release(); }
        catch (SemaphoreFullException) { } // one pending update is sufficient
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        double scale = 1;
        bool hasApplied = false;
        long lastTicks = Stopwatch.GetTimestamp();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Interlocked.Increment(ref _iterations);
                long nowTicks = Stopwatch.GetTimestamp();
                double dtMs = Math.Clamp(Stopwatch.GetElapsedTime(lastTicks, nowTicks).TotalMilliseconds, 0.1, 50);
                lastTicks = nowTicks;
                var preview = Volatile.Read(ref _preview);
                var state = (preview?.Detector ?? _detector).GetState(Environment.TickCount64);
                double target = 1 + ((preview?.MaxScale ?? Volatile.Read(ref _maxScale)) - 1) * state.Energy;
                double speed = target < scale && !state.Suppressed ? state.ShrinkSpeed : 1;
                double next = scale + (target - scale) * (1 - Math.Exp(-dtMs * speed / FollowTauMs));
                if (Math.Abs(target - next) < 0.01) next = target;
                if (next < scale)
                {
                    // A drag, disable or excluded foreground app should relinquish
                    // the enlarged cursor promptly even when all frames are cold.
                    double tau = state.Suppressed ? 120 : ShrinkTauMs;
                    next = Math.Max(next, scale * Math.Exp(-Math.Min(dtMs, MaxShrinkStepMs) * speed / tau));
                    if (!state.Suppressed && scale > VectorCursorArtwork.ArrowRest)
                    {
                        // Near rest, a small percentage of the whole cursor can
                        // cover most of the remaining outline motion. Limit the step
                        // even on the update entering the native handoff.
                        double dt = Math.Min(dtMs, 12) * speed;
                        next = Math.Max(next, 1 + (scale - 1) * Math.Exp(-dt / 115));
                        double endStep = (VectorCursorArtwork.ArrowTail - VectorCursorArtwork.ArrowRest) * dt / 360;
                        next = Math.Max(next, Math.Min(scale, VectorCursorArtwork.ArrowTail) - endStep);
                    }
                }
                scale = next;
                bool settled = scale == target;

                // Finish on the normal-sized frame through the same rendering
                // path. Reloading the entire system cursor theme at this point
                // can briefly remove the pointer while Windows replaces it.
                // Reapply after cache changes even when idle (e.g. a DPI change).
                if (_renderer.IsReady && (state.Energy > 0 || !settled || hasApplied))
                {
                    _renderer.Apply(scale);
                    hasApplied = true;
                }

                if (preview != null && state.Energy == 0 && settled &&
                    Interlocked.CompareExchange(ref _preview, null, preview) == preview)
                {
                    preview.Completion.TrySetResult();
                    Wake();
                }

                int waitMs = !_renderer.IsReady || (state.Energy == 0 && settled)
                    ? Timeout.Infinite
                    : settled && state.HoldRemainingMs > 0
                        ? Math.Max(1, (int)Math.Ceiling(state.HoldRemainingMs)) : 8;
                if (waitMs > 0)
                    waitMs = Math.Max(0, waitMs - (int)Stopwatch.GetElapsedTime(nowTicks).TotalMilliseconds);
                await _signal.WaitAsync(waitMs, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            CancelPreview();
            // Dispose joins this worker before returning, so no later frame can
            // overwrite the restored cursor (including exit during a long hold).
            // A completed animation still owns the normal-sized replacement.
            // Restore the native theme once on exit, including exit while idle.
            if (hasApplied) _renderer.Restore();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelPreview();
        _detector.StateChanged -= Wake;
        _renderer.Changed -= Wake;
        _stop.Cancel();
        _worker.GetAwaiter().GetResult();
        _signal.Dispose();
        _stop.Dispose();
    }
}
