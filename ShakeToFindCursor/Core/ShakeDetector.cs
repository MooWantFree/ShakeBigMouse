using System;
using System.Collections.Generic;
using static ShakeToFindCursor.MouseHook;

namespace ShakeToFindCursor;

/// <summary>Incremental, DPI-normalized oscillation detection and release envelope.</summary>
public class ShakeDetector
{
    private readonly object _lock = new();
    private readonly Queue<Segment> _segments = new(96);
    private readonly record struct Segment(double X, double Y, long StartMs, double Length, bool Reversed);
    public readonly record struct State(double Energy, double HoldRemainingMs, bool Suppressed = false, double ShrinkSpeed = 1);
    private double _energy, _heldEnergy, _holdDurationMs, _shrinkSpeed;
    private long _lastUpdateMs;
    private bool _suppressed, _proportional;
    private long? _lastShakeMs;
    private double _showUntilMs, _commandLineDurationMs;
    private int _minimumDistance, _windowMs, _diagonalPercent;
    private nint _monitor;
    private double _diagonal;
    private double _path, _x, _y, _anchorX, _anchorY, _prevDx, _prevDy;
    private int _reversals;
    private bool _hasPosition, _hasDirection;
    private NativePoint _lastPoint;
    private long _sampleMs, _anchorMs;
    private uint _dpi = 96;

    private const long SampleIntervalMs = 4; // at most 250 evaluations/s, including 8 kHz mice
    private const double WiggleGate = 0.55;
    private const int MinReversals = 2;
    private const double MinSegmentLength = 12;
    private const double MinReversalSpeed = 0.25; // logical pixels/ms
    private const double ReleaseTauMs = 140;

    // Raised outside the lock, only when the envelope or its deadline changes.
    public event Action? StateChanged;
    public ShakeDetector(AppSettings settings) => UpdateSettings(settings);

    public void UpdateSettings(AppSettings settings)
    {
        lock (_lock)
        {
            int distance = Math.Clamp(settings.MinimumShakeDistance, 1, 3000);
            int window = Math.Clamp(settings.ShakeWindowMs, 50, 1500);
            int percent = Math.Clamp(settings.ShakeSensitivityPercent, 1, 100);
            if ((_minimumDistance, _windowMs, _diagonalPercent) != (distance, window, percent))
                ClearMotion();
            (_minimumDistance, _windowMs, _diagonalPercent) = (distance, window, percent);
            _shrinkSpeed = settings.ShrinkSpeed;
            _commandLineDurationMs = settings.CommandLineDurationSeconds * 1000;
            _proportional = settings.ScaleWithShakeStrength;
            _holdDurationMs = double.IsFinite(settings.MaxSizeHoldSeconds)
                ? Math.Clamp(settings.MaxSizeHoldSeconds, 0, 10) * 1000 : 0;
            if (_holdDurationMs == 0) _lastShakeMs = null;
        }
        StateChanged?.Invoke();
    }

    public void SetSuppressed(bool suppressed)
    {
        bool changed;
        lock (_lock)
        {
            _suppressed = suppressed;
            changed = suppressed && _energy > 0;
            if (suppressed)
            {
                _energy = _heldEnergy = 0;
                _lastShakeMs = null;
                _showUntilMs = 0;
                ClearMotion();
            }
        }
        if (changed) StateChanged?.Invoke();
    }

    public double Energy { get { lock (_lock) return _energy; } }

    internal void Show(long nowMs)
    {
        lock (_lock)
        {
            if (_suppressed) return;
            ClearMotion();
            _energy = _heldEnergy = 1;
            _lastUpdateMs = nowMs;
            _lastShakeMs = null;
            _showUntilMs = nowMs + _commandLineDurationMs;
        }
        StateChanged?.Invoke();
    }

    internal int BufferedSegmentCount { get { lock (_lock) return _segments.Count; } }
    internal long EvaluationCount { get; private set; }

    public void AddSample(NativePoint point, long nowMs)
    {
        var display = MonitorDpi.DisplayAt(point);
        AddSample(point, nowMs, display.Dpi, display.DiagonalDip, display.Monitor);
    }

    internal void AddSample(NativePoint point, long nowMs, uint dpi, double diagonalDip, nint monitor)
    {
        bool changed = false;
        lock (_lock)
        {
            if (_suppressed) return;
            dpi = Math.Clamp(dpi, 96u, 768u);
            bool crossing = monitor != _monitor;
            double dxPhysical = (double)point.X - _lastPoint.X, dyPhysical = (double)point.Y - _lastPoint.Y;
            // The hook reports one physical desktop coordinate space. Normalize
            // displacements, never absolute coordinates. For a sample straddling
            // a seam, use the mean of the two physical-to-logical scale factors.
            double units = crossing ? (96.0 / _dpi + 96.0 / dpi) / 2 : 96.0 / dpi;
            double jumpLimit = Math.Max(_diagonal, diagonalDip);
            bool warp = crossing && jumpLimit > 0 &&
                (dxPhysical * dxPhysical + dyPhysical * dyPhysical) * units * units > jumpLimit * jumpLimit;
            // A single discontinuous jump or a display reconfiguration must not
            // manufacture a reversal. A normal seam crossing keeps the history.
            if (!_hasPosition || warp || (!crossing && (dpi != _dpi || diagonalDip != _diagonal)) || nowMs - _sampleMs > _windowMs)
            {
                ClearMotion();
                _hasPosition = true;
                _dpi = dpi;
                _monitor = monitor;
                _diagonal = diagonalDip;
                _lastPoint = point;
                _sampleMs = _anchorMs = nowMs;
                Decay(nowMs);
                return;
            }
            if (nowMs - _sampleMs < SampleIntervalMs) return;
            EvaluationCount++;
            _x += dxPhysical * units;
            _y += dyPhysical * units;
            _dpi = dpi;
            _monitor = monitor;
            _diagonal = diagonalDip;
            _lastPoint = point;
            _sampleMs = nowMs;
            Decay(nowMs);

            while (_segments.TryPeek(out var expired) && nowMs - expired.StartMs > _windowMs)
            {
                _segments.Dequeue();
                _path -= expired.Length;
                if (expired.Reversed) _reversals--;
            }
            double dx = _x - _anchorX, dy = _y - _anchorY;
            double length = Math.Sqrt(dx * dx + dy * dy);
            // A repeated endpoint is a fresh starting time, not a long movement
            // spanning the pause before the next shake.
            if (length == 0) _anchorMs = nowMs;
            if (length < MinSegmentLength) return;
            bool reversed = _hasDirection && dx * _prevDx + dy * _prevDy < 0
                && length / Math.Max(1, nowMs - _anchorMs) >= MinReversalSpeed;
            if (nowMs - _anchorMs <= _windowMs)
            {
                _segments.Enqueue(new Segment(_anchorX, _anchorY, _anchorMs, length, reversed));
                _path += length;
                if (reversed) _reversals++;
            }
            _anchorX = _x;
            _anchorY = _y;
            _anchorMs = nowMs;
            _prevDx = dx;
            _prevDy = dy;
            _hasDirection = true;

            if (!reversed || !_segments.TryPeek(out var first)) return;
            // The first segment's incoming direction is outside this window.
            int turns = _reversals - (first.Reversed ? 1 : 0);
            double threshold = Math.Max(_minimumDistance, diagonalDip * _diagonalPercent / 100);
            if (turns < MinReversals || _path < threshold) return;
            double netX = _x - first.X, netY = _y - first.Y;
            if (1 - Math.Sqrt(netX * netX + netY * netY) / _path < WiggleGate) return;

            double instant = _proportional
                ? Math.Clamp((_path - threshold) / (1.2 * threshold), 0, 1) : 1;
            _energy = Math.Max(_energy, instant);
            _heldEnergy = _energy;
            _lastShakeMs = _holdDurationMs > 0 ? nowMs : null;
            changed = true;
        }
        if (changed) StateChanged?.Invoke();
    }

    public double Tick(long nowMs) => GetState(nowMs).Energy;

    public State GetState(long nowMs)
    {
        lock (_lock)
        {
            Decay(nowMs);
            return new State(_energy, Math.Max(0, HoldUntilMs - nowMs), _suppressed, _shrinkSpeed);
        }
    }

    private double HoldUntilMs => Math.Max(_showUntilMs,
        _lastShakeMs is long last ? last + _holdDurationMs : 0);

    private void ClearMotion()
    {
        _segments.Clear();
        _path = _x = _y = _anchorX = _anchorY = 0;
        _reversals = 0;
        _hasPosition = _hasDirection = false;
    }

    private void Decay(long nowMs)
    {
        double from = _lastUpdateMs;
        _lastUpdateMs = Math.Max(_lastUpdateMs, nowMs);
        if (nowMs <= from) return;
        double until = HoldUntilMs;
        if (until > 0)
        {
            if (nowMs <= until) { _energy = _heldEnergy; return; }
            from = Math.Max(from, until);
            _lastShakeMs = null;
            _showUntilMs = 0;
        }
        _energy *= Math.Exp(-(nowMs - from) * _shrinkSpeed / ReleaseTauMs);
        if (_energy < 1e-3) _energy = 0;
    }
}
