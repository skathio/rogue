using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// A transport that leaves <see cref="IMessageTransport.ServiceName"/> out names no service: the bus
/// stamps no origin on an event, and the consumer drops no event, whatever origin it carries.
/// </summary>
public sealed class NamelessTransportOriginTests
{
    [Fact]
    public async Task EventPublishedThroughThePublisher_CarriesNoOrigin_AndItsCopyIsHandled()
    {
        var transport = new NamelessTransport();
        await using MessagingHost host = MessagingHost.Create(services: services => services.AddSingleton<IMessageTransport>(transport));
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new OrderPlaced(3, "none"));
        }

        MessageEnvelope sent = Assert.Single(transport.Sent);
        await host.Consumer.RunOnceAsync();

        Assert.Null(((IMessageTransport)transport).ServiceName);
        Assert.Null(sent.Origin);
        Assert.Equal(new[] { "placed:3", "placed:3" }, host.Probe.Ran);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("in-memory")]
    public async Task AnEvent_IsHandled_WhateverOriginItCarries(string? origin)
    {
        var transport = new NamelessTransport();
        await using MessagingHost host = MessagingHost.Create(services: services => services.AddSingleton<IMessageTransport>(transport));
        MessageEnvelope envelope = MessagingHost.Envelope("test.order-placed", "{\"Id\":8,\"Note\":\"none\"}");
        envelope.Origin = origin;

        await transport.SendAsync(new[] { envelope }, CancellationToken.None);
        await host.Consumer.RunOnceAsync();

        Assert.Equal(new[] { "placed:8" }, host.Probe.Ran);
        Assert.Empty(transport.Inner.DeadLettered);
    }

    /// <summary>An in-memory transport that does not implement <see cref="IMessageTransport.ServiceName"/>, as a transport written before it existed.</summary>
    private sealed class NamelessTransport : IMessageTransport
    {
        private readonly List<MessageEnvelope> _sent = new List<MessageEnvelope>();

        public InMemoryTransport Inner { get; } = new InMemoryTransport(TimeProvider.System);

        public IReadOnlyList<MessageEnvelope> Sent => _sent.ToArray();

        public TransportCapabilities Capabilities => Inner.Capabilities;

        public ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            _sent.AddRange(messages);
            return Inner.SendAsync(messages, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken) => Inner.ReceiveAsync(maxCount, cancellationToken);

        public ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken) => Inner.AckAsync(message, cancellationToken);

        public ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken) => Inner.NackAsync(message, delay, cancellationToken);

        public ValueTask DeferAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken) => Inner.DeferAsync(message, delay, cancellationToken);

        public ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken) => Inner.DeadLetterAsync(message, reason, cancellationToken);
    }
}
