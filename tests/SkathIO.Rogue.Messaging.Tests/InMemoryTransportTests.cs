using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

public sealed class InMemoryTransportTests
{
    private static readonly CancellationToken _none = CancellationToken.None;

    [Fact]
    public async Task Messages_AreReceivedInTheOrderSent()
    {
        var transport = new InMemoryTransport(new StepClock());

        await transport.SendAsync(new[] { Named("a"), Named("b"), Named("c") }, _none);

        Assert.Equal(new[] { "a", "b", "c" }, (await transport.ReceiveAsync(10, _none)).Select(static m => m.Envelope.Name));
    }

    [Fact]
    public async Task NackedMessage_ComesBackWithItsDeliveryCountOneHigher()
    {
        var transport = new InMemoryTransport(new StepClock());
        await transport.SendAsync(new[] { Named("a") }, _none);
        ReceivedMessage first = Assert.Single(await transport.ReceiveAsync(1, _none));

        await transport.NackAsync(first, TimeSpan.Zero, _none);
        ReceivedMessage second = Assert.Single(await transport.ReceiveAsync(1, _none));

        Assert.Equal(1, first.DeliveryCount);
        Assert.Equal(2, second.DeliveryCount);
        Assert.Same(first.Envelope, second.Envelope);
    }

    [Fact]
    public async Task DeferredMessage_ComesBackAfterItsDelay_WithTheSameDeliveryCount()
    {
        var clock = new StepClock();
        var transport = new InMemoryTransport(clock);
        await transport.SendAsync(new[] { Named("a") }, _none);
        ReceivedMessage first = Assert.Single(await transport.ReceiveAsync(1, _none));
        await transport.NackAsync(first, TimeSpan.Zero, _none);
        ReceivedMessage second = Assert.Single(await transport.ReceiveAsync(1, _none));

        await transport.DeferAsync(second, TimeSpan.FromSeconds(5), _none);
        IReadOnlyList<ReceivedMessage> early = await transport.ReceiveAsync(1, _none);
        clock.Advance(TimeSpan.FromSeconds(5));
        ReceivedMessage third = Assert.Single(await transport.ReceiveAsync(1, _none));

        Assert.Empty(early);
        Assert.Equal(2, third.DeliveryCount);
        Assert.Same(first.Envelope, third.Envelope);
    }

    [Fact]
    public async Task SettlingTwice_IsRefused()
    {
        var transport = new InMemoryTransport(new StepClock());
        await transport.SendAsync(new[] { Named("a") }, _none);
        ReceivedMessage delivery = Assert.Single(await transport.ReceiveAsync(1, _none));
        await transport.AckAsync(delivery, _none);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.AckAsync(delivery, _none));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.NackAsync(delivery, TimeSpan.Zero, _none));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.DeferAsync(delivery, TimeSpan.Zero, _none));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.DeadLetterAsync(delivery, "x", _none));
    }

    [Fact]
    public async Task SettlingADeliveryOfAnotherTransport_IsRefused()
    {
        var other = new InMemoryTransport(new StepClock());
        await other.SendAsync(new[] { Named("a") }, _none);
        ReceivedMessage foreign = Assert.Single(await other.ReceiveAsync(1, _none));
        var transport = new InMemoryTransport(new StepClock());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.AckAsync(foreign, _none));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.AckAsync(new ReceivedMessage(Named("a"), 1), _none));
    }

    [Fact]
    public async Task InvalidArguments_AreRefused()
    {
        var transport = new InMemoryTransport(new StepClock());
        await transport.SendAsync(new[] { Named("a") }, _none);
        ReceivedMessage delivery = Assert.Single(await transport.ReceiveAsync(1, _none));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await transport.ReceiveAsync(0, _none));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await transport.NackAsync(delivery, TimeSpan.FromTicks(-1), _none));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await transport.DeferAsync(delivery, TimeSpan.FromTicks(-1), _none));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await transport.DeadLetterAsync(delivery, null!, _none));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await transport.SendAsync(new MessageEnvelope[] { null! }, _none));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await transport.SendAsync(null!, _none));
    }

    [Fact]
    public void ReceivedMessage_NeedsADeliveryCountOfAtLeastOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReceivedMessage(Named("a"), 0));
        Assert.Equal(1, new ReceivedMessage(Named("a"), 1).DeliveryCount);
    }

    [Fact]
    public void Capabilities_AreNativeDelayDeadLetterAndBatchSend()
    {
        Assert.Equal(TransportCapabilities.NativeDelay | TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend, new InMemoryTransport().Capabilities);
    }

    [Fact]
    public void NewEnvelopes_GetDistinctIds()
    {
        Assert.NotEqual(new MessageEnvelope().Id, new MessageEnvelope().Id);
        Assert.Equal(MessageEnvelope.JsonContentType, new MessageEnvelope().ContentType);
    }

    private static MessageEnvelope Named(string name) => new MessageEnvelope { Name = name };
}
