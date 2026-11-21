using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// The ends of the retry delay's range. <see cref="MessagingOptions.RetryDelay"/> is at most 24 hours,
/// but <see cref="MessagingOptions.MaxRetryDelay"/> has no upper limit and the delay doubles up to it,
/// so a failed message must still be settled, and never lost, however long the delay is.
/// </summary>
public sealed class RetryDelayLimitsTests
{
    private const string Failing = "{\"Id\":1,\"Note\":\"fail\"}";

    // A message delivered this many times before, with a 24-hour first delay, has doubled past every
    // delay below, so its next failure is retried after MaxRetryDelay.
    private const int DeliveredBefore = 29;

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
        await using MessagingHost host = MessagingHost.Create(options => Configure(options, delay), TransportCapabilities.NativeDeadLetter);

        await SendDeliveredBeforeAsync(host);
        await host.RunRoundsAsync(1);

        Assert.Equal(TimeSpan.Zero, Assert.Single(host.Transport.NackDelays));
        IReadOnlyList<ReceivedMessage> again = await host.Transport.ReceiveAsync(1, CancellationToken.None);
        Assert.Equal(DeliveredBefore + 2, Assert.Single(again).DeliveryCount);
    }

    [Theory]
    [MemberData(nameof(LongDelays))]
    public async Task OnATransportWithNativeDelay_AFailedMessageIsNackedWithItsDelay_HoweverLong(TimeSpan delay)
    {
        await using MessagingHost host = MessagingHost.Create(options => Configure(options, delay));

        await SendDeliveredBeforeAsync(host);
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

    private static void Configure(MessagingOptions options, TimeSpan maxRetryDelay)
    {
        options.RetryDelay = MessagingOptions.MaxBusyDelay;
        options.MaxRetryDelay = maxRetryDelay;
        options.MaxAttempts = DeliveredBefore + 2;
    }

    // Sends a failing message and puts it back unhandled until it has been delivered DeliveredBefore
    // times, through the inner transport so no nack is recorded.
    private static async Task SendDeliveredBeforeAsync(MessagingHost host)
    {
        InMemoryTransport transport = host.Transport.Inner;
        await transport.SendAsync(new[] { MessagingHost.Envelope("test.order-placed", Failing) }, CancellationToken.None);
        for (int i = 0; i < DeliveredBefore; i++)
        {
            ReceivedMessage delivery = Assert.Single(await transport.ReceiveAsync(1, CancellationToken.None));
            await transport.NackAsync(delivery, TimeSpan.Zero, CancellationToken.None);
        }
    }
}
