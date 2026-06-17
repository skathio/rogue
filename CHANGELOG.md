# Changelog

All notable changes to SkathIO.Rogue are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0]

First public release. An AOT-safe, source-generated CQRS/mediator for .NET — handlers are discovered
and wired at compile time, with no runtime reflection or assembly scanning.

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
  `Publish(object)` via `RogueOptions.EnableObjectDispatch`.
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
- **Benchmarks.** BenchmarkDotNet suite comparing against MediatR 12.4.1 and martinothamar/Mediator
  3.0.2 across typed send, cold-start, behaviors, object path (and 25-type scaling), notification
  fan-out (N = 2/5/20), and streaming, with a documented NFR-PERF-5 honesty scenario (notification
  fan-out allocation). See [docs/benchmarks.md](docs/benchmarks.md).
- **Packaging.** All packages ship MIT-licensed with SourceLink, embedded symbols, deterministic
  builds, MinVer versioning, and a packed README. Public API surface is tracked per package and
  enforced by CI.

### Target frameworks

`netstandard2.0`, `net8.0`, `net10.0`. On netstandard2.0 the void path returns `ValueTask<Unit>`
(vs. `ValueTask` on net8.0+) and streaming types are net8.0+ only.

### Release notes

- **Repository visibility (NFR-LIC-2):** the repository must be **public** before tagging `v1.0.0`.
  The benchmark results, roadmap, and governance docs linked from the packages are only publicly
  accessible when the repo is public; tagging a release on a private repo would publish packages whose
  linked documentation is unreachable. Confirm visibility in GitHub settings before tagging. See
  [docs/governance.md](docs/governance.md#release-readiness).

[Unreleased]: https://github.com/skathio/rogue/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/skathio/rogue/releases/tag/v1.0.0
