using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>
/// The ends of the wake-up signal (no pulse, one, many) and of the stored error: it is the
/// exception's full type name and nothing else, while the log still gets the whole exception.
/// </summary>
public sealed class JobSignalAndErrorTextEdgeTests
{
    private const string Secret = "s3cret-token-91ab";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    public async Task PulsesBeforeTheWait_CountAsOneWakeUp(int pulses)
    {
        var signal = new JobSignal();
        using var stop = new CancellationTokenSource();

        for (int i = 0; i < pulses; i++)
        {
            signal.Pulse();
        }
        Task first = signal.WaitAsync(Timeout.InfiniteTimeSpan, stop.Token);
        Task second = signal.WaitAsync(Timeout.InfiniteTimeSpan, stop.Token);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(second.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task ACancelledWait_Throws_AndLeavesTheNextPulseForTheNextWait()
    {
        var signal = new JobSignal();
        using var stop = new CancellationTokenSource();
        Task cancelled = signal.WaitAsync(Timeout.InfiniteTimeSpan, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        signal.Pulse();
        Task next = signal.WaitAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);

        Assert.True(next.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RetriedJob_StoresExactlyTheFullTypeName_AndLogsTheWholeException()
    {
        var logs = new CapturingLoggerFactory();
        await using JobsHost host = JobsHost.Create(services: s => s.AddSingleton<ILoggerFactory>(logs));
        host.Probe.FailuresLeft[Secret] = 1;
        Guid id = await host.Scheduler.Enqueue(new FlakyJob(Secret));

        await host.Worker.RunOnceAsync();

        JobSnapshot snapshot = await host.SnapshotAsync(id);
        Assert.Equal("System.InvalidOperationException", snapshot.LastError);
        Exception logged = Assert.Single(logs.Exceptions);
        Assert.Contains(Secret, logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeadLetteredJob_StoresExactlyTheFullTypeName_AndLogsTheWholeException()
    {
        var logs = new CapturingLoggerFactory();
        await using JobsHost host = JobsHost.Create(services: s => s.AddSingleton<ILoggerFactory>(logs));
        host.Probe.FailuresLeft[Secret] = 100;
        Guid id = await host.Scheduler.Enqueue(new FlakyJob(Secret));

        await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(4));
        await host.Worker.RunOnceAsync();

        JobSnapshot snapshot = await host.SnapshotAsync(id);
        Assert.Equal(JobState.DeadLettered, snapshot.State);
        Assert.Equal("System.InvalidOperationException", snapshot.LastError);
        Assert.Equal(3, logs.Exceptions.Count);
        Assert.All(logs.Exceptions, logged => Assert.Contains(Secret, logged.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TimedOutJob_StoresOnlyTheTimeoutType()
    {
        await using JobsHost host = JobsHost.Create();
        Guid id = await host.Scheduler.Enqueue(new SlowJob(1));

        Task<int> running = host.Worker.RunOnceAsync();
        await host.Probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("System.TimeoutException", (await host.SnapshotAsync(id)).LastError);
    }

    /// <summary>Records the exception of each log entry.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ILogger _logger;

        public CapturingLoggerFactory()
        {
            _logger = DispatchProxy.Create<ILogger, ExceptionSink>();
            ((ExceptionSink)_logger).Exceptions = Exceptions;
        }

        public ConcurrentQueue<Exception> Exceptions { get; } = new ConcurrentQueue<Exception>();

        public ILogger CreateLogger(string categoryName) => _logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>A logger enabled at every level that keeps each entry's exception.</summary>
internal class ExceptionSink : DispatchProxy
{
    public ConcurrentQueue<Exception>? Exceptions { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ILogger.Log) && args![3] is Exception exception)
        {
            Exceptions!.Enqueue(exception);
        }
        return targetMethod?.Name == nameof(ILogger.IsEnabled) ? true : null;
    }
}
