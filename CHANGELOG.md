# Changelog

All notable changes to SkathIO.Rogue are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **New package: `SkathIO.Rogue.Jobs`**, background jobs on Rogue commands. An `ICommand` or
  `ICommand<T>` with a handler and a `[Job]` attribute is a job: enqueue it with
  `IJobScheduler.Enqueue(command)`, or many at once with `IJobScheduler.EnqueueMany(commands)` (one
  store call), and a `JobWorker` runs its handler through the full pipeline, in a service scope of
  its own. A command without `[Job]` is never run as a job, so a row written straight into the jobs
  table cannot send an arbitrary command. `[Job]` can set the kind, queue, attempts, first retry
  delay and timeout. A failed run is retried after a doubling, capped, jittered backoff, then
  dead-lettered; an unreadable payload is dead-lettered at once. A job whose kind this process does
  not know is put back after the default backoff without counting the attempt, so during a rolling
  deploy an older worker leaves a newer kind for a worker that knows it. `IJobStore.RequeueAsync(ids)`
  moves dead-lettered jobs back to ready with all their attempts. Stored errors have NUL characters
  removed. On shutdown running jobs are put back without counting the attempt.
  The core source generator emits the kind-to-invoker map, so jobs are found by a stable kind, never
  by a type name, and requests are dispatched exactly as before. Payloads go through your
  System.Text.Json `JsonSerializerContext` (`JobsOptions.SerializerContext`), checked at startup
  for `[Job]` commands, the same commands the build checks (ROGUE019), so adding the package never
  fails startup without a build warning. Every claimed job keeps its lease,
  renewed while it waits for a slot or runs, until its outcome is stored, and a lease the store no
  longer holds is logged as a warning. `JobsOptions.LeaseDuration` and `LeaderLeaseDuration` are at
  most 30 days (`JobsOptions.MaxLeaseDuration`), so a store adding either to the current time never overflows. `IJobStore` is the storage contract, with `InMemoryJobStore` as the default store; durable stores
  follow. Telemetry comes from its own `SkathIO.Rogue.Jobs` activity source and meter. Register it
  with `AddRogueJobs()` next to `AddRogue()`, which is enough to enqueue; add `AddRogueJobWorker()`
  in each host that should run jobs, which registers a hosted service that runs the worker while the
  host runs (the package depends on `Microsoft.Extensions.Hosting.Abstractions`). Targets `net8.0` and `net10.0`; no reflection, AOT-safe.
- **New package: `SkathIO.Rogue.Jobs.Postgres`**, a durable PostgreSQL job store. Register it with
  `AddRoguePostgresJobs(dataSource)` next to `AddRogueJobs()`. `IPostgresJobScheduler.Enqueue` and
  `EnqueueMany` take your own `NpgsqlTransaction` or `NpgsqlConnection`, so jobs commit or roll back
  with your data. A batch is stored with one binary COPY, workers claim batches with
  `FOR UPDATE SKIP LOCKED`, and each enqueue sends a NOTIFY on commit that wakes idle workers in
  every process, with the poll interval as the fallback when the LISTEN connection is down. Jobs whose
  worker stopped are claimed again once their lease runs out. Concurrent completions are written
  together in one statement, and a pruner deletes finished jobs past their retention
  (`PostgresJobsOptions`). The store creates and upgrades its own schema (`rogue_jobs` by default)
  under an advisory lock, or you call `PostgresJobStore.MigrateAsync()` yourself with
  `AutoMigrate = false`. The store passes the same contract tests as the in-memory store. Targets
  `net8.0` and `net10.0`; depends on Npgsql; no reflection, AOT-safe.
- **New package: `SkathIO.Rogue.Jobs.SqlServer`**, a durable SQL Server job store. Register it with
  `AddRogueSqlServerJobs(connectionString)` next to `AddRogueJobs()`. A batch is stored with one
  INSERT over a table-valued parameter, and workers claim batches in one statement under `READPAST`
  and `UPDLOCK`, so they never wait on each other's rows. SQL Server has no LISTEN/NOTIFY, so an
  adaptive poller probes for newly due jobs and wakes idle workers: it probes again at
  `MinPollInterval` after a find and backs off to `MaxPollInterval` while idle. Jobs whose worker
  stopped are claimed again once their lease runs out, dead-lettered jobs can be requeued, and a
  pruner deletes finished jobs past their retention (`SqlServerJobsOptions`). The store creates and
  upgrades its own schema (`rogue_jobs` by default) under `sp_getapplock`, or you call
  `SqlServerJobStore.MigrateAsync()` yourself with `AutoMigrate = false`. The store passes the same
  contract tests as the other stores. Targets `net8.0` and `net10.0`; depends on
  `Microsoft.Data.SqlClient`, whose native `Microsoft.Data.SqlClient.SNI.runtime` is under the
  Microsoft Software License Terms, a documented exception to the permissive-license policy; no
  reflection, AOT-safe.
- `DatabaseJobsOptions`, the options both database stores share (`Schema`, `AutoMigrate`,
  retention, prune interval and batch size), with a public `Validate()`. `PostgresJobsOptions` and
  `SqlServerJobsOptions` derive from it.
- `IJobScheduler.CreateJob(command)` serializes a command into a `NewJob` without storing it, for a
  store's own enqueue; `JobSignal.Pulse()` lets a store wake the worker; `JobLimits.CheckQueue`
  checks a queue name against the store contract.
- Job scheduling on every store (in-memory, Postgres, SQL Server):
  - `IJobScheduler.Schedule(command, at)` stores a delayed job that no worker runs before `at`;
    a time already past is due at once. `NewJob.RunAt` carries the time for a store's own enqueue.
  - `[Recurring("30 * * * *")]` on a parameterless `[Job]` command runs it at each occurrence of a
    five-field cron expression, in UTC, parsed in-house (no cron package). Each host that calls
    `AddRogueJobWorker()` runs a scheduler; one at a time leads, through a leader lock in the store
    (`JobsOptions.LeaderLeaseDuration`, default one minute), and stores the next occurrence as a
    delayed job. Each run is stored once per task key and run time, unique in the store, so two hosts
    never run one occurrence twice. Occurrences that fall while no host runs are skipped, not caught
    up. A recurring command that is not a `[Job]`, has no parameterless constructor, or whose cron
    expression is invalid fails the host at startup, naming it. `AddRecurringJob(new
    RecurringJob<T>(cron, create))` registers one in code.
  - `[Job(UniqueKey = "...")]`: while a job of that kind holding the key is enqueued or processing,
    enqueueing or scheduling the command again stores nothing and returns the pending job's id. The
    database stores keep it under a partial unique index.
  - `IJobStore` gains `EnqueueOnceAsync` (a keyed enqueue that returns the id of the job holding
    the key), `TryAcquireLeadershipAsync` and `ReleaseLeadershipAsync`; a batch `EnqueueAsync`
    rejects keyed jobs. `RequeueAsync` skips a dead-lettered job whose unique key a pending job
    holds. Both database stores add these through a second schema migration, applied on startup
    with `AutoMigrate` or by `MigrateAsync()`.
  - `IJobScheduler.CreateJobs(commands)` serializes a batch of commands into `NewJob`s without
    storing them, for a store's own batch enqueue.
- `JobsOptions.ScopePerBatch`: the jobs of one claimed batch share one service scope, disposed once
  the last of them has run, instead of a scope each. Off by default, since a scoped service such as
  a `DbContext` cannot be shared by jobs that run at the same time.
- New benchmark: Rogue's jobs against Hangfire (`bench/SkathIO.Rogue.Benchmarks.Jobs`), at 1k, 10k
  and 100k small jobs, against Hangfire.InMemory and against Hangfire.SqlServer on the same database
  as Rogue's SQL Server store. A CI smoke gates Rogue's allocation per job against
  `.bench/thresholds.json`. Hangfire is bench only and ships in no package.
- `ROGUE019` warning, with a code fix: a `[Job]` type is not covered by a `[JsonSerializable]`
  attribute on the project's `JsonSerializerContext`. The fix adds the attribute.
- **New package: `SkathIO.Rogue.Validation`**, source-generated request validation next to the
  FluentValidation package (which stays supported). Mark a request `[Validate]` and `partial`, then
  declare rules as attributes (`[NotEmpty]`, `[MinimumLength]`, `[MaximumLength]`, `[GreaterThan]`,
  `[LessThan]`, or your own `RuleAttribute`), in a `private static void Rules(T x, ref ValidationErrors
  errors)` hook, and in `IAsyncRules<T>` classes resolved from DI (registered Scoped by the generator).
  Nested `[Validate]` objects and collections are checked with ASP.NET-style paths such as
  `Lines[2].Sku`. The checks run inside the request's generated dispatch, before the handler, and a
  failure throws `RogueValidationException`, whose `ToProblemDictionary()` feeds
  `Results.ValidationProblem`. The valid path allocates nothing, and requests without `[Validate]`
  are generated exactly as before. Targets `net8.0` and `net10.0`; no reflection, AOT-safe. See
  `docs/validation.md`.
- Generator diagnostics for validation misuse: `ROGUE014` (a `[Validate]` type is not partial),
  `ROGUE015` (a rule does not fit its property), `ROGUE016` (a `Rules` method has the wrong
  signature) and `ROGUE017` (a `[Validate]` type from an assembly built without the generator) are
  errors; `ROGUE018` warns when rules are declared for a type without `[Validate]`.
- **`ROGUE013` warning: feature package version mismatch.** The upcoming feature packages
  (`SkathIO.Rogue.Validation`, `SkathIO.Rogue.Jobs`, `SkathIO.Rogue.Messaging`) have no generator of
  their own; the core source generator emits their code. When one of them is referenced with a
  different major.minor version than the core `SkathIO.Rogue` package, the build warns. Patch
  versions may differ. The version is read from each assembly's file version.
- The core source generator recognises the feature-module marker attributes
  (`SkathIO.Rogue.Validation.ValidateAttribute`, `SkathIO.Rogue.Jobs.JobAttribute`,
  `SkathIO.Rogue.Messaging.MessageAttribute`). A request that carries one gets a pre-handler step in
  its generated `Send_X`, ahead of handler resolution, for that module to fill in. Requests without a
  marker are generated exactly as before and keep their zero-allocation direct path.
- **New package: `SkathIO.Rogue.Messaging`**, a message bus between services on Rogue handlers. A
  type marked `[Message("orders.created")]` travels under that stable name, never its type name. An
  inbound event is published to its handlers through `IPublisher` and an inbound command sent to its
  handler through `ISender`, by code the core source generator emits, with no reflection or
  type-name lookup. `IMessageBus.Publish(message)` sends any marked type, and an event marked
  `[Message]` that you publish through `IPublisher` is also sent to the bus once its local handlers
  have succeeded (an event handled because it arrived from the bus is not sent back). Such an event
  carries the transport's `ServiceName` as its `Origin`, and the consumer drops an event whose origin
  is its own service, so a service that consumes the events it publishes runs their local handlers
  once, while other services still receive them; commands are always handled. Each message
  travels in a `MessageEnvelope` with an id, its name, a content type, correlation and causation ids
  (taken from the message being handled, if any) and the W3C `traceparent`. `IMessageTransport` is
  the transport contract: batch send, receive, ack, nack with a delay, defer (put back unhandled,
  with the same delivery count), dead-letter, and capability
  flags (`NativeDelay`, `NativeDeadLetter`, `BatchSend`) whose gaps the consumer fills in.
  `InMemoryTransport` keeps messages in memory, for tests and single-process development. The
  `MessageConsumer` runs at most `MaxConcurrency` handlers at once, each in its own service scope,
  redelivers a failed message after a doubling, capped delay until `MaxAttempts` deliveries have
  failed, then dead-letters it. On a transport without native delay the consumer waits out the
  retry delay itself, but a message waiting does not hold one of the `MaxConcurrency` slots, so
  failing messages cannot stall the others. A message with an unknown name, a body over `MaxMessageBytes`, or a
  body that is not JSON of its type within `MaxJsonDepth` is dead-lettered at once without running
  any handler. The reason sent with a dead-lettered message names the exception's type but never its
  message, which stays in the log. Logs carry a message's id and name, never its body. Bodies go through your
  System.Text.Json `JsonSerializerContext` (`MessagingOptions.SerializerContext`), checked at
  startup; a name taken by two types also fails startup. Telemetry comes from its own
  `SkathIO.Rogue.Messaging` activity source and meter, and the consume activity continues the
  publisher's trace. Register it with `AddRogueMessaging()` next to `AddRogue()` and an
  `IMessageTransport`; add `AddRogueMessageConsumer()` in each host that should consume. Targets
  `net8.0` and `net10.0`; no reflection, AOT-safe.
- **New package: `SkathIO.Rogue.Messaging.RabbitMQ`**, the RabbitMQ transport for
  `SkathIO.Rogue.Messaging`. Register it with `AddRogueRabbitMqTransport(options => ...)`. Every
  message is published to one durable topic exchange (`rogue` by default) under its message name, with
  publisher confirms. Each consuming service (`ServiceName`) gets its own durable queue bound to the
  names it handles, and a dead-letter exchange and queue for the messages it gives up on. Messages are
  taken with a prefetch limit and acked only after their handlers succeed. The connection reconnects
  and recovers its topology on its own. JSON from systems that do not use Rogue is mapped to a Rogue
  message by its routing key (`MapRoutingKey`) or by a header (`MapHeader`), never by a type name;
  a message from another exchange that no map names is dead-lettered unread. A message on the
  service's own exchange is always named by its routing key, which its queue was bound by: a header
  map never renames it, and a routing-key map for the own exchange fails registration. The transport's service
  name is its `ServiceName`, and an event's origin travels in the `rogue-origin` header.
  A message's delivery count is signed by the transport on each copy it sends back for a retry
  (`rogue-delivery-stamp`, an HMAC-SHA256 under `DeliveryStampKey`), and read only from that
  signature, never from the `rogue-delivery-count` header, so a sender cannot cut short the retries
  of its messages by setting a header or reusing an id. The count travels on the copy, so any number
  of messages can be in retry at once. `DeliveryStampKey` is a required secret of at least 32 bytes
  (UTF-8) with no default: registration fails at startup without it. Give every instance of a service
  the same key, from configuration or user-secrets, so a retry taken by another instance keeps its
  count and a message that always fails is dead-lettered after exactly `MaxAttempts` deliveries. The
  key is never logged or put in an exception. See [docs/messaging-rabbitmq.md](docs/messaging-rabbitmq.md).
  Targets `net8.0` and `net10.0`; depends on `RabbitMQ.Client`; no reflection.
- **New package: `SkathIO.Rogue.Messaging.Outbox`**, a transactional outbox and inbox on the
  Postgres and SQL Server job stores. Register it with `AddRogueMessagingOutbox()` next to
  `AddRogueMessaging()` and `AddRogueJobs()` with a database store.
  `IMessageOutbox.Publish(message, transaction)` stores the message as a job inside your own
  `DbTransaction`: once the transaction commits, a job worker relays it to the transport, and if it
  rolls back nothing is sent. The inbox, kept in the same store, records each message id the consumer
  handles, so a message delivered twice runs its handlers once (`MessageOutboxOptions.InboxLease`
  bounds how long one delivery holds the id, at most 30 days: `MessageOutboxOptions.MaxInboxLease`). A delivery put back because another one holds its id
  does not use up one of its `MaxAttempts`. `IMessageInbox` is the consumer's inbox contract in
  `SkathIO.Rogue.Messaging`. Targets `net8.0` and `net10.0`; adds no package of its own.
- A native AOT sample for the jobs and messaging packages
  (`samples/SkathIO.Rogue.Jobs.Messaging.Aot.Sample`): a job publishes a message that a handler
  consumes. CI publishes it with native AOT and runs it on every pull request.

### Removed

- **Breaking: `netstandard2.0` is no longer a target.** `SkathIO.Rogue.Abstractions`, `SkathIO.Rogue`,
  `SkathIO.Rogue.MediatR`, `SkathIO.Rogue.Logging` and `SkathIO.Rogue.Validation.FluentValidation`
  now target `net8.0` and `net10.0` only. .NET Framework applications and libraries that target
  `netstandard2.0` can no longer reference them; stay on 1.x for those. The netstandard2.0-only
  surface is gone with it: the `ValueTask<Unit>`-returning void handler, processor, publisher and
  `Publish` signatures (net8.0+ always returned bare `ValueTask`), and the
  `System.Threading.Tasks.Extensions`, `Microsoft.Bcl.AsyncInterfaces` and
  `System.Diagnostics.DiagnosticSource` package dependencies. The source generators and the
  migration analyzer still target `netstandard2.0`, because the compiler loads analyzers only from
  that target. Generated code no longer carries `#if !NETSTANDARD2_0` guards.

### Changed

- **Breaking: `SkathIO.Rogue.Validation.FluentValidation` now depends on FluentValidation 12**
  (12.2.0, up from 11.12.0). FluentValidation 12 targets net8.0 only and removes some APIs that
  were obsolete in 11; check FluentValidation's 12.0 upgrade notes if your validators still use
  them.

## [1.2.1] - 2026-11-10

### Changed

- **Dependency updates.** Build tooling and test dependencies are refreshed: MinVer 8, SourceLink
  10.0.403 (patched for CVE-2026-62900), `Microsoft.CodeAnalysis.Analyzers` 5.9, the
  `Microsoft.Extensions.*` 10.0.14 packages, and the test SDK and runner. `Microsoft.CodeAnalysis.CSharp`
  deliberately stays at 5.0.0, because it is compiled into the shipped generator and a higher pin
  breaks consumers on older SDKs. No public API or behavior change.

## [1.2.0] - 2026-07-16

### Added

- **FluentValidation validator auto-discovery.** `SkathIO.Rogue.Validation.FluentValidation` now
  discovers every concrete, public-constructible `AbstractValidator<T>` (`IValidator<T>` implementor)
  declared in a compilation that references the package, at compile time via a dedicated source
  generator, and registers it into DI automatically (`TryAddEnumerable`, hard-pinned `Scoped`
  lifetime, independent of `RogueOptions.Lifetime`, so a validator with a Scoped dependency such as a
  `DbContext` stays safe even under `Lifetime = Singleton`). Referencing the package and writing a
  validator is now the entire contract — no wiring call of any kind exists or is offered, exactly
  matching how commands/queries/handlers are already discovered. **Note:** this is a different
  lifetime pin than the 1.1.0 fix below, despite both mentioning Scoped/`DbContext`/`Singleton` —
  1.1.0 pinned pipeline *behavior* registrations to `Transient`; this pins the new *validator*
  registrations this discovery mechanism introduces to `Scoped`, one layer further down. See
  [Validation behavior](docs/behaviors.md#validation-behavior).

### Changed

- **Behavior change on upgrade: validators are now discovered automatically, with no opt-in and no
  gate.** If you already reference `SkathIO.Rogue.Validation.FluentValidation` and kept a validator
  around deliberately unwired (e.g. for ad-hoc/manual use, never registered into DI), it is now swept
  into the pipeline automatically after upgrading — there is no configuration call to prevent this.
  If you already register a validator manually (`services.AddScoped<IValidator<T>, TValidator>()` or
  `AddValidatorsFromAssemblyContaining<T>()`), remove that registration — otherwise the validator is
  registered twice, which can duplicate its failure message in the aggregated `ValidationException`.
  To opt a specific project out of *validator* discovery entirely while keeping `ValidationBehavior<,>`
  itself available at runtime, add `ExcludeAssets="analyzers"` to that project's `PackageReference`
  for `SkathIO.Rogue.Validation.FluentValidation` — this suppresses only this package's generator (an
  `analyzers/dotnet/cs` asset); the separate `SkathIO.Rogue` package's own generator, which discovers
  your commands/queries/handlers, is unaffected. See
  [Validation behavior](docs/behaviors.md#validation-behavior) for the full explanation and example.

## [1.1.1] - 2026-07-14

### Fixed

- **Multi-project solutions could throw a spurious `RogueUnregisteredRequestException` for
  correctly-registered requests.** ([#21](https://github.com/skathio/rogue/issues/21)) When
  handlers live in one project (e.g. `MyApp.Application`) and the DI host lives in another (e.g.
  `MyApp.Api`) via `ProjectReference`, adding `SkathIO.Rogue.Validation.FluentValidation` (or any
  behavior package) to the handler project made the host project's generator falsely believe it had
  its own behaviors to register — NuGet propagates `PackageReference` transitively across
  `ProjectReference`, so the host saw the behavior assembly too. That defeated the module-initializer
  suppression that normally keeps a handler-less compilation from registering an empty dispatcher,
  and the empty dispatcher could non-deterministically win the cross-assembly registration race and
  shadow the real one. A compilation's own module initializer is no longer forced to run just because
  it can see a behavior declared elsewhere; a project that only *transitively* sees a behavior type no
  longer contributes a competing (empty) `RogueDispatcher` registration. Reproduced with a dedicated
  generator-level regression test before fixing (`MultiProjectBehaviorSuppressionTests`) — see
  `.somi/plans/pd17-metadata-suppression/rca.md` for the full root-cause record. **Note:** this is
  unrelated to the 1.1.0 FluentValidation fix below, despite both mentioning FluentValidation — that
  release fixed a Scoped/Singleton captive-dependency DI-resolution defect, not module-initializer
  suppression.

### Added

- **Multi-project smoke test.** `SkathIO.Rogue.Smoke.{Infrastructure,Application,Api,Tests}` boot a
  real layered ASP.NET Core solution (host / application / infrastructure, mirroring
  [#21](https://github.com/skathio/rogue/issues/21)'s shape) via `WebApplicationFactory` and drive
  every core dispatch kind — commands with/without a response, queries, notification fan-out,
  streaming, a custom pipeline behavior, and FluentValidation validation — through real HTTP calls.
  Runs on every pull request as part of the existing `ci` job (part of `SkathIO.Rogue.sln`); see
  `tests/SkathIO.Rogue.Smoke.Tests/README.md`.

## [1.1.0] - 2026-07-13

### Fixed

- **FluentValidation (and any other Scoped-dependent pipeline behavior) could fail DI resolution
  when `RogueOptions.Lifetime` was set to `Singleton`.** Setting `Lifetime = Singleton` (typically
  done for handler-performance reasons) also made every generated pipeline behavior Singleton — a
  captive-dependency trap for any behavior with a Scoped dependency, such as a FluentValidation
  `IValidator<T>` or a `DbContext`: `Cannot consume scoped service 'FluentValidation.IValidator<T>'
  from singleton 'ValidationBehavior<T,TResponse>'`. Reproduced with a dedicated integration test
  suite before fixing (see "Added" below). Consumers who left `Lifetime` at its default
  (`Transient`) were never affected and see no change at all.

### Changed

- **Pipeline behaviors are now always registered `Transient`, regardless of `RogueOptions.Lifetime`.**
  This is the fix for the defect above: behaviors (and the behavior-list factory) now always
  resolve `Transient`, matching MediatR's default and eliminating that whole class of DI-resolution
  failure. Handler and stream-handler self-registrations are unaffected and continue to honor
  `Lifetime` as before.
- **Dispatching outside a request scope** (e.g. from a `Singleton` `BackgroundService`, or at
  startup) requires resolving `ISender`/`IMediator` from within a created scope
  (`IServiceScopeFactory.CreateScope()`), not the root `IServiceProvider` — this is a deliberate,
  documented requirement of the Scoped dispatcher, not a bug. See the new
  [Scoped dispatch](README.md#scoped-dispatch) section for a wrong/right example and the exact
  exception text.
- **Minimum consumer Roslyn/SDK raised.** The shipped source generator
  (`SkathIO.Rogue.SourceGenerator`) now targets `Microsoft.CodeAnalysis.CSharp` 5.0.0 (up from
  4.12.0) — consumers need a .NET SDK whose bundled compiler is Roslyn 5.0.0 or newer (any .NET 10
  SDK) to load the analyzer/generator; older SDKs will see `CS9057`. This is a deliberate trade for
  currency. The dev-time-only analyzer-authoring/API-tracking tooling
  (`Microsoft.CodeAnalysis.Analyzers`, `Microsoft.CodeAnalysis.PublicApiAnalyzers`) moved to 5.6.0,
  which does **not** affect consumer min-SDK (never compiled into the shipped generator).
- Routine dependency bumps: `Microsoft.Extensions.DependencyInjection*` 10.0.0→10.0.9,
  `Microsoft.Extensions.Logging*` 10.0.0→10.0.9, `FluentValidation` 11.10.0→11.12.0, `MinVer`
  5.0.0→7.0.0, `Microsoft.SourceLink.GitHub` 8.0.0→10.0.300, `BenchmarkDotNet` 0.15.2→0.15.8
  (benchmarks only), `MediatR` 12.4.1→12.5.0 (benchmarks only), and test-infrastructure majors
  (`Microsoft.NET.Test.Sdk` 17.13→18.7, `xunit.runner.visualstudio` 2.8→3.1.5, `coverlet.collector`
  6→10, `Verify.Xunit` 27→31). `MediatR` is deliberately held below v13 (which moved to a commercial
  license) — this repo's benchmarks-only comparison dependency must not take one on.

### Added

- **New DI-resolution regression suite** (`SkathIO.Rogue.DiResolution.Tests`,
  `SkathIO.Rogue.Integration.Tests`) proving the fix above and pinning the scoped-dispatch
  requirement as a permanent test, under the strictest DI validation ASP.NET Core applies
  (`ValidateScopes`/`ValidateOnBuild`).
- **New MediatR compatibility shim runtime test suite** (`SkathIO.Rogue.MediatR.Tests`) covering
  real DI dispatch through the migration shim (adapter-mapped commands/queries, notification
  fan-out, and the `AddMediatR(cfg => ...)` drop-in registration path).
- **New benchmark: Rogue vs. MediatR with a real FluentValidation behavior in the pipeline**
  (`bench/SkathIO.Rogue.Benchmarks.Validation`) — a direct, steady-state comparison of the exact
  scenario this release's fix is about (a pipeline behavior with a Scoped dependency). See
  [bench/RESULTS.md](bench/RESULTS.md) and [docs/benchmarks.md](docs/benchmarks.md).

## [1.0.2] - 2026-06-29

### Changed

- **CI/CD pipeline migrated to [`skathio/hashira`](https://github.com/skathio/hashira)'s v2
  contract.** Releases are now cut via an explicit `workflow_dispatch` with a `bump`
  (`patch`/`minor`/`major`) input, resolved by hashira's version-resolver and fed to MinVer at pack
  time, replacing the previous direct pin. NuGet publishing continues to use OIDC trusted
  publishing. CI now also runs hashira's shared `nuget-package-ci.yml` (coverage reporting,
  CodeQL/OSV/Gitleaks/dependency-review) alongside the existing AOT publish, benchmark smoke,
  license check, and Public API checks.
- No behavior change to `SkathIO.Rogue`, `SkathIO.Rogue.Abstractions`, `SkathIO.Rogue.MediatR`,
  `SkathIO.Rogue.Logging`, or `SkathIO.Rogue.Validation.FluentValidation` — this release is
  CI/tooling only.

## [1.0.1] - 2026-06-22

### Changed

- **Package metadata:** `<Authors>`/`<Company>` renamed `skathio` → `SkathIO` (the NuGet
  organization's display name) ahead of transferring the packages to the SkathIO org account.
- **Public docs scrubbed** of internal development-process references (the repo is public, so
  committed docs are publicly visible): removed links to the private `.somi/` planning
  directories, neutralized internal work-item codenames, stripped dangling internal
  requirement/decision IDs (kept the public `ROGUE0xx`/`ROGM0xx` diagnostic IDs), and rewrote
  `bench/RESULTS.md` as a clean public benchmark report (methodology + current results; numbers
  unchanged).

## [1.0.0] - 2026-06-21

First public release. An AOT-safe, source-generated CQRS/mediator for .NET — handlers are discovered
and wired at compile time, with no runtime reflection or assembly scanning. On the reference hardware
Rogue meets or beats MediatR across every measured head-to-head scenario — single-handler `Send` (both
the mean and the generated concrete fast path), the untyped object path, and notification fan-out at
N = 2 / 5 / 20 — on both wall-clock and allocated bytes, alongside a ~19× cold-start lead. We still
commit to reporting any scenario where Rogue is not fastest, honestly and in the open; see
[bench/RESULTS.md](bench/RESULTS.md) for the full measured tables and methodology.

### Packages

- **SkathIO.Rogue** — runtime + the source generator (bundled as an `analyzers/dotnet/cs` asset).
- **SkathIO.Rogue.Abstractions** — contracts-only package (interfaces, `Unit`, markers).
- **SkathIO.Rogue.Logging** — `LoggingBehavior`, `LoggingOptions`, `[LogPayload]`.
- **SkathIO.Rogue.Validation.FluentValidation** — `ValidationBehavior` over FluentValidation.
- **SkathIO.Rogue.MediatR** — MediatR compatibility shim + migration analyzer (bundled).

### Added

- **Source-generated dispatch.** Compile-time discovery of handlers; generated dispatcher routes
  requests via a `switch`, not reflection. `AddRogue()` one-call registration wired through a
  module-initializer bridge so the generator runs in the consumer's compilation.
- **Message shapes.** A CQS-explicit core: `IQuery<T>` (read), `ICommand<T>` / `ICommand`
  (write, with/without response), `IEvent` with handler fan-out, and `IStreamQuery<T>` streaming
  (`IAsyncEnumerable<T>`, net8.0+) — each an independent contract. The MediatR-shaped
  `IRequest<T>` / `IRequest` / `INotification` / `IStreamRequest<T>` surface lives in the
  `SkathIO.Rogue.MediatR` compatibility adapter for drop-in migration.
- **Dispatch entry points.** `ISender`, `IPublisher`, `IMediator`; opt-in untyped `Send(object)` /
  `Publish(object)` via `RogueOptions.EnableObjectDispatch`. The default `Mediator` implementation
  constructor-injects and caches the generated `RogueDispatcher` (registered `Scoped`, matching
  `RogueDispatcher`'s lifetime) rather than resolving it per dispatch.
- **Public concrete dispatch path.** The generator emits `public static`
  `RogueDispatcher.Send{RequestType}(request, ct)` extension methods (in `SkathIO.Rogue.Generated`,
  one per command/query handler) that dispatch through the generated concrete entry point, bypassing the
  `ISender` `ValueTask<T>` box. Inject `RogueDispatcher` (the public base, registered in DI) instead of
  `ISender` to opt into this fast lane. Measured: ~30 ns concrete vs ~90 ns `ISender`.
- **Pipeline behaviors.** `IPipelineBehavior<,>` and `IStreamPipelineBehavior<,>`, woven at compile
  time, with ordering via `[BehaviorOrder]` or registration order. Open-generic behaviors
  auto-discovered from referenced assemblies. `IRoguePipelineInspector` to inspect the resolved
  pipeline for a request type.
- **Pre/post processors and exception handlers.** `IRequestPreProcessor`, `IRequestPostProcessor`,
  `IRequestExceptionAction`, and `IRequestExceptionHandler` (with `RequestExceptionHandlerState`),
  invoked at runtime via a generator-emitted processor wrap around the behavior engine, with a fast
  path for processor-free requests and reflection-free exception type matching.
- **Notification publishers.** `ForeachAwaitPublisher` (default) and `WhenAllPublisher`, selectable
  via `RogueOptions.NotificationPublisher`.
- **Compile-time diagnostics.** The generator surfaces wiring mistakes as build diagnostics:
  `ROGUE001`–`ROGUE006` (no handler / multiple handlers / response-type mismatch / non-constructable
  handler / abstract-or-no-public-ctor / unsupported open-generic request), `ROGUE010` (an `IMediator`
  injection suggestion), `ROGUE011` (a type implements multiple CQS contracts — the clean-break
  ambiguity), and `ROGUE012` (a MediatR-adapter command-vs-query mapping conflict). `ROGUE007` is
  intentionally unused (a removed-from-scope id, never reissued).
- **Logging integration.** `LoggingBehavior` with request/timing logs; payload logging **off by
  default** for safety, opt-in per request via `[LogPayload]` or globally via `LoggingOptions`.
- **Validation integration.** `ValidationBehavior` aggregates all `IValidator<TRequest>` and throws
  `ValidationException` before the handler runs.
- **Observability.** `RogueTelemetry` shim exposing an `ActivitySource` + `Meter` named
  `"SkathIO.Rogue"`, gated by `RogueOptions.EnableTelemetry` (zero overhead when off).
- **MediatR migration tooling.** Runtime compatibility shim and a Roslyn analyzer with code-fixes
  (`ROGM001` MediatR using directive, `ROGM002` `Task`→`ValueTask` return type, `ROGM003` open-generic
  request manual-migration, `ROGM004` `AddMediatR`→`AddRogue` forwarding, `ROGM005` ambiguous
  command-vs-query intent — migrated to `ICommand`, flagged for manual review, and `ROGM006`
  MediatR marker/handler interface → CQS contract rewrite) plus a
  [migration guide](docs/migration-guide.md) and before/after sample.
- **AOT + trimming.** AOT sample publishes with no IL trim or AOT warnings.
- **Benchmarks.** BenchmarkDotNet suite comparing against MediatR 12.4.1 across typed send, cold-start,
  behaviors, object path (and 25-type scaling), notification fan-out (N = 2/5/20), and streaming, with a
  standing commitment to document any scenario where Rogue is not fastest. (martinothamar/Mediator was an
  early comparison target, since removed from the suite.) See [docs/benchmarks.md](docs/benchmarks.md).
- **Packaging.** All packages ship MIT-licensed with SourceLink, embedded symbols, deterministic
  builds, MinVer versioning, and a packed README. Public API surface is tracked per package and
  enforced by CI.

### Performance

- **Behavior-list bypass.** Generated dispatch for a request with no statically-discovered behaviors
  skips the per-`Send` `GetService<IReadOnlyList<IPipelineBehavior<…>>>()` DI lookup entirely and calls
  the handler directly: ~90 ns / 48 B for a behavior-free `Send` (vs. MediatR's ~111 ns / 224 B on the
  reference hardware — see [bench/RESULTS.md](bench/RESULTS.md)).
- **Notification handler caching.** `Publish` caches per-event `Func<IEventHandler<T>>[]` factory arrays
  in the dispatcher constructor, eliminating a per-`Publish` `GetServices<IEventHandler<T>>()`
  enumeration; each event handler is registered under its own concrete type (alongside the
  `IEventHandler<T>` registration) so the cached factories resolve correctly.
- **Allocation-free notification fan-out.** `IEventPublisher.Publish` is generic over the event type
  (`Publish<TEvent>(IReadOnlyList<IEventHandler<TEvent>>, …)`) and receives the resolved, strongly-typed
  handlers directly — no per-handler wrapper, closure, or boxed enumerator. The generated `Publish`
  caches the publisher singleton and, when telemetry is off, bypasses the async state machine entirely.
  Rogue wins Publish N=5 (258 ns / 208 B vs MediatR 410 ns / 920 B) and N=20 (884 ns / 688 B vs
  1,459 ns / 3,200 B) on both axes.
- **Static behavior chains.** For closed (per-request) behaviors, the generator emits statically-typed
  chain methods (`Send_X_Chain_N`, up to 8 deep) that pass each behavior as a typed parameter instead of
  folding over the behavior list at runtime — pipeline latency is flat across behavior depth.

### Target frameworks

`netstandard2.0`, `net8.0`, `net10.0`. On netstandard2.0 the void path returns `ValueTask<Unit>`
(vs. `ValueTask` on net8.0+) and streaming types are net8.0+ only.

### Release notes

- **Repository visibility:** the repository must be **public** before tagging `v1.0.0`.
  The benchmark results, roadmap, and governance docs linked from the packages are only publicly
  accessible when the repo is public; tagging a release on a private repo would publish packages whose
  linked documentation is unreachable. Confirm visibility in GitHub settings before tagging. See
  [docs/governance.md](docs/governance.md#release-readiness).

[Unreleased]: https://github.com/skathio/rogue/compare/v1.2.1...HEAD
[1.2.1]: https://github.com/skathio/rogue/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/skathio/rogue/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/skathio/rogue/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/skathio/rogue/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/skathio/rogue/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/skathio/rogue/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/skathio/rogue/releases/tag/v1.0.0
