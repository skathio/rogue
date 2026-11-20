using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>The consumer with an inbox: each message id runs its handlers once.</summary>
public sealed class MessageInboxTests
{
    private const string PlacedJson = "{\"Id\":1,\"Note\":\"n\"}";

    [Fact]
    public async Task FirstDelivery_RunsTheHandlers_AndIsRecordedHandled()
    {
        var inbox = new RecordingInbox();
        await using MessagingHost host = Host(inbox);

        await host.DeliverAsync(Placed("m-1"));

        Assert.Equal(new[] { "placed:1" }, host.Probe.Ran);
        Assert.Equal(new[] { "claim:m-1", "complete:m-1" }, inbox.Calls);
        Assert.Empty(host.Transport.NackDelays);
        Assert.Empty(host.DeadLettered);
    }

    [Fact]
    public async Task DuplicateDelivery_IsAcked_WithoutRunningTheHandlers()
    {
        var inbox = new RecordingInbox();
        await using MessagingHost host = Host(inbox);

        await host.DeliverAsync(Placed("m-1"));
        await host.DeliverAsync(Placed("m-1"));

        Assert.Equal(new[] { "placed:1" }, host.Probe.Ran);
        Assert.Equal(new[] { "claim:m-1", "complete:m-1", "claim:m-1" }, inbox.Calls);
        Assert.Empty(host.Transport.NackDelays);
        Assert.Empty(host.DeadLettered);
        Assert.Empty(await host.DrainAsync());
    }

    [Fact]
    public async Task MessageWhoseIdAnotherDeliveryHolds_IsPutBackForTheRetryDelay_Unhandled()
    {
        var inbox = new RecordingInbox();
        inbox.Busy.Add("m-1");
        await using MessagingHost host = Host(inbox, o => o.RetryDelay = TimeSpan.FromSeconds(7));

        await host.Transport.SendAsync(new[] { Placed("m-1") }, CancellationToken.None);
        await host.Consumer.RunOnceAsync();

        Assert.Empty(host.Probe.Ran);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, host.Transport.DeferDelays);
        Assert.Empty(host.Transport.NackDelays);
        Assert.Equal(new[] { "claim:m-1" }, inbox.Calls);
        Assert.Empty(host.DeadLettered);
    }

    [Fact]
    public async Task MessageWhoseIdAnotherDeliveryHolds_IsWaitedOutHere_OnATransportWithoutNativeDelay()
    {
        var inbox = new RecordingInbox();
        inbox.Busy.Add("m-1");
        await using MessagingHost host = Host(inbox, o => o.RetryDelay = TimeSpan.FromSeconds(7), TransportCapabilities.NativeDeadLetter);

        await host.Transport.SendAsync(new[] { Placed("m-1") }, CancellationToken.None);
        await host.Consumer.RunOnceAsync();

        Assert.Contains(TimeSpan.FromSeconds(7), host.Clock.Waited);
        Assert.Equal(new[] { TimeSpan.Zero }, host.Transport.DeferDelays);
        Assert.Empty(host.Transport.NackDelays);
        Assert.Empty(host.Probe.Ran);
    }

    [Fact]
    public async Task FailedHandlers_ReleaseTheClaim_SoTheRetryCanClaimIt()
    {
        var inbox = new RecordingInbox();
        await using MessagingHost host = Host(inbox);
        MessageEnvelope failing = MessagingHost.Envelope("test.order-placed", "{\"Id\":2,\"Note\":\"fail\"}");
        failing.Id = "m-2";

        await host.DeliverAsync(failing);
        await host.RunRoundsAsync(1);

        Assert.Equal(new[] { "claim:m-2", "release:m-2", "claim:m-2", "release:m-2" }, inbox.Calls);
        Assert.Equal(new[] { "placed:2", "placed:2" }, host.Probe.Ran);
    }

    [Fact]
    public async Task HandlersStoppedByShutdown_ReleaseTheClaim()
    {
        var inbox = new RecordingInbox();
        await using MessagingHost host = Host(inbox);
        MessageEnvelope waiting = MessagingHost.Envelope("test.order-placed", "{\"Id\":3,\"Note\":\"wait\"}");
        waiting.Id = "m-3";
        await host.Transport.SendAsync(new[] { waiting }, CancellationToken.None);
        using var stopping = new CancellationTokenSource();

        Task run = host.Consumer.RunOnceAsync(stopping.Token);
        await host.Probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await stopping.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { "claim:m-3", "release:m-3" }, inbox.Calls);
        Assert.Equal(new[] { TimeSpan.Zero }, host.Transport.NackDelays);
    }

    [Theory]
    [InlineData("")]
    [InlineData("m 1")]
    [InlineData("m\u00001")]
    [InlineData("m\t1")]
    public async Task MessageWithAnIdTheInboxCannotKeep_IsDeadLettered_WithoutAClaimOrItsHandlers(string id)
    {
        var inbox = new RecordingInbox();
        await using MessagingHost host = Host(inbox);

        await host.DeliverAsync(Placed(id));

        Assert.Empty(host.Probe.Ran);
        Assert.Empty(inbox.Calls);
        Assert.StartsWith("The message id is not 1 to 200 characters", Assert.Single(host.DeadLettered).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MessageLimits.MaxIdLength, true)]
    [InlineData(MessageLimits.MaxIdLength + 1, false)]
    public async Task MessageId_IsKept_UpToTheLongestLength(int length, bool handled)
    {
        var inbox = new RecordingInbox();
        await using MessagingHost host = Host(inbox);

        await host.DeliverAsync(Placed(new string('m', length)));

        Assert.Equal(handled ? 1 : 0, host.Probe.Ran.Count);
        Assert.Equal(handled ? 0 : 1, host.DeadLettered.Count);
    }

    [Fact]
    public async Task UnreadableMessage_IsDeadLettered_WithoutAClaim()
    {
        var inbox = new RecordingInbox();
        await using MessagingHost host = Host(inbox);
        MessageEnvelope unknown = MessagingHost.Envelope("test.unknown", PlacedJson);
        unknown.Id = "m-4";

        await host.DeliverAsync(unknown);

        Assert.Empty(inbox.Calls);
        Assert.Equal("No message type has this name.", Assert.Single(host.DeadLettered).Reason);
    }

    [Fact]
    public async Task InboxThatCannotClaim_LeavesTheMessageForARetry_Unhandled()
    {
        var inbox = new RecordingInbox { FailClaims = true };
        await using MessagingHost host = Host(inbox, o => o.RetryDelay = TimeSpan.FromSeconds(3));

        await host.DeliverAsync(Placed("m-5"));

        Assert.Empty(host.Probe.Ran);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, host.Transport.NackDelays);
        Assert.Empty(host.DeadLettered);
    }

    [Fact]
    public async Task InboxThatCannotRecordTheOutcome_StillAcksTheHandledMessage()
    {
        var inbox = new RecordingInbox { FailCompletes = true };
        await using MessagingHost host = Host(inbox);

        await host.DeliverAsync(Placed("m-6"));

        Assert.Equal(new[] { "placed:1" }, host.Probe.Ran);
        Assert.Empty(host.Transport.NackDelays);
        Assert.Empty(host.DeadLettered);
        Assert.Empty(await host.DrainAsync());
    }

    [Fact]
    public async Task WithoutAnInbox_TheSameMessageDeliveredTwice_RunsTwice()
    {
        await using MessagingHost host = MessagingHost.Create();

        await host.DeliverAsync(Placed("m-7"));
        await host.DeliverAsync(Placed("m-7"));

        Assert.Equal(new[] { "placed:1", "placed:1" }, host.Probe.Ran);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\u0000b", false)]
    [InlineData("{0f8fad5b-d9cb-469f-a165-70867728950e}", true)]
    [InlineData("urn:message/1@host", true)]
    public void IsValidId_AllowsAnyVisibleCharacters_ButNoWhiteSpaceOrControl(string? id, bool valid)
    {
        Assert.Equal(valid, MessageLimits.IsValidId(id));
    }

    [Fact]
    public void IsValidId_AllowsUpToTheLongestLength()
    {
        Assert.True(MessageLimits.IsValidId(new string('x', MessageLimits.MaxIdLength)));
        Assert.False(MessageLimits.IsValidId(new string('x', MessageLimits.MaxIdLength + 1)));
    }

    [Fact]
    public async Task CreateEnvelope_FillsTheEnvelopeAsPublishWould_AndSendsNothing()
    {
        await using MessagingHost host = MessagingHost.Create();
        IMessageBus bus = host.Services.GetRequiredService<IMessageBus>();

        MessageEnvelope envelope = bus.CreateEnvelope(new StockReport(9));
        MessageEnvelope other = bus.CreateEnvelope(new StockReport(9));

        Assert.Equal("test.report", envelope.Name);
        Assert.Equal(MessageEnvelope.JsonContentType, envelope.ContentType);
        Assert.Equal(envelope.Id, envelope.CorrelationId);
        Assert.Null(envelope.CausationId);
        Assert.NotEqual(envelope.Id, other.Id);
        Assert.Equal(new StockReport(9), JsonSerializer.Deserialize(envelope.Body.Span, TestMessagesJson.Default.StockReport));
        Assert.Empty(await host.DrainAsync());
    }

    [Theory]
    [InlineData(13, false)]
    [InlineData(12, true)]
    public async Task CreateEnvelope_RefusesABodyOverMaxMessageBytes(int limit, bool refused)
    {
        // {"Id":333333} is 13 bytes.
        await using MessagingHost host = MessagingHost.Create(o => o.MaxMessageBytes = limit);
        IMessageBus bus = host.Services.GetRequiredService<IMessageBus>();

        Exception? thrown = Record.Exception(() => bus.CreateEnvelope(new StockReport(333333)));

        Assert.Equal(refused, thrown is InvalidOperationException);
    }

    [Fact]
    public async Task CreateEnvelope_RefusesATypeThatIsNotAMessage_AndNull()
    {
        await using MessagingHost host = MessagingHost.Create();
        IMessageBus bus = host.Services.GetRequiredService<IMessageBus>();

        Assert.Throws<InvalidOperationException>(() => bus.CreateEnvelope("not a message"));
        Assert.Throws<ArgumentNullException>(() => bus.CreateEnvelope<StockReport>(null!));
    }

    private static MessagingHost Host(RecordingInbox inbox, Action<MessagingOptions>? configure = null, TransportCapabilities capabilities = TransportCapabilities.NativeDelay | TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend)
        => MessagingHost.Create(configure, capabilities, services: s => s.AddSingleton<IMessageInbox>(inbox));

    private static MessageEnvelope Placed(string id)
    {
        MessageEnvelope envelope = MessagingHost.Envelope("test.order-placed", PlacedJson);
        envelope.Id = id;
        return envelope;
    }

    /// <summary>An inbox in memory that records each call, and fails when told to.</summary>
    private sealed class RecordingInbox : IMessageInbox
    {
        private readonly HashSet<string> _handled = new HashSet<string>(StringComparer.Ordinal);

        public ConcurrentQueue<string> Calls { get; } = new ConcurrentQueue<string>();

        public HashSet<string> Busy { get; } = new HashSet<string>(StringComparer.Ordinal);

        public bool FailClaims { get; init; }

        public bool FailCompletes { get; init; }

        public ValueTask<InboxClaim> TryClaimAsync(string messageId, CancellationToken cancellationToken)
        {
            Calls.Enqueue("claim:" + messageId);
            if (FailClaims)
            {
                throw new InvalidOperationException("The inbox is down.");
            }
            InboxClaim claim = _handled.Contains(messageId) ? InboxClaim.Handled : Busy.Contains(messageId) ? InboxClaim.Busy : InboxClaim.Claimed;
            return new ValueTask<InboxClaim>(claim);
        }

        public ValueTask CompleteAsync(string messageId, CancellationToken cancellationToken)
        {
            Calls.Enqueue("complete:" + messageId);
            if (FailCompletes)
            {
                throw new InvalidOperationException("The inbox is down.");
            }
            _handled.Add(messageId);
            return default;
        }

        public ValueTask ReleaseAsync(string messageId, CancellationToken cancellationToken)
        {
            Calls.Enqueue("release:" + messageId);
            return default;
        }
    }
}
