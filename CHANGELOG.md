# Changelog

All notable changes to SkathIO.Rogue are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/skathio/rogue/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/skathio/rogue/releases/tag/v1.0.0
