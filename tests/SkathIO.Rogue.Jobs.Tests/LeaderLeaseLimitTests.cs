using System;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>
/// <see cref="JobsOptions.LeaderLeaseDuration"/> has the same upper limit as the job lease: the leader
/// lock's store adds it to the current time, so a lease past the limit would overflow on every renewal.
/// </summary>
public sealed class LeaderLeaseLimitTests
{
    public static TheoryData<long> PastTheLimit => new TheoryData<long>
    {
        (JobsOptions.MaxLeaseDuration + TimeSpan.FromTicks(1)).Ticks,
        (JobsOptions.MaxLeaseDuration + TimeSpan.FromDays(1)).Ticks,
        (DateTimeOffset.MaxValue - DateTimeOffset.MinValue).Ticks,
        TimeSpan.MaxValue.Ticks,
    };

    [Theory]
    [MemberData(nameof(PastTheLimit))]
    public void ALeaderLease_PastTheLimit_IsRejected(long ticks)
    {
        ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceCollection().AddRogueJobs(o => o.LeaderLeaseDuration = TimeSpan.FromTicks(ticks)));

        Assert.Equal(nameof(JobsOptions.LeaderLeaseDuration), thrown.ParamName);
    }

    [Fact]
    public void ALeaderLease_AtTheLimit_AndTheDefault_AreAccepted()
    {
        new ServiceCollection().AddRogueJobs(o => o.LeaderLeaseDuration = JobsOptions.MaxLeaseDuration);
        new ServiceCollection().AddRogueJobs(o => o.LeaderLeaseDuration = new JobsOptions().LeaderLeaseDuration);
    }
}
