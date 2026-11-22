using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// <see cref="MessageConsumer.RunOnceAsync"/> on a transport without native delay: a failed message waits
/// out its retry delay without holding one of the batch's <see cref="MessagingOptions.MaxConcurrency"/>
/// places, so the rest of the batch is handled meanwhile.
/// </summary>
public sealed class RunOnceBackoffTests
{
    private static readonly TimeSpan Backoff = TimeSpan.FromHours(1);

    // Waits only so a broken consumer fails the test instead of hanging it; nothing is timed.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AFailedMessageInBackoff_DoesNotStallTheRestOfTheBatch()
    {
        await using MessagingHost host = MessagingHost.Create(
            options =>
            {
                options.MaxConcurrency = 1;
                options.BatchSize = 2;
                options.RetryDelay = Backoff;
                options.MaxRetryDelay = Backoff;
            },
            TransportCapabilities.NativeDeadLetter,
            services: s => s.AddSingleton<TimeProvider>(new NoLongWaitClock(Backoff)));
        await host.Transport.SendAsync(new[]
        {
            MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"fail\"}"),
            MessagingHost.Envelope("test.order-placed", "{\"Id\":2,\"Note\":\"n\"}"),
        }, CancellationToken.None);
        using var stopping = new CancellationTokenSource();

        Task<int> run = host.Consumer.RunOnceAsync(stopping.Token);
        using (var guard = new CancellationTokenSource(HangGuard))
        {
            while (host.Probe.Ran.Count < 2)
            {
                Assert.False(guard.IsCancellationRequested, "The second message did not run while the first was in backoff.");
                await Task.Delay(5, CancellationToken.None);
            }
        }

        // The failed message is still waiting: stopping cuts its wait short and nacks it, and none is lost.
        Assert.False(run.IsCompleted);
        await stopping.CancelAsync();
        Assert.Equal(2, await run.WaitAsync(HangGuard));
        Assert.Equal(new[] { "placed:1", "placed:2" }, host.Probe.Ran.ToArray());
        Assert.Equal("{\"Id\":1,\"Note\":\"fail\"}", System.Text.Encoding.UTF8.GetString(Assert.Single(await host.DrainAsync()).Body.Span));
        Assert.Empty(host.DeadLettered);
    }

    /// <summary>Real time for short waits; a wait of at least the given length never ends on its own.</summary>
    private sealed class NoLongWaitClock : TimeProvider
    {
        private readonly TimeSpan _longFrom;

        public NoLongWaitClock(TimeSpan longFrom) => _longFrom = longFrom;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime >= _longFrom ? Timeout.InfiniteTimeSpan : dueTime, period);
    }
}
