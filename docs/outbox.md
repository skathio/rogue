# Outbox and inbox (`SkathIO.Rogue.Messaging.Outbox`)

`SkathIO.Rogue.Messaging.Outbox` closes the two gaps between a database and a message broker:

- **The outbox.** `IMessageOutbox.Publish` stores a message inside your own database transaction. If
  the transaction commits, a job worker relays it to the transport; if it rolls back, nothing is
  stored and nothing is sent. Your data and your messages can no longer disagree.
- **The inbox.** The consumer records each message id it handles, so a message delivered twice (by
  the outbox relay or by the broker) runs its handlers once.

Both live in the tables of a database job store from [`SkathIO.Rogue.Jobs`](jobs.md): the outbox
stores each message as a relay job, and the inbox keeps its records next to the jobs. So the package
needs a Postgres or SQL Server jobs store and the [messaging](messaging.md) bus.

```bash
dotnet add package SkathIO.Rogue.Messaging.Outbox
dotnet add package SkathIO.Rogue.Jobs.Postgres     # or SkathIO.Rogue.Jobs.SqlServer
dotnet add package SkathIO.Rogue.Messaging.RabbitMQ
```

## Register

```csharp
using Npgsql;
using SkathIO.Rogue;
using SkathIO.Rogue.Jobs;
using SkathIO.Rogue.Jobs.Postgres;
using SkathIO.Rogue.Messaging;
using SkathIO.Rogue.Messaging.Outbox;
using SkathIO.Rogue.Messaging.RabbitMQ;

NpgsqlDataSource dataSource = NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Orders")!);

builder.Services.AddSingleton(dataSource);   // for your own handlers, as below
builder.Services.AddRogue();
builder.Services.AddRogueMessaging(o => o.SerializerContext = AppJson.Default);
builder.Services.AddRogueRabbitMqTransport(o =>
{
    o.ConnectionUri = new Uri(builder.Configuration["RabbitMq:ConnectionUri"]!);
    o.ServiceName = "orders";
    o.DeliveryStampKey = builder.Configuration["RabbitMq:DeliveryStampKey"];
});
builder.Services.AddRogueJobs();
builder.Services.AddRoguePostgresJobs(dataSource, o => o.Schema = "orders_jobs");
builder.Services.AddRogueMessagingOutbox(o => o.InboxLease = TimeSpan.FromMinutes(5));
builder.Services.AddRogueJobWorker();        // runs the relay jobs
builder.Services.AddRogueMessageConsumer();  // consumes, through the inbox
```

`AddRogueMessagingOutbox` registers `IMessageOutbox`, the job kind that relays its messages, and an
`IMessageInbox` kept by the job store, which the `MessageConsumer` picks up. `AddRogueMessaging` and
`AddRogueJobs` must also be called, with a database job store (`AddRoguePostgresJobs` or
`AddRogueSqlServerJobs`). The relay job has its own serializer, so the jobs `SerializerContext` is
needed only for your own `[Job]` commands. Only the first call's options apply.

## Publish in your transaction

Pass `IMessageOutbox.Publish` the message and your open `DbTransaction` on the job store's database:
an `NpgsqlTransaction` for the Postgres store, a `SqlTransaction` for the SQL Server store.

```csharp
using Npgsql;
using SkathIO.Rogue;
using SkathIO.Rogue.Messaging.Outbox;

public sealed class PlaceOrderHandler(NpgsqlDataSource dataSource, IMessageOutbox outbox) : ICommandHandler<PlaceOrder>
{
    public async ValueTask Handle(PlaceOrder command, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        // ... insert the order with the same transaction ...

        string messageId = await outbox.Publish(new OrderPlaced(command.OrderId, command.Total), transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
```

With SQL Server:

```csharp
using Microsoft.Data.SqlClient;
using SkathIO.Rogue.Messaging.Outbox;

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync(ct);
await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
await outbox.Publish(new OrderPlaced(orderId, total), transaction, ct);
await transaction.CommitAsync(ct);
```

With EF Core, pass `Database.CurrentTransaction.GetDbTransaction()` after `BeginTransactionAsync`.

`Publish` builds the envelope `IMessageBus.Publish` would send, ids and trace context included,
stores it, and returns the message id. It throws `InvalidOperationException` when the type is not a
`[Message]`, the body is over `MessagingOptions.MaxMessageBytes`, the registered job store cannot join
a transaction (the in-memory store cannot), or the transaction has completed; and
`ArgumentException` when the transaction belongs to another database client than the store's.

## The relay

Each published message is one job of the kind `rogue.messaging.outbox` on the jobs' default queue.
Workers see it only once your transaction commits. A host that calls `AddRogueJobWorker` claims it
and sends the stored envelope to the transport unchanged, its id included.

- A failed send is retried with the jobs' [backoff](jobs.md#retries-backoff-and-dead-lettering), and
  dead-lettered in the job store after `JobsOptions.DefaultMaxAttempts`; requeue it with
  `IJobStore.RequeueAsync` once the broker is back.
- A relay can run more than once: its worker can stop after sending and before recording that. The
  same message, with the same id, then reaches the transport twice. The inbox on the consuming side
  handles it once.
- Messages are relayed one job each, in no guaranteed order. Do not rely on two messages arriving in
  the order you published them.

## The inbox

With the outbox package registered, the consumer asks the inbox before it runs a message's handlers:

- The first delivery of a message id claims it in the inbox for `InboxLease` (5 minutes) and runs the
  handlers. When they succeed, the id is recorded as handled.
- A later delivery of a handled id is acked without running anything.
- A delivery whose id another delivery is still handling is put back unhandled, as a deferral that
  costs no attempt, and comes round again after `MessagingOptions.RetryDelay` (or `PollInterval` when
  that is zero). The RabbitMQ transport holds it on the broker meanwhile, in the service's wait queue
  ([details](messaging-rabbitmq.md#busy-deliveries-and-the-wait-queue)).
- When the handlers fail, the claim is released so the retry can run them.

Set `InboxLease` longer than your slowest handler: a handler still running when its lease runs out
may run twice. It is at most 30 days (`MessageOutboxOptions.MaxInboxLease`).

Ids are kept per message name, so two message types never share an id. A delivery whose id is not
valid is dead-lettered.

The handlers' own writes and the inbox's record are not one transaction: if a process stops after the
handlers ran and before the record was written, the message runs again once the claim's lease ends.
Keep handlers idempotent where running twice would matter.

## See also

- [Messaging](messaging.md) — messages, the bus, the consumer and retries.
- [Background jobs](jobs.md) — the job stores the outbox runs on.
- [RabbitMQ transport](messaging-rabbitmq.md).
