using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>Retry delays: exponential from the first delay, capped, with part taken off at random.</summary>
internal static class JobBackoff
{
    /// <summary>
    /// The delay after failed attempt <paramref name="attempt"/> (one-based): <paramref name="first"/>
    /// doubled for each attempt after the first, capped at <paramref name="max"/>.
    /// </summary>
    /// <remarks>
    /// Never negative. A zero first delay stays zero at any attempt: doubling it would compute
    /// 0 times infinity past attempt 1024, which is NaN, and casting NaN to <see cref="long"/> gives
    /// a negative number on .NET 8.
    /// </remarks>
    public static TimeSpan Delay(TimeSpan first, int attempt, TimeSpan max)
    {
        if (first <= TimeSpan.Zero || max <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        double doubled = first.Ticks * Math.Pow(2, Math.Max(0, attempt - 1));
        return doubled >= max.Ticks ? max : TimeSpan.FromTicks((long)doubled);
    }

    /// <summary>
    /// <paramref name="delay"/> shortened by <paramref name="jitter"/> times <paramref name="random"/>
    /// (a number from 0 to 1) of itself; never negative and never longer than <paramref name="delay"/>.
    /// </summary>
    public static TimeSpan Jitter(TimeSpan delay, double jitter, double random)
    {
        double factor = 1 - (jitter * random);
        if (delay <= TimeSpan.Zero || !(factor > 0))
        {
            return TimeSpan.Zero;
        }
        return factor >= 1 ? delay : TimeSpan.FromTicks((long)(delay.Ticks * factor));
    }
}
