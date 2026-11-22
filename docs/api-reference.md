# API reference

The authoritative public surface is tracked per package in `PublicAPI.Shipped.txt` (enforced by the
`public-api` CI gate). This page is a human-readable map of it. Every package targets `net8.0` and
`net10.0` and exposes the same surface on both.

## Packages

| Package | Contents |
|---------|----------|
| `SkathIO.Rogue` | Runtime: `AddRogue`, dispatcher, options, telemetry, publishers. Bundles the source generator as an analyzer asset. |
| `SkathIO.Rogue.Abstractions` | Contracts only: the message/handler/behavior interfaces, `Unit`, marker types. Depend on this from layers that define messages but should not pull the runtime. |
| `SkathIO.Rogue.Logging` | `LoggingBehavior`, `LoggingOptions`, `[LogPayload]`. |
| `SkathIO.Rogue.Validation` | Source-generated validation for `[Validate]` requests: `ValidateAttribute`, the rule attributes and `RuleAttribute`, `ValidationErrors`, `IAsyncRules<T>`, `RogueValidationException` with `ToProblemDictionary()`. See [validation](validation.md). |
| `SkathIO.Rogue.Validation.FluentValidation` | `ValidationBehavior` over FluentValidation. |
| `SkathIO.Rogue.MediatR` | MediatR compatibility shim + migration analyzer (ROGM001–006) bundled as an analyzer asset. |
| `SkathIO.Rogue.Jobs` | Background jobs: `[Job]`, `[Recurring]`, `IJobScheduler`, `JobWorker`, `JobsOptions`, the `IJobStore` contract and `InMemoryJobStore`, `AddRogueJobs` / `AddRogueJobWorker`. Bundles the ROGUE019 serializer-coverage analyzer and its code fix. See [jobs](jobs.md). |
| `SkathIO.Rogue.Jobs.Postgres` | PostgreSQL job store: `AddRoguePostgresJobs`, `PostgresJobStore`, `PostgresJobsOptions`, `IPostgresJobScheduler` (enqueue in your own transaction). See [jobs](jobs.md#stores). |
| `SkathIO.Rogue.Jobs.SqlServer` | SQL Server job store: `AddRogueSqlServerJobs`, `SqlServerJobStore`, `SqlServerJobsOptions`. See [jobs](jobs.md#stores). |
| `SkathIO.Rogue.Messaging` | Message bus: `[Message]`, `IMessageBus`, `MessageConsumer`, `MessagingOptions`, `MessageEnvelope`, the `IMessageTransport` contract and `InMemoryTransport`, `IMessageInbox`, `AddRogueMessaging` / `AddRogueMessageConsumer`. See [messaging](messaging.md). |
| `SkathIO.Rogue.Messaging.RabbitMQ` | RabbitMQ transport: `AddRogueRabbitMqTransport`, `RabbitMqTransport`, `RabbitMqTransportOptions`. See [RabbitMQ](messaging-rabbitmq.md). |
| `SkathIO.Rogue.Messaging.Outbox` | Transactional outbox and inbox on the database job stores: `IMessageOutbox`, `MessageOutboxOptions`, `AddRogueMessagingOutbox`. See [outbox](outbox.md). |

## Abstractions (`SkathIO.Rogue.Abstractions`)

**Messages** (CQS-explicit core — each an independent contract)

- `IQuery<TResponse>` — a read. Dispatched by `ISender.Send`.
- `ICommand` / `ICommand<TResponse>` — a write, void or with a response. Dispatched by `ISender.Send`.
- `IEvent` — a fan-out message. Dispatched by `IPublisher.Publish`.
- `IStreamQuery<TItem>` — a streaming read returning `IAsyncEnumerable<TItem>` (net8.0+).
- `Unit` — the void response type (value type; `Unit.Value`, `Unit.Task`).

The MediatR-shaped surface (`IRequest`, `INotification`, `IStreamRequest`, …) is **not** in this
package; it lives in `SkathIO.Rogue.MediatR` for migration — see
[Compatibility](#compatibility-skathioroguemediatr) below.

**Handlers**

- `IQueryHandler<TQuery, TResponse>` — one handler per query.
- `ICommandHandler<TCommand, TResponse>` / `ICommandHandler<TCommand>` — one handler per command.
- `IEventHandler<TEvent>` — zero-to-many handlers per event (fan-out).
- `IStreamQueryHandler<TQuery, TItem>` (net8.0+)

**Pipeline**

- `IPipelineBehavior<TRequest, TResponse>` + `RequestHandlerDelegate<TResponse>`
- `IStreamPipelineBehavior<TRequest, TResponse>` + `StreamHandlerDelegate<TResponse>` (net8.0+)
- `IRequestPreProcessor<TRequest>`, `IRequestPostProcessor<TRequest, TResponse>`
- `IRequestExceptionAction<TRequest, TException>`
- `IRequestExceptionHandler<TRequest, TResponse, TException>` + `RequestExceptionHandlerState<TResponse>`
- `BehaviorOrderAttribute(int order)` — declarative behavior ordering.

**Dispatch entry points**

- `ISender` — `Send<TResponse>(IQuery<TResponse>)`, `Send<TResponse>(ICommand<TResponse>)`,
  `Send(ICommand)`, `Send(object)`, `CreateStream<TItem>(IStreamQuery<TItem>)`.
- `IPublisher` — `Publish(IEvent)`, `Publish(object)`.
- `IMediator` — combines `ISender` + `IPublisher`.

**Event publishing**

- `IEventPublisher` — fan-out strategy contract:
  `Publish<TEvent>(IReadOnlyList<IEventHandler<TEvent>>, TEvent, CancellationToken)`. Implementations
  receive the resolved, strongly-typed handlers directly — there is no wrapper/executor type.

**Inspection**

- `IRoguePipelineInspector` — `GetPipeline<TRequest>()` / `GetPipeline(Type)` returns the ordered `BehaviorInfo` list.
- `BehaviorInfo(Type BehaviorType, int Order, string Source)` — a resolved pipeline entry.

## Runtime (`SkathIO.Rogue`)

**Registration**

- `RogueServiceCollectionExtensions.AddRogue(this IServiceCollection, Action<RogueOptions>? configure = null)`
- `RogueOptions`:
  - `EnableObjectDispatch` (bool) — opt into untyped `Send(object)`/`Publish(object)`.
  - `EnableTelemetry` (bool) — turn on the telemetry shim.
  - `Lifetime` (`ServiceLifetime`) — DI lifetime applied to discovered handlers (default `Transient`). Note: this does not control pipeline behaviors, which are **always** registered `Transient` regardless of this setting (avoids a captive-dependency trap for behaviors with a Scoped dependency — see the [behaviors guide](behaviors.md#validation-behavior)), nor the `RogueDispatcher` registration, which is `Scoped`.
  - `NotificationPublisher` (`IEventPublisher`) — fan-out strategy.
  - `AddBehavior<TBehavior>(int order = 0)`, `AddOpenBehavior(Type, int order = 0)`, `BehaviorRegistrations`.
- `BehaviorRegistration(Type BehaviorType, int Order, bool IsOpen)` — a registered behavior.
- `RogueRegistrationBridge.GeneratedRegistrar` — the static seam the generated module initializer
  wires `AddRogue` to. Public for the generator's emitted code; not a consumer API.

**Dispatcher**

- `RogueDispatcher` — base type the generator subclasses (`RogueDispatcherImpl`). Implements
  `Send`/`Publish`/`CreateStream`/`SendObject`.
- `Mediator` — `IMediator` implementation.

**Concrete fast path (0-alloc) — `RogueExtensions`**

For the hottest send paths the generator emits `SkathIO.Rogue.Generated.RogueExtensions`, a static
class with one typed extension method **per command/query handler** on `RogueDispatcher`. It bypasses
the `ISender` interface dispatch (which boxes one `ValueTask<T>` by design) and is **0 B allocated**
for a synchronously-completing, behavior-free handler.

- **Inject `RogueDispatcher`**, not `ISender` — the fast path downcasts to the generated impl, and
  `ISender` resolves to `Mediator` (the cast would fail). `RogueDispatcher` is registered `Scoped`.
- **Naming**: `Send{RequestSimpleName}` — e.g. a `PingRequest` handler yields `SendPingRequest`. Two
  request types sharing a simple name across namespaces produce valid overloads (resolved by the
  request parameter type).
- **`using SkathIO.Rogue.Generated;`** must be in scope to call the extension methods.
- **Void commands** return `ValueTask<Unit>` (discard the `Unit`) to keep the path allocation-free.
- The method is emitted `internal` (still callable in-assembly) when the request or response type is
  not `public`, to respect accessibility.

```csharp
using SkathIO.Rogue;
using SkathIO.Rogue.Generated;

public sealed class Endpoint(RogueDispatcher dispatcher)   // inject the dispatcher, not ISender
{
    public ValueTask<string> Ping() => dispatcher.SendPingRequest(new PingRequest("ping"));
}
```

**Trade-off**: `ISender.Send(...)` stays the portable, decoupled default (one box). Use the
`RogueDispatcher` fast path only where the allocation matters and a direct dependency on the concrete
dispatcher is acceptable.

**Publishers**

- `ForeachAwaitPublisher` (default), `WhenAllPublisher`.

**Pipeline execution**

- `PipelineExecutor.Execute<TRequest, TResponse>(...)` — the fallback fold used by the runtime path.

**Telemetry**

- `RogueTelemetry` — `Name = "SkathIO.Rogue"`, `Enabled`, `StartDispatch<TRequest>()`, `StopDispatch(...)`.
- `DispatchScope`, `ValueStopwatch` — allocation-free timing helpers.

**Errors**

- `RogueUnregisteredRequestException(Type requestType)` — thrown when a request has no generated route
  (usually means the generator did not run in the dispatching compilation — see
  [getting-started](getting-started.md#troubleshooting)).

## Compatibility (`SkathIO.Rogue.MediatR`)

For drop-in MediatR migration. Types live in the `SkathIO.Rogue.Compatibility` namespace unless noted.

- **MediatR-shaped messages/handlers** — `IRequest` / `IRequest<TResponse>`,
  `IRequestHandler<TRequest, TResponse>` / `IRequestHandler<TRequest>`, `INotification`,
  `INotificationHandler<TNotification>`, `IStreamRequest<TResponse>`,
  `IStreamRequestHandler<TRequest, TItem>` (net8.0+), `IPipelineBehavior<TRequest, TResponse>`,
  `IMediator` / `ISender` / `IPublisher`, `Unit`, `ServiceFactory`.
- **Registration shim** — `MediatRCompatExtensions.AddMediatR(this IServiceCollection, …)` +
  `MediatRCompatOptions` (`RegisterServicesFromAssembly…`), forwarding to `AddRogue`.
- **Reflection escape hatch** — `ReflectionMediator` (obsolete; not AOT-safe) for open-generic request
  types the generator cannot close at compile time.
- **`[MapAsQuery]`** (`SkathIO.Rogue.MediatR.MapAsQueryAttribute`) — maps an otherwise-ambiguous MediatR
  request onto the CQS `IQuery` side during migration.

Migration analyzer diagnostics `ROGM001`–`ROGM006` — see [migration-guide](migration-guide.md).

## Jobs (`SkathIO.Rogue.Jobs`, `.Jobs.Postgres`, `.Jobs.SqlServer`)

Namespaces `SkathIO.Rogue.Jobs`, `SkathIO.Rogue.Jobs.Postgres` and `SkathIO.Rogue.Jobs.SqlServer`.
Guide: [jobs](jobs.md).

- **Declaring** — `JobAttribute` (`Kind`, `Queue`, `MaxAttempts`, `BackoffSeconds`,
  `TimeoutSeconds`, `UniqueKey`) and `RecurringAttribute(string cron)`.
- **Registration** — `JobsServiceCollectionExtensions.AddRogueJobs(Action<JobsOptions>?)`,
  `AddRogueJobWorker()`; `AddJobDefinition` and `AddRecurringJob` are called by generated code.
- **`JobsOptions`** — `SerializerContext`, `DefaultQueue`, `Queues`, `DefaultMaxAttempts`,
  `DefaultBackoff`, `MaxBackoff`, `BackoffJitter`, `BatchSize`, `MaxConcurrency`, `ScopePerBatch`,
  `PollInterval`, `LeaseDuration`, `MaxPayloadBytes`, `LeaderLeaseDuration`.
- **Scheduling** — `IJobScheduler`: `Enqueue`, `Schedule`, `EnqueueMany`, and `CreateJob` /
  `CreateJobs` for a store's own enqueue.
- **Running** — `JobWorker`: `RunAsync(stoppingToken)`, `RunOnceAsync()`.
- **Stores** — `IJobStore` (with `GetAsync` returning a `JobSnapshot`, and `RequeueAsync` for
  dead-lettered jobs), `ITransactionalJobStore`, `IMessageInboxStore`, `InMemoryJobStore`, `NewJob`,
  `ClaimedJob`, `JobLease`, `JobState`; `DatabaseJobsOptions` (`Schema`, `AutoMigrate`,
  `SucceededRetention`, `DeadLetteredRetention`, `PruneInterval`, `PruneBatchSize`).
- **Postgres** — `AddRoguePostgresJobs(NpgsqlDataSource, Action<PostgresJobsOptions>?)`,
  `PostgresJobStore` (`MigrateAsync`), `PostgresJobsOptions` (`ListenReconnectDelay`),
  `IPostgresJobScheduler` (`Enqueue` / `EnqueueMany` on an `NpgsqlTransaction` or `NpgsqlConnection`).
- **SQL Server** — `AddRogueSqlServerJobs(string connectionString, Action<SqlServerJobsOptions>?)`,
  `SqlServerJobStore` (`MigrateAsync`), `SqlServerJobsOptions` (`MinPollInterval`, `MaxPollInterval`).
- **Telemetry** — `RogueJobsTelemetry.Name = "SkathIO.Rogue.Jobs"`.

## Messaging (`SkathIO.Rogue.Messaging`, `.Messaging.RabbitMQ`, `.Messaging.Outbox`)

Namespaces `SkathIO.Rogue.Messaging`, `SkathIO.Rogue.Messaging.RabbitMQ` and
`SkathIO.Rogue.Messaging.Outbox`. Guides: [messaging](messaging.md), [RabbitMQ](messaging-rabbitmq.md),
[outbox and inbox](outbox.md).

- **Declaring** — `MessageAttribute(string name)`; `MessageLimits` (name and id limits).
- **Registration** — `MessagingServiceCollectionExtensions.AddRogueMessaging(Action<MessagingOptions>?)`,
  `AddRogueMessageConsumer()`; `AddMessageBinding` is called by generated code.
- **`MessagingOptions`** — `SerializerContext`, `MaxConcurrency`, `MaxAttempts`, `MaxMessageBytes`,
  `MaxJsonDepth`, `BatchSize`, `RetryDelay`, `MaxRetryDelay`, `PollInterval`.
- **Sending** — `IMessageBus`: `Publish`, `CreateEnvelope`; `MessageEnvelope`.
- **Consuming** — `MessageConsumer`: `RunAsync(stoppingToken)`, `RunOnceAsync()`; `IMessageInbox`
  with `InboxClaim` / `InboxClaimResult`.
- **Transports** — `IMessageTransport`, `TransportCapabilities`, `ReceivedMessage`, `InMemoryTransport`
  with `DeadLetteredMessage`.
- **RabbitMQ** — `AddRogueRabbitMqTransport(Action<RabbitMqTransportOptions>)`, `RabbitMqTransport`,
  `RabbitMqTransportOptions` (`ConnectionUri`, `ConfigureConnection`, `Exchange`, `ServiceName`,
  `DeliveryStampKey`, `PrefetchCount`, `WaitQueue`, `MapRoutingKey`, `MapHeader`).
- **Outbox and inbox** — `AddRogueMessagingOutbox(Action<MessageOutboxOptions>?)`,
  `IMessageOutbox.Publish(message, DbTransaction)`, `MessageOutboxOptions` (`InboxLease`,
  `MaxInboxLease`).
- **Telemetry** — `RogueMessagingTelemetry.Name = "SkathIO.Rogue.Messaging"`.

## Diagnostics

Compile-time generator diagnostics: `ROGUE001`–`ROGUE006` + `ROGUE010`–`ROGUE018`
(`ROGUE007` is intentionally unused) — see
[getting-started](getting-started.md#troubleshooting); `ROGUE014`–`ROGUE018` cover validation
misuse (see [validation](validation.md#diagnostics)). Migration analyzer diagnostics:
`ROGM001`–`ROGM006` (see [migration-guide](migration-guide.md)).
Jobs analyzer diagnostic: `ROGUE019`, a `[Job]` type the `JsonSerializerContext` does not cover (see
[jobs](jobs.md#the-serializer-context-and-rogue019)).
