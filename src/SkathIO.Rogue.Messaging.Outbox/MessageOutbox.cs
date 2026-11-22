using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.Messaging.Outbox;

/// <summary>The <see cref="IMessageOutbox"/> over the registered bus, job scheduler and job store.</summary>
internal sealed class MessageOutbox : IMessageOutbox
{
    private readonly IMessageBus _bus;
    private readonly IJobScheduler _scheduler;
    private readonly IJobStore _store;

    public MessageOutbox(IMessageBus bus, IJobScheduler scheduler, IJobStore store)
    {
        _bus = bus;
        _scheduler = scheduler;
        _store = store;
    }

    public async ValueTask<string> Publish<TMessage>(TMessage message, DbTransaction transaction, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(transaction);
        if (_store is not ITransactionalJobStore store)
        {
            throw new InvalidOperationException("The job store " + _store.GetType() + " cannot join a database transaction. Register the jobs store of the transaction's database, such as AddRoguePostgresJobs or AddRogueSqlServerJobs.");
        }

        MessageEnvelope envelope = _bus.CreateEnvelope(message);
        NewJob relay = _scheduler.CreateJob(OutboxRelay.From(envelope));
        await store.EnqueueAsync(new[] { relay }, transaction, cancellationToken).ConfigureAwait(false);
        return envelope.Id;
    }
}
