using System;
using System.Diagnostics;

namespace SkathIO.Rogue;

/// <summary>A non-allocating stopwatch for measuring elapsed time in behaviors.</summary>
public readonly struct ValueStopwatch
{
    private readonly long _startTimestamp;

    private ValueStopwatch(long startTimestamp) => _startTimestamp = startTimestamp;

    /// <summary>Starts a new stopwatch.</summary>
    public static ValueStopwatch StartNew() => new ValueStopwatch(Stopwatch.GetTimestamp());

    /// <summary>Gets the elapsed time since the stopwatch was started.</summary>
    public TimeSpan Elapsed
    {
#if NET7_0_OR_GREATER
        get => Stopwatch.GetElapsedTime(_startTimestamp);
#else
        get
        {
            var ticks = Stopwatch.GetTimestamp() - _startTimestamp;
            return TimeSpan.FromTicks((long)((double)ticks / Stopwatch.Frequency * TimeSpan.TicksPerSecond));
        }
#endif
    }

    /// <summary>Gets elapsed milliseconds.</summary>
    public double ElapsedMilliseconds => Elapsed.TotalMilliseconds;
}
