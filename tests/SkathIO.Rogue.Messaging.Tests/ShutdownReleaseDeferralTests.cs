using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// A message released because the consumer is stopping was never a failed attempt: it is settled as a
/// deferral, not a nack, so its delivery count does not go up, and a message on its last attempt is
/// not dead-lettered for being stopped. Checked with and without an inbox, and on a transport with and
/// without native delay, as RabbitMQ is.
/// </summary>
public sealed class ShutdownReleaseDeferralTests
{
    private const string WaitingJson = "{\"Id\":1,\"Note\":\"wait\"}";

    private const TransportCapabilities WithNativeDelay = TransportCapabilities.NativeDelay | TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend;

    // The capabilities the RabbitMQ transport reports: it cannot hold a nacked message for a delay.
    private const TransportCapabilities WithoutNativeDelay = TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend;

    public static TheoryData<int, int> AttemptsAndReleases() => new TheoryData<int, int>
    {
        { 1, 1 },
        { 1, 2 },
        { 3, 1 },
        { 3, 3 },
        { 3, 4 },
    };

    [Theory]
    [InlineData(WithNativeDelay)]
    [InlineData(WithoutNativeDelay)]
    public async Task StoppingWhileAHandlerRuns_DefersTheMessage_AndKeepsItsDeliveryCount(TransportCapabilities capabilities)
    {
        await using MessagingHost host = MessagingHost.Create(capabilities: capabilities);
        await host.Transport.SendAsync(new[] { Waiting() }, CancellationToken.None);

        await StopDuringRunAsync(host, host.Probe.Started.Task);

        Assert.Empty(host.Transport.NackDelays);
        Assert.Single(host.Transport.DeferDelays);
        Assert.Empty(host.DeadLettered);
        Assert.Equal(1, await NextDeliveryCountAsync(host));
    }

    [Theory]
    [MemberData(nameof(AttemptsAndReleases))]
    public async Task ReleasesAtShutdown_UpToAndPastMaxAttempts_NeverUseUpAnAttempt(int maxAttempts, int releases)
    {
        await using MessagingHost host = MessagingHost.Create(o => o.MaxAttempts = maxAttempts, WithoutNativeDelay);
        await host.Transport.SendAsync(new[] { Waiting() }, CancellationToken.None);

        for (int i = 0; i < releases; i++)
        {
            await StopDuringRunAsync(host, host.Probe.Started.Task);
            host.Clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.Empty(host.Transport.NackDelays);
        Assert.Equal(releases, host.Transport.DeferDelays.Count);
        Assert.Empty(host.DeadLettered);
        Assert.Equal(1, await NextDeliveryCountAsync(host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingWithAnInbox_WhileClaimingOrRunning_DefersTheMessage_AndKeepsItsDeliveryCount(bool stopWhileClaiming)
    {
        var inbox = new StallingInbox { StallClaims = stopWhileClaiming };
        await using MessagingHost host = MessagingHost.Create(o => o.MaxAttempts = 1, WithoutNativeDelay, services: s => s.AddSingleton<IMessageInbox>(inbox));
        await host.Transport.SendAsync(new[] { Waiting() }, CancellationToken.None);

        await StopDuringRunAsync(host, stopWhileClaiming ? inbox.Claiming.Task : host.Probe.Started.Task);

        Assert.Empty(host.Transport.NackDelays);
        Assert.Single(host.Transport.DeferDelays);
        Assert.Empty(host.DeadLettered);
        Assert.Equal(1, await NextDeliveryCountAsync(host));
    }

    private static MessageEnvelope Waiting()
    {
        MessageEnvelope envelope = MessagingHost.Envelope("test.order-placed", WaitingJson);
        envelope.Id = "m-1";
        return envelope;
    }

    // Runs the consumer once and stops it once the run has reached the point the test waits for.
    private static async Task StopDuringRunAsync(MessagingHost host, Task reached)
    {
        using var stopping = new CancellationTokenSource();
        Task run = host.Consumer.RunOnceAsync(stopping.Token);
        await reached.WaitAsync(TimeSpan.FromSeconds(30));
        await stopping.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task<int> NextDeliveryCountAsync(MessagingHost host)
    {
        host.Clock.Advance(TimeSpan.FromDays(1));
        ReceivedMessage next = Assert.Single(await host.Transport.ReceiveAsync(MessagingOptions.MaxBatchSize, CancellationToken.None));
        return next.DeliveryCount;
    }

    /// <summary>Claims every id; when told to, holds each claim call until the consumer stops.</summary>
    private sealed class StallingInbox : IMessageInbox
    {
        public bool StallClaims { get; init; }

        public TaskCompletionSource Claiming { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<InboxClaim> TryClaimAsync(string messageId, CancellationToken cancellationToken)
        {
            Claiming.TrySetResult();
            if (StallClaims)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return InboxClaim.Claimed;
        }

        public ValueTask CompleteAsync(string messageId, CancellationToken cancellationToken) => default;

        public ValueTask ReleaseAsync(string messageId, CancellationToken cancellationToken) => default;
    }
}
