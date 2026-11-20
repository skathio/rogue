using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// What a sender cannot steer through the consumer: the telemetry series it adds, and which message
/// an id it chose makes a duplicate.
/// </summary>
public sealed class ConsumerHardeningTests
{
    [Fact]
    public async Task UnknownNames_ShareOneFixedNameTag_SoASenderCannotAddATelemetrySeriesPerName()
    {
        string first = "unknown-" + Guid.NewGuid().ToString("N");
        string second = "unknown-" + Guid.NewGuid().ToString("N");
        var nameTags = new ConcurrentQueue<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RogueMessagingTelemetry.Name && instrument.Name == "rogue.messaging.consumed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "rogue.message.name")
                {
                    nameTags.Enqueue(tag.Value as string);
                }
            }
        });
        listener.Start();
        await using MessagingHost host = MessagingHost.Create();

        await host.DeliverAsync(MessagingHost.Envelope(first, "{}"), MessagingHost.Envelope(second, "{}"));

        Assert.Equal(2, host.DeadLettered.Count);
        Assert.DoesNotContain(first, nameTags);
        Assert.DoesNotContain(second, nameTags);
        Assert.Contains("(unknown)", nameTags);
    }

    [Fact]
    public async Task TheInbox_IsAskedByNameAndId_SoAnIdHandledUnderOneNameDoesNotSkipAnother()
    {
        var inbox = new PerNameInbox();
        await using MessagingHost host = MessagingHost.Create(services: s => s.AddSingleton<IMessageInbox>(inbox));
        MessageEnvelope placed = MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"n\"}");
        placed.Id = "shared-id";
        MessageEnvelope reserve = MessagingHost.Envelope("test.reserve", "{\"Id\":2}");
        reserve.Id = "shared-id";

        await host.DeliverAsync(placed);
        await host.DeliverAsync(reserve);

        Assert.Equal(new[] { "placed:1", "reserve:2" }, host.Probe.Ran);
        Assert.Equal(new[] { "claim:test.order-placed/shared-id", "complete:test.order-placed/shared-id", "claim:test.reserve/shared-id", "complete:test.reserve/shared-id" }, inbox.Calls);
    }

    [Fact]
    public async Task TheSameNameAndId_IsStillADuplicate()
    {
        var inbox = new PerNameInbox();
        await using MessagingHost host = MessagingHost.Create(services: s => s.AddSingleton<IMessageInbox>(inbox));
        MessageEnvelope placed = MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"n\"}");
        placed.Id = "m-1";

        await host.DeliverAsync(placed);
        await host.DeliverAsync(placed);

        Assert.Equal(new[] { "placed:1" }, host.Probe.Ran);
        Assert.Empty(host.DeadLettered);
    }

    /// <summary>
    /// An inbox that keeps ids per name when asked by name, and by id alone otherwise, as an inbox that
    /// predates the named calls does.
    /// </summary>
    private sealed class PerNameInbox : IMessageInbox
    {
        private readonly HashSet<string> _handled = new HashSet<string>(StringComparer.Ordinal);

        public ConcurrentQueue<string> Calls { get; } = new ConcurrentQueue<string>();

        public ValueTask<InboxClaim> TryClaimAsync(string messageId, CancellationToken cancellationToken)
        {
            Calls.Enqueue("claim:" + messageId);
            return new ValueTask<InboxClaim>(_handled.Contains(messageId) ? InboxClaim.Handled : InboxClaim.Claimed);
        }

        public ValueTask CompleteAsync(string messageId, CancellationToken cancellationToken)
        {
            Calls.Enqueue("complete:" + messageId);
            _handled.Add(messageId);
            return default;
        }

        public ValueTask ReleaseAsync(string messageId, CancellationToken cancellationToken)
        {
            Calls.Enqueue("release:" + messageId);
            return default;
        }

        public ValueTask<InboxClaim> TryClaimAsync(string messageName, string messageId, CancellationToken cancellationToken)
            => TryClaimAsync(messageName + "/" + messageId, cancellationToken);

        public ValueTask CompleteAsync(string messageName, string messageId, CancellationToken cancellationToken)
            => CompleteAsync(messageName + "/" + messageId, cancellationToken);

        public ValueTask ReleaseAsync(string messageName, string messageId, CancellationToken cancellationToken)
            => ReleaseAsync(messageName + "/" + messageId, cancellationToken);
    }
}
