using System;
using System.Globalization;

namespace RekordCore;

/// <summary>
/// Deep module tracking continuous recording timestamps across Pause / Resume cycles
/// so exported video, WebP, and GIF timelines have no frozen gaps.
/// </summary>
public struct RecordingTimeline
{
    public bool IsPaused { get; private set; }
    private TimeSpan? _startTimestamp;
    private TimeSpan? _pauseBeganTimestamp;
    private TimeSpan _totalPausedDuration;
    private TimeSpan _lastAdjustedTimestamp;

    public void Reset()
    {
        IsPaused = false;
        _startTimestamp = null;
        _pauseBeganTimestamp = null;
        _totalPausedDuration = TimeSpan.Zero;
        _lastAdjustedTimestamp = TimeSpan.Zero;
    }

    public void Pause()
    {
        if (IsPaused) return;
        IsPaused = true;
        _pauseBeganTimestamp = null;
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
    }

    /// <summary>
    /// Processes an incoming monotonic sample timestamp and returns the contiguous adjusted timestamp,
    /// or <c>null</c> if the frame should be dropped because recording is currently paused.
    /// </summary>
    public TimeSpan? ProcessFrameTimestamp(TimeSpan rawTimestamp)
    {
        if (rawTimestamp < TimeSpan.Zero)
            return null;

        if (IsPaused)
        {
            _pauseBeganTimestamp ??= rawTimestamp;
            return null;
        }

        if (_pauseBeganTimestamp is TimeSpan pauseStart)
        {
            var delta = rawTimestamp - pauseStart;
            if (delta > TimeSpan.Zero)
                _totalPausedDuration += delta;
            _pauseBeganTimestamp = null;
        }

        _startTimestamp ??= rawTimestamp;

        var elapsed = rawTimestamp - _startTimestamp.Value;
        var adjusted = elapsed - _totalPausedDuration;

        if (adjusted < TimeSpan.Zero || adjusted < _lastAdjustedTimestamp)
            return null;

        _lastAdjustedTimestamp = adjusted;
        return adjusted;
    }

    public readonly double RecordedDurationSeconds =>
        _lastAdjustedTimestamp.TotalSeconds > 0 ? _lastAdjustedTimestamp.TotalSeconds : 0.0;

    /// <summary>
    /// Formats a duration in seconds as <c>MM:SS</c> or <c>MM:SS.d</c>.
    /// </summary>
    public static string FormatDuration(double seconds, bool includeTenths = false)
    {
        var clamped = Math.Max(0.0, double.IsFinite(seconds) ? seconds : 0.0);
        var totalSeconds = (int)Math.Floor(clamped);
        var minutes = totalSeconds / 60;
        var secs = totalSeconds % 60;
        if (includeTenths)
        {
            var tenths = Math.Min(9, (int)Math.Floor((clamped - Math.Floor(clamped)) * 10.0));
            return string.Create(CultureInfo.InvariantCulture, $"{minutes:D2}:{secs:D2}.{tenths}");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{minutes:D2}:{secs:D2}");
    }
}

public readonly record struct TrimRange
{
    public double Start { get; }
    public double End { get; }

    public TrimRange(double start, double end, double totalDuration, double minLength = 0.2)
    {
        var safeTotal = Math.Max(minLength, totalDuration);
        var clampedStart = Math.Min(Math.Max(0.0, start), Math.Max(0.0, safeTotal - minLength));
        var clampedEnd = Math.Min(Math.Max(clampedStart + minLength, end), safeTotal);
        Start = clampedStart;
        End = clampedEnd;
    }

    public double Duration => Math.Max(0.0, End - Start);

    public bool IsFullRange(double totalDuration, double tolerance = 0.05) =>
        Start <= tolerance && Math.Abs(End - totalDuration) <= tolerance;
}
