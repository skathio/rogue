using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// The places a consumer gives deliveries that wait out a delay before they are put back (half the
/// transport's unsettled window) are never lost: a put-back the transport refuses still frees its
/// place, so later failed or busy deliveries can wait instead of being put back at once. A window the
/// transport reports as zero or less lets nothing wait, rather than letting everything wait.
/// </summary>
public sealed class WaitPlaceTests
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ABusyDelivery_WhosePutBackIsRefused_GivesUpItsPlace_SoTheNextOneStillWaits()
    {
        // A window of two leaves one place to wait in.
        await using WindowHost host = WindowHost.Create(window: 2, busyInbox: true);
        host.Transport.RefusePutBacks = 1;

        await host.DeliverAsync(Message("m-1", "ok"));
        Assert.Single(host.Clock.Waited);
        Assert.Equal(0, host.Transport.RefusePutBacks);

        await host.DeliverAsync(Message("m-2", "ok"));
        Assert.Equal(new[] { _retryDelay, _retryDelay }, host.Clock.Waited.ToArray());
        Assert.Equal(new[] { TimeSpan.Zero }, host.Transport.Deferred.ToArray());
    }

    [Fact]
    public async Task AFailedDelivery_WhosePutBackIsRefused_GivesUpItsPlace_SoTheNextOneStillWaits()
    {
        await using WindowHost host = WindowHost.Create(window: 2, busyInbox: false);
        host.Transport.RefusePutBacks = 1;

        await host.DeliverAsync(Message("m-1", "fail"));
        Assert.Single(host.Clock.Waited);
        Assert.Equal(0, host.Transport.RefusePutBacks);

        await host.DeliverAsync(Message("m-2", "fail"));
        Assert.Equal(new[] { _retryDelay, _retryDelay }, host.Clock.Waited.ToArray());
        Assert.Equal(new[] { TimeSpan.Zero }, host.Transport.Nacked.ToArray());
    }

    [Fact]
    public async Task ManyRefusedPutBacks_InARow_LeaveThePlaceFree()
    {
        await using WindowHost host = WindowHost.Create(window: 2, busyInbox: true);
        host.Transport.RefusePutBacks = 5;

        for (int i = 0; i < 5; i++)
        {
            await host.DeliverAsync(Message("m-" + i, "ok"));
        }
        await host.DeliverAsync(Message("m-last", "ok"));

        Assert.Equal(6, host.Clock.Waited.Count);
        Assert.Equal(new[] { TimeSpan.Zero }, host.Transport.Deferred.ToArray());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(-1, true)]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, true)]
    public async Task AWindowOfZeroOrLess_LetsNothingWait_AndPutsTheDeliveryBackAtOnce(int window, bool busyInbox)
    {
        await using WindowHost host = WindowHost.Create(window, busyInbox);

        await host.DeliverAsync(Message("m-1", busyInbox ? "ok" : "fail"));

        Assert.Empty(host.Clock.Waited);
        Assert.Equal(new[] { TimeSpan.Zero }, (busyInbox ? host.Transport.Deferred : host.Transport.Nacked).ToArray());
        ReceivedMessage back = Assert.Single(await host.Transport.ReceiveAsync(2, CancellationToken.None));
        Assert.Equal(busyInbox ? 1 : 2, back.DeliveryCount);
    }

    private static MessageEnvelope Message(string id, string note)
    {
        MessageEnvelope envelope = MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"" + note + "\"}");
        envelope.Id = id;
        return envelope;
    }

    private sealed class AlwaysBusyInbox : IMessageInbox
    {
        public ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken)
            => new ValueTask<InboxClaimResult>(new InboxClaimResult(InboxClaim.Busy, 0));

        public ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;

        public ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;
    }

    /// <summary>A consumer over a windowed transport with no native delay, on a clock whose timers fire at once.</summary>
    private sealed class WindowHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private WindowHost(ServiceProvider services, StepClock clock, RefusingTransport transport)
        {
            _services = services;
            Clock = clock;
            Transport = transport;
        }

        public StepClock Clock { get; }

        public RefusingTransport Transport { get; }

        public static WindowHost Create(int window, bool busyInbox)
        {
            var clock = new StepClock();
            var transport = new RefusingTransport(new InMemoryTransport(clock), window);
            var collection = new ServiceCollection();
            collection.AddSingleton<TimeProvider>(clock);
            collection.AddSingleton<MessageProbe>();
            collection.AddSingleton<IMessageTransport>(transport);
            if (busyInbox)
            {
                collection.AddSingleton<IMessageInbox>(new AlwaysBusyInbox());
            }
            collection.AddRogue();
            collection.AddRogueMessaging(options =>
            {
                options.SerializerContext = TestMessagesJson.Default;
                options.RetryDelay = _retryDelay;
                options.MaxRetryDelay = _retryDelay;
                options.MaxAttempts = 3;
            });
            return new WindowHost(collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), clock, transport);
        }

        /// <summary>Sends the message and runs the consumer once over it.</summary>
        public async Task DeliverAsync(MessageEnvelope envelope)
        {
            await Transport.SendAsync(new[] { envelope }, CancellationToken.None);
            await _services.GetRequiredService<MessageConsumer>().RunOnceAsync();
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    /// <summary>
    /// An in-memory transport with no native delay that reports an unsettled window, records the delay of
    /// each put-back it takes, and refuses the next <see cref="RefusePutBacks"/> nacks or deferrals.
    /// </summary>
    private sealed class RefusingTransport : IMessageTransport
    {
        private readonly InMemoryTransport _inner;
        private int _refusePutBacks;

        public RefusingTransport(InMemoryTransport inner, int window)
        {
            _inner = inner;
            UnsettledLimit = window;
        }

        public TransportCapabilities Capabilities => TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend;

        public int? UnsettledLimit { get; }

        public int RefusePutBacks
        {
            get => Volatile.Read(ref _refusePutBacks);
            set => Volatile.Write(ref _refusePutBacks, value);
        }

        public List<TimeSpan> Nacked { get; } = new List<TimeSpan>();

        public List<TimeSpan> Deferred { get; } = new List<TimeSpan>();

        public ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => _inner.SendAsync(messages, cancellationToken);

        public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken) => _inner.ReceiveAsync(maxCount, cancellationToken);

        public ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken) => _inner.AckAsync(message, cancellationToken);

        public ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
        {
            RefuseIfAsked();
            lock (Nacked)
            {
                Nacked.Add(delay);
            }
            return _inner.NackAsync(message, delay, cancellationToken);
        }

        public ValueTask DeferAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
        {
            RefuseIfAsked();
            lock (Deferred)
            {
                Deferred.Add(delay);
            }
            return _inner.DeferAsync(message, delay, cancellationToken);
        }

        public ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken) => _inner.DeadLetterAsync(message, reason, cancellationToken);

        private void RefuseIfAsked()
        {
            if (Interlocked.Decrement(ref _refusePutBacks) >= 0)
            {
                throw new InvalidOperationException("The broker refused the put-back.");
            }
            Interlocked.Increment(ref _refusePutBacks);
        }
    }
}
