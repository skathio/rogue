using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;
using SkathIO.Rogue.Jobs.StoreContract;

namespace SkathIO.Rogue.Messaging.Outbox.Tests;

/// <summary>A service provider wired as an application wires the outbox, over an in-memory store and transport.</summary>
internal sealed class OutboxHost : IAsyncDisposable
{
    private OutboxHost(ServiceProvider services) => Services = services;

    public ServiceProvider Services { get; }

    public IMessageOutbox Outbox => Services.GetRequiredService<IMessageOutbox>();

    public InMemoryTransport Transport => (InMemoryTransport)Services.GetRequiredService<IMessageTransport>();

    public JobWorker Worker => Services.GetRequiredService<JobWorker>();

    public MessageConsumer Consumer => Services.GetRequiredService<MessageConsumer>();

    public Handled Handled => Services.GetRequiredService<Handled>();

    public IJobStore Store => Services.GetRequiredService<IJobStore>();

    /// <summary>A host on <paramref name="store"/>, or on a store that joins <see cref="FakeTransaction"/>s.</summary>
    public static OutboxHost Create(IJobStore? store = null, Action<MessageOutboxOptions>? configure = null)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var collection = new ServiceCollection();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddSingleton<Handled>();
        collection.AddSingleton<IMessageTransport>(new InMemoryTransport(clock));
        collection.AddSingleton(store ?? new TransactionalStore(new InMemoryJobStore(clock)));
        collection.AddRogue();
        collection.AddRogueMessaging(o =>
        {
            o.SerializerContext = OutboxMessagesJson.Default;
            o.MaxConcurrency = 1;
        });
        collection.AddRogueJobs();
        collection.AddRogueMessagingOutbox(configure);
        return new OutboxHost(collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));
    }

    /// <summary>Takes every message the transport holds ready, and acks them.</summary>
    public async Task<MessageEnvelope[]> DrainAsync()
    {
        IReadOnlyList<ReceivedMessage> received = await Transport.ReceiveAsync(MessagingOptions.MaxBatchSize, CancellationToken.None);
        foreach (ReceivedMessage message in received)
        {
            await Transport.AckAsync(message, CancellationToken.None);
        }
        return received.Select(static m => m.Envelope).ToArray();
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

/// <summary>What the handlers ran, in order.</summary>
public sealed class Handled
{
    public ConcurrentQueue<int> Orders { get; } = new ConcurrentQueue<int>();
}

[Message("outbox.order-placed")]
public sealed record OrderPlaced(int Id) : IEvent;

public sealed class OrderPlacedHandler : IEventHandler<OrderPlaced>
{
    private readonly Handled _handled;

    public OrderPlacedHandler(Handled handled) => _handled = handled;

    public ValueTask Handle(OrderPlaced e, CancellationToken cancellationToken)
    {
        _handled.Orders.Enqueue(e.Id);
        return default;
    }
}

[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class OutboxMessagesJson : JsonSerializerContext
{
}

/// <summary>A transaction that holds the jobs stored in it until it commits, and drops them on a rollback.</summary>
internal sealed class FakeTransaction : DbTransaction
{
    private readonly List<(InMemoryJobStore Store, NewJob[] Jobs)> _pending = new List<(InMemoryJobStore, NewJob[])>();
    private bool _done;

    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection? DbConnection => null;

    public void Hold(InMemoryJobStore store, IReadOnlyList<NewJob> jobs)
    {
        if (_done)
        {
            throw new InvalidOperationException("The transaction has already been committed or rolled back.");
        }
        _pending.Add((store, jobs.ToArray()));
    }

    public override void Commit()
    {
        _done = true;
        foreach ((InMemoryJobStore store, NewJob[] jobs) in _pending)
        {
            store.EnqueueAsync(jobs, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        _pending.Clear();
    }

    public override void Rollback()
    {
        _done = true;
        _pending.Clear();
    }
}

/// <summary>An in-memory store that joins a <see cref="FakeTransaction"/>, as a database store joins the caller's transaction.</summary>
internal sealed class TransactionalStore : ITransactionalJobStore, IMessageInboxStore
{
    private readonly InMemoryJobStore _inner;

    public TransactionalStore(InMemoryJobStore inner) => _inner = inner;

    public ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, DbTransaction transaction, CancellationToken cancellationToken)
    {
        if (transaction is not FakeTransaction fake)
        {
            throw new ArgumentException("Not a fake transaction.", nameof(transaction));
        }
        fake.Hold(_inner, jobs);
        return default;
    }

    public ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken) => _inner.EnqueueAsync(jobs, cancellationToken);

    public ValueTask<Guid> EnqueueOnceAsync(NewJob job, CancellationToken cancellationToken) => _inner.EnqueueOnceAsync(job, cancellationToken);

    public ValueTask<bool> TryAcquireLeadershipAsync(string name, string owner, TimeSpan lease, CancellationToken cancellationToken) => _inner.TryAcquireLeadershipAsync(name, owner, lease, cancellationToken);

    public ValueTask ReleaseLeadershipAsync(string name, string owner, CancellationToken cancellationToken) => _inner.ReleaseLeadershipAsync(name, owner, cancellationToken);

    public ValueTask<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, int maxCount, TimeSpan lease, CancellationToken cancellationToken) => _inner.ClaimAsync(queue, maxCount, lease, cancellationToken);

    public ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken) => _inner.CompleteAsync(leases, cancellationToken);

    public ValueTask<bool> RetryAsync(JobLease lease, TimeSpan delay, string error, CancellationToken cancellationToken) => _inner.RetryAsync(lease, delay, error, cancellationToken);

    public ValueTask<bool> DeadLetterAsync(JobLease lease, string error, CancellationToken cancellationToken) => _inner.DeadLetterAsync(lease, error, cancellationToken);

    public ValueTask<bool> ReleaseAsync(JobLease lease, TimeSpan delay, CancellationToken cancellationToken) => _inner.ReleaseAsync(lease, delay, cancellationToken);

    public ValueTask<int> RequeueAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => _inner.RequeueAsync(ids, cancellationToken);

    public ValueTask<int> ExtendLeasesAsync(IReadOnlyList<JobLease> leases, TimeSpan lease, CancellationToken cancellationToken) => _inner.ExtendLeasesAsync(leases, lease, cancellationToken);

    public ValueTask<JobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken) => _inner.GetAsync(id, cancellationToken);

    public ValueTask<MessageClaimResult> TryClaimMessageAsync(string messageId, TimeSpan lease, CancellationToken cancellationToken) => _inner.TryClaimMessageAsync(messageId, lease, cancellationToken);

    public ValueTask CompleteMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken) => _inner.CompleteMessageAsync(messageId, claimToken, cancellationToken);

    public ValueTask ReleaseMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken) => _inner.ReleaseMessageAsync(messageId, claimToken, cancellationToken);
}
