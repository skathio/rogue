using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// The consumer hands each delivery's claim token back to the inbox when it completes or releases the
/// claim, so an inbox can tell that claim apart from one another delivery took over after its lease ran out.
/// </summary>
public sealed class ConsumerClaimTokenTests
{
    [Fact]
    public async Task HandledMessage_IsCompleted_WithTheTokenItsClaimReturned()
    {
        var inbox = new TokenInbox();
        await using MessagingHost host = Host(inbox);
        MessageEnvelope placed = MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"n\"}");
        placed.Id = "m-1";

        await host.DeliverAsync(placed);

        Assert.Equal(new[] { "claim:m-1=1", "complete:m-1=1" }, inbox.Calls);
    }

    [Fact]
    public async Task EachFailedDelivery_ReleasesItsOwnClaim_WithItsOwnToken()
    {
        var inbox = new TokenInbox();
        await using MessagingHost host = Host(inbox);
        MessageEnvelope failing = MessagingHost.Envelope("test.order-placed", "{\"Id\":2,\"Note\":\"fail\"}");
        failing.Id = "m-2";

        await host.DeliverAsync(failing);
        await host.RunRoundsAsync(1);

        Assert.Equal(new[] { "claim:m-2=1", "release:m-2=1", "claim:m-2=2", "release:m-2=2" }, inbox.Calls);
    }

    private static MessagingHost Host(TokenInbox inbox)
        => MessagingHost.Create(services: s => s.AddSingleton<IMessageInbox>(inbox));

    /// <summary>Claims every id with a new token each time, and records the token each call carries.</summary>
    private sealed class TokenInbox : IMessageInbox
    {
        private long _lastToken;

        public ConcurrentQueue<string> Calls { get; } = new ConcurrentQueue<string>();

        public ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken)
        {
            long token = Interlocked.Increment(ref _lastToken);
            Calls.Enqueue("claim:" + messageId + "=" + token);
            return new ValueTask<InboxClaimResult>(new InboxClaimResult(InboxClaim.Claimed, token));
        }

        public ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken)
        {
            Calls.Enqueue("complete:" + messageId + "=" + claimToken);
            return default;
        }

        public ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken)
        {
            Calls.Enqueue("release:" + messageId + "=" + claimToken);
            return default;
        }
    }
}
