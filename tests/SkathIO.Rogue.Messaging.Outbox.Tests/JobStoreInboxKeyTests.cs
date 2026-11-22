using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Outbox.Tests;

/// <summary>The job store's inbox keeps message ids apart per message name, whatever their length.</summary>
public sealed class JobStoreInboxKeyTests
{
    [Fact]
    public async Task AnIdHandledUnderOneName_DoesNotMakeAMessageOfAnotherNameADuplicate()
    {
        await using OutboxHost host = OutboxHost.Create();
        IMessageInbox inbox = host.Services.GetRequiredService<IMessageInbox>();

        InboxClaimResult first = await inbox.TryClaimAsync("outbox.order-placed", "x", CancellationToken.None);
        await inbox.CompleteAsync("outbox.order-placed", "x", first.Token, CancellationToken.None);
        InboxClaim otherName = (await inbox.TryClaimAsync("outbox.refund", "x", CancellationToken.None)).Outcome;
        InboxClaim sameName = (await inbox.TryClaimAsync("outbox.order-placed", "x", CancellationToken.None)).Outcome;

        Assert.Equal(InboxClaim.Claimed, first.Outcome);
        Assert.Equal(InboxClaim.Claimed, otherName);
        Assert.Equal(InboxClaim.Handled, sameName);
    }

    [Fact]
    public async Task ANameAndIdLongerThanTheStoreKeeps_AreStillKeptApart()
    {
        await using OutboxHost host = OutboxHost.Create();
        IMessageInbox inbox = host.Services.GetRequiredService<IMessageInbox>();
        string name = new string('n', MessageLimits.MaxNameLength);
        string id = new string('i', MessageLimits.MaxIdLength);

        InboxClaimResult first = await inbox.TryClaimAsync(name, id, CancellationToken.None);
        await inbox.CompleteAsync(name, id, first.Token, CancellationToken.None);
        InboxClaim again = (await inbox.TryClaimAsync(name, id, CancellationToken.None)).Outcome;
        InboxClaimResult otherId = await inbox.TryClaimAsync(name, id.Substring(1) + "j", CancellationToken.None);
        await inbox.ReleaseAsync(name, id.Substring(1) + "j", otherId.Token, CancellationToken.None);

        Assert.Equal(InboxClaim.Claimed, first.Outcome);
        Assert.Equal(InboxClaim.Handled, again);
        Assert.Equal(InboxClaim.Claimed, otherId.Outcome);
    }

    [Fact]
    public async Task AKeyOfExactlyTheStoresLength_IsKeptAsItIs_AndOneOver_IsHashed()
    {
        await using OutboxHost host = OutboxHost.Create();
        IMessageInbox inbox = host.Services.GetRequiredService<IMessageInbox>();
        // "a/" plus the id: at the store's limit, then one past it.
        string atLimit = new string('i', MessageLimits.MaxIdLength - 2);
        string pastLimit = atLimit + "i";

        InboxClaim kept = (await inbox.TryClaimAsync("a", atLimit, CancellationToken.None)).Outcome;
        InboxClaim hashed = (await inbox.TryClaimAsync("a", pastLimit, CancellationToken.None)).Outcome;
        InboxClaim heldAsItIs = (await inbox.TryClaimAsync("a/" + atLimit, CancellationToken.None)).Outcome;

        Assert.Equal(InboxClaim.Claimed, kept);
        Assert.Equal(InboxClaim.Claimed, hashed);
        Assert.Equal(InboxClaim.Busy, heldAsItIs);
    }
}
