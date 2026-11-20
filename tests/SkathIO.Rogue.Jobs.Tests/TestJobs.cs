using System;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>What the test handlers saw, and how a test tells them to behave. One per service provider.</summary>
public sealed class JobProbe
{
    public ConcurrentQueue<string> Ran { get; } = new ConcurrentQueue<string>();

    /// <summary>Failures still to throw, per key.</summary>
    public ConcurrentDictionary<string, int> FailuresLeft { get; } = new ConcurrentDictionary<string, int>();

    public TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<int> ScopeIds { get; } = new ConcurrentQueue<int>();

    /// <summary>Holds every <see cref="GateJob"/> until a test opens it.</summary>
    public TaskCompletionSource Gate { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set once this many gate jobs are running at the same time.</summary>
    public int StartTarget { get; set; } = 1;

    public TaskCompletionSource Finished { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Running;

    public int MaxRunning;
}

/// <summary>A scoped service, to show each job runs in a scope of its own.</summary>
public sealed class ScopedCounter
{
    private static int _next;

    public int Id { get; } = Interlocked.Increment(ref _next);
}

[Job]
public sealed record RecordValue(int Value) : ICommand;

public sealed class RecordValueHandler : ICommandHandler<RecordValue>
{
    private readonly JobProbe _probe;
    private readonly ScopedCounter _scoped;

    public RecordValueHandler(JobProbe probe, ScopedCounter scoped)
    {
        _probe = probe;
        _scoped = scoped;
    }

    public ValueTask Handle(RecordValue command, CancellationToken cancellationToken)
    {
        _probe.Ran.Enqueue("value:" + command.Value);
        _probe.ScopeIds.Enqueue(_scoped.Id);
        return default;
    }
}

[Job(MaxAttempts = 3, BackoffSeconds = 2)]
public sealed record FlakyJob(string Key) : ICommand;

public sealed class FlakyJobHandler : ICommandHandler<FlakyJob>
{
    private readonly JobProbe _probe;

    public FlakyJobHandler(JobProbe probe) => _probe = probe;

    public ValueTask Handle(FlakyJob command, CancellationToken cancellationToken)
    {
        _probe.Ran.Enqueue("flaky:" + command.Key);

        // FailuresLeft holds how many runs still fail; it counts down on each run.
        if (_probe.FailuresLeft.AddOrUpdate(command.Key, -1, static (_, left) => left - 1) >= 0)
        {
            throw new InvalidOperationException("flaky failure for " + command.Key);
        }
        return default;
    }
}

[Job(Kind = "tests.priced", Queue = "billing")]
public sealed record PriceJob(int Amount) : ICommand<int>;

public sealed class PriceJobHandler : ICommandHandler<PriceJob, int>
{
    private readonly JobProbe _probe;

    public PriceJobHandler(JobProbe probe) => _probe = probe;

    public ValueTask<int> Handle(PriceJob command, CancellationToken cancellationToken)
    {
        _probe.Ran.Enqueue("price:" + command.Amount);
        return new ValueTask<int>(command.Amount);
    }
}

[Job(TimeoutSeconds = 5, MaxAttempts = 2)]
public sealed record SlowJob(int Id) : ICommand;

public sealed class SlowJobHandler : ICommandHandler<SlowJob>
{
    private readonly JobProbe _probe;

    public SlowJobHandler(JobProbe probe) => _probe = probe;

    public async ValueTask Handle(SlowJob command, CancellationToken cancellationToken)
    {
        _probe.Ran.Enqueue("slow:" + command.Id);
        _probe.Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}

/// <summary>Runs until the probe's gate opens; it has no timeout.</summary>
[Job]
public sealed record GateJob(int Id) : ICommand;

public sealed class GateJobHandler : ICommandHandler<GateJob>
{
    private readonly JobProbe _probe;

    public GateJobHandler(JobProbe probe) => _probe = probe;

    public async ValueTask Handle(GateJob command, CancellationToken cancellationToken)
    {
        int running = Interlocked.Increment(ref _probe.Running);
        InterlockedMax(ref _probe.MaxRunning, running);
        if (running >= _probe.StartTarget)
        {
            _probe.Started.TrySetResult();
        }
        try
        {
            await _probe.Gate.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _probe.Running);
        }
        _probe.Ran.Enqueue("gate:" + command.Id);
        _probe.Finished.TrySetResult();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}

/// <summary>A command without <c>[Job]</c>: it is a job all the same.</summary>
public sealed record UnmarkedJob(int Id) : ICommand;

public sealed class UnmarkedJobHandler : ICommandHandler<UnmarkedJob>
{
    private readonly JobProbe _probe;

    public UnmarkedJobHandler(JobProbe probe) => _probe = probe;

    public ValueTask Handle(UnmarkedJob command, CancellationToken cancellationToken)
    {
        _probe.Ran.Enqueue("unmarked:" + command.Id);
        return default;
    }
}

/// <summary>Used only by the telemetry tests, so the measurements they read are their own.</summary>
[Job(MaxAttempts = 1)]
public sealed record TelemetryJob(bool Fail) : ICommand;

public sealed class TelemetryJobHandler : ICommandHandler<TelemetryJob>
{
    public ValueTask Handle(TelemetryJob command, CancellationToken cancellationToken)
        => command.Fail ? throw new FormatException("telemetry failure") : default;
}

/// <summary>A job whose pending instance holds a unique key.</summary>
[Job(UniqueKey = "unique-test")]
public sealed record UniqueJob(int Id) : ICommand;

public sealed class UniqueJobHandler : ICommandHandler<UniqueJob>
{
    private readonly JobProbe _probe;

    public UniqueJobHandler(JobProbe probe) => _probe = probe;

    public ValueTask Handle(UniqueJob command, CancellationToken cancellationToken)
    {
        _probe.Ran.Enqueue("unique:" + command.Id);
        return default;
    }
}

/// <summary>A query: never a job.</summary>
public sealed record LookupQuery(int Id) : IQuery<int>;

public sealed class LookupQueryHandler : IQueryHandler<LookupQuery, int>
{
    public ValueTask<int> Handle(LookupQuery query, CancellationToken cancellationToken) => new ValueTask<int>(query.Id);
}

[JsonSerializable(typeof(RecordValue))]
[JsonSerializable(typeof(FlakyJob))]
[JsonSerializable(typeof(PriceJob))]
[JsonSerializable(typeof(SlowJob))]
[JsonSerializable(typeof(UnmarkedJob))]
[JsonSerializable(typeof(TelemetryJob))]
[JsonSerializable(typeof(GateJob))]
[JsonSerializable(typeof(UniqueJob))]
public sealed partial class TestJobsJson : JsonSerializerContext
{
}

/// <summary>Covers one job only, for the registry's coverage check.</summary>
[JsonSerializable(typeof(RecordValue))]
public sealed partial class PartialJobsJson : JsonSerializerContext
{
}
