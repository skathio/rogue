using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>Retry delays: exponential from the first delay, capped, with part taken off at random.</summary>
internal static class JobBackoff
{
    /// <summary>
    /// The delay after failed attempt <paramref name="attempt"/> (one-based): <paramref name="first"/>
    /// doubled for each attempt after the first, capped at <paramref name="max"/>.
    /// </summary>
    public static TimeSpan Delay(TimeSpan first, int attempt, TimeSpan max)
    {
        double doubled = first.Ticks * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromTicks((long)Math.Min(doubled, max.Ticks));
    }

    /// <summary>
    /// <paramref name="delay"/> shortened by <paramref name="jitter"/> times <paramref name="random"/>
    /// (a number from 0 to 1) of itself.
    /// </summary>
    public static TimeSpan Jitter(TimeSpan delay, double jitter, double random)
        => TimeSpan.FromTicks((long)(delay.Ticks * (1 - (jitter * random))));
}
