using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.Consumer.Jobs.Tests;

/// <summary>What the job handlers did, and how many failures a test asks them to throw first.</summary>
public sealed class JobProbe
{
    private readonly ConcurrentQueue<string> _ran = new ConcurrentQueue<string>();
    private readonly TaskCompletionSource _pruned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Failures <see cref="ChargeCardHandler"/> still throws, per card.</summary>
    public ConcurrentDictionary<string, int> DeclinesLeft { get; } = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Completes when <see cref="PruneCarts"/> first runs.</summary>
    public Task Pruned => _pruned.Task;

    public void Record(string entry)
    {
        _ran.Enqueue(entry);
        if (entry == PruneCartsHandler.Entry)
        {
            _pruned.TrySetResult();
        }
    }

    /// <summary>Every entry so far, sorted, since the worker runs a batch's jobs concurrently.</summary>
    public IReadOnlyList<string> Ran() => _ran.Order(StringComparer.Ordinal).ToArray();
}

/// <summary>A plain job: enqueued, enqueued in a batch, or scheduled for later.</summary>
[Job]
public sealed record ShipParcel(int Number) : ICommand;

/// <summary>At most one pending rebuild: enqueueing another while one is pending stores nothing.</summary>
[Job(UniqueKey = RebuildSearchIndex.Key)]
public sealed record RebuildSearchIndex(int Request) : ICommand
{
    public const string Key = "search-index";
}

/// <summary>Declined as often as the probe says, then charged; three attempts, two seconds apart at first.</summary>
[Job(MaxAttempts = 3, BackoffSeconds = 2)]
public sealed record ChargeCard(string Card) : ICommand;

/// <summary>Always fails, so its second and last attempt dead-letters it.</summary>
[Job(MaxAttempts = 2, BackoffSeconds = 1)]
public sealed record CallPartner(int Number) : ICommand;

/// <summary>Runs at minute 30 of every hour, in hosts that run the worker.</summary>
[Job]
[Recurring("30 * * * *")]
public sealed record PruneCarts : ICommand;

public sealed class ShipParcelHandler(JobProbe probe) : ICommandHandler<ShipParcel>
{
    public ValueTask Handle(ShipParcel command, CancellationToken cancellationToken)
    {
        probe.Record("shipped " + command.Number);
        return default;
    }
}

public sealed class RebuildSearchIndexHandler(JobProbe probe) : ICommandHandler<RebuildSearchIndex>
{
    public ValueTask Handle(RebuildSearchIndex command, CancellationToken cancellationToken)
    {
        probe.Record("rebuilt for " + command.Request);
        return default;
    }
}

public sealed class ChargeCardHandler(JobProbe probe) : ICommandHandler<ChargeCard>
{
    public const string Declined = "The card was declined.";

    public ValueTask Handle(ChargeCard command, CancellationToken cancellationToken)
    {
        if (probe.DeclinesLeft.TryGetValue(command.Card, out int left) && left > 0)
        {
            probe.DeclinesLeft[command.Card] = left - 1;
            probe.Record("declined " + command.Card);
            throw new InvalidOperationException(Declined);
        }
        probe.Record("charged " + command.Card);
        return default;
    }
}

public sealed class CallPartnerHandler(JobProbe probe) : ICommandHandler<CallPartner>
{
    public const string Down = "The partner is down.";

    public ValueTask Handle(CallPartner command, CancellationToken cancellationToken)
    {
        probe.Record("called " + command.Number);
        throw new InvalidOperationException(Down);
    }
}

public sealed class PruneCartsHandler(JobProbe probe) : ICommandHandler<PruneCarts>
{
    public const string Entry = "pruned carts";

    public ValueTask Handle(PruneCarts command, CancellationToken cancellationToken)
    {
        probe.Record(Entry);
        return default;
    }
}

/// <summary>Serializer metadata for every job command, as the jobs package requires.</summary>
[JsonSerializable(typeof(ShipParcel))]
[JsonSerializable(typeof(RebuildSearchIndex))]
[JsonSerializable(typeof(ChargeCard))]
[JsonSerializable(typeof(CallPartner))]
[JsonSerializable(typeof(PruneCarts))]
internal sealed partial class ConsumerJobsJson : JsonSerializerContext
{
}
