using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Jobs.StoreContract;

/// <summary>
/// The behaviour every <see cref="IMessageInboxStore"/> must show, written once and run against each
/// store: a store's test project derives from this class and creates its store on the given clock.
/// Every test uses message ids of its own, so a database store may share one database across the suite.
/// </summary>
public abstract class MessageInboxStoreContract
{
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(1);

    /// <summary>The clock the store runs on; tests move it forward by hand.</summary>
    protected ManualTimeProvider Clock { get; } = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Creates the store under test on <paramref name="clock"/>.</summary>
    protected abstract Task<IMessageInboxStore> CreateInboxAsync(TimeProvider clock);

    private static CancellationToken None => CancellationToken.None;

    // The token a caller holding no claim passes: no claim a store makes has it.
    private const long NoClaim = 0;

    [StoreFact]
    public async Task FirstClaim_IsClaimed_AndAnotherIsBusy_WhileTheLeaseHolds()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = NewId();

        MessageClaim first = (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome;
        Clock.Advance(_lease - TimeSpan.FromMilliseconds(1));
        MessageClaim second = (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome;

        Assert.Equal(MessageClaim.Claimed, first);
        Assert.Equal(MessageClaim.Busy, second);
    }

    [StoreFact]
    public async Task Claim_WhoseLeaseRanOut_IsTakenByTheNextClaim()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = NewId();
        await inbox.TryClaimMessageAsync(id, _lease, None);

        Clock.Advance(_lease);
        MessageClaim retaken = (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome;
        MessageClaim afterRetake = (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome;

        Assert.Equal(MessageClaim.Claimed, retaken);
        Assert.Equal(MessageClaim.Busy, afterRetake);
    }

    [StoreFact]
    public async Task CompletedMessage_IsHandled_ForEveryLaterClaim_EvenAfterTheLease()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = NewId();
        MessageClaimResult claim = await inbox.TryClaimMessageAsync(id, _lease, None);

        await inbox.CompleteMessageAsync(id, claim.Token, None);
        MessageClaim now = (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome;
        Clock.Advance(_lease * 10);
        MessageClaim later = (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome;

        Assert.Equal(MessageClaim.Handled, now);
        Assert.Equal(MessageClaim.Handled, later);
    }

    [StoreFact]
    public async Task Complete_WithoutAClaim_MarksTheMessageHandled()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = NewId();

        await inbox.CompleteMessageAsync(id, NoClaim, None);
        await inbox.CompleteMessageAsync(id, NoClaim, None);

        Assert.Equal(MessageClaim.Handled, (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome);
    }

    [StoreFact]
    public async Task ReleasedClaim_CanBeClaimedAtOnce()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = NewId();
        MessageClaimResult claim = await inbox.TryClaimMessageAsync(id, _lease, None);

        await inbox.ReleaseMessageAsync(id, claim.Token, None);

        Assert.Equal(MessageClaim.Claimed, (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome);
    }

    [StoreFact]
    public async Task Release_LeavesAHandledMessageHandled_AndIgnoresAnUnknownId()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string handled = NewId();
        await inbox.CompleteMessageAsync(handled, NoClaim, None);

        await inbox.ReleaseMessageAsync(handled, NoClaim, None);
        await inbox.ReleaseMessageAsync(NewId(), NoClaim, None);

        Assert.Equal(MessageClaim.Handled, (await inbox.TryClaimMessageAsync(handled, _lease, None)).Outcome);
    }

    [StoreFact]
    public async Task MessageIds_AreComparedExactly()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = NewId();
        await inbox.CompleteMessageAsync(id.ToUpperInvariant(), NoClaim, None);

        Assert.Equal(MessageClaim.Claimed, (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome);
    }

    [StoreFact]
    public async Task AnIdOfTheLongestLength_IsKept()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = (NewId() + new string('x', JobLimits.MaxKeyLength)).Substring(0, JobLimits.MaxKeyLength);

        await inbox.CompleteMessageAsync(id, NoClaim, None);

        Assert.Equal(MessageClaim.Handled, (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome);
    }

    [StoreFact]
    public async Task InvalidIds_AndLeases_AreRejected_BeforeAnythingIsStored()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string tooLong = new string('x', JobLimits.MaxKeyLength + 1);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await inbox.TryClaimMessageAsync(null!, _lease, None));
        foreach (string bad in new[] { string.Empty, tooLong, "a b", "a\0b", "a\nb" })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await inbox.TryClaimMessageAsync(bad, _lease, None));
            await Assert.ThrowsAsync<ArgumentException>(async () => await inbox.CompleteMessageAsync(bad, NoClaim, None));
            await Assert.ThrowsAsync<ArgumentException>(async () => await inbox.ReleaseMessageAsync(bad, NoClaim, None));
        }
        string id = NewId();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await inbox.TryClaimMessageAsync(id, TimeSpan.Zero, None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await inbox.TryClaimMessageAsync(id, TimeSpan.FromTicks(-1), None));

        Assert.Equal(MessageClaim.Claimed, (await inbox.TryClaimMessageAsync(id, TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond), None)).Outcome);
    }

    [StoreFact]
    public async Task StaleReleaseAndComplete_LeaveTheClaimThatTookOverHeld()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = NewId();
        MessageClaimResult a = await inbox.TryClaimMessageAsync(id, _lease, None);
        Clock.Advance(_lease);
        MessageClaimResult b = await inbox.TryClaimMessageAsync(id, _lease, None);

        await inbox.ReleaseMessageAsync(id, a.Token, None);
        await inbox.CompleteMessageAsync(id, a.Token, None);

        Assert.Equal(MessageClaim.Claimed, b.Outcome);
        Assert.Equal(MessageClaim.Busy, (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome);
    }

    [StoreFact]
    public async Task TheClaimThatTookOver_StillCompletes_AfterAStaleReleaseAndComplete()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = NewId();
        MessageClaimResult a = await inbox.TryClaimMessageAsync(id, _lease, None);
        Clock.Advance(_lease);
        MessageClaimResult b = await inbox.TryClaimMessageAsync(id, _lease, None);
        await inbox.ReleaseMessageAsync(id, a.Token, None);
        await inbox.CompleteMessageAsync(id, a.Token, None);

        await inbox.CompleteMessageAsync(id, b.Token, None);

        Assert.Equal(MessageClaim.Handled, (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome);
    }

    [StoreFact]
    public async Task TheClaimThatTookOver_StillReleases_AfterAStaleRelease()
    {
        IMessageInboxStore inbox = await CreateInboxAsync(Clock);
        string id = NewId();
        MessageClaimResult a = await inbox.TryClaimMessageAsync(id, _lease, None);
        Clock.Advance(_lease);
        MessageClaimResult b = await inbox.TryClaimMessageAsync(id, _lease, None);
        await inbox.ReleaseMessageAsync(id, a.Token, None);

        await inbox.ReleaseMessageAsync(id, b.Token, None);

        Assert.Equal(MessageClaim.Claimed, (await inbox.TryClaimMessageAsync(id, _lease, None)).Outcome);
    }

    private static string NewId() => "m-" + Guid.NewGuid().ToString("N");
}
