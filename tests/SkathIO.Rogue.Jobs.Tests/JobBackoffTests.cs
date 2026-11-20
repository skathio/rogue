using System;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>Retry delays: doubling from the first delay, capped, shortened by the jitter fraction.</summary>
public sealed class JobBackoffTests
{
    private static readonly TimeSpan _first = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _max = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(1000, 60)]
    public void Delay_DoublesPerAttempt_UpToTheCap(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), JobBackoff.Delay(_first, attempt, _max));
    }

    [Theory]
    [InlineData(0.5, 0, 4)]
    [InlineData(0.5, 1, 2)]
    [InlineData(1, 1, 0)]
    [InlineData(0.2, 0.5, 3.6)]
    public void Jitter_TakesItsFractionOffAtRandom(double jitter, double random, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), JobBackoff.Jitter(JobBackoff.Delay(_first, 2, _max), jitter, random));
    }

    [Fact]
    public void ZeroFirstDelay_RetriesAtOnce()
    {
        Assert.Equal(TimeSpan.Zero, JobBackoff.Delay(TimeSpan.Zero, 4, _max));
    }
}
