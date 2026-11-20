using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Messaging.Outbox.Tests;

/// <summary>
/// The upper limit on <see cref="MessageOutboxOptions.InboxLease"/> and <see cref="JobsOptions.LeaseDuration"/>:
/// validation rejects a lease past it, so a store adding the lease to the current time never overflows.
/// The value of the limit is the builder's choice, so each test finds the largest accepted lease
/// through validation itself and checks the cases at, just past and well past it.
/// </summary>
public sealed class LeaseLimitTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The smallest lease whose end, counted from <see cref="Now"/>, no longer fits a <see cref="DateTimeOffset"/>.</summary>
    private static readonly TimeSpan Overflowing = DateTimeOffset.MaxValue - Now + TimeSpan.FromTicks(1);

    public static TheoryData<long> WellPastTheLimit => new TheoryData<long>
    {
        TimeSpan.MaxValue.Ticks,
        Overflowing.Ticks,
        (DateTimeOffset.MaxValue - DateTimeOffset.MinValue).Ticks,
    };

    [Theory]
    [MemberData(nameof(WellPastTheLimit))]
    public void InboxLease_WellPastTheLimit_IsRejected(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddRogueMessagingOutbox(o => o.InboxLease = TimeSpan.FromTicks(ticks)));
    }

    [Theory]
    [MemberData(nameof(WellPastTheLimit))]
    public void LeaseDuration_WellPastTheLimit_IsRejected(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddRogueJobs(o => o.LeaseDuration = TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void InboxLease_AtTheLimit_IsAccepted_AndOnePast_IsRejected()
    {
        TimeSpan limit = LargestAccepted(InboxLeaseAccepted);

        Assert.True(InboxLeaseAccepted(limit));
        Assert.False(InboxLeaseAccepted(limit + TimeSpan.FromTicks(1)));
        Assert.True(limit < Overflowing, "A lease at the limit must not overflow the current time.");
        Assert.True(InboxLeaseAccepted(new MessageOutboxOptions().InboxLease), "The default lease must stay within the limit.");
    }

    [Fact]
    public void LeaseDuration_AtTheLimit_IsAccepted_AndOnePast_IsRejected()
    {
        TimeSpan limit = LargestAccepted(LeaseDurationAccepted);

        Assert.True(LeaseDurationAccepted(limit));
        Assert.False(LeaseDurationAccepted(limit + TimeSpan.FromTicks(1)));
        Assert.True(limit < Overflowing, "A lease at the limit must not overflow the current time.");
        Assert.True(LeaseDurationAccepted(new JobsOptions().LeaseDuration), "The default lease must stay within the limit.");
    }

    [Fact]
    public void Leases_AtTheLowerEnd_KeepTheirLimits()
    {
        Assert.False(InboxLeaseAccepted(TimeSpan.Zero));
        Assert.True(InboxLeaseAccepted(TimeSpan.FromTicks(1)));
        Assert.False(LeaseDurationAccepted(TimeSpan.Zero));
        Assert.True(LeaseDurationAccepted(TimeSpan.FromTicks(1)));
    }

    [Fact]
    public async Task InboxLease_AtTheLimit_HoldsTheMessageIdInTheStore()
    {
        TimeSpan limit = LargestAccepted(InboxLeaseAccepted);
        var store = new InMemoryJobStore(new ManualTimeProvider(Now));
        await using OutboxHost host = OutboxHost.Create(store, o => o.InboxLease = limit);
        IMessageInbox inbox = host.Services.GetRequiredService<IMessageInbox>();

        InboxClaim first = await inbox.TryClaimAsync("m-1", CancellationToken.None);
        InboxClaim second = await inbox.TryClaimAsync("m-1", CancellationToken.None);

        Assert.Equal(InboxClaim.Claimed, first);
        Assert.Equal(InboxClaim.Busy, second);
    }

    [Fact]
    public async Task LeaseDuration_AtTheLimit_ClaimsAndExtendsInTheStore()
    {
        TimeSpan limit = LargestAccepted(LeaseDurationAccepted);
        var store = new InMemoryJobStore(new ManualTimeProvider(Now));
        await store.EnqueueAsync(new[] { new NewJob { Id = Guid.NewGuid(), Kind = "k", Queue = "q", Payload = default, MaxAttempts = 1 } }, CancellationToken.None);

        IReadOnlyList<ClaimedJob> claimed = await store.ClaimAsync("q", 1, limit, CancellationToken.None);
        ClaimedJob job = Assert.Single(claimed);
        int extended = await store.ExtendLeasesAsync(new[] { job.Lease }, limit, CancellationToken.None);

        Assert.Equal(1, extended);
        Assert.Empty(await store.ClaimAsync("q", 1, limit, CancellationToken.None));
    }

    private static bool InboxLeaseAccepted(TimeSpan lease)
        => Accepted(() => new ServiceCollection().AddRogueMessagingOutbox(o => o.InboxLease = lease));

    private static bool LeaseDurationAccepted(TimeSpan lease)
        => Accepted(() => new ServiceCollection().AddRogueJobs(o => o.LeaseDuration = lease));

    private static bool Accepted(Action register)
    {
        try
        {
            register();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// The largest lease validation accepts, found by halving the range between one tick (accepted) and
    /// <see cref="TimeSpan.MaxValue"/>, which must be rejected for a limit to exist at all.
    /// </summary>
    private static TimeSpan LargestAccepted(Func<TimeSpan, bool> accepted)
    {
        Assert.True(accepted(TimeSpan.FromTicks(1)), "A lease of one tick must be accepted.");
        Assert.False(accepted(TimeSpan.MaxValue), "A lease of TimeSpan.MaxValue must be rejected: there is no upper limit.");
        long low = 1;
        long high = TimeSpan.MaxValue.Ticks;
        while (high - low > 1)
        {
            long middle = low + ((high - low) / 2);
            if (accepted(TimeSpan.FromTicks(middle)))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return TimeSpan.FromTicks(low);
    }
}
