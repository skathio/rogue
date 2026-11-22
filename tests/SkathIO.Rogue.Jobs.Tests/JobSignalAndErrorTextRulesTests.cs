using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>
/// The worker's wake-up signal raises no exception and loses no wake-up, and a failed job's stored
/// error names the exception's type but never its message, which can quote the payload or a secret.
/// </summary>
public sealed class JobSignalAndErrorTextRulesTests
{
    // Only a guard against a hang: a lost wake-up shows as a wait that never completes.
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(30);

    private const string SecretKey = "s3cret-token-7f2c";

    [Fact]
    public async Task NoPulse_LeavesTheWaiterWaiting()
    {
        var signal = new JobSignal();
        using var stop = new CancellationTokenSource();

        Task waiting = signal.WaitAsync(Timeout.InfiniteTimeSpan, stop.Token);

        Assert.False(waiting.IsCompleted);
        await stop.CancelAsync();
        await Task.WhenAny(waiting);
    }

    [Fact]
    public async Task OnePulse_BeforeTheWait_WakesTheWaiter()
    {
        var signal = new JobSignal();
        using var guard = new CancellationTokenSource(_hangGuard);

        signal.Pulse();
        Task waiting = signal.WaitAsync(Timeout.InfiniteTimeSpan, guard.Token);
        await waiting;

        Assert.True(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task OnePulse_DuringTheWait_WakesTheWaiter()
    {
        var signal = new JobSignal();
        using var guard = new CancellationTokenSource(_hangGuard);

        Task waiting = signal.WaitAsync(Timeout.InfiniteTimeSpan, guard.Token);
        Assert.False(waiting.IsCompleted);
        signal.Pulse();
        await waiting;

        Assert.True(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ManyPulsesBeforeTheWait_StillWakeTheWaiter()
    {
        var signal = new JobSignal();
        using var guard = new CancellationTokenSource(_hangGuard);

        for (int i = 0; i < 100; i++)
        {
            signal.Pulse();
        }
        Task waiting = signal.WaitAsync(Timeout.InfiniteTimeSpan, guard.Token);
        await waiting;

        Assert.True(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task APulseAfterAWakeUp_WakesTheNextWait()
    {
        var signal = new JobSignal();
        using var guard = new CancellationTokenSource(_hangGuard);

        for (int round = 0; round < 3; round++)
        {
            signal.Pulse();
            Task waiting = signal.WaitAsync(Timeout.InfiniteTimeSpan, guard.Token);
            await waiting;
            Assert.True(waiting.IsCompletedSuccessfully);
        }
    }

    [Fact]
    public async Task APulseRacingTheNextWait_IsNeverLost()
    {
        const int rounds = 2000;
        var signal = new JobSignal();
        using var guard = new CancellationTokenSource(_hangGuard);
        int released = -1;

        // Each pulse comes only after the previous wait has woken, and races the start of the next one.
        var pulser = new Thread(() =>
        {
            for (int round = 0; round < rounds; round++)
            {
                var spin = new SpinWait();
                while (Volatile.Read(ref released) < round)
                {
                    if (guard.IsCancellationRequested)
                    {
                        return;
                    }
                    spin.SpinOnce();
                }
                signal.Pulse();
            }
        })
        { IsBackground = true };
        pulser.Start();

        for (int round = 0; round < rounds; round++)
        {
            Volatile.Write(ref released, round);
            await signal.WaitAsync(Timeout.InfiniteTimeSpan, guard.Token);
        }

        pulser.Join();
        Assert.False(guard.IsCancellationRequested);
    }

    [Fact]
    public async Task ConcurrentPulsesWhileAWakeUpIsPending_ThrowNoException_AndStillWakeTheWaiter()
    {
        var signal = new JobSignal();
        using var guard = new CancellationTokenSource(_hangGuard);
        var pulserIds = new ConcurrentDictionary<int, bool>();
        int thrown = 0;
        EventHandler<FirstChanceExceptionEventArgs> count = (_, _) =>
        {
            if (pulserIds.ContainsKey(Environment.CurrentManagedThreadId))
            {
                Interlocked.Increment(ref thrown);
            }
        };
        using var start = new Barrier(8);
        var pulsers = new Thread[8];
        for (int t = 0; t < pulsers.Length; t++)
        {
            pulsers[t] = new Thread(() =>
            {
                pulserIds[Environment.CurrentManagedThreadId] = true;
                start.SignalAndWait();
                for (int i = 0; i < 1000; i++)
                {
                    signal.Pulse();
                }
            });
        }

        AppDomain.CurrentDomain.FirstChanceException += count;
        try
        {
            foreach (Thread pulser in pulsers)
            {
                pulser.Start();
            }
            foreach (Thread pulser in pulsers)
            {
                pulser.Join();
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= count;
        }

        Assert.Equal(0, Volatile.Read(ref thrown));
        Task waiting = signal.WaitAsync(Timeout.InfiniteTimeSpan, guard.Token);
        await waiting;
        Assert.True(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RetriedJob_StoresTheExceptionType_ButNotItsMessage()
    {
        await using JobsHost host = JobsHost.Create();
        host.Probe.FailuresLeft[SecretKey] = 1;
        Guid id = await host.Scheduler.Enqueue(new FlakyJob(SecretKey));

        Assert.Equal(1, await host.Worker.RunOnceAsync());

        JobSnapshot snapshot = await host.SnapshotAsync(id);
        Assert.Equal(JobState.Enqueued, snapshot.State);
        AssertTypeOnly(snapshot.LastError);
    }

    [Fact]
    public async Task DeadLetteredJob_StoresTheExceptionType_ButNotItsMessage()
    {
        await using JobsHost host = JobsHost.Create();
        host.Probe.FailuresLeft[SecretKey] = 100;
        Guid id = await host.Scheduler.Enqueue(new FlakyJob(SecretKey));

        await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(4));
        await host.Worker.RunOnceAsync();

        JobSnapshot snapshot = await host.SnapshotAsync(id);
        Assert.Equal(JobState.DeadLettered, snapshot.State);
        AssertTypeOnly(snapshot.LastError);
    }

    private static void AssertTypeOnly(string? lastError)
    {
        Assert.NotNull(lastError);
        Assert.Contains(nameof(InvalidOperationException), lastError, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, lastError, StringComparison.Ordinal);
        Assert.DoesNotContain("flaky failure", lastError, StringComparison.Ordinal);
    }
}
