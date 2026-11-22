using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Messaging.Outbox.Tests;

/// <summary>
/// Releasing or completing an inbox claim acts only on the claim the caller holds. A claim returns a
/// token, and release and complete take it back and change nothing once another delivery has taken the
/// id over after the first lease ran out. Checked on the in-memory store directly, and through the
/// consumer's <see cref="IMessageInbox"/> that the job store keeps. What the token holds is the
/// store's choice, so the tests only hand it back.
/// </summary>
public sealed class InboxClaimTokenTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    private const string Name = "outbox.order-placed";

    private static CancellationToken None => CancellationToken.None;

    [Fact]
    public async Task Store_AStaleRelease_LeavesTheClaimThatTookOverHeld()
    {
        var clock = NewClock();
        IMessageInboxStore store = new InMemoryJobStore(clock);
        string id = NewId();
        var a = await store.TryClaimMessageAsync(id, Lease, None);

        clock.Advance(Lease);
        var b = await store.TryClaimMessageAsync(id, Lease, None);
        await store.ReleaseMessageAsync(id, a.Token, None);
        var third = await store.TryClaimMessageAsync(id, Lease, None);

        Assert.Equal(MessageClaim.Claimed, a.Outcome);
        Assert.Equal(MessageClaim.Claimed, b.Outcome);
        Assert.Equal(MessageClaim.Busy, third.Outcome);
    }

    [Fact]
    public async Task Store_AStaleComplete_NeitherMarksTheMessageHandled_NorDropsTheClaimThatTookOver()
    {
        var clock = NewClock();
        IMessageInboxStore store = new InMemoryJobStore(clock);
        string id = NewId();
        var a = await store.TryClaimMessageAsync(id, Lease, None);

        clock.Advance(Lease);
        var b = await store.TryClaimMessageAsync(id, Lease, None);
        await store.CompleteMessageAsync(id, a.Token, None);
        var third = await store.TryClaimMessageAsync(id, Lease, None);

        Assert.Equal(MessageClaim.Claimed, b.Outcome);
        Assert.Equal(MessageClaim.Busy, third.Outcome);
    }

    [Fact]
    public async Task Store_TheClaimThatTookOver_StillCompletes_AfterAStaleReleaseAndComplete()
    {
        var clock = NewClock();
        IMessageInboxStore store = new InMemoryJobStore(clock);
        string id = NewId();
        var a = await store.TryClaimMessageAsync(id, Lease, None);
        clock.Advance(Lease);
        var b = await store.TryClaimMessageAsync(id, Lease, None);
        await store.ReleaseMessageAsync(id, a.Token, None);
        await store.CompleteMessageAsync(id, a.Token, None);

        await store.CompleteMessageAsync(id, b.Token, None);

        Assert.Equal(MessageClaim.Handled, (await store.TryClaimMessageAsync(id, Lease, None)).Outcome);
    }

    [Fact]
    public async Task Store_TheClaimThatTookOver_StillReleases_AfterAStaleRelease()
    {
        var clock = NewClock();
        IMessageInboxStore store = new InMemoryJobStore(clock);
        string id = NewId();
        var a = await store.TryClaimMessageAsync(id, Lease, None);
        clock.Advance(Lease);
        var b = await store.TryClaimMessageAsync(id, Lease, None);
        await store.ReleaseMessageAsync(id, a.Token, None);

        await store.ReleaseMessageAsync(id, b.Token, None);

        Assert.Equal(MessageClaim.Claimed, (await store.TryClaimMessageAsync(id, Lease, None)).Outcome);
    }

    [Fact]
    public async Task Store_TheHolder_ReleasesItsClaim_UpToTheLastMomentOfItsLease()
    {
        var clock = NewClock();
        IMessageInboxStore store = new InMemoryJobStore(clock);
        string id = NewId();
        var a = await store.TryClaimMessageAsync(id, Lease, None);

        clock.Advance(Lease - TimeSpan.FromTicks(1));
        await store.ReleaseMessageAsync(id, a.Token, None);

        Assert.Equal(MessageClaim.Claimed, (await store.TryClaimMessageAsync(id, Lease, None)).Outcome);
    }

    [Fact]
    public async Task Store_TheHolder_CompletesItsClaim_UpToTheLastMomentOfItsLease()
    {
        var clock = NewClock();
        IMessageInboxStore store = new InMemoryJobStore(clock);
        string id = NewId();
        var a = await store.TryClaimMessageAsync(id, Lease, None);

        clock.Advance(Lease - TimeSpan.FromTicks(1));
        await store.CompleteMessageAsync(id, a.Token, None);

        Assert.Equal(MessageClaim.Handled, (await store.TryClaimMessageAsync(id, Lease, None)).Outcome);
    }

    [Fact]
    public async Task Store_ReleaseAndComplete_StillRejectInvalidIds()
    {
        var clock = NewClock();
        IMessageInboxStore store = new InMemoryJobStore(clock);
        var claim = await store.TryClaimMessageAsync(NewId(), Lease, None);
        string tooLong = new string('x', JobLimits.MaxKeyLength + 1);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.ReleaseMessageAsync(null!, claim.Token, None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.CompleteMessageAsync(null!, claim.Token, None));
        foreach (string bad in new[] { string.Empty, tooLong, "a b" })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.ReleaseMessageAsync(bad, claim.Token, None));
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.CompleteMessageAsync(bad, claim.Token, None));
        }
    }

    [Fact]
    public async Task Inbox_AStaleReleaseAndComplete_LeaveTheClaimThatTookOverHeld()
    {
        await using OutboxHost host = OutboxHost.Create(configure: o => o.InboxLease = Lease);
        var clock = (ManualTimeProvider)host.Services.GetRequiredService<TimeProvider>();
        IMessageInbox inbox = host.Services.GetRequiredService<IMessageInbox>();
        string id = NewId();
        var a = await inbox.TryClaimAsync(Name, id, None);

        clock.Advance(Lease);
        var b = await inbox.TryClaimAsync(Name, id, None);
        await inbox.ReleaseAsync(Name, id, a.Token, None);
        await inbox.CompleteAsync(Name, id, a.Token, None);
        var third = await inbox.TryClaimAsync(Name, id, None);

        Assert.Equal(InboxClaim.Claimed, a.Outcome);
        Assert.Equal(InboxClaim.Claimed, b.Outcome);
        Assert.Equal(InboxClaim.Busy, third.Outcome);
    }

    [Fact]
    public async Task Inbox_TheClaimThatTookOver_StillCompletes_AfterAStaleRelease()
    {
        await using OutboxHost host = OutboxHost.Create(configure: o => o.InboxLease = Lease);
        var clock = (ManualTimeProvider)host.Services.GetRequiredService<TimeProvider>();
        IMessageInbox inbox = host.Services.GetRequiredService<IMessageInbox>();
        string id = NewId();
        var a = await inbox.TryClaimAsync(Name, id, None);
        clock.Advance(Lease);
        var b = await inbox.TryClaimAsync(Name, id, None);
        await inbox.ReleaseAsync(Name, id, a.Token, None);

        await inbox.CompleteAsync(Name, id, b.Token, None);

        Assert.Equal(InboxClaim.Handled, (await inbox.TryClaimAsync(Name, id, None)).Outcome);
    }

    [Fact]
    public async Task Inbox_TheHolder_ReleasesItsClaim_SoTheNextDeliveryClaimsAtOnce()
    {
        await using OutboxHost host = OutboxHost.Create(configure: o => o.InboxLease = Lease);
        IMessageInbox inbox = host.Services.GetRequiredService<IMessageInbox>();
        string id = NewId();
        var a = await inbox.TryClaimAsync(Name, id, None);

        await inbox.ReleaseAsync(Name, id, a.Token, None);

        Assert.Equal(InboxClaim.Claimed, (await inbox.TryClaimAsync(Name, id, None)).Outcome);
    }

    private static ManualTimeProvider NewClock() => new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static string NewId() => "m-" + Guid.NewGuid().ToString("N");
}
