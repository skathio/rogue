using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// The ends of the retry delay's range. <see cref="MessagingOptions.RetryDelay"/> and
/// <see cref="MessagingOptions.MaxRetryDelay"/> have no upper limit, so a failed message must still be
/// settled, and never lost, however long the delay is.
/// </summary>
public sealed class RetryDelayLimitsTests
{
    private const string Failing = "{\"Id\":1,\"Note\":\"fail\"}";

    public static TheoryData<TimeSpan> LongDelays() => new TheoryData<TimeSpan>
    {
        TimeSpan.FromDays(49),
        TimeSpan.FromDays(50),
        TimeSpan.MaxValue,
    };

    [Theory]
    [MemberData(nameof(LongDelays))]
    public async Task OnATransportWithoutNativeDelay_AFailedMessageIsNacked_HoweverLongItsRetryDelay(TimeSpan delay)
    {
        await using MessagingHost host = MessagingHost.Create(
            options =>
            {
                options.RetryDelay = delay;
                options.MaxRetryDelay = delay;
            },
            TransportCapabilities.NativeDeadLetter);

        await host.DeliverAsync(MessagingHost.Envelope("test.order-placed", Failing));

        Assert.Equal(TimeSpan.Zero, Assert.Single(host.Transport.NackDelays));
        IReadOnlyList<ReceivedMessage> again = await host.Transport.ReceiveAsync(1, CancellationToken.None);
        Assert.Equal(2, Assert.Single(again).DeliveryCount);
    }

    [Theory]
    [MemberData(nameof(LongDelays))]
    public async Task OnATransportWithNativeDelay_AFailedMessageIsNackedWithItsDelay_HoweverLong(TimeSpan delay)
    {
        await using MessagingHost host = MessagingHost.Create(options =>
        {
            options.RetryDelay = delay;
            options.MaxRetryDelay = delay;
        });

        await host.Transport.SendAsync(new[] { MessagingHost.Envelope("test.order-placed", Failing) }, CancellationToken.None);
        await host.Consumer.RunOnceAsync();

        Assert.Equal(delay, Assert.Single(host.Transport.NackDelays));
        Assert.Empty(host.DeadLettered);
    }

    [Fact]
    public async Task InMemoryTransport_HoldsAMessageNackedWithTheLongestDelay_AndDoesNotLoseIt()
    {
        var clock = new StepClock();
        var transport = new InMemoryTransport(clock);
        await transport.SendAsync(new[] { MessagingHost.Envelope("test.order-placed", Failing) }, CancellationToken.None);
        ReceivedMessage delivery = Assert.Single(await transport.ReceiveAsync(1, CancellationToken.None));

        await transport.NackAsync(delivery, TimeSpan.MaxValue, CancellationToken.None);

        // Settled once: the delivery cannot be settled again, and the message is held, not ready.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.AckAsync(delivery, CancellationToken.None));
        Assert.Empty(await transport.ReceiveAsync(1, CancellationToken.None));
        clock.Advance(TimeSpan.FromDays(365));
        Assert.Empty(await transport.ReceiveAsync(1, CancellationToken.None));
    }
}
