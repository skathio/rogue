# Background jobs (`SkathIO.Rogue.Jobs`)

`SkathIO.Rogue.Jobs` runs Rogue commands as background jobs. You mark a command `[Job]`, enqueue it
through `IJobScheduler`, and a worker claims it from a job store and runs its handler through the
normal Rogue pipeline, behaviors included. The SkathIO.Rogue source generator writes the dispatch
and the payload serialization into your project, so there is no reflection, no expression tree and no
assembly-qualified type name in the store: it works under Native AOT and trimming.

```bash
dotnet add package SkathIO.Rogue.Jobs
dotnet add package SkathIO.Rogue.Jobs.Postgres     # or SkathIO.Rogue.Jobs.SqlServer, for a durable store
```

The package carries no generator of its own. The core `SkathIO.Rogue` generator emits the job code,
so keep both packages on the same major.minor version (`ROGUE013` warns when they differ). With
project references instead of packages, the project that declares the commands needs the analyzer
reference described in [getting-started](getting-started.md#troubleshooting).

## Declare a job

A job is an `ICommand` or `ICommand<TResponse>` with a handler, marked `[Job]`. Only a marked command
can be enqueued or run: the store holds a kind name, and the worker runs only kinds bound to a
`[Job]` command, so a row someone writes into the jobs table can never run an arbitrary command.

```csharp
using System.Text.Json.Serialization;
using SkathIO.Rogue;
using SkathIO.Rogue.Jobs;

[Job(MaxAttempts = 3, BackoffSeconds = 30, TimeoutSeconds = 120)]
public sealed record ShipOrder(int OrderId) : ICommand;

public sealed class ShipOrderHandler : ICommandHandler<ShipOrder>
{
    public ValueTask Handle(ShipOrder command, CancellationToken cancellationToken)
    {
        // Ship the order.
        return default;
    }
}

// Every [Job] command must be covered by the serializer context (ROGUE019 checks it at build time).
[JsonSerializable(typeof(ShipOrder))]
internal sealed partial class AppJson : JsonSerializerContext
{
}
```

`[Job]` sets the job's defaults. A value left at zero or `null` keeps the one in `JobsOptions`:

| Property | Meaning | Default |
|----------|---------|---------|
| `Kind` | The stable name the job is stored under. Set it to keep queued jobs runnable after the type is renamed or moved. | The command's full type name |
| `Queue` | The queue it is enqueued on. | `JobsOptions.DefaultQueue` (`"default"`) |
| `MaxAttempts` | How many times it runs before it is dead-lettered. | `JobsOptions.DefaultMaxAttempts` (5) |
| `BackoffSeconds` | The delay before the first retry; it doubles on each later one. | `JobsOptions.DefaultBackoff` (10 s) |
| `TimeoutSeconds` | How long one run may take before it is cancelled and counted as failed. | No limit |
| `UniqueKey` | See [Unique jobs](#unique-jobs). | None |

## Register

```csharp
using SkathIO.Rogue;
using SkathIO.Rogue.Jobs;

builder.Services.AddRogue();
builder.Services.AddRogueJobs(o => o.SerializerContext = AppJson.Default);
builder.Services.AddRogueJobWorker();   // only in hosts that should run jobs
```

- `AddRogueJobs` registers `IJobScheduler`, the `JobWorker` and, unless a store is already
  registered, an in-memory store. That is enough to enqueue. `AddRogue` must also be called: it
  registers the job kinds the generator found.
- `AddRogueJobWorker` is the opt-in that runs jobs. It adds the hosted service that runs the worker
  and the one that schedules [recurring jobs](#recurring-jobs). A web API that only enqueues leaves it
  out; a worker service calls it. Calling it twice adds no second worker.

Only the first call's options apply. An option out of range throws at registration.

## Enqueue

Inject `IJobScheduler`:

```csharp
using SkathIO.Rogue.Jobs;

app.MapPost("/orders/{id:int}/ship", async (int id, IJobScheduler jobs, CancellationToken ct) =>
{
    Guid jobId = await jobs.Enqueue(new ShipOrder(id), ct);
    return Results.Accepted(value: jobId);
});
```

| Method | What it does |
|--------|--------------|
| `Enqueue(command)` | Stores one job, due now, and returns its id. |
| `Schedule(command, at)` | Stores one job that no worker runs before `at`. A time already past makes it due at once. |
| `EnqueueMany(commands)` | Stores every command in one store call, all or none, and returns the ids in order. The database stores write the batch in one round trip (binary `COPY` on Postgres, a table-valued parameter on SQL Server). |

Enqueueing a command that is not a `[Job]` throws `InvalidOperationException`; a payload over
`JobsOptions.MaxPayloadBytes` (1 MiB by default) throws `ArgumentException`.

### Delayed jobs

```csharp
await jobs.Schedule(new SendReminder(orderId), DateTimeOffset.UtcNow.AddHours(24), ct);
```

A delayed job waits in the store, not in memory, so it survives a restart with a database store.

### Recurring jobs

Add `[Recurring]` with a five-field cron expression, in UTC, to a `[Job]` command that has a
parameterless constructor:

```csharp
using SkathIO.Rogue;
using SkathIO.Rogue.Jobs;

[Job, Recurring("0 3 * * *")]   // 03:00 UTC every day
public sealed record PruneCarts : ICommand;
```

Every host that calls `AddRogueJobWorker` takes part, but only one at a time, the leader, stores the
next occurrence, as a delayed job. The store keeps one job per occurrence, so each occurrence runs
once however many hosts there are. If the leader stops, another host takes over once
`JobsOptions.LeaderLeaseDuration` (1 minute) runs out. Occurrences that fall while no host runs are
skipped, not caught up. The expression has the fields minute, hour, day of month, month and day of
week; each is `*`, a value, a range `a-b`, a step `*/n` or `a-b/n`, or a comma list of these. An
invalid expression fails the host at startup.

### Unique jobs

`UniqueKey` keeps at most one pending (enqueued or processing) job of the kind:

```csharp
using SkathIO.Rogue;
using SkathIO.Rogue.Jobs;

[Job(UniqueKey = "rebuild-search-index")]
public sealed record RebuildSearchIndex : ICommand;
```

While such a job is pending, enqueueing or scheduling the command again stores nothing and returns
the pending job's id. Once it has succeeded or been dead-lettered, the next enqueue stores a new job.
In `EnqueueMany`, commands with a unique key are stored one at a time, so each gets the id of the job
that holds the key.

## Retries, backoff and dead-lettering

When a handler throws, or a run passes its timeout, the worker stores the failure and retries the job:

- The first retry waits `BackoffSeconds` (or `JobsOptions.DefaultBackoff`), and each later retry
  waits twice as long, up to `JobsOptions.MaxBackoff` (1 hour).
- `JobsOptions.BackoffJitter` (0.2) takes up to that fraction off each delay at random, so jobs that
  failed together do not retry together.
- After `MaxAttempts` runs have failed, the job is dead-lettered: it stays in the store in the
  `DeadLettered` state with its last error, for `DeadLetteredRetention` (14 days) on the database
  stores. `IJobStore.RequeueAsync(ids)` moves dead-lettered jobs back to `Enqueued` with all their
  attempts.
- A payload that cannot be read is dead-lettered at once.
- A job of a kind this process does not know is put back for later without counting the attempt, so
  during a rolling deploy an older worker leaves a newer kind to a worker that knows it.
- On shutdown, running jobs are cancelled and put back without counting the attempt.

`IJobStore.GetAsync(id)` returns a `JobSnapshot` with the job's state, attempts and last error.

## The worker

The worker claims up to `BatchSize` jobs from a queue in one round trip and runs at most
`MaxConcurrency` at a time. Each claimed job holds a lease of `LeaseDuration` (5 minutes), which the
worker renews until the job's outcome is stored; a job whose worker died is claimed again once its
lease runs out. An enqueue in the same process wakes the worker at once; otherwise it looks for work
every `PollInterval` (1 second), or sooner when the store's own wake-up (Postgres `LISTEN/NOTIFY`, SQL
Server adaptive polling) tells it.

```csharp
builder.Services.AddRogueJobs(o =>
{
    o.SerializerContext = AppJson.Default;
    o.DefaultMaxAttempts = 5;
    o.DefaultBackoff = TimeSpan.FromSeconds(10);
    o.MaxBackoff = TimeSpan.FromHours(1);
    o.BatchSize = 100;
    o.MaxConcurrency = 8;
    o.Queues.Add("default");
    o.Queues.Add("emails");   // empty (the default) means every queue a known job uses
});
```

Outside a host, for example in a test, drain the store yourself instead of calling
`AddRogueJobWorker`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;

await provider.GetRequiredService<IJobScheduler>().Enqueue(new ShipOrder(42));
int ran = await provider.GetRequiredService<JobWorker>().RunOnceAsync();
```

### `JobsOptions.ScopePerBatch`

By default each job runs in a service scope of its own. With `ScopePerBatch = true`, the jobs of one
claimed batch share a single scope, disposed once the last of them has run. That saves building a
scope and its scoped services for every job, which is most of the per-job cost for small jobs (see
the Jobs results in [benchmarks](benchmarks.md#jobs-rogue-vs-hangfire)).

```csharp
builder.Services.AddRogueJobs(o =>
{
    o.SerializerContext = AppJson.Default;
    o.ScopePerBatch = true;
});
```

Turn it on only when every scoped service your job handlers use can be shared by jobs running one
after another and, unless `MaxConcurrency` is 1, at the same time. A `DbContext` cannot.

## Stores

| Store | Package | Registration |
|-------|---------|--------------|
| In-memory (`InMemoryJobStore`) | `SkathIO.Rogue.Jobs` | The default when no other store is registered. Not durable: jobs are lost when the process stops. For tests and single-process tools. |
| PostgreSQL (`PostgresJobStore`) | `SkathIO.Rogue.Jobs.Postgres` | `AddRoguePostgresJobs(dataSource)`. Batched `FOR UPDATE SKIP LOCKED` claims, binary `COPY` bulk enqueue, `LISTEN/NOTIFY` wake-ups. |
| SQL Server (`SqlServerJobStore`) | `SkathIO.Rogue.Jobs.SqlServer` | `AddRogueSqlServerJobs(connectionString)`. Batched `READPAST`/`UPDLOCK` claims, table-valued-parameter bulk enqueue, adaptive polling. |

A database store replaces any store registered before it, and `AddRogueJobs` must still be called,
before or after:

```csharp
using Npgsql;
using SkathIO.Rogue.Jobs;
using SkathIO.Rogue.Jobs.Postgres;

NpgsqlDataSource dataSource = NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Jobs")!);

builder.Services.AddRogueJobs(o => o.SerializerContext = AppJson.Default);
builder.Services.AddRoguePostgresJobs(dataSource, o =>
{
    o.Schema = "rogue_jobs";
    o.SucceededRetention = TimeSpan.FromDays(1);
    o.DeadLetteredRetention = TimeSpan.FromDays(14);
});
```

```csharp
using SkathIO.Rogue.Jobs.SqlServer;

builder.Services.AddRogueSqlServerJobs(builder.Configuration.GetConnectionString("Jobs")!, o =>
{
    o.Schema = "rogue_jobs";
    o.MinPollInterval = TimeSpan.FromMilliseconds(100);
    o.MaxPollInterval = TimeSpan.FromSeconds(2);
});
```

Both database stores keep one row per job, with its state as a column, in the schema `Schema` names
(lower-case letters, digits and underscores, up to 50 characters). With `AutoMigrate` on (the
default) the store creates or upgrades its tables before its first command; turn it off and call the
store's `MigrateAsync` from a deployment step if the application's database user may not change the
schema. A pruner deletes succeeded jobs after `SucceededRetention` and dead-lettered ones after
`DeadLetteredRetention`.

### Enqueue in your own transaction (Postgres)

`IPostgresJobScheduler` enqueues on your own `NpgsqlConnection` or `NpgsqlTransaction`, so a job
commits or rolls back with the data it was enqueued for, and workers are woken only once it commits:

```csharp
using Npgsql;
using SkathIO.Rogue.Jobs.Postgres;

await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct);
await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
// ... write the order with the same transaction ...
await postgresJobs.Enqueue(new ShipOrder(orderId), transaction, ct);
await transaction.CommitAsync(ct);
```

To publish a message in your transaction instead, use the [outbox](outbox.md).

## The serializer context and ROGUE019

Job payloads are serialized only through the `JsonSerializerContext` in
`JobsOptions.SerializerContext`, never by reflection. It is required once any `[Job]` command exists,
and each `[Job]` command must be covered by a `[JsonSerializable(typeof(...))]` attribute on it.

- **At build time**, the analyzer that ships in the package reports **`ROGUE019`** (warning) for a
  `[Job]` type that no `[JsonSerializable]` attribute on the project's context covers. Its code fix
  adds the attribute.
- **At startup**, the job registry fails, naming each uncovered command, the first time the
  scheduler or the worker is resolved.

A command without `[Job]` is never a job, whether the context covers it or not, and `ROGUE019`
ignores it.

## Telemetry

The scheduler and worker emit traces and metrics under `RogueJobsTelemetry.Name`
(`"SkathIO.Rogue.Jobs"`). Logs carry a job's id and kind, never its payload.

## See also

- [Messaging](messaging.md) — send messages between services; a job handler can publish them.
- [Outbox and inbox](outbox.md) — publish messages in your database transaction, relayed by jobs.
- [Benchmarks](benchmarks.md#jobs-rogue-vs-hangfire) — Rogue jobs against Hangfire.
