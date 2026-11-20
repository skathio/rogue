using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// A delivery the inbox turns away as busy (another delivery holds its id) is put back without running
/// any handler, so it is not a failed attempt: a message is dead-lettered only once its handlers have
/// really failed <see cref="MessagingOptions.MaxAttempts"/> times, however often it was put back before
/// or between those runs. Checked on the in-memory transport and on a transport without native delay,
/// as RabbitMQ is.
/// </summary>
public sealed class InboxDeferralAttemptTests
{
    private const string FailingJson = "{\"Id\":1,\"Note\":\"fail\"}";
    private const string PassingJson = "{\"Id\":1,\"Note\":\"n\"}";

    // The capabilities the RabbitMQ transport reports: it cannot hold a nacked message for a delay.
    private const TransportCapabilities WithoutNativeDelay = TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend;

    public static TheoryData<int, int> AttemptsAndBusyClaims() => new TheoryData<int, int>
    {
        { 1, 0 },
        { 1, 1 },
        { 1, 2 },
        { 3, 0 },
        { 3, 1 },
        { 3, 2 },
        { 3, 3 },
        { 3, 4 },
    };

    [Theory]
    [MemberData(nameof(AttemptsAndBusyClaims))]
    public async Task OnTheInMemoryTransport_BusyDeferrals_DoNotCountAsFailedAttempts(int maxAttempts, int busyClaims)
    {
        var inbox = new ScriptedInbox(Enumerable.Repeat(InboxClaim.Busy, busyClaims));
        await using MessagingHost host = InMemoryHost(inbox, maxAttempts);
        InMemoryTransport transport = (InMemoryTransport)host.Services.GetRequiredService<IMessageTransport>();

        await transport.SendAsync(new[] { Message(FailingJson) }, CancellationToken.None);
        await host.RunRoundsAsync(busyClaims + maxAttempts + 2);

        Assert.Equal(maxAttempts, host.Probe.Ran.Count);
        Assert.Single(transport.DeadLettered);
        Assert.Empty(await transport.ReceiveAsync(MessagingOptions.MaxBatchSize, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(AttemptsAndBusyClaims))]
    public async Task WithoutNativeDelay_BusyDeferrals_DoNotCountAsFailedAttempts(int maxAttempts, int busyClaims)
    {
        var inbox = new ScriptedInbox(Enumerable.Repeat(InboxClaim.Busy, busyClaims));
        await using MessagingHost host = Host(inbox, maxAttempts, WithoutNativeDelay);

        await host.Transport.SendAsync(new[] { Message(FailingJson) }, CancellationToken.None);
        await host.RunRoundsAsync(busyClaims + maxAttempts + 2);

        Assert.Equal(maxAttempts, host.Probe.Ran.Count);
        Assert.Single(host.DeadLettered);
        Assert.Empty(await host.DrainAsync());
    }

    [Theory]
    [InlineData(TransportCapabilities.NativeDelay | TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend)]
    [InlineData(WithoutNativeDelay)]
    public async Task BusyDeferralsBetweenFailedRuns_DoNotCountAsFailedAttempts(TransportCapabilities capabilities)
    {
        // Run, put back, run, put back twice, run: three real failures, so the third is the last attempt.
        var inbox = new ScriptedInbox(new[]
        {
            InboxClaim.Claimed,
            InboxClaim.Busy,
            InboxClaim.Claimed,
            InboxClaim.Busy,
            InboxClaim.Busy,
        });
        await using MessagingHost host = Host(inbox, maxAttempts: 3, capabilities);

        await host.Transport.SendAsync(new[] { Message(FailingJson) }, CancellationToken.None);
        await host.RunRoundsAsync(8);

        Assert.Equal(3, host.Probe.Ran.Count);
        Assert.Single(host.DeadLettered);
        Assert.Empty(await host.DrainAsync());
    }

    [Theory]
    [InlineData(TransportCapabilities.NativeDelay | TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend)]
    [InlineData(WithoutNativeDelay)]
    public async Task AFailedRunAfterABusyDeferral_IsTheFirstAttempt_NotTheLast(TransportCapabilities capabilities)
    {
        // Put back once, then one failed run: that run is the first attempt of two, not the last.
        var inbox = new ScriptedInbox(new[] { InboxClaim.Busy });
        await using MessagingHost host = Host(inbox, maxAttempts: 2, capabilities);

        await host.Transport.SendAsync(new[] { Message(FailingJson) }, CancellationToken.None);
        await host.RunRoundsAsync(2);

        Assert.Single(host.Probe.Ran);
        Assert.Empty(host.DeadLettered);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public async Task AMessagePutBackAsBusy_AtOrPastMaxAttemptsTimes_StillRunsAndIsAckedOnceItsIdIsFree(int maxAttempts, int busyClaims)
    {
        var inbox = new ScriptedInbox(Enumerable.Repeat(InboxClaim.Busy, busyClaims));
        await using MessagingHost host = Host(inbox, maxAttempts, WithoutNativeDelay);

        await host.Transport.SendAsync(new[] { Message(PassingJson) }, CancellationToken.None);
        await host.RunRoundsAsync(busyClaims + 2);

        Assert.Single(host.Probe.Ran);
        Assert.Empty(host.DeadLettered);
        Assert.Empty(await host.DrainAsync());
    }

    private static MessagingHost Host(ScriptedInbox inbox, int maxAttempts, TransportCapabilities capabilities)
        => MessagingHost.Create(o => o.MaxAttempts = maxAttempts, capabilities, services: s => s.AddSingleton<IMessageInbox>(inbox));

    // The real in-memory transport, on the host's clock, in place of the recording wrapper.
    private static MessagingHost InMemoryHost(ScriptedInbox inbox, int maxAttempts)
        => MessagingHost.Create(o => o.MaxAttempts = maxAttempts, services: s =>
        {
            s.AddSingleton<IMessageInbox>(inbox);
            s.AddSingleton<IMessageTransport>(sp => new InMemoryTransport(sp.GetRequiredService<StepClock>()));
        });

    private static MessageEnvelope Message(string json)
    {
        MessageEnvelope envelope = MessagingHost.Envelope("test.order-placed", json);
        envelope.Id = "m-1";
        return envelope;
    }

    /// <summary>
    /// Answers each claim from its script, then as a real inbox would: handled once completed, otherwise
    /// claimed. Releases and completions are recorded as they come.
    /// </summary>
    private sealed class ScriptedInbox : IMessageInbox
    {
        private readonly ConcurrentQueue<InboxClaim> _script;
        private readonly ConcurrentDictionary<string, bool> _handled = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        public ScriptedInbox(IEnumerable<InboxClaim> script) => _script = new ConcurrentQueue<InboxClaim>(script);

        public ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken)
        {
            if (_handled.ContainsKey(messageId))
            {
                return new ValueTask<InboxClaimResult>(new InboxClaimResult(InboxClaim.Handled, 0));
            }
            return new ValueTask<InboxClaimResult>(new InboxClaimResult(_script.TryDequeue(out InboxClaim next) ? next : InboxClaim.Claimed, 0));
        }

        public ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken)
        {
            _handled[messageId] = true;
            return default;
        }

        public ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;
    }
}
