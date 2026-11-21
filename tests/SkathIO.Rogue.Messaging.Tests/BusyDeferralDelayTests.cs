using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// How long a delivery the inbox turns away as busy stays away: <see cref="MessagingOptions.RetryDelay"/>,
/// not doubled, or <see cref="MessagingOptions.PollInterval"/> when no retry delay is set, so duplicates
/// of one id never come round again at the transport's speed. A transport with native delay is given the
/// delay; without it the consumer waits the delay out and puts the delivery back with none left.
/// </summary>
public sealed class BusyDeferralDelayTests
{
    private const TransportCapabilities WithNativeDelay = TransportCapabilities.NativeDelay | TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend;
    private const TransportCapabilities WithoutNativeDelay = TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend;

    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public async Task WithNativeDelay_ABusyDelivery_IsDeferredForTheRetryDelay_OrThePollIntervalWhenThereIsNone(int retrySeconds, int expectedSeconds)
    {
        await using MessagingHost host = Host(retrySeconds, WithNativeDelay);

        await host.Transport.SendAsync(new[] { Message() }, CancellationToken.None);
        await host.Consumer.RunOnceAsync();

        Assert.Equal(new[] { TimeSpan.FromSeconds(expectedSeconds) }, host.Transport.DeferDelays.ToArray());
        Assert.Empty(host.Clock.Waited);
        Assert.Empty(await host.DrainAsync());
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public async Task WithoutNativeDelay_ABusyDelivery_WaitsTheRetryDelayHere_OrThePollIntervalWhenThereIsNone(int retrySeconds, int expectedSeconds)
    {
        await using MessagingHost host = Host(retrySeconds, WithoutNativeDelay);

        await host.Transport.SendAsync(new[] { Message() }, CancellationToken.None);
        await host.Consumer.RunOnceAsync();

        Assert.Equal(new[] { TimeSpan.FromSeconds(expectedSeconds) }, host.Clock.Waited.ToArray());
        Assert.Equal(new[] { TimeSpan.Zero }, host.Transport.DeferDelays.ToArray());
    }

    [Theory]
    [InlineData(WithNativeDelay)]
    [InlineData(WithoutNativeDelay)]
    public async Task ABusyDelivery_PutBackManyTimes_StaysAwayTheSameDelayEachTime_AndKeepsItsCount(TransportCapabilities capabilities)
    {
        await using MessagingHost host = Host(retrySeconds: 2, capabilities);

        await host.Transport.SendAsync(new[] { Message() }, CancellationToken.None);
        await host.RunRoundsAsync(4);

        bool native = (capabilities & TransportCapabilities.NativeDelay) != 0;
        TimeSpan[] stayedAway = native ? host.Transport.DeferDelays.ToArray() : host.Clock.Waited.ToArray();
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(2), 4).ToArray(), stayedAway);
        Assert.Empty(host.Transport.NackDelays);
        Assert.Empty(host.Probe.Ran);
        Assert.Empty(host.DeadLettered);
        ReceivedMessage back = Assert.Single(await host.Transport.ReceiveAsync(2, CancellationToken.None));
        Assert.Equal(1, back.DeliveryCount);
    }

    private static MessagingHost Host(int retrySeconds, TransportCapabilities capabilities)
        => MessagingHost.Create(
            options =>
            {
                options.RetryDelay = TimeSpan.FromSeconds(retrySeconds);
                options.PollInterval = _pollInterval;
                options.MaxAttempts = 2;
            },
            capabilities,
            services: s => s.AddSingleton<IMessageInbox>(new AlwaysBusyInbox()));

    private static MessageEnvelope Message()
    {
        MessageEnvelope envelope = MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"n\"}");
        envelope.Id = "m-1";
        return envelope;
    }

    private sealed class AlwaysBusyInbox : IMessageInbox
    {
        public ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken)
            => new ValueTask<InboxClaimResult>(new InboxClaimResult(InboxClaim.Busy, 0));

        public ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;

        public ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;
    }
}
